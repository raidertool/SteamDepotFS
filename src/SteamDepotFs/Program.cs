using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Mono.Fuse.NETStandard;
using Mono.Unix.Native;
using SteamKit2;
using SteamKit2.CDN;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Usage();
                return args.Length == 0 ? 1 : 0;
            }

            var command = args[0];
            if (args.Skip(1).Any(static arg => arg is "-h" or "--help"))
            {
                Usage();
                return 0;
            }

            var parsed = ParsedArgs.Parse(args.Skip(1).ToArray());
            var options = DepotOptions.From(parsed);

            using var cts = new CancellationTokenSource(options.Timeout);

            return command switch
            {
                "smoke" => await RunSmokeAsync(options, cts.Token),
                "list" => await RunListAsync(options, parsed, cts.Token),
                "read" => await RunReadAsync(options, parsed, cts.Token),
                "mount" => await RunMountAsync(options, parsed, cts.Token),
                _ => UnknownCommand(command)
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Timed out.");
            return 124;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            if (Environment.GetEnvironmentVariable("STEAM_DEPOTFS_DEBUG") == "1")
            {
                Console.Error.WriteLine(ex);
            }

            return 1;
        }
    }

    private static async Task<int> RunSmokeAsync(DepotOptions options, CancellationToken cancellationToken)
    {
        await using var depot = await DepotReader.OpenAsync(options, cancellationToken);

        var file = depot.Index.AllFiles
            .Where(static f => !f.Flags.HasFlag(EDepotFileFlag.Directory) &&
                               !f.Flags.HasFlag(EDepotFileFlag.Symlink) &&
                               f.TotalSize > 0 &&
                               f.Chunks.Count > 0)
            .OrderBy(static f => f.TotalSize)
            .FirstOrDefault();

        if (file is null)
        {
            throw new InvalidOperationException("Manifest has no readable non-empty files.");
        }

        var bytesToRead = (int)Math.Min(4096, file.TotalSize);
        var buffer = new byte[bytesToRead];
        var read = await depot.ReadAsync(file, 0, buffer, cancellationToken);
        var digest = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read))).ToLowerInvariant();

        Console.WriteLine($"connected app={options.AppId} depot={options.DepotId} manifest={depot.Manifest.ManifestGID}");
        Console.WriteLine($"files={depot.Index.AllFiles.Count} depot_size={FormatBytes((long)depot.Manifest.TotalUncompressedSize)}");
        Console.WriteLine($"read path=\"{file.FileName}\" bytes={read} sha256={digest}");
        Console.WriteLine(depot.Cache.Stats.Format());
        return 0;
    }

    private static async Task<int> RunListAsync(DepotOptions options, ParsedArgs parsed, CancellationToken cancellationToken)
    {
        await using var depot = await DepotReader.OpenAsync(options, cancellationToken);

        var limit = parsed.GetInt("--limit") ?? 200;
        foreach (var file in depot.Index.AllFiles.OrderBy(static f => f.FileName, StringComparer.Ordinal).Take(limit))
        {
            Console.WriteLine($"{file.TotalSize,12} {file.FileName}");
        }

        if (depot.Index.AllFiles.Count > limit)
        {
            Console.WriteLine($"... {depot.Index.AllFiles.Count - limit} more files");
        }

        return 0;
    }

    private static async Task<int> RunReadAsync(DepotOptions options, ParsedArgs parsed, CancellationToken cancellationToken)
    {
        var path = parsed.Get("--path") ?? parsed.Positionals.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("read requires --path <depot-path>.");
        }

        await using var depot = await DepotReader.OpenAsync(options, cancellationToken);
        if (!depot.Index.TryGetFile(path, out var file))
        {
            throw new FileNotFoundException($"Path not found in depot: {path}");
        }

        var offset = parsed.GetLong("--offset") ?? 0;
        if (offset < 0 || (ulong)offset > file.TotalSize)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset is outside the file.");
        }

        var remaining = (long)file.TotalSize - offset;
        var length = parsed.GetLong("--length") ?? remaining;
        length = Math.Min(length, remaining);
        if (length < 0 || length > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Length must fit in a single read buffer.");
        }

        var buffer = new byte[(int)length];
        var read = await depot.ReadAsync(file, offset, buffer, cancellationToken);
        var output = parsed.Get("--out");
        if (output is null)
        {
            await Console.OpenStandardOutput().WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".");
            await File.WriteAllBytesAsync(output, buffer.AsMemory(0, read).ToArray(), cancellationToken);
            Console.WriteLine($"wrote {read} bytes to {output}");
        }

        Console.Error.WriteLine(depot.Cache.Stats.Format());
        return 0;
    }

    private static async Task<int> RunMountAsync(DepotOptions options, ParsedArgs parsed, CancellationToken cancellationToken)
    {
        var mountPoint = parsed.Get("--mount-point") ?? parsed.Positionals.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            throw new ArgumentException("mount requires --mount-point <directory> or a positional mount point.");
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The FUSE mount command must run on Linux or WSL. Use smoke/list/read here to test the Steam path.");
        }

        await using var depot = await DepotReader.OpenAsync(options, cancellationToken);
        using var fs = new DepotFuseFileSystem(Path.GetFullPath(mountPoint), depot);

        Console.Error.WriteLine($"mounting app={options.AppId} depot={options.DepotId} manifest={depot.Manifest.ManifestGID} at {fs.MountPoint}");
        fs.Start();
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Usage();
        return 1;
    }

    private static void Usage()
    {
        Console.Error.WriteLine("""
        SteamDepotFs

        Commands:
          smoke                         Connect anonymously by default, resolve a manifest, and read one file.
          list [--limit N]              List paths from the manifest.
          read --path PATH [--out FILE] Read a depot path, optionally with --offset and --length.
          mount --mount-point DIR       Mount the depot read-only through Linux FUSE.

        Common options:
          --app ID                      Steam app id. Default: 480.
          --depot ID                    Steam depot id. Default: 481.
          --manifest ID                 Manifest gid. If omitted, resolved from appinfo branch data.
          --branch NAME                 Branch name. Default: public.
          --branch-password-hash HASH   Optional pre-hashed branch password.
          --cache-dir DIR               Cache root. Default: RUNNER_TEMP or temp/steam-depotfs-cache.
          --cache-max-bytes SIZE        Hard cache cap. Default: 5G.
          --cache-low-watermark SIZE    Eviction target after crossing cap. Default: 80% of max.
          --cache-min-free-bytes SIZE   Free-space guard for the cache filesystem. Default: 1G.
          --max-chunk-concurrency N     Max concurrent chunk fetches/downloads. Default: 4.
          --read-ahead-chunks N         Chunks to prefetch after reads. Use 0 to disable. Default: 1.
          --username NAME               Steam username. Env: STEAM_USERNAME.
          --password VALUE              Steam password. Env: STEAM_PASSWORD.
          --auth-code VALUE             Steam Guard email code. Env: STEAM_AUTH_CODE.
          --two-factor-code VALUE       Steam mobile auth code. Env: STEAM_TWO_FACTOR_CODE.
          --access-token VALUE          Steam access token. Env: STEAM_ACCESS_TOKEN.
          --login-id ID                 Optional SteamKit login id. Env: STEAM_LOGIN_ID.
          --remember-password           Set SteamKit remember password flag.
          --timeout SECONDS             Overall command timeout. Default: 300.

        Size suffixes accept K, M, G, and T, for example --cache-max-bytes 8G.
        """);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }
}

