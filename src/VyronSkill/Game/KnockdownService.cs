using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using VyronSkill.Config;
using VyronSkill.Core;

namespace VyronSkill.Game;

/// <summary>State of one downed (鍑诲€? player.</summary>
internal sealed class DownedState
{
    public DownedState(int slot, float bleedOutAt)
    {
        Slot = slot;
        BleedOutAt = bleedOutAt;
    }

    public int Slot { get; }

    /// <summary>Game time at which the player bleeds out and dies.</summary>
    public float BleedOutAt { get; }

    public float ReviveProgress { get; set; }

    public int ReviverSlot { get; set; } = -1;

    public float NextHudAt { get; set; }
}

/// <summary>
/// Optional 鍑诲€?(down but not out) module.
/// <para>
/// CS2 has no native downed state, so a lethal hit from an enemy is converted on the fly: the damage
/// is clamped in <c>OnPlayerTakeDamagePre</c> so the victim survives at low health, is frozen,
/// disarmed and can be saved by a teammate who holds USE next to them. If they are not saved they
/// bleed out and die, and the enemy who finishes them gets a normal kill.
/// </para>
/// <para>
/// This is what makes 鐑界伀鍦板甫鐨?"鍑诲€?鍑绘潃鐜╁ -10 s" work exactly like Delta Force, instead of
/// approximating it with assists. It is disabled by default because it also changes round flow.
/// </para>
/// </summary>
internal sealed class KnockdownService
{
    private const float HudIntervalSeconds = 0.25f;

    private readonly VyronSkillPlugin _plugin;
    private readonly Dictionary<int, DownedState> _downed = new();
    private readonly Dictionary<int, int> _knockdownsThisRound = new();

    public KnockdownService(VyronSkillPlugin plugin)
    {
        _plugin = plugin;
    }

    /// <summary>Raised when an enemy player is knocked down, so the attacker gets the 鍑诲€?reward.</summary>
    public event Action<CCSPlayerController, CooldownReward>? KnockedDown;

    private KnockdownConfig Config => _plugin.Config.Knockdown;

    private bool Active => _plugin.Config.Enabled && _plugin.Config.Knockdown.Enabled;

    public bool IsDowned(int slot) => _downed.ContainsKey(slot);

    public int DownedCount => _downed.Count;

    /// <summary>
    /// Converts a lethal enemy hit into a knockdown by clamping the damage before it is applied.
    /// Returning <see cref="HookResult.Continue"/> keeps every other damage source untouched.
    /// </summary>
    public HookResult OnPlayerTakeDamagePre(CCSPlayerPawn pawn, CTakeDamageInfo info)
    {
        try
        {
            if (!Active || pawn is null || !pawn.IsValid || info is null)
            {
                return HookResult.Continue;
            }

            var pluginConfig = _plugin.Config;
            var config = pluginConfig.Knockdown;

            if (config.OnlyInHazardOps && pluginConfig.Mode != GameModeKind.HazardOps)
            {
                return HookResult.Continue;
            }

            if (!_plugin.RoundLive)
            {
                return HookResult.Continue;
            }

            var victim = pawn.Controller?.Value as CCSPlayerController;
            if (victim is null || !victim.IsValid || !victim.PawnIsAlive)
            {
                return HookResult.Continue;
            }

            if (_downed.ContainsKey(victim.Slot) || (victim.IsBot && !config.ApplyToBots))
            {
                return HookResult.Continue;
            }

            if (config.MaxPerRound > 0 && CountThisRound(victim.Slot) >= config.MaxPerRound)
            {
                return HookResult.Continue;
            }

            var health = pawn.Health;
            if (health <= 0 || info.Damage < health)
            {
                // Not lethal - nothing to convert.
                return HookResult.Continue;
            }

            if (config.HeadshotBypasses && info.HitGroupId == HitGroup_t.HITGROUP_HEAD)
            {
                return HookResult.Continue;
            }

            var attacker = ResolveAttacker(info);
            if (attacker is null || attacker.Slot == victim.Slot || !IsEnemy(attacker, victim))
            {
                return HookResult.Continue;
            }

            var targetHealth = Math.Min(health, config.HealthAfterKnockdown);
            info.Damage = MathF.Max(0f, health - targetHealth);

            Apply(victim, attacker);

            return HookResult.Continue;
        }
        catch (Exception exception)
        {
            _plugin.LogThrottled(exception, "knockdown damage hook");
            return HookResult.Continue;
        }
    }

