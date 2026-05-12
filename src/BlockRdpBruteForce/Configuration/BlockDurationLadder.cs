namespace BlockRdpBruteForce.Configuration;

public static class BlockDurationLadder
{
    public const int MaxSteps = 20;

    public static TimeSpan? Resolve(
        IReadOnlyList<int>? ladderMinutes,
        int priorBlockCount,
        int fallbackMinutes)
    {
        if (priorBlockCount < 0) priorBlockCount = 0;

        if (ladderMinutes is not null && ladderMinutes.Count > 0)
        {
            var step = Math.Min(priorBlockCount, ladderMinutes.Count - 1);
            var minutes = ladderMinutes[step];
            return minutes <= 0 ? null : TimeSpan.FromMinutes(minutes);
        }

        return fallbackMinutes <= 0 ? null : TimeSpan.FromMinutes(fallbackMinutes);
    }

    public static int StepFor(IReadOnlyList<int>? ladderMinutes, int priorBlockCount)
    {
        if (ladderMinutes is null || ladderMinutes.Count == 0) return 0;
        if (priorBlockCount < 0) priorBlockCount = 0;
        return Math.Min(priorBlockCount, ladderMinutes.Count - 1);
    }

    public static void ValidateOrThrow(IReadOnlyList<int>? ladderMinutes)
    {
        if (ladderMinutes is null || ladderMinutes.Count == 0) return;
        if (ladderMinutes.Count > MaxSteps)
            throw new ArgumentException(
                $"BlockDurationLadderMinutes has too many entries ({ladderMinutes.Count}); max is {MaxSteps}.");

        for (var i = 0; i < ladderMinutes.Count; i++)
        {
            var v = ladderMinutes[i];
            if (v < 0)
                throw new ArgumentException(
                    $"BlockDurationLadderMinutes[{i}]={v} is invalid; entries must be >= 0.");
            if (v == 0 && i != ladderMinutes.Count - 1)
                throw new ArgumentException(
                    $"BlockDurationLadderMinutes[{i}]=0 (permanent) is only allowed as the last entry.");
        }
    }
}