internal sealed record DepotOptions
{
    public uint AppId { get; init; } = 480;
    public uint DepotId { get; init; } = 481;
    public ulong ManifestId { get; init; }
    public string Branch { get; init; } = "public";
    public string? BranchPasswordHash { get; init; }
    public string CacheDir { get; init; } = DefaultCacheDir();
    public long CacheMaxBytes { get; init; } = SizeParser.Parse("5G");
    public long CacheLowWatermarkBytes { get; init; }
    public long CacheMinFreeBytes { get; init; } = SizeParser.Parse("1G");
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
    public int MaxServers { get; init; } = 12;
    public int MaxChunkConcurrency { get; init; } = 4;
    public int ReadAheadChunks { get; init; } = 1;
    public SteamCredentials Credentials { get; init; } = SteamCredentials.FromEnvironment();

    public static DepotOptions From(ParsedArgs parsed)
    {
        var maxBytes = parsed.GetSize("--cache-max-bytes") ?? SizeParser.Parse("5G");
        var lowWatermark = parsed.GetSize("--cache-low-watermark") ?? (long)(maxBytes * 0.8);

        return new DepotOptions
        {
            AppId = parsed.GetUInt("--app") ?? 480,
            DepotId = parsed.GetUInt("--depot") ?? 481,
            ManifestId = parsed.GetULong("--manifest") ?? 0,
            Branch = parsed.Get("--branch") ?? "public",
            BranchPasswordHash = parsed.Get("--branch-password-hash"),
            CacheDir = parsed.Get("--cache-dir") ?? DefaultCacheDir(),
            CacheMaxBytes = maxBytes,
            CacheLowWatermarkBytes = Math.Min(lowWatermark, maxBytes),
            CacheMinFreeBytes = parsed.GetSize("--cache-min-free-bytes") ?? SizeParser.Parse("1G"),
            Timeout = TimeSpan.FromSeconds(parsed.GetInt("--timeout") ?? 300),
            MaxServers = parsed.GetInt("--max-servers") ?? 12,
            MaxChunkConcurrency = Math.Clamp(parsed.GetInt("--max-chunk-concurrency") ?? 4, 1, 64),
            ReadAheadChunks = Math.Clamp(parsed.GetInt("--read-ahead-chunks") ?? 1, 0, 16),
            Credentials = SteamCredentials.From(parsed)
        };
    }

    private static string DefaultCacheDir()
    {
        var root = Environment.GetEnvironmentVariable("RUNNER_TEMP");
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.GetTempPath();
        }

        return Path.Combine(root, "steam-depotfs-cache");
    }
}

