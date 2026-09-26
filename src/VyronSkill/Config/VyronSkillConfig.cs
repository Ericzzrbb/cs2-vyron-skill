using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using VyronSkill.Core;

namespace VyronSkill.Config;

/// <summary>
/// Which Delta Force mode the cooldown-reduction rules should follow.
/// </summary>
public enum GameModeKind
{
    /// <summary>烽火地带 (Hazard Operations / extraction): knockdowns reduce the cooldown by a fixed amount.</summary>
    HazardOps = 0,

    /// <summary>全面战场 (All-out Warfare): defeating an enemy refreshes the cooldown completely.</summary>
    AllOutWarfare = 1,
}

/// <summary>
/// Plugin configuration. The file is written to
/// <c>addons/counterstrikesharp/configs/plugins/VyronSkill/VyronSkill.json</c> on first load.
/// </summary>
public sealed class VyronSkillConfig : BasePluginConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>"HazardOps" (烽火地带, default) or "AllOutWarfare" (全面战场).</summary>
    [JsonPropertyName("game_mode")]
    public string GameMode { get; set; } = "HazardOps";

    [JsonPropertyName("dash")]
    public DashConfig Dash { get; set; } = new();

    [JsonPropertyName("cooldown")]
    public CooldownConfig Cooldown { get; set; } = new();

    [JsonPropertyName("feedback")]
    public FeedbackConfig Feedback { get; set; } = new();

    [JsonPropertyName("knockdown")]
    public KnockdownConfig Knockdown { get; set; } = new();

    [JsonIgnore]
    public GameModeKind Mode => ParseMode(GameMode);

    [JsonIgnore]
    public DashDirectionMode DirectionMode => ParseDirectionMode(Dash.DirectionMode);

    public static GameModeKind ParseMode(string? value)
        => string.Equals(value, "AllOutWarfare", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "warfare", StringComparison.OrdinalIgnoreCase)
            ? GameModeKind.AllOutWarfare
            : GameModeKind.HazardOps;

    public static DashDirectionMode ParseDirectionMode(string? value)
        => string.Equals(value, "FourWay", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "4", StringComparison.OrdinalIgnoreCase)
            ? DashDirectionMode.FourWay
            : DashDirectionMode.EightWay;

    public RewardSettings ToRewardSettings() => new(
        allOutWarfare: Mode == GameModeKind.AllOutWarfare,
        refreshOnKillInAllOutWarfare: Cooldown.AllOutWarfareKillRefreshes,
        onBotKnockdownSeconds: Cooldown.BotKnockdownSeconds,
        onPlayerKnockdownSeconds: Cooldown.PlayerKnockdownSeconds,
        onPlayerKillSeconds: Cooldown.PlayerKillSeconds,
        countAssistAsKnockdown: Cooldown.CountAssistAsKnockdown);

    /// <summary>
    /// Clamps and repairs invalid values so a typo in the JSON file can never break the server.
    /// </summary>
    public void Validate(ILogger? logger)
    {
        Dash ??= new DashConfig();
        Cooldown ??= new CooldownConfig();
        Feedback ??= new FeedbackConfig();
        Knockdown ??= new KnockdownConfig();

        if (!string.Equals(GameMode, "HazardOps", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(GameMode, "AllOutWarfare", StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogWarning("[VyronSkill] Unknown game_mode '{Mode}', falling back to HazardOps.", GameMode);
            GameMode = "HazardOps";
        }

        if (DirectionMode == Core.DashDirectionMode.FourWay)
        {
            Dash.DirectionMode = "FourWay";
        }
        else if (!string.Equals(Dash.DirectionMode, "EightWay", StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogWarning("[VyronSkill] Unknown direction_mode '{Mode}', falling back to EightWay.", Dash.DirectionMode);
            Dash.DirectionMode = "EightWay";
        }

        Dash.DistanceMeters = Clamp(Dash.DistanceMeters, 0.5f, 200f, 10f, "dash.distance_meters", logger);
        Dash.DurationSeconds = Clamp(Dash.DurationSeconds, 0.05f, 5f, 0.35f, "dash.duration_seconds", logger);
        Dash.AirVerticalBoost = Clamp(Dash.AirVerticalBoost, -2000f, 2000f, 0f, "dash.air_vertical_boost", logger);

        Cooldown.BaseSeconds = Clamp(Cooldown.BaseSeconds, 0f, 600f, 15f, "cooldown.base_seconds", logger);
        Cooldown.BotKnockdownSeconds = Clamp(Cooldown.BotKnockdownSeconds, 0f, 600f, 7f, "cooldown.bot_knockdown_seconds", logger);
        Cooldown.PlayerKnockdownSeconds = Clamp(Cooldown.PlayerKnockdownSeconds, 0f, 600f, 10f, "cooldown.player_knockdown_seconds", logger);
        Cooldown.PlayerKillSeconds = Clamp(Cooldown.PlayerKillSeconds, 0f, 600f, 10f, "cooldown.player_kill_seconds", logger);

        Feedback.HudIntervalSeconds = Clamp(Feedback.HudIntervalSeconds, 0.05f, 5f, 0.25f, "feedback.hud_interval_seconds", logger);

        Knockdown.HealthAfterKnockdown = (int)Clamp(Knockdown.HealthAfterKnockdown, 1f, 100f, 30f, "knockdown.health_after_knockdown", logger);
        Knockdown.ReviveHealth = (int)Clamp(Knockdown.ReviveHealth, 1f, 200f, 50f, "knockdown.revive_health", logger);
        Knockdown.BleedOutSeconds = Clamp(Knockdown.BleedOutSeconds, 1f, 600f, 25f, "knockdown.bleed_out_seconds", logger);
        Knockdown.MaxPerRound = (int)Clamp(Knockdown.MaxPerRound, 0f, 64f, 1f, "knockdown.max_per_round", logger);
        Knockdown.ReviveHoldSeconds = Clamp(Knockdown.ReviveHoldSeconds, 0.1f, 30f, 3f, "knockdown.revive_hold_seconds", logger);
        Knockdown.ReviveRadiusUnits = Clamp(Knockdown.ReviveRadiusUnits, 16f, 1000f, 90f, "knockdown.revive_radius_units", logger);
    }

    private static float Clamp(float value, float min, float max, float fallback, string name, ILogger? logger)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            logger?.LogWarning("[VyronSkill] Config value '{Name}' is not a number, using {Fallback}.", name, fallback);
            return fallback;
        }

        if (value < min || value > max)
        {
            var clamped = Math.Clamp(value, min, max);
            logger?.LogWarning("[VyronSkill] Config value '{Name}' = {Value} is out of range [{Min}, {Max}], using {Clamped}.", name, value, min, max, clamped);
            return clamped;
        }

        return value;
    }
}

