using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Utils;
using VyronSkill.Config;
using VyronSkill.Core;

namespace VyronSkill.Game;

/// <summary>Outcome of a dash request, mainly used for logging and player feedback.</summary>
internal enum DashActivationResult
{
    Activated = 0,
    Disabled = 1,
    NotAlive = 2,
    Downed = 3,
    FreezeTime = 4,
    AlreadyDashing = 5,
    OnCooldown = 6,
    AirNotAllowed = 7,
}

/// <summary>Per player state of 动力推进 (dash + cooldown).</summary>
internal sealed class PlayerAbilityState
{
    public PlayerAbilityState(int slot, float readyAt)
    {
        Slot = slot;
        ReadyAt = readyAt;
    }

    public int Slot { get; }

    /// <summary>Game time (Server.CurrentTime) at which the ability becomes ready again.</summary>
    public float ReadyAt { get; set; }

    public bool IsDashing { get; set; }

    public float DashEndsAt { get; set; }

    public float DashSpeed { get; set; }

    public float DirectionX { get; set; }

    public float DirectionY { get; set; }

    public bool HudVisible { get; set; }

    public float NextHudAt { get; set; }

    /// <summary>True once the "ability ready" notification was played for the current cooldown.</summary>
    public bool ReadyAnnounced { get; set; } = true;

    /// <summary>
    /// Per player 干员偏好 override: true = 仅4个正方向, false = 8 directions, null = follow the server
    /// default from the config file.
    /// </summary>
    public bool? PreferFourWay { get; set; }

    public int DashCount { get; set; }

    public int RewardCount { get; set; }

    public float RemainingCooldown(float now) => MathF.Max(0f, ReadyAt - now);
}

/// <summary>
/// 动力推进 (Power Boost): an 8-direction (or 4-direction) 10 m jet dash on a 15 s cooldown that also
/// works in mid-air, plus the kill driven cooldown reduction loop that makes Vyron so mobile.
/// </summary>
internal sealed class PowerBoostService
{
    private readonly VyronSkillPlugin _plugin;
    private readonly KnockdownService _knockdown;
    private readonly Dictionary<int, PlayerAbilityState> _states = new();

    public PowerBoostService(VyronSkillPlugin plugin, KnockdownService knockdown)
    {
        _plugin = plugin;
        _knockdown = knockdown;
    }

    private VyronSkillConfig Config => _plugin.Config;

    public bool IsDashing(int slot) => _states.TryGetValue(slot, out var state) && state.IsDashing;

    public float RemainingCooldown(int slot)
        => _states.TryGetValue(slot, out var state) ? state.RemainingCooldown(Server.CurrentTime) : 0f;

    public IReadOnlyDictionary<int, PlayerAbilityState> States => _states;

    /// <summary>Called on spawn (or on demand) to make the ability ready immediately.</summary>
    public void ResetCooldown(int slot)
    {
        var state = GetState(slot);
        state.ReadyAt = Server.CurrentTime;
        state.IsDashing = false;
        state.ReadyAnnounced = true;
    }

    public void ResetAllCooldowns()
    {
        var now = Server.CurrentTime;
        foreach (var state in _states.Values)
        {
            state.ReadyAt = now;
            state.IsDashing = false;
            state.ReadyAnnounced = true;
        }
    }

    public void Clear(int slot)
    {
        if (_states.TryGetValue(slot, out var state))
        {
            state.IsDashing = false;
        }

        _states.Remove(slot);
    }

    /// <summary>Attempts to fire the jet. Handles all validation and player feedback.</summary>
    public DashActivationResult TryActivate(CCSPlayerController player)
    {
        if (!Config.Enabled)
        {
            return Reject(player, DashActivationResult.Disabled);
        }

        if (player is null || !player.IsValid || !player.PawnIsAlive)
        {
            return Reject(player, DashActivationResult.NotAlive);
        }

        var state = GetState(player.Slot);
        var now = Server.CurrentTime;

        if (_knockdown.IsDowned(player.Slot))
        {
            return Reject(player, DashActivationResult.Downed);
        }

        if (state.IsDashing)
        {
            return Reject(player, DashActivationResult.AlreadyDashing);
        }

        if (Config.Dash.BlockDuringFreezeTime && !_plugin.RoundLive)
        {
            return Reject(player, DashActivationResult.FreezeTime);
        }

        if (now < state.ReadyAt)
        {
            return Reject(player, DashActivationResult.OnCooldown);
        }

        var pawn = player.PlayerPawn?.Value;
        if (pawn is null || !pawn.IsValid)
        {
            return Reject(player, DashActivationResult.NotAlive);
        }

        if (IsAirborne(pawn) && !Config.Dash.AllowInAir)
        {
            return Reject(player, DashActivationResult.AirNotAllowed);
        }

        var direction = ResolveDirection(player, pawn);
        var speed = DashMath.SpeedForDash(Config.Dash.DistanceMeters, Config.Dash.DurationSeconds);

        if (speed <= 0f || direction.IsZero)
        {
            return Reject(player, DashActivationResult.NotAlive);
        }

        state.IsDashing = true;
        state.DashEndsAt = now + Config.Dash.DurationSeconds;
        state.DashSpeed = speed;
        state.DirectionX = direction.X;
        state.DirectionY = direction.Y;
        state.DashCount++;
        state.ReadyAt = now + Config.Cooldown.BaseSeconds;
        state.ReadyAnnounced = Config.Cooldown.BaseSeconds <= 0f;

        if (Config.Feedback.ChatOnUse)
        {
            Notify(player, "vyron.dash.activated", Config.Cooldown.BaseSeconds);
        }

        PlaySound(player, Config.Feedback.SoundOnUse);

        return DashActivationResult.Activated;
    }