internal sealed record SteamCredentials(
    string? Username,
    string? Password,
    string? AuthCode,
    string? TwoFactorCode,
    string? AccessToken,
    uint? LoginId,
    bool RememberPassword)
{
    public bool IsAnonymous => string.IsNullOrWhiteSpace(Username) && string.IsNullOrWhiteSpace(AccessToken);

    public static SteamCredentials FromEnvironment()
        => new(
            Environment.GetEnvironmentVariable("STEAM_USERNAME"),
            Environment.GetEnvironmentVariable("STEAM_PASSWORD"),
            Environment.GetEnvironmentVariable("STEAM_AUTH_CODE"),
            Environment.GetEnvironmentVariable("STEAM_TWO_FACTOR_CODE"),
            Environment.GetEnvironmentVariable("STEAM_ACCESS_TOKEN"),
            TryParseUInt(Environment.GetEnvironmentVariable("STEAM_LOGIN_ID")),
            IsTruthy(Environment.GetEnvironmentVariable("STEAM_REMEMBER_PASSWORD")));

    public static SteamCredentials From(ParsedArgs parsed)
    {
        var env = FromEnvironment();
        return env with
        {
            Username = parsed.Get("--username") ?? env.Username,
            Password = parsed.Get("--password") ?? env.Password,
            AuthCode = parsed.Get("--auth-code") ?? env.AuthCode,
            TwoFactorCode = parsed.Get("--two-factor-code") ?? env.TwoFactorCode,
            AccessToken = parsed.Get("--access-token") ?? env.AccessToken,
            LoginId = parsed.GetUInt("--login-id") ?? env.LoginId,
            RememberPassword = parsed.Has("--remember-password") || env.RememberPassword
        };
    }

    private static uint? TryParseUInt(string? value)
        => uint.TryParse(value, out var parsed) ? parsed : null;

    private static bool IsTruthy(string? value)
        => value is not null && (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                                 value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                 value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}

internal sealed class DepotReader : IAsyncDisposable
{
    private readonly SteamSession _steam;
    private readonly SteamKit2.CDN.Client _cdn;
    private readonly byte[] _depotKey;
    private readonly List<Server> _servers;
    private readonly Dictionary<string, string?> _cdnTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _cdnTokenGate = new(1, 1);
    private readonly SemaphoreSlim _downloadGate;
    private int _serverCursor;

    private DepotReader(
        DepotOptions options,
        SteamSession steam,
        SteamKit2.CDN.Client cdn,
        byte[] depotKey,
        DepotManifest manifest,
        List<Server> servers,
        ChunkCache cache)
    {
        Options = options;
        _steam = steam;
        _cdn = cdn;
        _depotKey = depotKey;
        Manifest = manifest;
        _servers = servers;
        Cache = cache;
        Index = FileIndex.FromManifest(manifest);
        _downloadGate = new SemaphoreSlim(options.MaxChunkConcurrency, options.MaxChunkConcurrency);
        Prefetcher = new ChunkPrefetcher<DepotManifest.ChunkData>(
            options.ReadAheadChunks,
            chunk => ChunkKey(chunk, out _),
            async (chunk, ct) =>
            {
                await GetChunkAsync(chunk, ct);
            },
            Cache.Stats);
    }

    public DepotOptions Options { get; }
    public DepotManifest Manifest { get; }
    public FileIndex Index { get; }
    public ChunkCache Cache { get; }
    private ChunkPrefetcher<DepotManifest.ChunkData> Prefetcher { get; }

    public static async Task<DepotReader> OpenAsync(DepotOptions options, CancellationToken cancellationToken)
    {
        var steam = await SteamSession.ConnectAsync(options.Credentials, cancellationToken);
        var cdn = new SteamKit2.CDN.Client(steam.Client);
        var depotKey = await steam.GetDepotKeyAsync(options.AppId, options.DepotId, cancellationToken);
        var manifestId = await steam.ResolveManifestIdAsync(options, cancellationToken);
        var requestCode = await steam.Content.GetManifestRequestCode(
            options.DepotId,
            options.AppId,
            manifestId,
            options.Branch,
            options.BranchPasswordHash ?? string.Empty).WaitAsync(cancellationToken);

        var servers = await steam.Content.GetServersForSteamPipe(null, (uint)options.MaxServers).WaitAsync(cancellationToken);
        var usableServers = servers
            .Where(static s => !s.UseAsProxy && !s.SteamChinaOnly)
            .OrderBy(static s => s.WeightedLoad)
            .ThenBy(static s => s.Load)
            .ToList();

        if (usableServers.Count == 0)
        {
            throw new InvalidOperationException("Steam returned no usable CDN servers.");
        }

        var cache = new ChunkCache(options.CacheDir, options.CacheMaxBytes, options.CacheLowWatermarkBytes, options.CacheMinFreeBytes);
        var firstServer = usableServers[0];
        var token = await steam.TryGetCdnTokenAsync(options.AppId, options.DepotId, firstServer, cancellationToken);
        var manifest = await cdn.DownloadManifestAsync(
            options.DepotId,
            manifestId,
            requestCode,
            firstServer,
            depotKey,
            proxyServer: null,
            cdnAuthToken: token).WaitAsync(cancellationToken);

        return new DepotReader(options, steam, cdn, depotKey, manifest, usableServers, cache);
    }

    public async Task<int> ReadAsync(DepotManifest.FileData file, long offset, byte[] destination, CancellationToken cancellationToken)
        => await ChunkReadPipeline.ReadAsync(
            file.Chunks,
            file.TotalSize,
            offset,
            destination,
            Options.MaxChunkConcurrency,
            static chunk => (long)chunk.Offset,
            static chunk => chunk.UncompressedLength,
            GetChunkAsync,
            lastChunkIndex => Prefetcher.Schedule(file.Chunks, lastChunkIndex),
            cancellationToken);

    private async Task<byte[]> GetChunkAsync(DepotManifest.ChunkData chunk, CancellationToken cancellationToken)
    {
        var key = ChunkKey(chunk, out var chunkId);
        return await Cache.GetOrAddAsync(key, chunk.UncompressedLength, async ct =>
        {
            await _downloadGate.WaitAsync(ct);
            try
            {
                var buffer = new byte[(int)chunk.UncompressedLength];
                Exception? lastFailure = null;
                for (var attempt = 0; attempt < _servers.Count; attempt++)
                {
                    var server = NextServer();
                    var token = await GetCachedCdnTokenAsync(server, ct);
                    try
                    {
                        var bytes = await _cdn.DownloadDepotChunkAsync(
                            Options.DepotId,
                            chunk,
                            server,
                            buffer,
                            _depotKey,
                            proxyServer: null,
                            cdnAuthToken: token).WaitAsync(ct);

                        if (bytes != chunk.UncompressedLength)
                        {
                            throw new InvalidDataException($"Chunk {chunkId} returned {bytes} bytes; expected {chunk.UncompressedLength}.");
                        }

                        return buffer;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        lastFailure = ex;
                    }
                }

                throw new IOException($"Failed to download chunk {chunkId} from all CDN servers.", lastFailure);
            }
            finally
            {
                _downloadGate.Release();
            }
        }, cancellationToken);
    }

    private Server NextServer()
    {
        var index = Interlocked.Increment(ref _serverCursor);
        return _servers[Math.Abs(index) % _servers.Count];
    }

    private async Task<string?> GetCachedCdnTokenAsync(Server server, CancellationToken cancellationToken)
    {
        var host = server.Host ?? server.VHost ?? server.ToString();
        await _cdnTokenGate.WaitAsync(cancellationToken);
        try
        {
            if (_cdnTokens.TryGetValue(host, out var token))
            {
                return token;
            }

            token = await _steam.TryGetCdnTokenAsync(Options.AppId, Options.DepotId, server, cancellationToken);
            _cdnTokens[host] = token;
            return token;
        }
        finally
        {
            _cdnTokenGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Prefetcher.DisposeAsync();
        _downloadGate.Dispose();
        _cdnTokenGate.Dispose();
        _cdn.Dispose();
        await _steam.DisposeAsync();
    }

    private string ChunkKey(DepotManifest.ChunkData chunk, out string chunkId)
    {
        if (chunk.ChunkID is null)
        {
            throw new InvalidDataException("Manifest chunk is missing its chunk id.");
        }

        chunkId = Hex(chunk.ChunkID);
        return $"{Options.DepotId}/{chunkId}";
    }

    private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();

}

internal static class ChunkReadPipeline
{
    public static async Task<int> ReadAsync<TChunk>(
        IReadOnlyList<TChunk> chunks,
        ulong totalSize,
        long offset,
        byte[] destination,
        int maxConcurrency,
        Func<TChunk, long> chunkOffset,
        Func<TChunk, long> chunkLength,
        Func<TChunk, CancellationToken, Task<byte[]>> fetchChunk,
        Action<int>? afterRead,
        CancellationToken cancellationToken)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if ((ulong)offset >= totalSize || destination.Length == 0)
        {
            return 0;
        }

        var end = Math.Min((long)totalSize, offset + destination.Length);
        var reads = new List<ChunkRead<TChunk>>();
        var written = 0;

        for (var chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
        {
            var chunk = chunks[chunkIndex];
            var currentChunkOffset = chunkOffset(chunk);
            var currentChunkLength = chunkLength(chunk);
            var chunkEnd = currentChunkOffset + currentChunkLength;
            if (chunkEnd <= offset)
            {
                continue;
            }

            if (currentChunkOffset >= end)
            {
                break;
            }

            var readStart = Math.Max(offset, currentChunkOffset);
            var readEnd = Math.Min(end, chunkEnd);
            var readLength = (int)(readEnd - readStart);
            reads.Add(new ChunkRead<TChunk>(
                chunkIndex,
                chunk,
                currentChunkOffset,
                readStart,
                (int)(readStart - offset),
                readLength));
            written += readLength;
        }

        if (reads.Count == 0)
        {
            return 0;
        }

        var chunkBytes = await FetchChunksAsync(
            reads,
            Math.Max(1, maxConcurrency),
            fetchChunk,
            cancellationToken);

        for (var i = 0; i < reads.Count; i++)
        {
            var read = reads[i];
            Buffer.BlockCopy(
                chunkBytes[i],
                (int)(read.ReadStart - read.ChunkStart),
                destination,
                read.DestinationOffset,
                read.Length);
        }

        afterRead?.Invoke(reads[^1].ChunkIndex);
        return written;
    }

    private static async Task<byte[][]> FetchChunksAsync<TChunk>(
        IReadOnlyList<ChunkRead<TChunk>> reads,
        int maxConcurrency,
        Func<TChunk, CancellationToken, Task<byte[]>> fetchChunk,
        CancellationToken cancellationToken)
    {
        var results = new byte[reads.Count][];
        if (reads.Count == 1 || maxConcurrency == 1)
        {
            for (var i = 0; i < reads.Count; i++)
            {
                results[i] = await fetchChunk(reads[i].Chunk, cancellationToken);
            }

            return results;
        }

        using var readGate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = new Task[reads.Count];
        for (var i = 0; i < reads.Count; i++)
        {
            var index = i;
            tasks[index] = FetchOneAsync(index);
        }

        await Task.WhenAll(tasks);
        return results;

        async Task FetchOneAsync(int index)
        {
            await readGate.WaitAsync(cancellationToken);
            try
            {
                results[index] = await fetchChunk(reads[index].Chunk, cancellationToken);
            }
            finally
            {
                readGate.Release();
            }
        }
    }

    private readonly record struct ChunkRead<TChunk>(
        int ChunkIndex,
        TChunk Chunk,
        long ChunkStart,
        long ReadStart,
        int DestinationOffset,
        int Length);
}

internal sealed class ChunkPrefetcher<TChunk> : IAsyncDisposable
{
    private readonly int _readAheadChunks;
    private readonly Func<TChunk, string> _keyForChunk;
    private readonly Func<TChunk, CancellationToken, Task> _prefetchChunk;
    private readonly CacheStats _stats;
    private readonly ConcurrentDictionary<string, Task> _prefetches = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();

    public ChunkPrefetcher(
        int readAheadChunks,
        Func<TChunk, string> keyForChunk,
        Func<TChunk, CancellationToken, Task> prefetchChunk,
        CacheStats stats)
    {
        _readAheadChunks = Math.Max(0, readAheadChunks);
        _keyForChunk = keyForChunk;
        _prefetchChunk = prefetchChunk;
        _stats = stats;
    }

    public void Schedule(IReadOnlyList<TChunk> chunks, int chunkIndex)
    {
        if (_readAheadChunks <= 0)
        {
            return;
        }

        for (var offset = 1; offset <= _readAheadChunks; offset++)
        {
            var nextIndex = chunkIndex + offset;
            if (nextIndex >= chunks.Count)
            {
                break;
            }

            var chunk = chunks[nextIndex];
            string key;
            try
            {
                key = _keyForChunk(chunk);
            }
            catch
            {
                _stats.RecordPrefetchFailed();
                continue;
            }

            if (!_prefetches.TryAdd(key, Task.CompletedTask))
            {
                _stats.RecordPrefetchSkipped();
                continue;
            }

            _stats.RecordPrefetchScheduled();
            _prefetches[key] = Task.Run(async () =>
            {
                try
                {
                    await _prefetchChunk(chunk, _cts.Token);
                    _stats.RecordPrefetchCompleted();
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                    _stats.RecordPrefetchFailed();
                }
                finally
                {
                    _prefetches.TryRemove(key, out _);
                }
            }, CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        var prefetches = _prefetches.Values.Where(static task => !task.IsCompleted).ToArray();
        if (prefetches.Length > 0)
        {
            try
            {
                await Task.WhenAll(prefetches);
            }
            catch
            {
            }
        }

        _cts.Dispose();
    }
}

internal sealed class SteamSession : IAsyncDisposable
{
    private readonly CallbackManager _callbacks;
    private readonly SteamUser _user;
    private readonly CancellationTokenSource _callbackLoopCts = new();
    private readonly Task _callbackLoop;

    private SteamSession(SteamClient client)
    {
        Client = client;
        _callbacks = new CallbackManager(client);
        _user = client.GetHandler<SteamUser>() ?? throw new InvalidOperationException("SteamUser handler unavailable.");
        Apps = client.GetHandler<SteamApps>() ?? throw new InvalidOperationException("SteamApps handler unavailable.");
        Content = client.GetHandler<SteamContent>() ?? throw new InvalidOperationException("SteamContent handler unavailable.");
        _callbackLoop = Task.Run(RunCallbacksAsync);
    }

    public SteamClient Client { get; }
    public SteamApps Apps { get; }
    public SteamContent Content { get; }

    public static async Task<SteamSession> ConnectAsync(SteamCredentials credentials, CancellationToken cancellationToken)
    {
        var configuration = SteamConfiguration.Create(static builder => { });
        var client = new SteamClient(configuration);
        var session = new SteamSession(client);

        var connected = new TaskCompletionSource<SteamClient.ConnectedCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loggedOn = new TaskCompletionSource<SteamUser.LoggedOnCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource<SteamClient.DisconnectedCallback>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var connectedSub = session._callbacks.Subscribe<SteamClient.ConnectedCallback>(callback =>
        {
            connected.TrySetResult(callback);
            session.LogOn(credentials);
        });

        using var loggedOnSub = session._callbacks.Subscribe<SteamUser.LoggedOnCallback>(callback =>
        {
            if (callback.Result == EResult.OK)
            {
                loggedOn.TrySetResult(callback);
            }
            else
            {
                loggedOn.TrySetException(new UnauthorizedAccessException($"Steam logon failed: {callback.Result} ({callback.ExtendedResult})"));
            }
        });

        using var disconnectedSub = session._callbacks.Subscribe<SteamClient.DisconnectedCallback>(callback =>
        {
            disconnected.TrySetResult(callback);
        });

        client.Connect(null!);
        var first = await Task.WhenAny(connected.Task, disconnected.Task).WaitAsync(cancellationToken);
        if (first == disconnected.Task)
        {
            throw new IOException("Steam disconnected before CM connection completed.");
        }

        await connected.Task.WaitAsync(cancellationToken);

        var completed = await Task.WhenAny(loggedOn.Task, disconnected.Task).WaitAsync(cancellationToken);
        if (completed == disconnected.Task)
        {
            throw new IOException("Steam disconnected before logon completed.");
        }

        await loggedOn.Task.WaitAsync(cancellationToken);
        return session;
    }

    public async Task<byte[]> GetDepotKeyAsync(uint appId, uint depotId, CancellationToken cancellationToken)
    {
        var result = await Apps.GetDepotDecryptionKey(depotId, appId).ToTask().WaitAsync(cancellationToken);
        if (result.Result != EResult.OK)
        {
            throw new UnauthorizedAccessException($"Could not get depot key for app={appId} depot={depotId}: {result.Result}");
        }

        return result.DepotKey;
    }

    public async Task<ulong> ResolveManifestIdAsync(DepotOptions options, CancellationToken cancellationToken)
    {
        if (options.ManifestId != 0)
        {
            return options.ManifestId;
        }

        var request = new SteamApps.PICSRequest(options.AppId, access_token: 0);
        var resultSet = await Apps.PICSGetProductInfo(request, null, metaDataOnly: false).ToTask().WaitAsync(cancellationToken);
        if (resultSet.Failed)
        {
            throw new IOException($"PICS appinfo request failed for app={options.AppId}.");
        }

        foreach (var callback in resultSet.Results!)
        {
            if (!callback.Apps.TryGetValue(options.AppId, out var appInfo))
            {
                continue;
            }

            if (appInfo.MissingToken)
            {
                throw new UnauthorizedAccessException($"PICS appinfo for app={options.AppId} requires an access token.");
            }

            if (appInfo.KeyValues is null)
            {
                continue;
            }

            var manifestId = appInfo.KeyValues["depots"][options.DepotId.ToString()]["manifests"][options.Branch]["gid"].AsUnsignedLong(0);
            if (manifestId != 0)
            {
                return manifestId;
            }
        }

        throw new InvalidOperationException($"Could not resolve manifest for app={options.AppId} depot={options.DepotId} branch={options.Branch}. Pass --manifest explicitly if this is a private branch.");
    }

    public async Task<string?> TryGetCdnTokenAsync(uint appId, uint depotId, Server server, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(server.Host))
            {
                return null;
            }

            var token = await Content.GetCDNAuthToken(appId, depotId, server.Host).WaitAsync(cancellationToken);
            return token.Result == EResult.OK && !string.IsNullOrWhiteSpace(token.Token) ? token.Token : null;
        }
        catch
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Client.IsConnected)
            {
                _user.LogOff();
                Client.Disconnect();
            }
        }
        catch
        {
            // Best-effort shutdown only.
        }

        _callbackLoopCts.Cancel();
        try
        {
            await _callbackLoop.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best-effort shutdown only.
        }

        _callbackLoopCts.Dispose();
    }

    private void LogOn(SteamCredentials credentials)
    {
        if (credentials.IsAnonymous)
        {
            _user.LogOnAnonymous();
            return;
        }

        _user.LogOn(new SteamUser.LogOnDetails
        {
            Username = credentials.Username,
            Password = credentials.Password,
            AccessToken = credentials.AccessToken,
            AuthCode = credentials.AuthCode,
            TwoFactorCode = credentials.TwoFactorCode,
            LoginID = credentials.LoginId,
            ShouldRememberPassword = credentials.RememberPassword
        });
    }

    private async Task RunCallbacksAsync()
    {
        while (!_callbackLoopCts.IsCancellationRequested)
        {
            try
            {
                await _callbacks.RunWaitCallbackAsync(_callbackLoopCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

internal sealed class ChunkCache
{
    private readonly string _root;
    private readonly long _maxBytes;
    private readonly long _lowWatermarkBytes;
    private readonly long _minFreeBytes;
    private readonly Dictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly object _locksGate = new();
    private readonly SemaphoreSlim _storeGate = new(1, 1);

    public ChunkCache(string root, long maxBytes, long lowWatermarkBytes, long minFreeBytes)
    {
        _root = Path.GetFullPath(root);
        _maxBytes = maxBytes;
        _lowWatermarkBytes = Math.Min(lowWatermarkBytes, maxBytes);
        _minFreeBytes = minFreeBytes;
        Directory.CreateDirectory(_root);
    }

    public CacheStats Stats { get; } = new();

    public async Task<byte[]> GetOrAddAsync(string key, long expectedLength, Func<CancellationToken, Task<byte[]>> download, CancellationToken cancellationToken)
    {
        var path = PathForKey(key);
        var gate = LockForKey(key);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var cached = await TryReadAsync(path, expectedLength, cancellationToken);
            if (cached is not null)
            {
                Stats.RecordHit(cached.Length);
                return cached;
            }

            Stats.RecordMiss();
            var bytes = await download(cancellationToken);
            Stats.RecordDownloaded(bytes.Length);

            if (bytes.LongLength == expectedLength)
            {
                await _storeGate.WaitAsync(cancellationToken);
                try
                {
                    if (await CanStoreAsync(bytes.LongLength, cancellationToken))
                    {
                        await StoreAsync(path, bytes, cancellationToken);
                        Stats.RecordStored();
                    }
                }
                finally
                {
                    _storeGate.Release();
                }
            }

            return bytes;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<byte[]?> TryReadAsync(string path, long expectedLength, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != expectedLength)
        {
            return null;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            if (bytes.LongLength != expectedLength)
            {
                return null;
            }

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return bytes;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<bool> CanStoreAsync(long incomingBytes, CancellationToken cancellationToken)
    {
        if (incomingBytes > _maxBytes)
        {
            return false;
        }

        await EvictIfNeededAsync(incomingBytes, cancellationToken);
        return CacheBytes() + incomingBytes <= _maxBytes && FreeBytes() - incomingBytes >= _minFreeBytes;
    }

    private async Task StoreAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
        File.Move(temp, path, overwrite: true);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
    }

    private Task EvictIfNeededAsync(long incomingBytes, CancellationToken cancellationToken)
    {
        var cacheBytes = CacheBytes();
        var freeBytes = FreeBytes();
        if (cacheBytes + incomingBytes <= _maxBytes && freeBytes - incomingBytes >= _minFreeBytes)
        {
            return Task.CompletedTask;
        }

        var targetBytes = Math.Max(0, _lowWatermarkBytes - incomingBytes);
        foreach (var file in Directory.EnumerateFiles(_root, "*.chunk", SearchOption.AllDirectories)
                     .Select(static path => new FileInfo(path))
                     .Where(static info => info.Exists)
                     .OrderBy(static info => info.LastWriteTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var length = file.Length;
                file.Delete();
                Stats.RecordEvicted(length);
                cacheBytes -= length;
                freeBytes += length;
            }
            catch
            {
                continue;
            }

            if (cacheBytes <= targetBytes && cacheBytes + incomingBytes <= _maxBytes && freeBytes - incomingBytes >= _minFreeBytes)
            {
                break;
            }
        }

        return Task.CompletedTask;
    }

    private long CacheBytes()
        => Directory.EnumerateFiles(_root, "*.chunk", SearchOption.AllDirectories)
            .Select(static path => new FileInfo(path))
            .Where(static info => info.Exists)
            .Sum(static info => info.Length);

    private long FreeBytes()
        => new DriveInfo(Path.GetPathRoot(_root) ?? _root).AvailableFreeSpace;

    private string PathForKey(string key)
    {
        var safe = key.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var leaf = safe[^1];
        var prefix = leaf.Length >= 2 ? leaf[..2] : "00";
        return Path.Combine(new[] { _root }.Concat(safe[..^1]).Concat([prefix, leaf + ".chunk"]).ToArray());
    }

    private SemaphoreSlim LockForKey(string key)
    {
        lock (_locksGate)
        {
            if (!_locks.TryGetValue(key, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _locks[key] = gate;
            }

            return gate;
        }
    }
}

internal sealed class CacheStats
{
    private long _hits;
    private long _misses;
    private long _stored;
    private long _evicted;
    private long _bytesDownloaded;
    private long _bytesReadFromCache;
    private long _bytesEvicted;
    private long _prefetchScheduled;
    private long _prefetchCompleted;
    private long _prefetchSkipped;
    private long _prefetchFailed;

    public long Hits => Volatile.Read(ref _hits);
    public long Misses => Volatile.Read(ref _misses);
    public long Stored => Volatile.Read(ref _stored);
    public long Evicted => Volatile.Read(ref _evicted);
    public long BytesDownloaded => Volatile.Read(ref _bytesDownloaded);
    public long BytesReadFromCache => Volatile.Read(ref _bytesReadFromCache);
    public long BytesEvicted => Volatile.Read(ref _bytesEvicted);
    public long PrefetchScheduled => Volatile.Read(ref _prefetchScheduled);
    public long PrefetchCompleted => Volatile.Read(ref _prefetchCompleted);
    public long PrefetchSkipped => Volatile.Read(ref _prefetchSkipped);
    public long PrefetchFailed => Volatile.Read(ref _prefetchFailed);

    public void RecordHit(long bytes)
    {
        Interlocked.Increment(ref _hits);
        Interlocked.Add(ref _bytesReadFromCache, bytes);
    }

    public void RecordMiss()
        => Interlocked.Increment(ref _misses);

    public void RecordStored()
        => Interlocked.Increment(ref _stored);

    public void RecordEvicted(long bytes)
    {
        Interlocked.Increment(ref _evicted);
        Interlocked.Add(ref _bytesEvicted, bytes);
    }

    public void RecordDownloaded(long bytes)
        => Interlocked.Add(ref _bytesDownloaded, bytes);

    public void RecordPrefetchScheduled()
        => Interlocked.Increment(ref _prefetchScheduled);

    public void RecordPrefetchCompleted()
        => Interlocked.Increment(ref _prefetchCompleted);

    public void RecordPrefetchSkipped()
        => Interlocked.Increment(ref _prefetchSkipped);

    public void RecordPrefetchFailed()
        => Interlocked.Increment(ref _prefetchFailed);

    public string Format()
        => $"cache hits={Hits} misses={Misses} stored={Stored} evicted={Evicted} downloaded={BytesDownloaded} read_from_cache={BytesReadFromCache} evicted_bytes={BytesEvicted} prefetch_scheduled={PrefetchScheduled} prefetch_completed={PrefetchCompleted} prefetch_skipped={PrefetchSkipped} prefetch_failed={PrefetchFailed}";
}

internal sealed class FileIndex
{
    private readonly Dictionary<string, DepotManifest.FileData> _filesByPath;
    private readonly Dictionary<string, DirectoryNode> _directoriesByPath;

    private FileIndex(
        DirectoryNode root,
        List<DepotManifest.FileData> allFiles,
        Dictionary<string, DepotManifest.FileData> filesByPath,
        Dictionary<string, DirectoryNode> directoriesByPath)
    {
        Root = root;
        AllFiles = allFiles;
        _filesByPath = filesByPath;
        _directoriesByPath = directoriesByPath;
    }

    public DirectoryNode Root { get; }
    public IReadOnlyList<DepotManifest.FileData> AllFiles { get; }

    public static FileIndex FromManifest(DepotManifest manifest)
    {
        var root = new DirectoryNode("", "");
        var allFiles = new List<DepotManifest.FileData>();
        var filesByPath = new Dictionary<string, DepotManifest.FileData>(StringComparer.Ordinal);
        var dirsByPath = new Dictionary<string, DirectoryNode>(StringComparer.Ordinal) { [""] = root };

        foreach (var file in manifest.Files ?? [])
        {
            if (string.IsNullOrWhiteSpace(file.FileName))
            {
                continue;
            }

            var normalized = Normalize(file.FileName);
            if (normalized.Length == 0)
            {
                continue;
            }

            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            var currentPath = "";

            var dirPartCount = file.Flags.HasFlag(EDepotFileFlag.Directory) ? parts.Length : parts.Length - 1;
            for (var i = 0; i < dirPartCount; i++)
            {
                currentPath = currentPath.Length == 0 ? parts[i] : currentPath + "/" + parts[i];
                current = current.GetOrAddDirectory(parts[i], currentPath);
                dirsByPath[currentPath] = current;
            }

            if (file.Flags.HasFlag(EDepotFileFlag.Directory))
            {
                continue;
            }

            current.Files[parts[^1]] = file;
            filesByPath[normalized] = file;
            allFiles.Add(file);
        }

        return new FileIndex(root, allFiles, filesByPath, dirsByPath);
    }

    public bool TryGetFile(string path, [NotNullWhen(true)] out DepotManifest.FileData? file)
        => _filesByPath.TryGetValue(Normalize(path), out file);

    public bool TryGetDirectory(string path, [NotNullWhen(true)] out DirectoryNode? node)
        => _directoriesByPath.TryGetValue(Normalize(path), out node);

    public bool Exists(string path)
        => TryGetFile(path, out _) || TryGetDirectory(path, out _);

    public static string Normalize(string path)
        => path.Replace('\\', '/').Trim('/').Replace("//", "/");
}

internal sealed class DirectoryNode
{
    public DirectoryNode(string name, string path)
    {
        Name = name;
        Path = path;
    }

    public string Name { get; }
    public string Path { get; }
    public Dictionary<string, DirectoryNode> Directories { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, DepotManifest.FileData> Files { get; } = new(StringComparer.Ordinal);

    public DirectoryNode GetOrAddDirectory(string name, string path)
    {
        if (!Directories.TryGetValue(name, out var child))
        {
            child = new DirectoryNode(name, path);
            Directories[name] = child;
        }

        return child;
    }
}

internal sealed class DepotFuseFileSystem : FileSystem
{
    private static readonly FilePermissions DirectoryMode =
        FilePermissions.S_IFDIR |
        FilePermissions.S_IRUSR | FilePermissions.S_IXUSR |
        FilePermissions.S_IRGRP | FilePermissions.S_IXGRP |
        FilePermissions.S_IROTH | FilePermissions.S_IXOTH;

    private static readonly FilePermissions FileMode =
        FilePermissions.S_IFREG |
        FilePermissions.S_IRUSR |
        FilePermissions.S_IRGRP |
        FilePermissions.S_IROTH;

    private static readonly FilePermissions ExecutableFileMode =
        FileMode |
        FilePermissions.S_IXUSR |
        FilePermissions.S_IXGRP |
        FilePermissions.S_IXOTH;

    private static readonly FilePermissions SymlinkMode =
        FilePermissions.S_IFLNK |
        FilePermissions.S_IRUSR |
        FilePermissions.S_IRGRP |
        FilePermissions.S_IROTH;

    private readonly DepotReader _reader;
    private readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public DepotFuseFileSystem(string mountPoint, DepotReader reader)
        : base(mountPoint)
    {
        _reader = reader;
        Name = "steam-depotfs";
        MultiThreaded = true;
        EnableKernelCache = true;
        EnableDirectIO = false;
        EnableLargeReadRequests = true;
        MaxReadSize = 1024 * 1024;
        AttributeTimeout = 60;
        PathTimeout = 60;
    }

    protected override Errno OnGetPathStatus(string path, out Stat stat)
    {
        if (_reader.Index.TryGetDirectory(path, out var directory))
        {
            stat = StatForDirectory(directory);
            return 0;
        }

        if (_reader.Index.TryGetFile(path, out var file))
        {
            stat = StatForFile(file);
            return 0;
        }

        stat = default;
        return Errno.ENOENT;
    }

    protected override Errno OnReadSymbolicLink(string link, out string target)
    {
        target = string.Empty;
        if (!_reader.Index.TryGetFile(link, out var file))
        {
            return Errno.ENOENT;
        }

        if (!file.Flags.HasFlag(EDepotFileFlag.Symlink) || string.IsNullOrEmpty(file.LinkTarget))
        {
            return Errno.EINVAL;
        }

        target = file.LinkTarget;
        return 0;
    }

    protected override Errno OnOpenHandle(string file, OpenedPathInfo info)
    {
        if (!_reader.Index.TryGetFile(file, out _))
        {
            return _reader.Index.TryGetDirectory(file, out _) ? Errno.EISDIR : Errno.ENOENT;
        }

        if ((((int)info.OpenAccess) & 3) != (int)OpenFlags.O_RDONLY)
        {
            return Errno.EROFS;
        }

        info.KeepCache = true;
        info.DirectIO = false;
        return 0;
    }

    protected override Errno OnReadHandle(string file, OpenedPathInfo info, byte[] buf, long offset, out int bytesWritten)
    {
        bytesWritten = 0;
        try
        {
            if (!_reader.Index.TryGetFile(file, out var depotFile))
            {
                return Errno.ENOENT;
            }

            bytesWritten = _reader.ReadAsync(depotFile, offset, buf, CancellationToken.None).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"read failed for {file}: {ex.Message}");
            return Errno.EIO;
        }
    }

    protected override Errno OnOpenDirectory(string directory, OpenedPathInfo info)
        => _reader.Index.TryGetDirectory(directory, out _) ? 0 : Errno.ENOENT;

    protected override Errno OnReleaseDirectory(string directory, OpenedPathInfo info)
        => 0;

    protected override Errno OnReadDirectory(string directory, OpenedPathInfo info, out IEnumerable<DirectoryEntry> paths)
    {
        paths = [];
        if (!_reader.Index.TryGetDirectory(directory, out var node))
        {
            return Errno.ENOENT;
        }

        var entries = new List<DirectoryEntry>
        {
            Entry(".", StatForDirectory(node)),
            Entry("..", StatForDirectory(node))
        };

        entries.AddRange(node.Directories.Values
            .OrderBy(static d => d.Name, StringComparer.Ordinal)
            .Select(d => Entry(d.Name, StatForDirectory(d))));

        entries.AddRange(node.Files
            .OrderBy(static f => f.Key, StringComparer.Ordinal)
            .Select(f => Entry(f.Key, StatForFile(f.Value))));

        paths = entries;
        return 0;
    }

    protected override Errno OnAccessPath(string path, AccessModes mode)
    {
        if (!_reader.Index.Exists(path))
        {
            return Errno.ENOENT;
        }

        return mode.HasFlag(AccessModes.W_OK) ? Errno.EROFS : 0;
    }

    protected override Errno OnGetFileSystemStatus(string path, out Statvfs buf)
    {
        buf = default;
        const ulong blockSize = 4096;
        var blocks = Math.Max(1, (_reader.Manifest.TotalUncompressedSize + blockSize - 1) / blockSize);
        buf.f_bsize = blockSize;
        buf.f_frsize = blockSize;
        buf.f_blocks = blocks;
        buf.f_bfree = 0;
        buf.f_bavail = 0;
        buf.f_files = (ulong)_reader.Index.AllFiles.Count + 1;
        buf.f_ffree = 0;
        buf.f_favail = 0;
        buf.f_namemax = 255;
        return 0;
    }

    protected override Errno OnCreateHandle(string file, OpenedPathInfo info, FilePermissions mode) => Errno.EROFS;
    protected override Errno OnWriteHandle(string file, OpenedPathInfo info, byte[] buf, long offset, out int bytesRead)
    {
        bytesRead = 0;
        return Errno.EROFS;
    }
    protected override Errno OnCreateDirectory(string directory, FilePermissions mode) => Errno.EROFS;
    protected override Errno OnRemoveFile(string file) => Errno.EROFS;
    protected override Errno OnRemoveDirectory(string directory) => Errno.EROFS;
    protected override Errno OnRenamePath(string oldpath, string newpath) => Errno.EROFS;
    protected override Errno OnChangePathPermissions(string path, FilePermissions mode) => Errno.EROFS;
    protected override Errno OnChangePathOwner(string path, long owner, long group) => Errno.EROFS;
    protected override Errno OnTruncateFile(string file, long length) => Errno.EROFS;

    private static DirectoryEntry Entry(string name, Stat stat) => new(name) { Stat = stat };

    private Stat StatForDirectory(DirectoryNode directory)
        => new()
        {
            st_ino = StableInode(directory.Path),
            st_mode = DirectoryMode,
            st_nlink = 2,
            st_size = 0,
            st_blksize = 4096,
            st_blocks = 0,
            st_atime = _now,
            st_mtime = _now,
            st_ctime = _now
        };

    private Stat StatForFile(DepotManifest.FileData file)
    {
        var mode = file.Flags.HasFlag(EDepotFileFlag.Symlink)
            ? SymlinkMode
            : file.Flags.HasFlag(EDepotFileFlag.Executable)
                ? ExecutableFileMode
                : FileMode;

        var size = file.Flags.HasFlag(EDepotFileFlag.Symlink) && file.LinkTarget is not null
            ? file.LinkTarget.Length
            : (long)file.TotalSize;

        return new Stat
        {
            st_ino = StableInode(file.FileName),
            st_mode = mode,
            st_nlink = 1,
            st_size = size,
            st_blksize = 4096,
            st_blocks = (size + 511) / 512,
            st_atime = _now,
            st_mtime = new DateTimeOffset(_reader.Manifest.CreationTime.ToUniversalTime()).ToUnixTimeSeconds(),
            st_ctime = _now
        };
    }

    private static ulong StableInode(string path)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path));
        return BitConverter.ToUInt64(hash, 0) & 0x7fffffffffffffff;
    }
}

internal sealed class ParsedArgs
{
    private readonly Dictionary<string, string?> _options;

    private ParsedArgs(Dictionary<string, string?> options, List<string> positionals)
    {
        _options = options;
        Positionals = positionals;
    }

    public IReadOnlyList<string> Positionals { get; }

    public static ParsedArgs Parse(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        var positionals = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(arg);
                continue;
            }

            var equals = arg.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                options[arg[..equals]] = arg[(equals + 1)..];
                continue;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options[arg] = args[++i];
            }
            else
            {
                options[arg] = "true";
            }
        }

        return new ParsedArgs(options, positionals);
    }

    public bool Has(string key) => _options.ContainsKey(key);
    public string? Get(string key) => _options.TryGetValue(key, out var value) ? value : null;
    public int? GetInt(string key) => int.TryParse(Get(key), out var value) ? value : null;
    public long? GetLong(string key) => long.TryParse(Get(key), out var value) ? value : null;
    public uint? GetUInt(string key) => uint.TryParse(Get(key), out var value) ? value : null;
    public ulong? GetULong(string key) => ulong.TryParse(Get(key), out var value) ? value : null;
    public long? GetSize(string key) => Get(key) is { } value ? SizeParser.Parse(value) : null;
}

internal static class SizeParser
{
    public static long Parse(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0)
        {
            throw new FormatException("Size value is empty.");
        }

        var suffix = char.ToUpperInvariant(raw[^1]);
        long multiplier = suffix switch
        {
            'K' => 1024L,
            'M' => 1024L * 1024,
            'G' => 1024L * 1024 * 1024,
            'T' => 1024L * 1024 * 1024 * 1024,
            _ => 1
        };

        var number = multiplier == 1 ? raw : raw[..^1];
        if (!double.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException($"Invalid size value: {raw}");
        }

        return checked((long)(value * multiplier));
    }
}
