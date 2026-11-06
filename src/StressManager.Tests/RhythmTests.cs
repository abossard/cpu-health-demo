using StressManager.Calculations;

namespace StressManager.Tests;

public class RhythmTests
{
    [Theory]
    // already valid (divides 1440)
    [InlineData("10@100 20@0", "10@100 20@0", 30, 0)]
    [InlineData("60@100", "60@100", 60, 0)]
    [InlineData("1440@50", "1440@50", 1440, 0)]
    // padding: extend final break or append one
    [InlineData("7@100", "7@100 1@0", 8, 1)]
    [InlineData("10@100 3@0", "10@100 5@0", 15, 2)]
    [InlineData("25@80 10@0 2@100", "25@80 10@0 2@100 3@0", 40, 3)]
    [InlineData("50@100 11@0", "50@100 22@0", 72, 11)]
    // clamp load and minutes, drop zero-length
    [InlineData("10@150 5@-3", "10@100 5@0", 15, 0)]
    [InlineData("0@100 10@100 -5@0 20@0", "10@100 20@0", 30, 0)]
    // merge adjacent breaks
    [InlineData("10@100 5@0 5@0 10@0", "10@100 20@0", 30, 0)]
    [InlineData("5@0 5@0 10@100 10@0", "10@0 10@100 10@0", 30, 0)]
    // cap at 1440 by truncating the tail
    [InlineData("2000@100", "1440@100", 1440, 0)]
    [InlineData("1000@100 1000@0 30@100", "1000@100 440@0", 1440, 0)]
    [InlineData("1430@100 20@40", "1430@100 10@40", 1440, 0)]
    public void Normalize(string input, string expected, int cycle, int padded)
    {
        var (rhythm, error) = Rhythm.Normalize(TestData.Steps(input));

        Assert.Null(error);
        Assert.Equal(expected, TestData.Notation(rhythm!.Steps));
        Assert.Equal(cycle, rhythm.CycleMinutes);
        Assert.Equal(padded, rhythm.PaddedMinutes);
        Assert.Equal(cycle, rhythm.Steps.Sum(s => s.Minutes));
        Assert.Equal(0, Rhythm.MinutesPerDay % cycle);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10@0")]
    [InlineData("10@0 20@0")]
    [InlineData("0@100 -1@50")]
    [InlineData("1440@0 10@100")]
    public void Normalize_RejectsRhythmWithoutOnStep(string? input)
    {
        var (rhythm, error) = Rhythm.Normalize(input is null ? null : TestData.Steps(input));

        Assert.Null(rhythm);
        Assert.Equal(Rhythm.NoOnStep, error);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 8)]
    [InlineData(11, 12)]
    [InlineData(13, 15)]
    [InlineData(61, 72)]
    [InlineData(721, 1440)]
    [InlineData(1440, 1440)]
    public void SmallestDivisorIsUsed(int length, int cycle) =>
        Assert.Equal(cycle, Rhythm.Normalize([new Step(length, 100)]).Rhythm!.CycleMinutes);

    [Theory]
    // 10 on / 20 off, cycle 30: ON at :00 and :30
    [InlineData("10@100 20@0", "2026-11-05T20:05:00Z", "2026-11-05T20:30:00Z", 0)]
    [InlineData("10@100 20@0", "2026-11-05T20:30:00Z", "2026-11-05T21:00:00Z", 0)]
    [InlineData("10@100 20@0", "2026-11-05T20:29:59Z", "2026-11-05T20:30:00Z", 0)]
    [InlineData("10@100 20@0", "2026-11-05T23:45:00Z", "2026-11-06T00:00:00Z", 0)]
    // break first: ON step 1 at offset 10
    [InlineData("10@0 5@80 15@0", "2026-11-05T20:00:00Z", "2026-11-05T20:10:00Z", 1)]
    // two ON steps: step 2 at offset 15
    [InlineData("5@100 10@0 5@50 10@0", "2026-11-05T20:07:00Z", "2026-11-05T20:15:00Z", 2)]
    // cycle 1440
    [InlineData("60@0 60@100 1320@0", "2026-11-05T02:00:00Z", "2026-11-06T01:00:00Z", 1)]
    public void NextOn(string steps, string now, string expectedAt, int expectedStep)
    {
        var rhythm = Rhythm.Normalize(TestData.Steps(steps)).Rhythm!;

        var next = Rhythm.NextOn(rhythm.Steps, rhythm.CycleMinutes, DateTimeOffset.Parse(now));

        Assert.Equal((DateTimeOffset.Parse(expectedAt), expectedStep), next);
    }
}