    /// <summary>
    /// Applies a takedown reward: 烽火地带 subtracts a fixed number of seconds, 全面战场 refreshes the
    /// cooldown so a player who keeps killing can keep dashing.
    /// </summary>
    public void ApplyReward(CCSPlayerController player, in CooldownReward reward)
    {
        if (player is null || !player.IsValid || reward.IsNothing)
        {
            return;
        }

        var state = GetState(player.Slot);
        var now = Server.CurrentTime;

        if (reward.Kind == RewardKind.FullRefresh)
        {
            state.ReadyAt = now;
            state.ReadyAnnounced = false;
            state.RewardCount++;

            if (Config.Feedback.ChatOnReward)
            {
                Notify(player, "vyron.reward.refresh");
            }

            return;
        }

        if (now >= state.ReadyAt)
        {
            // The ability is already ready - there is nothing left to reduce.
            return;
        }

        state.ReadyAt = MathF.Max(now, state.ReadyAt - reward.Seconds);
        state.RewardCount++;

        if (Config.Feedback.ChatOnReward)
        {
            var actionKey = ActionKey(reward.Source, reward.VictimIsBot);
            var action = _plugin.Localizer.ForPlayer(player, actionKey);
            Notify(player, "vyron.reward.reduce", action, reward.Seconds, state.RemainingCooldown(now));
        }
    }

    /// <summary>Runs every server tick: drives the jet, the cooldown HUD and the "ready" notification.</summary>
    public void Tick()
    {
        if (_states.Count == 0)
        {
            return;
        }

        var config = Config;
        var now = Server.CurrentTime;

        foreach (var state in _states.Values)
        {
            var player = Utilities.GetPlayerFromSlot(state.Slot);
            if (player is null || !player.IsValid)
            {
                state.IsDashing = false;
                continue;
            }

            if (state.IsDashing)
            {
                UpdateDash(player, state, config, now);
            }

            UpdateHud(player, state, config, now);
            UpdateReadyNotification(player, state, config, now);
        }
    }

    /// <summary>
    /// Writes the dash velocity straight into the pawn. The player movement code uses this velocity
    /// as the starting velocity of the tick, so the burst is collision aware (it cannot push a player
    /// through a wall, unlike teleport based dashes) and it keeps momentum when the burn ends.
    /// </summary>
    private void UpdateDash(CCSPlayerController player, PlayerAbilityState state, VyronSkillConfig config, float now)
    {
        var pawn = player.PlayerPawn?.Value;

        if (pawn is null || !pawn.IsValid || !player.PawnIsAlive || now >= state.DashEndsAt || _knockdown.IsDowned(state.Slot))
        {
            state.IsDashing = false;
            return;
        }

        var velocityX = state.DirectionX * state.DashSpeed;
        var velocityY = state.DirectionY * state.DashSpeed;

        pawn.Velocity.X = velocityX;
        pawn.Velocity.Y = velocityY;
        pawn.AbsVelocity.X = velocityX;
        pawn.AbsVelocity.Y = velocityY;

        if (config.Dash.AirVerticalBoost != 0f && IsAirborne(pawn))
        {
            pawn.Velocity.Z = config.Dash.AirVerticalBoost;
            pawn.AbsVelocity.Z = config.Dash.AirVerticalBoost;
        }
    }

    private void UpdateHud(CCSPlayerController player, PlayerAbilityState state, VyronSkillConfig config, float now)
    {
        if (!config.Feedback.HudEnabled || _knockdown.IsDowned(state.Slot))
        {
            if (state.HudVisible)
            {
                ClearHud(player);
                state.HudVisible = false;
            }

            return;
        }

        var remaining = state.RemainingCooldown(now);

        if (remaining <= 0f)
        {
            if (state.HudVisible)
            {
                state.HudVisible = false;
                ClearHud(player);
            }

            return;
        }

        if (now < state.NextHudAt)
        {
            return;
        }

        var interval = MathF.Max(0.05f, config.Feedback.HudIntervalSeconds);
        state.NextHudAt = now + interval;
        state.HudVisible = true;

        var text = _plugin.Localizer.ForPlayer(player, "vyron.hud.cooldown", remaining);
        player.PrintToCenterHtml(
            $"<font color='#7dd3fc'>{text}</font>",
            (int)MathF.Max(150f, (interval * 1000f) + 50f));
    }

