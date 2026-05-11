using AwesomeAssertions;

namespace Tickless.Tests;

public class ChunkDelayTests
{
    [Fact]
    public void Zero_YieldsSingleZero()
    {
        InProcessJobScheduler.ChunkDelay(TimeSpan.Zero)
            .Should().ContainSingle()
            .Which.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Negative_TreatedAsZero()
    {
        InProcessJobScheduler.ChunkDelay(TimeSpan.FromSeconds(-5))
            .Should().ContainSingle()
            .Which.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void SmallDelay_YieldsSingleChunk()
    {
        var delay = TimeSpan.FromSeconds(5);
        InProcessJobScheduler.ChunkDelay(delay)
            .Should().ContainSingle()
            .Which.Should().Be(delay);
    }

    [Fact]
    public void ExactlyMaxIntMs_YieldsSingleChunk()
    {
        var max = TimeSpan.FromMilliseconds(int.MaxValue);
        InProcessJobScheduler.ChunkDelay(max)
            .Should().ContainSingle()
            .Which.Should().Be(max);
    }

    [Fact]
    public void TwoMaxPlusRemainder_YieldsThreeChunks()
    {
        var max = TimeSpan.FromMilliseconds(int.MaxValue);
        var total = max + max + TimeSpan.FromSeconds(30);

        var chunks = InProcessJobScheduler.ChunkDelay(total).ToList();

        chunks.Should().HaveCount(3);
        chunks[0].Should().Be(max);
        chunks[1].Should().Be(max);
        chunks[2].Should().Be(TimeSpan.FromSeconds(30));
        chunks.Aggregate(TimeSpan.Zero, (acc, c) => acc + c).Should().Be(total);
    }

    [Fact]
    public void TripleMax_YieldsThreeFullChunks()
    {
        var max = TimeSpan.FromMilliseconds(int.MaxValue);
        var total = max + max + max;

        var chunks = InProcessJobScheduler.ChunkDelay(total).ToList();

        chunks.Should().HaveCount(3);
        chunks.Should().AllBeEquivalentTo(max);
    }
}
