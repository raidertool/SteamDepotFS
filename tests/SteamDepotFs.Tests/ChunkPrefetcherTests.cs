using Xunit;

namespace SteamDepotFs.Tests;

public sealed class ChunkPrefetcherTests
{
    [Fact]
    public async Task Schedule_DoesNotDuplicateInflightChunkWork()
    {
        var stats = new CacheStats();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var chunks = new[] { new TestChunk("0"), new TestChunk("1") };
        var prefetcher = new ChunkPrefetcher<TestChunk>(
            readAheadChunks: 1,
            keyForChunk: static chunk => chunk.Key,
            prefetchChunk: async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            },
            stats);

        try
        {
            prefetcher.Schedule(chunks, chunkIndex: 0);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            prefetcher.Schedule(chunks, chunkIndex: 0);

            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.Equal(1, stats.PrefetchScheduled);
            Assert.Equal(1, stats.PrefetchSkipped);

            release.SetResult();
            await WaitUntilAsync(() => stats.PrefetchCompleted == 1);

            Assert.Equal(1, stats.PrefetchCompleted);
            Assert.Equal(0, stats.PrefetchFailed);
        }
        finally
        {
            await prefetcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task Schedule_DoesNothingWhenReadAheadIsZero()
    {
        var stats = new CacheStats();
        var calls = 0;
        var prefetcher = new ChunkPrefetcher<TestChunk>(
            readAheadChunks: 0,
            keyForChunk: static chunk => chunk.Key,
            prefetchChunk: (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            },
            stats);

        try
        {
            prefetcher.Schedule([new TestChunk("0"), new TestChunk("1")], chunkIndex: 0);
            await Task.Delay(50);

            Assert.Equal(0, Volatile.Read(ref calls));
            Assert.Equal(0, stats.PrefetchScheduled);
            Assert.Equal(0, stats.PrefetchSkipped);
        }
        finally
        {
            await prefetcher.DisposeAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
        }
    }

    private sealed record TestChunk(string Key);
}
