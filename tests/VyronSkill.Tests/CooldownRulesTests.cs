using VyronSkill.Core;

namespace VyronSkill.Tests;

/// <summary>
/// Verifies the 冷却缩减 loop for both Delta Force modes:
/// 烽火地带 (Hazard Operations) -7 s for bots and -10 s for players, 全面战场 (All-out Warfare) a full
/// refresh after every kill.
/// </summary>
public class CooldownRulesTests
{
    private static RewardSettings HazardOps(bool countAssists = true) => new(
        allOutWarfare: false,
        refreshOnKillInAllOutWarfare: true,
        onBotKnockdownSeconds: 7f,
        onPlayerKnockdownSeconds: 10f,
        onPlayerKillSeconds: 10f,
        countAssistAsKnockdown: countAssists);

    private static RewardSettings AllOutWarfare(bool refreshOnKill = true, bool countAssists = true) => new(
        allOutWarfare: true,
        refreshOnKillInAllOutWarfare: refreshOnKill,
        onBotKnockdownSeconds: 7f,
        onPlayerKnockdownSeconds: 10f,
        onPlayerKillSeconds: 10f,
        countAssistAsKnockdown: countAssists);

    [Fact]
    public void HazardOps_BotTakedown_ReducesSevenSeconds()
    {
        var reward = CooldownRules.ForKill(HazardOps(), victimIsBot: true);

        Assert.Equal(RewardKind.Reduce, reward.Kind);
        Assert.Equal(7f, reward.Seconds);
        Assert.True(reward.VictimIsBot);
        Assert.Equal(RewardSource.Kill, reward.Source);
    }

    [Fact]
    public void HazardOps_PlayerKill_ReducesTenSeconds()
    {
        var reward = CooldownRules.ForKill(HazardOps(), victimIsBot: false);

        Assert.Equal(RewardKind.Reduce, reward.Kind);
        Assert.Equal(10f, reward.Seconds);
        Assert.False(reward.VictimIsBot);
    }

    [Fact]
    public void HazardOps_PlayerKnockdown_ReducesTenSeconds()
    {
        var reward = CooldownRules.ForKnockdown(HazardOps(), victimIsBot: false);

        Assert.Equal(RewardKind.Reduce, reward.Kind);
        Assert.Equal(10f, reward.Seconds);
        Assert.Equal(RewardSource.Knockdown, reward.Source);
    }

    [Fact]
    public void HazardOps_BotKnockdown_ReducesSevenSeconds()
    {
        var reward = CooldownRules.ForKnockdown(HazardOps(), victimIsBot: true);

        Assert.Equal(RewardKind.Reduce, reward.Kind);
        Assert.Equal(7f, reward.Seconds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AllOutWarfare_AnyKill_FullyRefreshesTheCooldown(bool victimIsBot)
    {
        var reward = CooldownRules.ForKill(AllOutWarfare(), victimIsBot);

        Assert.Equal(RewardKind.FullRefresh, reward.Kind);
        Assert.Equal(0f, reward.Seconds);
        Assert.False(reward.IsNothing);
    }

    [Fact]
    public void AllOutWarfare_WithRefreshDisabled_FallsBackToFixedReductions()
    {
        var settings = AllOutWarfare(refreshOnKill: false);

        Assert.Equal(7f, CooldownRules.ForKill(settings, victimIsBot: true).Seconds);
        Assert.Equal(10f, CooldownRules.ForKill(settings, victimIsBot: false).Seconds);
        Assert.Equal(RewardKind.Reduce, CooldownRules.ForKill(settings, victimIsBot: false).Kind);
    }

    [Fact]
    public void Assist_OnPlayerKill_CountsAsASharedTakedown()
    {
        var reward = CooldownRules.ForAssist(HazardOps(), victimIsBot: false);

        Assert.Equal(RewardKind.Reduce, reward.Kind);
        Assert.Equal(10f, reward.Seconds);
        Assert.Equal(RewardSource.Assist, reward.Source);
        Assert.False(reward.VictimIsBot);
    }

    [Fact]
    public void Assist_IsIgnoredWhenDisabledInTheConfig()
    {
        var reward = CooldownRules.ForAssist(HazardOps(countAssists: false), victimIsBot: false);

        Assert.True(reward.IsNothing);
        Assert.Equal(RewardKind.None, reward.Kind);
    }

    [Fact]
    public void Assist_StillGivesAPlayerRewardInAllOutWarfare()
    {
        var reward = CooldownRules.ForAssist(AllOutWarfare(), victimIsBot: false);

        Assert.Equal(RewardKind.Reduce, reward.Kind);
        Assert.Equal(10f, reward.Seconds);
    }

    [Fact]
    public void ZeroedConfigValues_ProduceNoReward()
    {
        var settings = new RewardSettings(false, true, 0f, 0f, 0f, true);

        Assert.True(CooldownRules.ForKill(settings, victimIsBot: false).IsNothing);
        Assert.True(CooldownRules.ForKill(settings, victimIsBot: true).IsNothing);
        Assert.True(CooldownRules.ForKnockdown(settings, victimIsBot: false).IsNothing);
        Assert.True(CooldownRules.ForAssist(settings, victimIsBot: false).IsNothing);
    }

    /// <summary>
    /// 全面战场 "击败敌人会直接刷新动力推进的冷却" - the loop that makes chained dashing possible:
    /// killing an enemy while the ability is still cooling down makes it ready instantly.
    /// </summary>
    [Fact]
    public void AllOutWarfare_KillChainsIntoAnInstantSecondDash()
    {
        var settings = AllOutWarfare();
        const float baseCooldown = 15f;
        var readyAt = 100f + baseCooldown;

        var reward = CooldownRules.ForKill(settings, victimIsBot: false);
        var afterReward = reward.Kind == RewardKind.FullRefresh ? 110f : readyAt - reward.Seconds;

        Assert.Equal(110f, afterReward);
        Assert.True(afterReward <= 110f, "the ability must be usable again immediately after the kill");
    }

    /// <summary>
    /// 烽火地带: chaining two player takedowns inside one cooldown window must not make the cooldown
    /// negative, it simply becomes ready.
    /// </summary>
    [Fact]
    public void HazardOps_RepeatedTakedownsNeverGoBelowZeroCooldown()
    {
        var settings = HazardOps();
        var now = 50f;
        var readyAt = now + 15f;

        foreach (var _ in new[] { 0, 1 })
        {
            var reward = CooldownRules.ForKill(settings, victimIsBot: false);
            readyAt = MathF.Max(now, readyAt - reward.Seconds);
        }

        Assert.Equal(now, readyAt);
    }
}
