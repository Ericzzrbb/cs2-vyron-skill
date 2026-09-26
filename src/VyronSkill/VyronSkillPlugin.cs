using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using VyronSkill.Config;
using VyronSkill.Core;
using VyronSkill.Game;

namespace VyronSkill;

/// <summary>
/// Delta Force: Hawk Ops 干员 威龙 (Vyron) - 动力推进 (Power Boost) for Counter-Strike 2.
/// <para>
/// A 10 m jet dash that works on the ground and in mid-air (8 directions by default, or the four
/// cardinals with the 干员偏好 4-way option) on a 15 s base cooldown, whose cooldown is reduced by
/// taking enemies down:
/// 烽火地带 (Hazard Operations) -7 s per bot takedown and -10 s per player takedown/kill;
/// 全面战场 (All-out Warfare) refreshes the cooldown completely after every kill, which is what
/// allows the endless chain dashing Vyron is known for.
/// </para>
/// </summary>
public sealed class VyronSkillPlugin : BasePlugin, IPluginConfig<VyronSkillConfig>
{
    private float _nextErrorLogAt;

    public override string ModuleName => "VyronSkill";

    public override string ModuleVersion => "1.0.0";

    public override string ModuleAuthor => "cs2-vyron-skill";

    public override string ModuleDescription => "Delta Force operator Vyron's 动力推进 (Power Boost) jet dash for CS2.";

    public VyronSkillConfig Config { get; set; } = new();

    /// <summary>Movement key history, used for the 4-direction (仅4个正方向) preference.</summary>
    internal MovementInputTracker Inputs { get; } = new();

    internal KnockdownService Knockdown { get; private set; } = null!;

    internal PowerBoostService PowerBoost { get; private set; } = null!;

    /// <summary>True between <c>round_freeze_end</c> and <c>round_end</c>.</summary>
    public bool RoundLive { get; private set; }

    public void OnConfigParsed(VyronSkillConfig config)
    {
        config.Validate(Logger);
        Config = config;

        Logger?.LogInformation(
            "[VyronSkill] config: mode={Mode}, dash={Distance}m/{Duration}s, cooldown={Cooldown}s, direction={Direction}, knockdown={Knockdown}",
            Config.Mode,
            Config.Dash.DistanceMeters,
            Config.Dash.DurationSeconds,
            Config.Cooldown.BaseSeconds,
            Config.DirectionMode,
            Config.Knockdown.Enabled);
    }

