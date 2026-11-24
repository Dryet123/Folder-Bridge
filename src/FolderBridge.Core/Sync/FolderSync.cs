using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using FolderBridge.Core.Net;

namespace FolderBridge.Core.Sync;

public enum FolderState { Idle, Scanning, Syncing, Paused, Error }

public sealed class FolderSync : IDisposable
{
    public const string MetaDir = ".folderbridge";
    public const int ChunkSize = 256 * 1024;
    const int Pipeline = 6;
    const int BatchSize = 500;
    const int MaxDirtyPaths = 2000;
    static readonly TimeSpan FullScanInterval = TimeSpan.FromMinutes(30);
    static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);
    static readonly long MinTicks = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    readonly Engine engine;
    readonly string indexPath;
    readonly Dictionary<string, FileEntry> index = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<DeviceId, Dictionary<string, FileEntry>> remote = [];
    readonly HashSet<string> dirty = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> reportedErrors = [];
    readonly object gate = new();
    readonly object saveGate = new();
    readonly SemaphoreSlim wake = new(0);
    readonly CancellationTokenSource cts = new();
    readonly Timer debounce;
    readonly Timer retry;
    readonly Timer saveTimer;
    FileSystemWatcher? watcher;
    bool fullScanRequested = true;
    volatile bool pullRequested;
    bool indexDirty, saveScheduled, tmpReady;
    int fileCount, conflictCount;
    long byteCount, doneBytes, bytesIn, bytesOut;
    DateTime lastSendUtc, lastFullScan;

    public FolderConfig Config { get; }
    public string Id => Config.Id;
    public string Root => Config.Path;
    public FolderState State { get; private set; }
    public string? Error { get; private set; }
    public int NeedFiles { get; private set; }
    public int DoneFiles { get; private set; }
    public long NeedBytes { get; private set; }
    public string? CurrentFile { get; private set; }
    public DeviceId? CurrentPeer { get; private set; }
    public int FailedFiles { get; private set; }
    public DateTime? LastSync { get; private set; }
    public bool IsBusy => State is FolderState.Syncing or FolderState.Scanning || DateTime.UtcNow - lastSendUtc < TimeSpan.FromSeconds(2);

    internal FolderSync(Engine engine, FolderConfig config, string indexPath)
    {
        this.engine = engine;
        this.indexPath = indexPath;
        Config = config;
        debounce = new Timer(_ => wake.Release());
        retry = new Timer(_ => wake.Release());
        saveTimer = new Timer(_ => SaveIndex());
        if (File.Exists(indexPath))
        {
            using var fs = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            foreach (var e in JsonSerializer.Deserialize<List<FileEntry>>(fs, Json.Options) ?? [])
                Put(e);
        }
    }

    internal void Start() => _ = Task.Run(RunAsync);

    public void RequestScan()
    {
        lock (gate)
            fullScanRequested = true;
        pullRequested = true;
        wake.Release();
    }

    public void Wake() => wake.Release();

    // Looks at one path again a bit later, e.g. a file that was locked or changed under us.
    void RetryLater(string rel)
    {
        lock (gate)
            dirty.Add(rel);
        pullRequested = true;
        retry.Change(10_000, Timeout.Infinite);
    }

    internal void OnRemoteIndex(DeviceId peer, IEnumerable<FileEntry> files)
    {
        lock (gate)
        {
            if (!remote.TryGetValue(peer, out var map))
                remote[peer] = map = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                if (!IsValidPath(f.Path) || f.Size < 0 || (f.IsFile && f.Hash == null))
                    continue;
                // Updates can arrive out of order; never let an older version replace a newer one.
                if (map.TryGetValue(f.Path, out var old) && f.Version.Compare(old.Version) == Ordering.Lesser)
                    continue;
                map[f.Path] = f;
            }
        }
        pullRequested = true;
        wake.Release();
    }

    internal void RemovePeer(DeviceId peer)
    {
        lock (gate)
            remote.Remove(peer);
    }

    internal List<FileEntry> SnapshotIndex()
    {
        lock (gate)
            return index.Values.ToList();
    }

    internal FolderSnapshot Snapshot(IReadOnlyList<DeviceId> activePeers)
    {
        int files, conflicts;
        long bytes;
        lock (gate)
        {
            files = fileCount;
            conflicts = conflictCount;
            bytes = byteCount;
        }
        return new FolderSnapshot(Id, Config.Name, Root, State, Error, Config.Paused, files, bytes, conflicts,
            NeedFiles, DoneFiles, NeedBytes, Interlocked.Read(ref doneBytes), CurrentFile, CurrentPeer, FailedFiles, LastSync,
            Interlocked.Read(ref bytesIn), Interlocked.Read(ref bytesOut), DateTime.UtcNow - lastSendUtc < TimeSpan.FromSeconds(2),
            Config.Devices.Select(d => new DeviceId(d)).ToList(), activePeers);
    }

    // All index writes go through here so the counters shown in the UI never need a full pass.
    void Put(FileEntry e)
    {
        if (index.TryGetValue(e.Path, out var old))
            Count(old, -1);
        index[e.Path] = e;
        Count(e, 1);
    }

    void Count(FileEntry e, int sign)
    {
        if (!e.IsFile)
            return;
        fileCount += sign;
        byteCount += sign * e.Size;
        if (e.Path.Contains(".conflict-", StringComparison.OrdinalIgnoreCase))
            conflictCount += sign;
    }

    async Task RunAsync()
    {
        var ct = cts.Token;
        CleanMeta();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PassAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                State = FolderState.Error;
                Error = ex.Message;
                engine.Log($"{Config.Name}: {ex}");
            }

            // The watcher reports changes as they happen, so full rescans are only a rare safety net.
            var wait = watcher == null || FailedFiles > 0 ? RetryInterval : FullScanInterval - (DateTime.UtcNow - lastFullScan);
            if (wait < TimeSpan.FromSeconds(1))
                wait = TimeSpan.FromSeconds(1);
            try
            {
                if (!await wake.WaitAsync(wait, ct))
                {
                    if (watcher == null || DateTime.UtcNow - lastFullScan >= FullScanInterval)
                        lock (gate)
                            fullScanRequested = true;
                    if (FailedFiles > 0)
                        pullRequested = true;
                }
                while (wake.CurrentCount > 0)
                    wake.Wait(0);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    async Task PassAsync(CancellationToken ct)
    {
        if (Config.Paused)
        {
            State = FolderState.Paused;
            return;
        }
        if (!Directory.Exists(Root))
        {
            DropWatcher();
            State = FolderState.Error;
            Error = "The folder can't be found. Is the drive connected?";
            return;
        }
        if (!Directory.Exists(Path.Combine(Root, MetaDir)))
        {
            bool known;
            lock (gate)
                known = fileCount > 0 || index.Values.Any(e => !e.Deleted);
            // An empty folder in place of a synced one usually means an unplugged drive or a replaced folder.
            // Scanning it would tell the other devices to delete everything.
            if (known)
            {
                State = FolderState.Error;
                Error = "The hidden .folderbridge folder is missing, so syncing stopped to protect your files. Remove the folder from Folder Bridge and add it again.";
                return;
            }
            CreateMeta();
        }
        Error = null;
        EnsureWatcher();

        bool full;
        List<string>? paths = null;
        lock (gate)
        {
            full = fullScanRequested;
            if (!full && dirty.Count > 0)
                paths = dirty.ToList();
            fullScanRequested = false;
            dirty.Clear();
        }
        if (full || paths != null)
        {
            State = FolderState.Scanning;
            engine.CountWork();
            var changed = Scan(full ? null : paths);
            if (full)
                lastFullScan = DateTime.UtcNow;
            if (changed.Count > 0)
                engine.Broadcast(this, changed);
        }
        if (pullRequested)
        {
            pullRequested = false;
            await PullAsync(ct);
        }
        State = FolderState.Idle;
    }

    // Scans the whole folder (paths == null) or only the given paths and everything below them.
    List<FileEntry> Scan(List<string>? paths)
    {
        var me = engine.Id.Short;
        var changed = new List<FileEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string>? sweep = null;

        if (paths == null)
        {
            foreach (var (rel, info) in Walk(new DirectoryInfo(Root), unreadable, true))
            {
                seen.Add(rel);
                Examine(rel, info, me, changed);
            }
        }
        else
        {
            sweep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rel in TopLevel(paths))
            {
                if (!IsValidPath(rel))
                    continue;
                var full = FullPath(rel);
                FileSystemInfo? info = Directory.Exists(full) ? new DirectoryInfo(full) : File.Exists(full) ? new FileInfo(full) : null;
                if (info != null && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                if (info is FileInfo)
                {
                    seen.Add(rel);
                    Examine(rel, info, me, changed);
                    if (WasDirectory(rel))
                        sweep.Add(rel);
                    continue;
                }
                sweep.Add(rel);
                if (info is DirectoryInfo dir)
                {
                    seen.Add(rel);
                    Examine(rel, dir, me, changed);
                    foreach (var (r, i) in Walk(dir, unreadable, false))
                    {
                        seen.Add(r);
                        Examine(r, i, me, changed);
                    }
                }
            }
        }

        if (sweep == null || sweep.Count > 0)
            MarkMissing(sweep, seen, unreadable, me, changed);
        lock (gate)
        {
            foreach (var e in changed)
                Put(e);
            if (changed.Count > 0)
                indexDirty = true;
        }
        ScheduleSave();
        return changed;
    }

    void Examine(string rel, FileSystemInfo info, string me, List<FileEntry> changed)
    {
        FileEntry? old;
        lock (gate)
            index.TryGetValue(rel, out old);

        if (info is DirectoryInfo)
        {
            if (old is not { IsDirectory: true, Deleted: false })
                changed.Add(new FileEntry { Path = rel, IsDirectory = true, Version = (old?.Version ?? VersionVector.Empty).Increment(me) });
            return;
        }

        var fi = (FileInfo)info;
        long size = fi.Length, ticks = fi.LastWriteTimeUtc.Ticks;
        if (old is { IsFile: true } && old.Size == size && old.ModifiedTicks == ticks)
            return;
        ContentHash hash;
        try
        {
            hash = HashFile(fi.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RetryLater(rel);
            return;
        }
        fi.Refresh();
        if (!fi.Exists || fi.Length != size || fi.LastWriteTimeUtc.Ticks != ticks)
        {
            RetryLater(rel);
            return;
        }
        if (old is { IsFile: true } && old.Hash == hash)
        {
            lock (gate)
            {
                Put(old with { Size = size, ModifiedTicks = ticks });
                indexDirty = true;
            }
            return;
        }
        changed.Add(new FileEntry
        {
            Path = rel,
            Size = size,
            ModifiedTicks = ticks,
            Hash = hash,
            Version = (old?.Version ?? VersionVector.Empty).Increment(me),
        });
    }

    void MarkMissing(HashSet<string>? sweep, HashSet<string> seen, HashSet<string> unreadable, string me, List<FileEntry> changed)
    {
        HashSet<string>.AlternateLookup<ReadOnlySpan<char>> sweepLookup = default;
        if (sweep != null)
            sweepLookup = sweep.GetAlternateLookup<ReadOnlySpan<char>>();
        var unreadableLookup = unreadable.GetAlternateLookup<ReadOnlySpan<char>>();
        lock (gate)
        {
            foreach (var e in index.Values)
            {
                if (e.Deleted || seen.Contains(e.Path))
                    continue;
                if (sweep != null && !UnderAny(e.Path, sweepLookup))
                    continue;
                if (unreadable.Count > 0 && UnderAny(e.Path, unreadableLookup))
                    continue;
                changed.Add(e with { Deleted = true, Hash = null, Size = 0, Version = e.Version.Increment(me) });
            }
        }
    }

    static bool UnderAny(string path, HashSet<string>.AlternateLookup<ReadOnlySpan<char>> roots)
    {
        ReadOnlySpan<char> p = path;
        while (true)
        {
            if (roots.Contains(p))
                return true;
            int slash = p.LastIndexOf('/');
            if (slash <= 0)
                return false;
            p = p[..slash];
        }
    }

    // Drops paths whose parent folder is also listed, since scanning the parent covers them.
    static List<string> TopLevel(List<string> paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var lookup = set.GetAlternateLookup<ReadOnlySpan<char>>();
        var result = new List<string>();
        foreach (var path in set)
        {
            var slash = path.LastIndexOf('/');
            if (slash <= 0 || !UnderAny(path[..slash], lookup))
                result.Add(path);
        }
        return result;
    }

    bool WasDirectory(string rel)
    {
        lock (gate)
            return index.TryGetValue(rel, out var e) && e is { IsDirectory: true, Deleted: false };
    }

    IEnumerable<(string Rel, FileSystemInfo Info)> Walk(DirectoryInfo start, HashSet<string> unreadable, bool isRoot)
    {
        var stack = new Stack<DirectoryInfo>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            FileSystemInfo[] items;
            try
            {
                items = dir.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (isRoot && dir == start)
                    throw;
                unreadable.Add(Relative(dir.FullName));
                continue;
            }
            foreach (var item in items)
            {
                if (IsIgnored(item.Name) || item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                yield return (Relative(item.FullName), item);
                if (item is DirectoryInfo d)
                    stack.Push(d);
            }
        }
    }

    List<(DeviceId Peer, FileEntry Entry)> ComputeNeeds()
    {
        var best = new Dictionary<string, (DeviceId Peer, FileEntry Entry)>(StringComparer.OrdinalIgnoreCase);
        lock (gate)
        {
            var stale = new List<(Dictionary<string, FileEntry> Map, string Path)>();
            foreach (var (peer, files) in remote)
            {
                foreach (var r in files.Values)
                {
                    index.TryGetValue(r.Path, out var l);
                    if (!Needed(l, r))
                    {
                        stale.Add((files, r.Path));
                        continue;
                    }
                    if (!best.TryGetValue(r.Path, out var b) || r.Version.Compare(b.Entry.Version) == Ordering.Greater)
                        best[r.Path] = (peer, r);
                }
            }
            // Remote entries we already match are dropped, so the peer's index isn't kept twice in memory.
            foreach (var (map, path) in stale)
                map.Remove(path);
        }
        // Directories first, then files, then deletions with the deepest directories removed last.
        return best.Values
            .OrderBy(n => n.Entry switch { { IsDirectory: true, Deleted: false } => 0, { Deleted: false } => 1, { IsDirectory: false } => 2, _ => 3 })
            .ThenBy(n => n.Entry is { IsDirectory: true, Deleted: true } ? -Depth(n.Entry.Path) : Depth(n.Entry.Path))
            .ThenBy(n => n.Entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static bool Needed(FileEntry? local, FileEntry remote)
    {
        if (local == null)
            return !remote.Deleted;
        var cmp = remote.Version.Compare(local.Version);
        return cmp == Ordering.Greater || (cmp == Ordering.Concurrent && (local.SameContent(remote) || RemoteWins(local, remote)));
    }

    static int Depth(string path) => path.Count(c => c == '/');

    // Both devices evaluate this with the arguments swapped, so exactly one of them takes the other's version.
    static bool RemoteWins(FileEntry local, FileEntry remote)
    {
        if (local.Deleted != remote.Deleted)
            return local.Deleted;
        if (local.IsDirectory != remote.IsDirectory)
            return !remote.IsDirectory;
        if (local.ModifiedTicks != remote.ModifiedTicks)
            return remote.ModifiedTicks > local.ModifiedTicks;
        return (remote.Hash ?? default).CompareTo(local.Hash ?? default) > 0;
    }

    async Task PullAsync(CancellationToken ct)
    {
        var needs = ComputeNeeds();
        if (needs.Count == 0)
        {
            FailedFiles = 0;
            LastSync = DateTime.Now;
            return;
        }

        State = FolderState.Syncing;
        engine.CountWork();
        NeedFiles = needs.Count(n => n.Entry.IsFile);
        NeedBytes = needs.Where(n => n.Entry.IsFile).Sum(n => n.Entry.Size);
        DoneFiles = 0;
        Interlocked.Exchange(ref doneBytes, 0);
        int failed = 0;
        bool conflicts = false;
        var adopted = new List<FileEntry>();

        foreach (var (peer, entry) in needs)
        {
            if (ct.IsCancellationRequested || Config.Paused)
                break;
            if (!engine.IsConnected(peer))
            {
                failed++;
                continue;
            }
            CurrentPeer = peer;
            try
            {
                var (result, conflict) = await ApplyAsync(peer, entry, ct);
                if (result != null)
                    adopted.Add(result);
                conflicts |= conflict;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                ReportError(entry, ex);
            }
            if (entry.IsFile)
                DoneFiles++;
            if (adopted.Count >= BatchSize)
                Flush(adopted);
        }
        Flush(adopted);

        CurrentFile = null;
        CurrentPeer = null;
        NeedFiles = 0;
        NeedBytes = 0;
        FailedFiles = failed;
        if (failed == 0)
            LastSync = DateTime.Now;
        if (conflicts)
            wake.Release();
    }

    void Flush(List<FileEntry> adopted)
    {
        if (adopted.Count == 0)
            return;
        ScheduleSave();
        engine.Broadcast(this, adopted.ToList());
        adopted.Clear();
    }

    async Task<(FileEntry? Adopted, bool Conflict)> ApplyAsync(DeviceId peer, FileEntry r, CancellationToken ct)
    {
        FileEntry? l;
        lock (gate)
            index.TryGetValue(r.Path, out l);
        var cmp = l == null ? Ordering.Greater : r.Version.Compare(l.Version);
        if (cmp is Ordering.Equal or Ordering.Lesser)
            return default;
        if (l != null && l.SameContent(r))
            return (Adopt(l with { Version = l.Version.Merge(r.Version) }), false);
        bool conflict = cmp == Ordering.Concurrent;
        if (conflict && !RemoteWins(l!, r))
            return default;

        var full = FullPath(r.Path);
        if (!MatchesDisk(full, l))
        {
            // Changed locally after the last scan; scanning it first turns this into a proper conflict.
            RetryLater(r.Path);
            return default;
        }

        if (r.IsDirectory)
        {
            if (r.Deleted)
                RemoveDirectory(full);
            else
                Directory.CreateDirectory(full);
            return (Adopt(r with { Size = 0, ModifiedTicks = 0 }), false);
        }

        if (r.Deleted)
        {
            if (File.Exists(full))
            {
                MoveToTrash(full, r.Path);
                Record(ActivityKind.Trashed, r.Path, peer, l?.Size ?? 0);
            }
            return (Adopt(r), false);
        }

        CurrentFile = r.Path;
        var tmp = await DownloadAsync(peer, r, ct);
        try
        {
            if (!MatchesDisk(full, l))
            {
                RetryLater(r.Path);
                return default;
            }
            string? copy = null;
            if (conflict && l is { IsFile: true } && File.Exists(full))
            {
                copy = ConflictPath(full);
                File.Move(full, copy);
                lock (gate)
                    dirty.Add(Relative(copy));
            }
            if (Directory.Exists(full))
            {
                RemoveDirectory(full);
                if (Directory.Exists(full))
                    throw new IOException("A folder with the same name is in the way.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.Move(tmp, full, true);
            var fi = new FileInfo(full);
            Record(ActivityKind.Received, r.Path, peer, r.Size);
            if (copy != null)
                Record(ActivityKind.Conflict, Relative(copy), peer, l!.Size);
            return (Adopt(r with { Size = fi.Length, ModifiedTicks = fi.LastWriteTimeUtc.Ticks }), copy != null);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    async Task<string> DownloadAsync(DeviceId peer, FileEntry r, CancellationToken ct)
    {
        var conn = engine.GetConnection(peer) ?? throw new IOException("The other device disconnected.");
        var tmpDir = Path.Combine(Root, MetaDir, "tmp");
        if (!tmpReady)
        {
            Directory.CreateDirectory(tmpDir);
            tmpReady = true;
        }
        var tmp = Path.Combine(tmpDir, Guid.NewGuid().ToString("N"));
        var queue = new Queue<(Task<Chunk?> Task, int Length)>();
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var handle = File.OpenHandle(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.None, r.Size))
            {
                long next = 0, written = 0;
                while (next < r.Size || queue.Count > 0)
                {
                    while (queue.Count < Pipeline && next < r.Size)
                    {
                        int length = (int)Math.Min(ChunkSize, r.Size - next);
                        queue.Enqueue((conn.RequestAsync(Id, r.Path, r.Hash!.Value, next, length, ct), length));
                        next += length;
                    }
                    var (task, expected) = queue.Dequeue();
                    using var chunk = await task ?? throw new IOException("The file changed on the other device.");
                    if (chunk.Length != expected)
                        throw new IOException("The other device sent a damaged block.");
                    RandomAccess.Write(handle, chunk.Memory.Span, written);
                    written += chunk.Length;
                    hash.AppendData(chunk.Memory.Span);
                    Interlocked.Add(ref doneBytes, chunk.Length);
                    Interlocked.Add(ref bytesIn, chunk.Length);
                    engine.CountIn(chunk.Length);
                }
                // Set on the open handle so the final rename keeps it and no second open is needed.
                if (r.ModifiedTicks >= MinTicks)
                    File.SetLastWriteTimeUtc(handle, new DateTime(r.ModifiedTicks, DateTimeKind.Utc));
            }
            Span<byte> digest = stackalloc byte[32];
            hash.GetHashAndReset(digest);
            if (ContentHash.From(digest) != r.Hash)
                throw new IOException("The received file is damaged (checksum mismatch).");
            return tmp;
        }
        catch
        {
            foreach (var (task, _) in queue)
                _ = task.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully)
                        t.Result?.Dispose();
                });
            TryDelete(tmp);
            throw;
        }
    }

    internal (bool Found, long FileSize) ServeBlock(string path, ContentHash hash, long offset, byte[] buffer, int size)
    {
        FileEntry? e;
        lock (gate)
            index.TryGetValue(path, out e);
        if (e is not { IsFile: true } || e.Hash != hash || offset < 0 || offset + size > e.Size)
            return (false, 0);
        var full = FullPath(path);
        var fi = new FileInfo(full);
        if (!fi.Exists || fi.Length != e.Size || fi.LastWriteTimeUtc.Ticks != e.ModifiedTicks)
        {
            RetryLater(path);
            return (false, 0);
        }
        using (var handle = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            int done = 0;
            while (done < size)
            {
                int n = RandomAccess.Read(handle, buffer.AsSpan(done, size - done), offset + done);
                if (n == 0)
                    return (false, 0);
                done += n;
            }
        }
        lastSendUtc = DateTime.UtcNow;
        Interlocked.Add(ref bytesOut, size);
        return (true, e.Size);
    }

    FileEntry Adopt(FileEntry e)
    {
        lock (gate)
        {
            Put(e);
            indexDirty = true;
        }
        return e;
    }

    void Record(ActivityKind kind, string path, DeviceId peer, long size) =>
        engine.Activity.Add(new ActivityEntry(DateTime.Now, kind, "", Id, path, peer, engine.PeerName(peer), size));

    void ReportError(FileEntry entry, Exception ex)
    {
        engine.Log($"{Config.Name}: {entry.Path}: {ex.Message}");
        lock (gate)
            if (!reportedErrors.Add(entry.Path + "|" + entry.Version))
                return;
        engine.Activity.Add(new ActivityEntry(DateTime.Now, ActivityKind.Error, ex.Message, Id, entry.Path));
    }

    void ScheduleSave()
    {
        lock (gate)
        {
            if (!indexDirty || saveScheduled)
                return;
            saveScheduled = true;
        }
        saveTimer.Change(3000, Timeout.Infinite);
    }

    // Streams the index to disk at most every few seconds instead of after every change.
    void SaveIndex()
    {
        lock (saveGate)
        {
            List<FileEntry> entries;
            lock (gate)
            {
                saveScheduled = false;
                if (!indexDirty)
                    return;
                entries = index.Values.ToList();
                indexDirty = false;
            }
            try
            {
                var tmp = indexPath + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    JsonSerializer.Serialize(fs, entries, Json.Options);
                File.Move(tmp, indexPath, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lock (gate)
                    indexDirty = true;
                engine.Log($"{Config.Name}: can't save index: {ex.Message}");
            }
        }
    }

    void EnsureWatcher()
    {
        if (watcher != null)
            return;
        try
        {
            var w = new FileSystemWatcher(Root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            w.Changed += OnFsEvent;
            w.Created += OnFsEvent;
            w.Deleted += OnFsEvent;
            w.Renamed += OnFsEvent;
            w.Error += (_, _) =>
            {
                DropWatcher();
                lock (gate)
                    fullScanRequested = true;
                debounce.Change(1000, Timeout.Infinite);
            };
            w.EnableRaisingEvents = true;
            if (lastFullScan != default)
                lock (gate)
                    fullScanRequested = true;
            watcher = w;
        }
        catch (Exception ex)
        {
            engine.Log($"{Config.Name}: can't watch folder: {ex.Message}");
        }
    }

    void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        // A folder's own timestamp changes whenever something inside it does; those files report themselves.
        if (e.ChangeType == WatcherChangeTypes.Changed && Directory.Exists(e.FullPath))
            return;
        MarkDirty(e.Name);
        if (e is RenamedEventArgs renamed)
            MarkDirty(renamed.OldName);
        debounce.Change(1000, Timeout.Infinite);
    }

    void MarkDirty(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return;
        var rel = name.Replace('\\', '/');
        if (rel.Equals(MetaDir, StringComparison.OrdinalIgnoreCase) || rel.StartsWith(MetaDir + "/", StringComparison.OrdinalIgnoreCase))
            return;
        lock (gate)
        {
            if (dirty.Count >= MaxDirtyPaths)
                fullScanRequested = true;
            else
                dirty.Add(rel);
        }
    }

    void DropWatcher()
    {
        watcher?.Dispose();
        watcher = null;
    }

    void CreateMeta()
    {
        tmpReady = false;
        var meta = Directory.CreateDirectory(Path.Combine(Root, MetaDir));
        meta.Attributes |= FileAttributes.Hidden;
    }

    void CleanMeta()
    {
        try
        {
            var tmp = Path.Combine(Root, MetaDir, "tmp");
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, true);
            var trash = new DirectoryInfo(Path.Combine(Root, MetaDir, "trash"));
            if (trash.Exists)
                foreach (var day in trash.GetDirectories())
                    if (day.CreationTimeUtc < DateTime.UtcNow.AddDays(-30))
                        day.Delete(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    void MoveToTrash(string full, string rel)
    {
        var dest = Path.Combine(Root, MetaDir, "trash", DateTime.Now.ToString("yyyy-MM-dd"), rel.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Move(full, Unique(dest));
    }

    string ConflictPath(string full)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var device = new string(engine.DeviceName.Select(c => invalid.Contains(c) || c == ' ' ? '-' : c).ToArray());
        var name = Path.GetFileNameWithoutExtension(full);
        var ext = Path.GetExtension(full);
        return Unique(Path.Combine(Path.GetDirectoryName(full)!, $"{name}.conflict-{device}-{DateTime.Now:yyyyMMdd-HHmmss}{ext}"));
    }

    static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    static void RemoveDirectory(string full)
    {
        if (!Directory.Exists(full))
            return;
        var entries = Directory.GetFileSystemEntries(full);
        if (!entries.All(e => File.Exists(e) && IsIgnored(Path.GetFileName(e))))
            return;
        foreach (var e in entries)
        {
            File.SetAttributes(e, FileAttributes.Normal);
            File.Delete(e);
        }
        Directory.Delete(full);
    }

    static bool MatchesDisk(string full, FileEntry? l)
    {
        if (l == null || l.Deleted)
            return !File.Exists(full) && !Directory.Exists(full);
        if (l.IsDirectory)
            return Directory.Exists(full);
        var fi = new FileInfo(full);
        return fi.Exists && fi.Length == l.Size && fi.LastWriteTimeUtc.Ticks == l.ModifiedTicks;
    }

    static ContentHash HashFile(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            long offset = 0;
            int n;
            while ((n = RandomAccess.Read(handle, buffer, offset)) > 0)
            {
                hash.AppendData(buffer, 0, n);
                offset += n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return ContentHash.From(digest);
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    string Relative(string full) => Path.GetRelativePath(Root, full).Replace('\\', '/');

    string FullPath(string rel)
    {
        var root = Path.GetFullPath(Root).TrimEnd('\\') + '\\';
        var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', '\\')));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Path points outside the folder.");
        return full;
    }

    public static bool IsIgnored(string name) =>
        name.Equals(MetaDir, StringComparison.OrdinalIgnoreCase)
        || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
        || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
        || name.Equals(".DS_Store", StringComparison.Ordinal)
        || name.StartsWith("~$", StringComparison.Ordinal);

    static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    static bool IsValidPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 4000)
            return false;
        foreach (var segment in path.Split('/'))
            if (segment is "" or "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.IndexOfAny(InvalidChars) >= 0 || IsIgnored(segment))
                return false;
        return true;
    }

    public void Dispose()
    {
        cts.Cancel();
        debounce.Dispose();
        retry.Dispose();
        saveTimer.Dispose();
        DropWatcher();
        SaveIndex();
    }
}