/// <summary>动力推进 (Power Boost) movement settings.</summary>
public sealed class DashConfig
{
    /// <summary>Single dash distance in metres. Delta Force uses 10 m (≈394 Source units).</summary>
    [JsonPropertyName("distance_meters")]
    public float DistanceMeters { get; set; } = 10f;

    /// <summary>How long the jet burn lasts. Distance / duration gives the dash speed.</summary>
    [JsonPropertyName("duration_seconds")]
    public float DurationSeconds { get; set; } = 0.35f;

    /// <summary>"EightWay" (default, 8 directions) or "FourWay" (仅4个正方向).</summary>
    [JsonPropertyName("direction_mode")]
    public string DirectionMode { get; set; } = "EightWay";

    /// <summary>动力推进 works in mid-air, which is what makes Vyron's entry angles so strong.</summary>
    [JsonPropertyName("allow_in_air")]
    public bool AllowInAir { get; set; } = true;

    /// <summary>
    /// Vertical velocity (units/s) forced during the burn while airborne. 0 keeps normal gravity,
    /// positive values hover or lift the player.
    /// </summary>
    [JsonPropertyName("air_vertical_boost")]
    public float AirVerticalBoost { get; set; } = 0f;

    /// <summary>Blocks the dash during freeze time so nobody can skip the round start.</summary>
    [JsonPropertyName("block_during_freeze_time")]
    public bool BlockDuringFreezeTime { get; set; } = true;
}

/// <summary>冷却缩减 (cooldown reduction) settings for both Delta Force modes.</summary>
public sealed class CooldownConfig
{
    /// <summary>Base cooldown of 动力推进: 15 s.</summary>
    [JsonPropertyName("base_seconds")]
    public float BaseSeconds { get; set; } = 15f;

    /// <summary>烽火地带: 击倒人机 reduces the cooldown by 7 s.</summary>
    [JsonPropertyName("bot_knockdown_seconds")]
    public float BotKnockdownSeconds { get; set; } = 7f;