    public override void Load(bool hotReload)
    {
        Knockdown = new KnockdownService(this);
        PowerBoost = new PowerBoostService(this, Knockdown);

        // 击倒 -> 冷却缩减: the knockdown module reports takedowns back to the ability.
        Knockdown.KnockedDown += (attacker, reward) => PowerBoost.ApplyReward(attacker, reward);

        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterListener<Listeners.OnPlayerButtonsChanged>(Inputs.OnButtonsChanged);
        RegisterListener<Listeners.OnPlayerTakeDamagePre>(Knockdown.OnPlayerTakeDamagePre);

        RegisterEventHandler<EventRoundStart>(OnRoundStart, HookMode.Post);
        RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd, HookMode.Post);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd, HookMode.Post);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn, HookMode.Post);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath, HookMode.Post);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect, HookMode.Post);

        RegisterAliasCommands();

        Logger?.LogInformation("[VyronSkill] loaded (hotReload={HotReload}), enable/disable with css_vyron_status / css_vyron_mode", hotReload);
    }

    /// <summary>
    /// Chat shortcuts and console aliases. Registered programmatically because a counter-strike chat
    /// trigger (<c>!dash</c>) is resolved through the same command table, and because each of them is
    /// also usable from a client <c>bind</c> (for example <c>bind "f" "css_vyron"</c>).
    /// </summary>
    private void RegisterAliasCommands()
    {
        AddCommand("dash", "Activate Vyron's Power Boost dash.", (player, command) => HandleDash(player, command));
        AddCommand("vyron", "Activate Vyron's Power Boost dash.", (player, command) => HandleDash(player, command));
        AddCommand("dashmode", "Toggle the 8/4 direction preference (干员偏好).", OnAliasDirectionCommand);
        AddCommand("vyronhelp", "Show how to use Vyron's Power Boost.", OnAliasHelpCommand);
    }

    private void OnAliasDirectionCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is null || !player.IsValid)
        {
            return;
        }

        PowerBoost.ToggleDirectionPreference(player);
    }

    private void OnAliasHelpCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is null || !player.IsValid)
        {
            return;
        }

        PowerBoost.SendHelp(player);
    }

    public override void Unload(bool hotReload)
    {
        // Never leave a player frozen if the plugin is unloaded while someone is downed.
        Knockdown?.ClearAll();
        Inputs.ClearAll();
    }

    /// <summary>Throttled error logging used by the tick loop and the damage hook.</summary>
    internal void LogThrottled(Exception exception, string context)
    {
        var now = Server.CurrentTime;
        if (now < _nextErrorLogAt)
        {
            return;
        }

        _nextErrorLogAt = now + 5f;
        Logger?.LogError(exception, "[VyronSkill] Unexpected error in {Context}.", context);
    }

    // ---------------------------------------------------------------- commands

    [ConsoleCommand("css_vyron", "Activate Vyron's Power Boost dash (chat: !dash, !vyron).")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnDashCommand(CCSPlayerController? player, CommandInfo command) => HandleDash(player, command);

    [ConsoleCommand("css_vyron_dir", "Toggle the 干员偏好: 8 directions / 仅4个正方向 (chat: !dashmode).")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnDirectionCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is null || !player.IsValid)
        {
            return;
        }

        PowerBoost.ToggleDirectionPreference(player);
    }

    [ConsoleCommand("css_vyron_help", "Show how to use Vyron's Power Boost (chat: !vyronhelp).")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnHelpCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is null || !player.IsValid)
        {
            return;
        }

        PowerBoost.SendHelp(player);
    }

    [ConsoleCommand("css_vyron_status", "Print the VyronSkill status and the current cooldowns.")]
    [RequiresPermissions("@css/root")]
    public void OnStatusCommand(CCSPlayerController? player, CommandInfo command)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"[VyronSkill] v{ModuleVersion} enabled={Config.Enabled} mode={Config.Mode} direction={Config.DirectionMode}");
        builder.AppendLine($"  dash: {Config.Dash.DistanceMeters} m in {Config.Dash.DurationSeconds} s (speed {DashMath.SpeedForDash(Config.Dash.DistanceMeters, Config.Dash.DurationSeconds):0} u/s), air={Config.Dash.AllowInAir}, freezeBlock={Config.Dash.BlockDuringFreezeTime}");
        builder.AppendLine($"  cooldown: base={Config.Cooldown.BaseSeconds}s botTakedown={Config.Cooldown.BotKnockdownSeconds}s playerTakedown={Config.Cooldown.PlayerKnockdownSeconds}s playerKill={Config.Cooldown.PlayerKillSeconds}s refreshOnKill={Config.Cooldown.AllOutWarfareKillRefreshes}");
        builder.AppendLine($"  knockdown module: {(Config.Knockdown.Enabled ? "on" : "off")}, downed players: {Knockdown.DownedCount}");
        builder.AppendLine("  players:");

        foreach (var target in Utilities.GetPlayers())
        {
            if (target is null || !target.IsValid)
            {
                continue;
            }

            builder.AppendLine($"    - {target.PlayerName}: cooldown {PowerBoost.RemainingCooldown(target.Slot):0.0}s{(Knockdown.IsDowned(target.Slot) ? ", downed" : string.Empty)}");
        }

        command.ReplyToCommand(builder.ToString());
    }

    [ConsoleCommand("css_vyron_mode", "Switch mode: hazard (烽火地带) or warfare (全面战场).")]
    [CommandHelper(minArgs: 1, usage: "<hazard|warfare>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnModeCommand(CCSPlayerController? player, CommandInfo command)
    {
        var argument = command.GetArg(1);

        if (argument.Equals("hazard", StringComparison.OrdinalIgnoreCase) || argument.Equals("HazardOps", StringComparison.OrdinalIgnoreCase))
        {
            Config.GameMode = "HazardOps";
        }
        else if (argument.Equals("warfare", StringComparison.OrdinalIgnoreCase) || argument.Equals("AllOutWarfare", StringComparison.OrdinalIgnoreCase))
        {
            Config.GameMode = "AllOutWarfare";
        }
        else
        {
            command.ReplyToCommand("[VyronSkill] usage: css_vyron_mode <hazard|warfare>");
            return;
        }

        Config.Update();
        command.ReplyToCommand($"[VyronSkill] game_mode is now {Config.GameMode}.");
        Server.PrintToChatAll(Localizer["vyron.mode.changed", Config.Mode == GameModeKind.AllOutWarfare ? Localizer["vyron.mode.warfare"] : Localizer["vyron.mode.hazard"]]);
    }

    [ConsoleCommand("css_vyron_reset", "Make Power Boost ready again for every player.")]
    [RequiresPermissions("@css/root")]
    public void OnResetCommand(CCSPlayerController? player, CommandInfo command)
    {
        PowerBoost.ResetAllCooldowns();
        command.ReplyToCommand("[VyronSkill] all Power Boost cooldowns reset.");
    }

    [ConsoleCommand("css_vyron_reload", "Reload the VyronSkill config from disk.")]
    [RequiresPermissions("@css/root")]
    public void OnReloadCommand(CCSPlayerController? player, CommandInfo command)
    {
        Config.Reload();
        command.ReplyToCommand($"[VyronSkill] config reloaded: mode={Config.Mode}, cooldown={Config.Cooldown.BaseSeconds}s, knockdown={Config.Knockdown.Enabled}.");
    }

    private void HandleDash(CCSPlayerController? player, CommandInfo command)
    {
        if (player is null || !player.IsValid)
        {
            command.ReplyToCommand("[VyronSkill] this command can only be used by a player.");
            return;
        }

        PowerBoost.TryActivate(player);
    }

    // ---------------------------------------------------------------- game events

    private void OnTick()
    {
        if (!Config.Enabled)
        {
            return;
        }

        try
        {
            PowerBoost.Tick();
            Knockdown.Tick();
        }
        catch (Exception exception)
        {
            LogThrottled(exception, "tick");
        }
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        RoundLive = false;
        Inputs.ClearAll();
        Knockdown.OnRoundStart();

        if (Config.Cooldown.ResetOnRoundStart)
        {
            PowerBoost.ResetAllCooldowns();
        }

        return HookResult.Continue;
    }

    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        RoundLive = true;
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        RoundLive = false;

        // Nobody should stay frozen during the round transition.
        Knockdown.OnRoundEnd();

        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (player is null || !player.IsValid)
        {
            return HookResult.Continue;
        }

        Inputs.Clear(player.Slot);
        Knockdown.OnPlayerSpawn(player);

        if (Config.Cooldown.ResetOnSpawn)
        {
            PowerBoost.ResetCooldown(player.Slot);
        }

        return HookResult.Continue;
    }

    /// <summary>
    /// 冷却缩减 core: every kill and assist is converted into a cooldown reward through the pure
    /// <see cref="CooldownRules"/> (烽火地带 -7 s / -10 s, 全面战场 full refresh).
    /// </summary>
    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var victim = @event.Userid;

        if (victim is null || !victim.IsValid)
        {
            return HookResult.Continue;
        }

        Knockdown.OnVictimDied(victim);

        if (!Config.Enabled)
        {
            return HookResult.Continue;
        }

        try
        {
            var settings = Config.ToRewardSettings();
            var attacker = @event.Attacker;
            var assister = @event.Assister;

            if (IsEnemy(attacker, victim))
            {
                PowerBoost.ApplyReward(attacker!, CooldownRules.ForKill(settings, victim.IsBot));
            }

            if (IsEnemy(assister, victim) && (attacker is null || assister!.Slot != attacker.Slot))
            {
                PowerBoost.ApplyReward(assister!, CooldownRules.ForAssist(settings, victim.IsBot));
            }
        }
        catch (Exception exception)
        {
            LogThrottled(exception, "player_death");
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (player is null)
        {
            return HookResult.Continue;
        }

        var slot = player.Slot;
        Inputs.Clear(slot);
        Knockdown.Clear(slot);
        PowerBoost.Clear(slot);

        return HookResult.Continue;
    }

    private static bool IsEnemy(CCSPlayerController? candidate, CCSPlayerController victim)
        => candidate is { IsValid: true }
           && candidate.Slot != victim.Slot
           && IsCombatTeam(candidate.Team)
           && IsCombatTeam(victim.Team)
           && candidate.Team != victim.Team;

    private static bool IsCombatTeam(CsTeam team) => team is CsTeam.Terrorist or CsTeam.CounterTerrorist;
}
