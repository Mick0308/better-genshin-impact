using BetterGenshinImpact.GameTask.AutoDomain.TrainingGuide;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoDomain;

public class TrainingGuideEntryLayoutTests
{
    private static readonly TrainingGuideEntry[] Group = TrainingGuideEntryCatalog.Entries.Take(3).ToArray();

    private static TrainingGuideEntryLayout.Row[] Rows(int size, double shift = 0) =>
        Enumerable.Range(0, size == 1 ? 4 : 8).Select(i =>
            new TrainingGuideEntryLayout.Row(Group[size - 1 - i % size], 960 - i * 110 + shift)).ToArray();

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void BottomGroupAndPreviousGroupDetermineDifficulty(int size)
    {
        var layout = new TrainingGuideEntryLayout(Rows(size), size, 1);
        Assert.Equal(size - 1, layout.Index(Group[0], 4));
        Assert.Equal(2 * size - 1, layout.Index(Group[0], 3));
        Assert.True(layout.TryUpdate(Rows(size, 35)));
        Assert.Equal(35d, layout.LastShift);
        Assert.True(layout.IsTarget(new(Group[0], 995 - (2 * size - 1) * 110), layout.Index(Group[0], 3)));
    }

    [Fact]
    public void MissingMiddleRowDoesNotRenumberLevels()
    {
        var layout = new TrainingGuideEntryLayout(Rows(3), 3, 1);
        Assert.True(layout.TryUpdate(Rows(3, 35).Where((_, i) => i != 4).ToArray()));
        Assert.Equal(4, layout.Index(Group[1], 3));
        Assert.Equal(555d, layout.Y(4));
    }

    [Fact]
    public void InconsistentMovementDoesNotChangeReference()
    {
        var layout = new TrainingGuideEntryLayout(Rows(3), 3, 1);
        var rows = Rows(3, 35);
        rows[2] = rows[2] with { Y = rows[2].Y - 30 };
        Assert.False(layout.TryUpdate(rows));
        Assert.Equal(960d, layout.BottomY);
    }

    [Fact]
    public void WrongFamilyOrderIsRejected()
    {
        var layout = new TrainingGuideEntryLayout(Rows(3), 3, 1);
        var rows = Rows(3);
        rows[2] = rows[2] with { Entry = Group[1] };
        Assert.False(layout.TryUpdate(rows));
    }
}