    /// <summary>Freezes downed players, runs the bleed-out timer and processes revives.</summary>
    public void Tick()
    {
        if (_downed.Count == 0)
        {
            return;
        }

        if (!Active)
        {
            ClearAll();
            return;
        }

        var config = Config;
        var now = Server.CurrentTime;
        List<int>? release = null;

        foreach (var state in _downed.Values)
        {
            var player = Utilities.GetPlayerFromSlot(state.Slot);
            if (player is null || !player.IsValid || !player.PawnIsAlive)
            {
                (release ??= new List<int>()).Add(state.Slot);
                continue;
            }

            var pawn = player.PlayerPawn?.Value;
            if (pawn is null || !pawn.IsValid)
            {
                (release ??= new List<int>()).Add(state.Slot);
                continue;
            }

            Freeze(pawn);

            if (now >= state.BleedOutAt)
            {
                Notify(player, "vyron.knockdown.bleed_out");
                ClearHud(player);
                player.CommitSuicide(false, true);
                (release ??= new List<int>()).Add(state.Slot);
                continue;
            }

            if (TryRevive(player, pawn, state, config, now))
            {
                (release ??= new List<int>()).Add(state.Slot);
                continue;
            }

            UpdateDownedHud(player, state, config, now);
        }

        if (release is not null)
        {
            foreach (var slot in release)
            {
                _downed.Remove(slot);
            }
        }
    }

    /// <summary>A player respawned: drop any downed bookkeeping for them.</summary>
    public void OnPlayerSpawn(CCSPlayerController player)
    {
        if (player is null || !player.IsValid)
        {
            return;
        }

        Release(player, player.Slot);
    }

    /// <summary>A new round started: clear everything and let everyone stand up again.</summary>
    public void OnRoundStart()
    {
        ClearAll();
        _knockdownsThisRound.Clear();
    }

    /// <summary>
    /// The round ended: stand everyone up so nobody stays frozen while the next round is prepared.
    /// </summary>
    public void OnRoundEnd() => ClearAll();

    /// <summary>A player died (possibly while downed): forget their state.</summary>
    public void OnVictimDied(CCSPlayerController victim)
    {
        if (victim is null || !victim.IsValid)
        {
            return;
        }

        if (_downed.ContainsKey(victim.Slot))
        {
            ClearHud(victim);
        }

        Release(victim, victim.Slot);
    }

