namespace VyronSkill.Core;

/// <summary>What an event does to the Power Boost cooldown.</summary>
public enum RewardKind
{
    /// <summary>Nothing happens.</summary>
    None = 0,

    /// <summary>Subtract <see cref="CooldownReward.Seconds"/> from the remaining cooldown.</summary>
    Reduce = 1,

    /// <summary>全面战场: the cooldown is refreshed completely, so chained kills allow chained dashes.</summary>
    FullRefresh = 2,
}

/// <summary>Which takedown produced a reward (used for player feedback).</summary>
public enum RewardSource
{
    Kill = 1,
    Knockdown = 2,
    Assist = 3,
}

/// <summary>The cooldown change granted by one takedown event.</summary>
public readonly struct CooldownReward
{
    public CooldownReward(RewardKind kind, float seconds, bool victimIsBot, RewardSource source)
    {
        Kind = kind;
        Seconds = seconds;
        VictimIsBot = victimIsBot;
        Source = source;
    }

    public RewardKind Kind { get; }

    public float Seconds { get; }

    public bool VictimIsBot { get; }

    public RewardSource Source { get; }

    public bool IsNothing => Kind == RewardKind.None;

    public static CooldownReward Nothing { get; } = new(RewardKind.None, 0f, false, RewardSource.Kill);
}

/// <summary>Flattened cooldown configuration used by the pure rules below.</summary>
public readonly struct RewardSettings
{
    public RewardSettings(
        bool allOutWarfare,
        bool refreshOnKillInAllOutWarfare,
        float onBotKnockdownSeconds,
        float onPlayerKnockdownSeconds,
        float onPlayerKillSeconds,
        bool countAssistAsKnockdown)
    {
        AllOutWarfare = allOutWarfare;
        RefreshOnKillInAllOutWarfare = refreshOnKillInAllOutWarfare;
        OnBotKnockdownSeconds = onBotKnockdownSeconds;
        OnPlayerKnockdownSeconds = onPlayerKnockdownSeconds;
        OnPlayerKillSeconds = onPlayerKillSeconds;
        CountAssistAsKnockdown = countAssistAsKnockdown;
    }

    /// <summary>True for 全面战场 (All-out Warfare), false for 烽火地带 (Hazard Operations).</summary>
    public bool AllOutWarfare { get; }

    public bool RefreshOnKillInAllOutWarfare { get; }

    /// <summary>烽火地带: 击倒人机 reduces the cooldown by 7 s.</summary>
    public float OnBotKnockdownSeconds { get; }

    /// <summary>烽火地带: 击倒玩家 reduces the cooldown by 10 s.</summary>
    public float OnPlayerKnockdownSeconds { get; }

    /// <summary>烽火地带: 击杀玩家 reduces the cooldown by 10 s.</summary>
    public float OnPlayerKillSeconds { get; }

    public bool CountAssistAsKnockdown { get; }
}

/// <summary>
/// The core 冷却缩减 (cooldown reduction) loop of 动力推进, expressed as pure functions so that the
/// balance rules can be unit tested without a running game server:
/// <list type="bullet">
///   <item><description>烽火地带 (Hazard Operations): 击倒人机 -7 s, 击倒/击杀玩家 -10 s.</description></item>
///   <item><description>全面战场 (All-out Warfare): 击败敌人 completely refreshes the cooldown.</description></item>
/// </list>
/// </summary>
public static class CooldownRules
{
    /// <summary>击杀 / 击败: the victim actually died.</summary>
    public static CooldownReward ForKill(in RewardSettings settings, bool victimIsBot)
    {
        if (settings.AllOutWarfare && settings.RefreshOnKillInAllOutWarfare)
        {
            return new CooldownReward(RewardKind.FullRefresh, 0f, victimIsBot, RewardSource.Kill);
        }

        // In 烽火地带 a bot dies outright and never enters the downed state, which is exactly what
        // "击倒人机" means for us, hence the bot knockdown value is used for bot kills.
        var seconds = victimIsBot ? settings.OnBotKnockdownSeconds : settings.OnPlayerKillSeconds;
        return seconds <= 0f
            ? CooldownReward.Nothing
            : new CooldownReward(RewardKind.Reduce, seconds, victimIsBot, RewardSource.Kill);
    }

    /// <summary>击倒: the victim was put into the downed state instead of dying.</summary>
    public static CooldownReward ForKnockdown(in RewardSettings settings, bool victimIsBot)
    {
        var seconds = victimIsBot ? settings.OnBotKnockdownSeconds : settings.OnPlayerKnockdownSeconds;
        return seconds <= 0f
            ? CooldownReward.Nothing
            : new CooldownReward(RewardKind.Reduce, seconds, victimIsBot, RewardSource.Knockdown);
    }

    /// <summary>
    /// CS2 has no native "down but not out" state, so an assist on an enemy kill is treated as a
    /// shared takedown (击倒) unless the server disables it.
    /// </summary>
    public static CooldownReward ForAssist(in RewardSettings settings, bool victimIsBot)
    {
        if (!settings.CountAssistAsKnockdown)
        {
            return CooldownReward.Nothing;
        }

        var seconds = victimIsBot ? settings.OnBotKnockdownSeconds : settings.OnPlayerKnockdownSeconds;
        return seconds <= 0f
            ? CooldownReward.Nothing
            : new CooldownReward(RewardKind.Reduce, seconds, victimIsBot, RewardSource.Assist);
    }
}
