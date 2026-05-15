using BlockRdpBruteForce.Configuration;

namespace BlockRdpBruteForce.Tests;

public sealed class BlockDurationLadderTests
{
    [Fact]
    public void Resolve_null_uses_default_minutes()
    {
        var actual = BlockDurationLadder.Resolve(null, priorBlockCount: 5);
        Assert.Equal(TimeSpan.FromMinutes(BlockDurationLadder.DefaultMinutes), actual);
    }

    [Fact]
    public void Resolve_empty_uses_default_minutes()
    {
        var actual = BlockDurationLadder.Resolve(new List<int>(), priorBlockCount: 0);
        Assert.Equal(TimeSpan.FromMinutes(BlockDurationLadder.DefaultMinutes), actual);
    }

    [Fact]
    public void Resolve_single_entry_zero_means_permanent()
    {
        var actual = BlockDurationLadder.Resolve(new List<int> { 0 }, priorBlockCount: 0);
        Assert.Null(actual);
    }

    [Fact]
    public void Resolve_single_entry_acts_as_flat_duration()
    {
        var ladder = new List<int> { 60 };
        Assert.Equal(TimeSpan.FromMinutes(60), BlockDurationLadder.Resolve(ladder, 0));
        Assert.Equal(TimeSpan.FromMinutes(60), BlockDurationLadder.Resolve(ladder, 5));
    }

    [Fact]
    public void Resolve_first_block_uses_first_step()
    {
        var ladder = new List<int> { 60, 1440, 10080 };
        Assert.Equal(TimeSpan.FromMinutes(60), BlockDurationLadder.Resolve(ladder, 0));
    }

    [Fact]
    public void Resolve_second_block_uses_second_step()
    {
        var ladder = new List<int> { 60, 1440, 10080 };
        Assert.Equal(TimeSpan.FromMinutes(1440), BlockDurationLadder.Resolve(ladder, 1));
    }

    [Fact]
    public void Resolve_past_end_uses_last_step()
    {
        var ladder = new List<int> { 60, 1440, 10080 };
        Assert.Equal(TimeSpan.FromMinutes(10080), BlockDurationLadder.Resolve(ladder, 99));
    }

    [Fact]
    public void Resolve_trailing_zero_means_permanent_at_cap()
    {
        var ladder = new List<int> { 60, 1440, 0 };
        Assert.Equal(TimeSpan.FromMinutes(60), BlockDurationLadder.Resolve(ladder, 0));
        Assert.Equal(TimeSpan.FromMinutes(1440), BlockDurationLadder.Resolve(ladder, 1));
        Assert.Null(BlockDurationLadder.Resolve(ladder, 2));
        Assert.Null(BlockDurationLadder.Resolve(ladder, 50));
    }

    [Fact]
    public void Resolve_negative_prior_treated_as_zero()
    {
        var ladder = new List<int> { 60, 1440 };
        Assert.Equal(TimeSpan.FromMinutes(60), BlockDurationLadder.Resolve(ladder, -5));
    }

    [Fact]
    public void StepFor_returns_capped_index()
    {
        var ladder = new List<int> { 60, 1440, 10080 };
        Assert.Equal(0, BlockDurationLadder.StepFor(ladder, 0));
        Assert.Equal(1, BlockDurationLadder.StepFor(ladder, 1));
        Assert.Equal(2, BlockDurationLadder.StepFor(ladder, 2));
        Assert.Equal(2, BlockDurationLadder.StepFor(ladder, 99));
    }

    [Fact]
    public void Validate_rejects_null_or_empty()
    {
        Assert.Throws<ArgumentException>(() => BlockDurationLadder.ValidateOrThrow(null));
        Assert.Throws<ArgumentException>(() => BlockDurationLadder.ValidateOrThrow(new List<int>()));
    }

    [Fact]
    public void Validate_accepts_single_entry()
    {
        BlockDurationLadder.ValidateOrThrow(new List<int> { 1440 });
        BlockDurationLadder.ValidateOrThrow(new List<int> { 0 });
    }

    [Fact]
    public void Validate_rejects_negative_entry()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            BlockDurationLadder.ValidateOrThrow(new List<int> { 60, -1 }));
        Assert.Contains("[1]=-1", ex.Message);
    }

    [Fact]
    public void Validate_rejects_zero_not_at_end()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            BlockDurationLadder.ValidateOrThrow(new List<int> { 60, 0, 1440 }));
        Assert.Contains("[1]=0", ex.Message);
    }

    [Fact]
    public void Validate_accepts_trailing_zero()
    {
        BlockDurationLadder.ValidateOrThrow(new List<int> { 60, 1440, 0 });
    }

    [Fact]
    public void Validate_rejects_too_many_entries()
    {
        var tooMany = Enumerable.Repeat(60, BlockDurationLadder.MaxSteps + 1).ToList();
        Assert.Throws<ArgumentException>(() => BlockDurationLadder.ValidateOrThrow(tooMany));
    }
}
