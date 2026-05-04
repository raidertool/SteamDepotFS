using Xunit;

namespace SteamDepotFs.Tests;

public sealed class ChunkReadPipelineTests
{
    [Fact]
    public async Task ReadAsync_PreservesByteOrderAcrossConcurrentChunkFetches()
    {
        var chunks = Enumerable.Range(0, 4)
            .Select(index => new TestChunk(index, index * 4, Enumerable.Range(index * 4, 4).Select(static value => (byte)value).ToArray()))
            .ToArray();
        var destination = new byte[9];
        var afterReadChunk = -1;

        var read = await ChunkReadPipeline.ReadAsync(
            chunks,
            totalSize: 16,
            offset: 2,
            destination,
            maxConcurrency: 4,
            static chunk => chunk.Offset,
            static chunk => chunk.Length,
            async (chunk, cancellationToken) =>
            {
                await Task.Delay((4 - chunk.Index) * 5, cancellationToken);
                return chunk.Bytes;
            },
            lastChunkIndex => afterReadChunk = lastChunkIndex,
            CancellationToken.None);

        Assert.Equal(9, read);
        Assert.Equal(Enumerable.Range(2, 9).Select(static value => (byte)value), destination);
        Assert.Equal(2, afterReadChunk);
    }

    [Fact]
    public async Task ReadAsync_DoesNotExceedConfiguredFetchConcurrency()
    {
        var chunks = Enumerable.Range(0, 8)
            .Select(index => new TestChunk(index, index, [(byte)index]))
            .ToArray();
        var destination = new byte[8];
        var active = 0;
        var peak = 0;
        var fetches = 0;

        var read = await ChunkReadPipeline.ReadAsync(
            chunks,
            totalSize: 8,
            offset: 0,
            destination,
            maxConcurrency: 2,
            static chunk => chunk.Offset,
            static chunk => chunk.Length,
            async (chunk, cancellationToken) =>
            {
                Interlocked.Increment(ref fetches);
                var current = Interlocked.Increment(ref active);
                UpdatePeak(ref peak, current);
                try
                {
                    await Task.Delay(50, cancellationToken);
                    return chunk.Bytes;
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            },
            afterRead: null,
            CancellationToken.None);

        Assert.Equal(8, read);
        Assert.Equal(chunks.Select(static chunk => chunk.Bytes[0]), destination);
        Assert.Equal(8, fetches);
        Assert.Equal(2, peak);
    }

    [Fact]
    public async Task ReadAsync_UsesOffsetOrderedChunksWhenManifestOrderIsUnsorted()
    {
        var manifestChunks = new[]
        {
            new TestChunk(0, 8, [8, 9, 10, 11]),
            new TestChunk(1, 0, [0, 1, 2, 3]),
            new TestChunk(2, 4, [4, 5, 6, 7])
        };
        var chunks = ChunkOrdering.EnsureOffsetOrder(manifestChunks, static chunk => chunk.Offset);
        var destination = new byte[8];
        var afterReadChunk = -1;

        var read = await ChunkReadPipeline.ReadAsync(
            chunks,
            totalSize: 12,
            offset: 0,
            destination,
            maxConcurrency: 4,
            static chunk => chunk.Offset,
            static chunk => chunk.Length,
            static (chunk, _) => Task.FromResult(chunk.Bytes),
            lastChunkIndex => afterReadChunk = lastChunkIndex,
            CancellationToken.None);

        Assert.Equal([1, 2, 0], chunks.Select(static chunk => chunk.Index));
        Assert.Equal(8, read);
        Assert.Equal(Enumerable.Range(0, 8).Select(static value => (byte)value), destination);
        Assert.Equal(1, afterReadChunk);
    }

    private static void UpdatePeak(ref int peak, int current)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref peak);
            if (current <= snapshot || Interlocked.CompareExchange(ref peak, current, snapshot) == snapshot)
            {
                return;
            }
        }
    }

    private sealed record TestChunk(int Index, long Offset, byte[] Bytes)
    {
        public long Length => Bytes.Length;
    }
}
