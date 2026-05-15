namespace BlockRdpBruteForce.Configuration;

public static class BlockDurationLadder
{
    public const int MaxSteps = 20;
    public const int DefaultMinutes = 1440;

    public static TimeSpan? Resolve(IReadOnlyList<int>? ladderMinutes, int priorBlockCount)
    {
        if (priorBlockCount < 0) priorBlockCount = 0;

        if (ladderMinutes is null || ladderMinutes.Count == 0)
            return TimeSpan.FromMinutes(DefaultMinutes);

        var step = Math.Min(priorBlockCount, ladderMinutes.Count - 1);
        var minutes = ladderMinutes[step];
        return minutes <= 0 ? null : TimeSpan.FromMinutes(minutes);
    }

    public static int StepFor(IReadOnlyList<int>? ladderMinutes, int priorBlockCount)
    {
        if (ladderMinutes is null || ladderMinutes.Count == 0) return 0;
        if (priorBlockCount < 0) priorBlockCount = 0;
        return Math.Min(priorBlockCount, ladderMinutes.Count - 1);
    }

    public static void ValidateOrThrow(IReadOnlyList<int>? ladderMinutes)
    {
        if (ladderMinutes is null || ladderMinutes.Count == 0)
            throw new ArgumentException(
                "BlockDurationMinutes must contain at least one entry (e.g. [1440]).");
        if (ladderMinutes.Count > MaxSteps)
            throw new ArgumentException(
                $"BlockDurationMinutes has too many entries ({ladderMinutes.Count}); max is {MaxSteps}.");

        for (var i = 0; i < ladderMinutes.Count; i++)
        {
            var v = ladderMinutes[i];
            if (v < 0)
                throw new ArgumentException(
                    $"BlockDurationMinutes[{i}]={v} is invalid; entries must be >= 0.");
            if (v == 0 && i != ladderMinutes.Count - 1)
                throw new ArgumentException(
                    $"BlockDurationMinutes[{i}]=0 (permanent) is only allowed as the last entry.");
        }
    }
}
