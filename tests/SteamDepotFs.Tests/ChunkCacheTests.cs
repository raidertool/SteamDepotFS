using Xunit;

namespace SteamDepotFs.Tests;

public sealed class ChunkCacheTests
{
    [Fact]
    public async Task GetOrAddAsync_DedupesConcurrentCacheMissesForSameChunk()
    {
        var root = Path.Combine(Path.GetTempPath(), "SteamDepotFs.Tests", Guid.NewGuid().ToString("N"));
        var cache = new ChunkCache(root, maxBytes: 1024 * 1024, lowWatermarkBytes: 512 * 1024, minFreeBytes: 0);
        var downloads = 0;

        try
        {
            var reads = Enumerable.Range(0, 8)
                .Select(_ => cache.GetOrAddAsync(
                    "481/testchunk",
                    expectedLength: 3,
                    async cancellationToken =>
                    {
                        Interlocked.Increment(ref downloads);
                        await Task.Delay(50, cancellationToken);
                        return [1, 2, 3];
                    },
                    CancellationToken.None))
                .ToArray();

            var results = await Task.WhenAll(reads);

            Assert.All(results, bytes => Assert.Equal([1, 2, 3], bytes));
            Assert.Equal(1, Volatile.Read(ref downloads));
            Assert.Equal(1, cache.Stats.Misses);
            Assert.Equal(7, cache.Stats.Hits);
            Assert.Equal(1, cache.Stats.Stored);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
