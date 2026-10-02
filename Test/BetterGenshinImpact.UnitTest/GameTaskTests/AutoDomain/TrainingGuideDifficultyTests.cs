using BetterGenshinImpact.GameTask.AutoDomain.TrainingGuide;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoDomain;

public class TrainingGuideDifficultyTests
{
    [Theory]
    [InlineData("精通秘境：菫染之国 IV", 4)]
    [InlineData("精通秘境：菫染之国 Ⅲ", 3)]
    [InlineData("炼武秘境：水光之城 II", 2)]
    [InlineData("炼武秘境：水光之城 I", 1)]
    [InlineData("推荐队伍等级 88", 0)]
    public void DifficultyComesFromEntryTitle(string text, int expected) =>
        Assert.Equal(expected, TrainingGuideDropExpectations.ReadDifficulty(text));

    [Fact]
    public void LowDifficultyBudgetUsesItsOwnYieldWithoutHigherMaterialReadings()
    {
        var material = TrainingGuideMaterialCatalog.Materials.First(m => !m.IsWeapon && m.Tier == 0);
        var readings = new[] { new TrainingGuideMaterialReading(material, 0, 32, true) };
        Assert.Equal(200, new TrainingGuideFamilyPlan(readings, 1).RemainingResin(0));
        Assert.Equal(300, new TrainingGuideFamilyPlan(readings, 4).RemainingResin(0));
    }

    [Fact]
    public void RewardRecalculationKeepsLowDifficultyPrior()
    {
        var material = TrainingGuideMaterialCatalog.Materials.First(m => !m.IsWeapon && m.Tier == 0);
        var plan = new TrainingGuideFamilyPlan(new[] { new TrainingGuideMaterialReading(material, 0, 32, true) }, 1);
        Assert.True(plan.ApplyRewards(new Dictionary<string, int> { [material.Name] = 3 }, 20));
        // 更新库存为3，期望为(3.2+3)/2=3.1，剩余29需要10次。
        Assert.Equal(200, plan.RemainingResin(0));
    }

    [Fact]
    public void MissingIntermediateInventoryIsNotAssumedZero()
    {
        var low = TrainingGuideMaterialCatalog.Materials.First(m => m.IsWeapon && m.Tier == 0);
        var high = TrainingGuideMaterialCatalog.Materials.Single(m => m.Family == low.Family && m.Tier == 2);
        var readings = new[]
        {
            new TrainingGuideMaterialReading(low, 0, 0, false),
            new TrainingGuideMaterialReading(high, 0, 1, true)
        };
        Assert.Null(new TrainingGuideFamilyPlan(readings, 3).RemainingResin(0));
    }

    [Fact]
    public void NonDroppingTiersRemainZero()
    {
        Assert.Equal(new decimal[] { 2.70m, 2m, 0m, 0m }, TrainingGuideDropExpectations.Per20(true, 2));
        Assert.Equal(new decimal[] { 1.80m, 2m, 0m }, TrainingGuideDropExpectations.Per20(false, 3));
    }
}
