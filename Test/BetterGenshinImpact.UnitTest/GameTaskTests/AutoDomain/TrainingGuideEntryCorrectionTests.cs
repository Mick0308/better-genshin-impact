using BetterGenshinImpact.GameTask.AutoDomain.TrainingGuide;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoDomain;

public class TrainingGuideEntryCorrectionTests
{
    [Theory]
    [InlineData("菫色之庭", "精通秘境: 董染之国 IV", "精通秘境：菫染之国")]
    [InlineData("苍白的遗荣", "精通秘境：铭Ⅲ", "精通秘境：箴铭")]
    [InlineData("蕴火的幽墟", "精通秘境：转竞II", "精通秘境：转竞")]
    [InlineData("蕴火的幽墟", "精通秘境：转竟IV", "精通秘境：转竞")]
    [InlineData("菫色之庭", "精通秘境：菫染之国IV", "精通秘境：菫染之国")]
    public void KnownAliasesResolveWithinTheirDomain(string domain, string text, string expected)
        => Assert.Equal(expected, TrainingGuideEntryCatalog.Find(domain, text)?.Entry);

    [Theory]
    [InlineData("菫色之庭", "精通秘境：染之国IV")]
    [InlineData("菫色之庭", "精通秘境：铭IV")]
    [InlineData("苍白的遗荣", "精通秘境：其他铭IV")]
    public void UnknownSubstringsAndWrongDomainsAreRejected(string domain, string text)
        => Assert.Null(TrainingGuideEntryCatalog.Find(domain, text));
}