    private static void ClearHud(CCSPlayerController player)
        => player.PrintToCenterHtml("<font> </font>", 50);

    private void UpdateReadyNotification(CCSPlayerController player, PlayerAbilityState state, VyronSkillConfig config, float now)
    {
        if (state.ReadyAnnounced || state.IsDashing || now < state.ReadyAt)
        {
            return;
        }

        state.ReadyAnnounced = true;
        PlaySound(player, config.Feedback.SoundOnReady);
    }

    private DashVector ResolveDirection(CCSPlayerController player, CCSPlayerPawn pawn)
    {
        var yaw = pawn.EyeAngles.Y;
        var buttons = player.Buttons;

        var forward = buttons.HasFlag(PlayerButtons.Forward);
        var back = buttons.HasFlag(PlayerButtons.Back);
        var left = buttons.HasFlag(PlayerButtons.Moveleft);
        var right = buttons.HasFlag(PlayerButtons.Moveright);

        var fourWay = _states.TryGetValue(player.Slot, out var state) && state.PreferFourWay.HasValue
            ? state.PreferFourWay.Value
            : Config.DirectionMode == DashDirectionMode.FourWay;

        if (fourWay)
        {
            // 干员偏好 "仅4个正方向": only the most recently pressed key counts, so no diagonal can
            // ever be triggered by accident.
            var key = _plugin.Inputs.ActiveKey(player.Slot);
            if (key == DirectionKey.None)
            {
                key = DashMath.KeyFromButtonFlags(forward, back, left, right);
            }

            return DashMath.ResolveFourWay(yaw, key);
        }

        return DashMath.ResolveEightWay(yaw, forward, back, left, right);
    }

    /// <summary>
    /// Toggles the player's 干员偏好 between 8 directions and 仅4个正方向 and reports the new setting.
    /// </summary>
    public bool ToggleDirectionPreference(CCSPlayerController player)
    {
        var state = GetState(player.Slot);
        var current = state.PreferFourWay ?? Config.DirectionMode == DashDirectionMode.FourWay;
        state.PreferFourWay = !current;

        Notify(player, state.PreferFourWay.Value ? "vyron.pref.four_way" : "vyron.pref.eight_way");

        return state.PreferFourWay.Value;
    }

    /// <summary>Prints the in-game help for the ability.</summary>
    public void SendHelp(CCSPlayerController player)
    {
        Notify(player, "vyron.help.controls");
        Notify(player, "vyron.help.direction");
        Notify(player, "vyron.help.cooldown");
    }

    private DashActivationResult Reject(CCSPlayerController? player, DashActivationResult result)
    {
        if (player is null || !player.IsValid || result == DashActivationResult.Activated || !Config.Feedback.ChatOnCooldown)
        {
            return result;
        }

        switch (result)
        {
            case DashActivationResult.Disabled:
                Notify(player, "vyron.dash.disabled");
                break;

            case DashActivationResult.NotAlive:
                Notify(player, "vyron.dash.not_alive");
                break;

            case DashActivationResult.Downed:
                Notify(player, "vyron.dash.downed");
                break;

            case DashActivationResult.FreezeTime:
                Notify(player, "vyron.dash.freeze_time");
                break;

            case DashActivationResult.AlreadyDashing:
                Notify(player, "vyron.dash.in_progress");
                break;

            case DashActivationResult.AirNotAllowed:
                Notify(player, "vyron.dash.air_not_allowed");
                break;

            case DashActivationResult.OnCooldown:
                Notify(player, "vyron.dash.cooldown", RemainingCooldown(player.Slot));
                break;
        }

        return result;
    }

    private PlayerAbilityState GetState(int slot)
    {
        if (!_states.TryGetValue(slot, out var state))
        {
            state = new PlayerAbilityState(slot, Server.CurrentTime);
            _states[slot] = state;
        }

        return state;
    }

    private static bool IsAirborne(CCSPlayerPawn pawn)
        => (pawn.Flags & (uint)PlayerFlags.FL_ONGROUND) == 0;

    private static string ActionKey(RewardSource source, bool victimIsBot) => (source, victimIsBot) switch
    {
        (RewardSource.Kill, true) => "vyron.action.kill_bot",
        (RewardSource.Kill, false) => "vyron.action.kill_player",
        (RewardSource.Knockdown, true) => "vyron.action.knockdown_bot",
        (RewardSource.Knockdown, false) => "vyron.action.knockdown_player",
        (RewardSource.Assist, true) => "vyron.action.assist_bot",
        _ => "vyron.action.assist_player",
    };

    private void Notify(CCSPlayerController player, string key, params object[] args)
        => player.PrintToChat(_plugin.Localizer.ForPlayer(player, key, args));

    private void PlaySound(CCSPlayerController player, string sound)
    {
        if (string.IsNullOrWhiteSpace(sound))
        {
            return;
        }

        player.ExecuteClientCommand($"play {sound}");
    }
}