    /// <summary>烽火地带: 击倒玩家 reduces the cooldown by 10 s.</summary>
    [JsonPropertyName("player_knockdown_seconds")]
    public float PlayerKnockdownSeconds { get; set; } = 10f;

    /// <summary>烽火地带: 击杀玩家 reduces the cooldown by 10 s.</summary>
    [JsonPropertyName("player_kill_seconds")]
    public float PlayerKillSeconds { get; set; } = 10f;

    /// <summary>全面战场: 击败敌人 completely refreshes the cooldown.</summary>
    [JsonPropertyName("all_out_warfare_kill_refreshes")]
    public bool AllOutWarfareKillRefreshes { get; set; } = true;

    /// <summary>Treats an assist on an enemy kill as a shared takedown (击倒).</summary>
    [JsonPropertyName("count_assist_as_knockdown")]
    public bool CountAssistAsKnockdown { get; set; } = true;

    [JsonPropertyName("reset_on_spawn")]
    public bool ResetOnSpawn { get; set; } = true;

    [JsonPropertyName("reset_on_round_start")]
    public bool ResetOnRoundStart { get; set; } = true;
}

/// <summary>Player feedback (chat, HUD, sound).</summary>
public sealed class FeedbackConfig
{
    [JsonPropertyName("chat_on_use")]
    public bool ChatOnUse { get; set; } = true;

    [JsonPropertyName("chat_on_cooldown")]
    public bool ChatOnCooldown { get; set; } = true;

    [JsonPropertyName("chat_on_reward")]
    public bool ChatOnReward { get; set; } = true;

    [JsonPropertyName("hud_enabled")]
    public bool HudEnabled { get; set; } = true;

    [JsonPropertyName("hud_interval_seconds")]
    public float HudIntervalSeconds { get; set; } = 0.25f;

    /// <summary>Client side sound file played on activation. Empty string disables it.</summary>
    [JsonPropertyName("sound_on_use")]
    public string SoundOnUse { get; set; } = "buttons/blip1.wav";

    /// <summary>Client side sound played when the cooldown finishes. Empty string disables it.</summary>
    [JsonPropertyName("sound_on_ready")]
    public string SoundOnReady { get; set; } = string.Empty;
}

/// <summary>
/// Optional 击倒 (down-but-not-out) module. CS2 has no native downed state, so when enabled a lethal
/// hit on an enemy player leaves them alive at low health: frozen, disarmed and revivable by a
/// teammate holding USE. Disabled by default because it changes normal CS2 round flow.
/// </summary>
public sealed class KnockdownConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>Only allow knockdowns while game_mode is HazardOps (烽火地带).</summary>
    [JsonPropertyName("only_in_hazard_ops")]
    public bool OnlyInHazardOps { get; set; } = true;

    [JsonPropertyName("apply_to_bots")]
    public bool ApplyToBots { get; set; }

    /// <summary>Health the victim is left with when a lethal hit is converted into a knockdown.</summary>
    [JsonPropertyName("health_after_knockdown")]
    public int HealthAfterKnockdown { get; set; } = 30;

    /// <summary>Seconds before a downed player bleeds out and dies.</summary>
    [JsonPropertyName("bleed_out_seconds")]
    public float BleedOutSeconds { get; set; } = 25f;

    /// <summary>Maximum knockdowns a single player can suffer per round (0 = unlimited).</summary>
    [JsonPropertyName("max_per_round")]
    public int MaxPerRound { get; set; } = 1;

    /// <summary>Headshots bypass the downed state and kill instantly, like a finishing shot.</summary>
    [JsonPropertyName("headshot_bypasses")]
    public bool HeadshotBypasses { get; set; } = true;

    /// <summary>Strips the downed player's weapons so they cannot keep fighting while downed.</summary>
    [JsonPropertyName("remove_weapons")]
    public bool RemoveWeapons { get; set; } = true;

    [JsonPropertyName("give_knife_on_revive")]
    public bool GiveKnifeOnRevive { get; set; } = true;

    [JsonPropertyName("revive_health")]
    public int ReviveHealth { get; set; } = 50;

    [JsonPropertyName("revive_hold_seconds")]
    public float ReviveHoldSeconds { get; set; } = 3f;

    [JsonPropertyName("revive_radius_units")]
    public float ReviveRadiusUnits { get; set; } = 90f;
}