    public void Clear(int slot)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        Release(player, slot);
    }

    /// <summary>Removes every downed state and unfreezes the players (round start, unload, disable).</summary>
    public void ClearAll()
    {
        if (_downed.Count == 0)
        {
            return;
        }

        // Copy the slots first: Release() removes entries from the dictionary.
        var slots = new int[_downed.Count];
        _downed.Keys.CopyTo(slots, 0);

        foreach (var slot in slots)
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            Release(player, slot);
        }

        _downed.Clear();
    }

    private void Apply(CCSPlayerController victim, CCSPlayerController attacker)
    {
        var config = Config;
        var now = Server.CurrentTime;

        _downed[victim.Slot] = new DownedState(victim.Slot, now + config.BleedOutSeconds);
        _knockdownsThisRound[victim.Slot] = CountThisRound(victim.Slot) + 1;

        if (config.RemoveWeapons)
        {
            victim.RemoveWeapons();
        }

        Notify(victim, "vyron.knockdown.victim", config.BleedOutSeconds);

        var reward = CooldownRules.ForKnockdown(_plugin.Config.ToRewardSettings(), victim.IsBot);
        KnockedDown?.Invoke(attacker, reward);

        _plugin.Logger?.LogDebug("[VyronSkill] {Victim} downed by {Attacker} (bleed out in {Seconds}s)", victim.PlayerName, attacker.PlayerName, config.BleedOutSeconds);
    }

    private bool TryRevive(CCSPlayerController player, CCSPlayerPawn pawn, DownedState state, KnockdownConfig config, float now)
    {
        var reviver = FindReviver(player, pawn, config);

        if (reviver is null)
        {
            state.ReviverSlot = -1;
            state.ReviveProgress = 0f;
            return false;
        }

        if (state.ReviverSlot != reviver.Slot)
        {
            state.ReviverSlot = reviver.Slot;
            state.ReviveProgress = 0f;
        }

        state.ReviveProgress += Server.TickInterval;

        if (state.ReviveProgress < config.ReviveHoldSeconds)
        {
            return false;
        }

        Revive(player, pawn, reviver, config);
        return true;
    }

    private CCSPlayerController? FindReviver(CCSPlayerController victim, CCSPlayerPawn victimPawn, KnockdownConfig config)
    {
        var victimOrigin = victimPawn.AbsOrigin;
        if (victimOrigin is null)
        {
            return null;
        }

        CCSPlayerController? best = null;
        var bestDistance = float.MaxValue;

        foreach (var candidate in Utilities.GetPlayers())
        {
            if (candidate is null || !candidate.IsValid || !candidate.PawnIsAlive || candidate.Slot == victim.Slot)
            {
                continue;
            }

            if (candidate.Team != victim.Team || !IsCombatTeam(candidate.Team))
            {
                continue;
            }

            if ((candidate.Buttons & PlayerButtons.Use) == 0)
            {
                continue;
            }

            var candidatePawn = candidate.PlayerPawn?.Value;
            var candidateOrigin = candidatePawn?.AbsOrigin;

            if (candidatePawn is null || !candidatePawn.IsValid || candidateOrigin is null)
            {
                continue;
            }

            var deltaX = candidateOrigin.X - victimOrigin.X;
            var deltaY = candidateOrigin.Y - victimOrigin.Y;
            var deltaZ = candidateOrigin.Z - victimOrigin.Z;
            var distance = MathF.Sqrt((deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ));

            if (distance > config.ReviveRadiusUnits || distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            best = candidate;
        }

        return best;
    }

    private void Revive(CCSPlayerController player, CCSPlayerPawn pawn, CCSPlayerController reviver, KnockdownConfig config)
    {
        Unfreeze(pawn);

        var maxHealth = pawn.MaxHealth > 0 ? pawn.MaxHealth : config.ReviveHealth;
        var health = Math.Clamp(config.ReviveHealth, 1, maxHealth);

        pawn.Health = health;
        player.PawnHealth = (uint)health;

        if (config.RemoveWeapons && config.GiveKnifeOnRevive)
        {
            player.GiveNamedItem("weapon_knife");
        }

        ClearHud(player);
        Notify(player, "vyron.knockdown.revived", reviver.PlayerName);
        Notify(reviver, "vyron.knockdown.revive_done", player.PlayerName);
    }

    private void UpdateDownedHud(CCSPlayerController player, DownedState state, KnockdownConfig config, float now)
    {
        if (now < state.NextHudAt)
        {
            return;
        }

        state.NextHudAt = now + HudIntervalSeconds;

        var progress = config.ReviveHoldSeconds <= 0f
            ? 0f
            : Math.Clamp(state.ReviveProgress / config.ReviveHoldSeconds, 0f, 1f);

        var text = state.ReviverSlot >= 0
            ? _plugin.Localizer.ForPlayer(player, "vyron.knockdown.hud_reviving", progress * 100f)
            : _plugin.Localizer.ForPlayer(player, "vyron.knockdown.hud_downed", MathF.Max(0f, state.BleedOutAt - now));

        player.PrintToCenterHtml($"<font color='#f87171'>{text}</font>", 300);

        if (state.ReviverSlot < 0)
        {
            return;
        }

        var reviver = Utilities.GetPlayerFromSlot(state.ReviverSlot);
        if (reviver is null || !reviver.IsValid)
        {
            return;
        }

        var reviverText = _plugin.Localizer.ForPlayer(reviver, "vyron.knockdown.hud_rescuing", player.PlayerName, progress * 100f);
        reviver.PrintToCenterHtml($"<font color='#4ade80'>{reviverText}</font>", 300);
    }

    private void Release(CCSPlayerController? player, int slot)
    {
        if (!_downed.Remove(slot))
        {
            return;
        }

        var pawn = player?.PlayerPawn?.Value;
        if (pawn is not null && pawn.IsValid)
        {
            Unfreeze(pawn);
        }
    }

    private int CountThisRound(int slot) => _knockdownsThisRound.TryGetValue(slot, out var count) ? count : 0;

    private static CCSPlayerController? ResolveAttacker(CTakeDamageInfo info)
    {
        if (info.Attacker is null || !info.Attacker.IsValid)
        {
            return null;
        }

        var entity = info.Attacker.Value;
        if (entity is null)
        {
            return null;
        }

        if (entity is CCSPlayerController controller)
        {
            return controller.IsValid ? controller : null;
        }

        if (entity is CCSPlayerPawn pawn)
        {
            var owner = pawn.Controller?.Value as CCSPlayerController;
            return owner is not null && owner.IsValid ? owner : null;
        }

        // Grenades, projectiles and other entities: fall back to an entity index lookup.
        return Utilities.GetPlayerFromIndex((int)entity.Index);
    }

    private static bool IsEnemy(CCSPlayerController candidate, CCSPlayerController victim)
        => candidate.Slot != victim.Slot
           && IsCombatTeam(candidate.Team)
           && IsCombatTeam(victim.Team)
           && candidate.Team != victim.Team;

    private static bool IsCombatTeam(CsTeam team) => team is CsTeam.Terrorist or CsTeam.CounterTerrorist;

    private static void Freeze(CCSPlayerPawn pawn)
    {
        pawn.Flags = pawn.Flags | (uint)PlayerFlags.FL_FROZEN;
        pawn.Velocity.X = 0f;
        pawn.Velocity.Y = 0f;
        pawn.AbsVelocity.X = 0f;
        pawn.AbsVelocity.Y = 0f;
    }

    private static void Unfreeze(CCSPlayerPawn pawn)
        => pawn.Flags = pawn.Flags & ~(uint)PlayerFlags.FL_FROZEN;

    private static void ClearHud(CCSPlayerController player) => player.PrintToCenterHtml("<font> </font>", 50);

    private void Notify(CCSPlayerController player, string key, params object[] args)
        => player.PrintToChat(_plugin.Localizer.ForPlayer(player, key, args));
}
