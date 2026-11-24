using System.Security.Cryptography;
using System.Text.Json;

namespace FolderBridge.Core.Sync;

public enum FolderState { Idle, Scanning, Syncing, Paused, Error }

public sealed class FolderSync : IDisposable
{
    public const string MetaDir = ".folderbridge";
    public const int ChunkSize = 512 * 1024;
    const int Pipeline = 4;
    const int BatchSize = 500;
    static readonly long MinTicks = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    readonly Engine engine;
    readonly string indexPath;
    readonly Dictionary<string, FileEntry> index = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<DeviceId, Dictionary<string, FileEntry>> remote = [];
    readonly HashSet<string> reportedErrors = [];
    readonly object gate = new();
    readonly SemaphoreSlim wake = new(0);
    readonly CancellationTokenSource cts = new();
    readonly Timer debounce;
    FileSystemWatcher? watcher;
    volatile bool scanRequested = true;
    volatile bool pullRequested;
    bool indexDirty;
    long doneBytes, bytesIn, bytesOut;
    DateTime lastSendUtc;

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

    internal FolderSync(Engine engine, FolderConfig config, string indexPath)
    {
        this.engine = engine;
        this.indexPath = indexPath;
        Config = config;
        debounce = new Timer(_ => RequestScan());
        if (File.Exists(indexPath))
        {
            var entries = JsonSerializer.Deserialize<List<FileEntry>>(File.ReadAllBytes(indexPath), Json.Options) ?? [];
            foreach (var e in entries)
                index[e.Path] = e;
        }
    }

    internal void Start() => _ = Task.Run(RunAsync);

    public void RequestScan()
    {
        scanRequested = true;
        pullRequested = true;
        wake.Release();
    }

    public void Wake() => wake.Release();

    internal void OnRemoteIndex(DeviceId peer, IEnumerable<FileEntry> files)
    {
        lock (gate)
        {
            if (!remote.TryGetValue(peer, out var map))
                remote[peer] = map = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                if (!IsValidPath(f.Path) || f.Size < 0 || (f.IsFile && f.Hash is not { Length: 64 }))
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
        int files = 0, conflicts = 0;
        long bytes = 0;
        lock (gate)
        {
            foreach (var e in index.Values)
            {
                if (!e.IsFile)
                    continue;
                files++;
                bytes += e.Size;
                if (e.Path.Contains(".conflict-", StringComparison.OrdinalIgnoreCase))
                    conflicts++;
            }
        }
        return new FolderSnapshot(Id, Config.Name, Root, State, Error, Config.Paused, files, bytes, conflicts,
            NeedFiles, DoneFiles, NeedBytes, Interlocked.Read(ref doneBytes), CurrentFile, CurrentPeer, FailedFiles, LastSync,
            Interlocked.Read(ref bytesIn), Interlocked.Read(ref bytesOut), DateTime.UtcNow - lastSendUtc < TimeSpan.FromSeconds(2),
            Config.Devices.Select(d => new DeviceId(d)).ToList(), activePeers);
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

            try
            {
                if (!await wake.WaitAsync(TimeSpan.FromSeconds(60), ct))
                {
                    scanRequested = true;
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
                known = index.Values.Any(e => !e.Deleted);
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

        if (scanRequested)
        {
            scanRequested = false;
            State = FolderState.Scanning;
            var changed = Scan();
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

    List<FileEntry> Scan()
    {
        var me = engine.Id.Short;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreadable = new List<string>();
        var changed = new List<FileEntry>();

        foreach (var (rel, info) in Walk(unreadable))
        {
            seen.Add(rel);
            FileEntry? old;
            lock (gate)
                index.TryGetValue(rel, out old);

            if (info is DirectoryInfo)
            {
                if (old is not { IsDirectory: true, Deleted: false })
                    changed.Add(new FileEntry { Path = rel, IsDirectory = true, Version = (old?.Version ?? VersionVector.Empty).Increment(me) });
                continue;
            }

            var fi = (FileInfo)info;
            long size = fi.Length, ticks = fi.LastWriteTimeUtc.Ticks;
            if (old is { IsFile: true } && old.Size == size && old.ModifiedTicks == ticks)
                continue;
            string hash;
            try
            {
                hash = HashFile(fi.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            fi.Refresh();
            if (!fi.Exists || fi.Length != size || fi.LastWriteTimeUtc.Ticks != ticks)
                continue;
            if (old is { IsFile: true } && old.Hash == hash)
            {
                lock (gate)
                {
                    index[rel] = old with { Size = size, ModifiedTicks = ticks };
                    indexDirty = true;
                }
                continue;
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

        lock (gate)
        {
            foreach (var e in index.Values)
            {
                if (e.Deleted || seen.Contains(e.Path) || unreadable.Any(u => e.Path.StartsWith(u + "/", StringComparison.OrdinalIgnoreCase)))
                    continue;
                changed.Add(e with { Deleted = true, Hash = null, Size = 0, Version = e.Version.Increment(me) });
            }
            foreach (var e in changed)
                index[e.Path] = e;
            if (changed.Count > 0)
                indexDirty = true;
        }
        SaveIndex();
        return changed;
    }

    IEnumerable<(string Rel, FileSystemInfo Info)> Walk(List<string> unreadable)
    {
        var root = new DirectoryInfo(Root);
        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);
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
                if (dir == root)
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
        var needs = new List<(DeviceId Peer, FileEntry Entry)>();
        lock (gate)
        {
            var best = new Dictionary<string, (DeviceId Peer, FileEntry Entry)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (peer, files) in remote)
                foreach (var r in files.Values)
                    if (!best.TryGetValue(r.Path, out var b) || r.Version.Compare(b.Entry.Version) == Ordering.Greater)
                        best[r.Path] = (peer, r);

            foreach (var (peer, r) in best.Values)
            {
                if (!index.TryGetValue(r.Path, out var l))
                {
                    if (!r.Deleted)
                        needs.Add((peer, r));
                    continue;
                }
                var cmp = r.Version.Compare(l.Version);
                if (cmp == Ordering.Greater || (cmp == Ordering.Concurrent && (l.SameContent(r) || RemoteWins(l, r))))
                    needs.Add((peer, r));
            }
        }
        // Directories first, then files, then deletions with the deepest directories removed last.
        return needs
            .OrderBy(n => n.Entry switch { { IsDirectory: true, Deleted: false } => 0, { Deleted: false } => 1, { IsDirectory: false } => 2, _ => 3 })
            .ThenBy(n => n.Entry is { IsDirectory: true, Deleted: true } ? -Depth(n.Entry.Path) : Depth(n.Entry.Path))
            .ThenBy(n => n.Entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
        return string.CompareOrdinal(remote.Hash, local.Hash) > 0;
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
            RequestScan();
    }

    void Flush(List<FileEntry> adopted)
    {
        if (adopted.Count == 0)
            return;
        SaveIndex();
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
            // Changed locally after the last scan; the next scan turns it into a proper conflict.
            scanRequested = true;
            pullRequested = true;
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
                scanRequested = true;
                pullRequested = true;
                return default;
            }
            string? copy = null;
            if (conflict && l is { IsFile: true } && File.Exists(full))
            {
                copy = ConflictPath(full);
                File.Move(full, copy);
            }
            if (Directory.Exists(full))
            {
                RemoveDirectory(full);
                if (Directory.Exists(full))
                    throw new IOException("A folder with the same name is in the way.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.Move(tmp, full, true);
            if (r.ModifiedTicks >= MinTicks)
                File.SetLastWriteTimeUtc(full, new DateTime(r.ModifiedTicks, DateTimeKind.Utc));
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
        var tmp = Path.Combine(Root, MetaDir, "tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
        var queue = new Queue<Task<byte[]?>>();
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true))
            {
                long next = 0;
                while (next < r.Size || queue.Count > 0)
                {
                    while (queue.Count < Pipeline && next < r.Size)
                    {
                        int length = (int)Math.Min(ChunkSize, r.Size - next);
                        queue.Enqueue(conn.RequestAsync(Id, r.Path, r.Hash!, next, length, ct));
                        next += length;
                    }
                    var data = await queue.Dequeue() ?? throw new IOException("The file changed on the other device.");
                    await fs.WriteAsync(data, ct);
                    hash.AppendData(data);
                    Interlocked.Add(ref doneBytes, data.Length);
                    Interlocked.Add(ref bytesIn, data.Length);
                    engine.CountIn(data.Length);
                }
            }
            if (Convert.ToHexStringLower(hash.GetHashAndReset()) != r.Hash)
                throw new IOException("The received file is damaged (checksum mismatch).");
            return tmp;
        }
        catch
        {
            foreach (var t in queue)
                _ = t.ContinueWith(x => x.Exception, TaskContinuationOptions.OnlyOnFaulted);
            TryDelete(tmp);
            throw;
        }
    }

    internal (byte[]? Data, long FileSize) ServeBlock(string path, string hash, long offset, int size)
    {
        FileEntry? e;
        lock (gate)
            index.TryGetValue(path, out e);
        if (e is not { IsFile: true } || e.Hash != hash || offset < 0 || offset + size > e.Size)
            return (null, 0);
        var full = FullPath(path);
        var fi = new FileInfo(full);
        if (!fi.Exists || fi.Length != e.Size || fi.LastWriteTimeUtc.Ticks != e.ModifiedTicks)
        {
            RequestScan();
            return (null, 0);
        }
        var buffer = new byte[size];
        using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            fs.Position = offset;
            fs.ReadExactly(buffer);
        }
        lastSendUtc = DateTime.UtcNow;
        Interlocked.Add(ref bytesOut, size);
        return (buffer, e.Size);
    }

    FileEntry Adopt(FileEntry e)
    {
        lock (gate)
        {
            index[e.Path] = e;
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

    void SaveIndex()
    {
        byte[] data;
        lock (gate)
        {
            if (!indexDirty)
                return;
            data = JsonSerializer.SerializeToUtf8Bytes(index.Values, Json.Options);
            indexDirty = false;
        }
        AtomicFile.Write(indexPath, data);
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
                debounce.Change(1000, Timeout.Infinite);
            };
            w.EnableRaisingEvents = true;
            watcher = w;
        }
        catch (Exception ex)
        {
            engine.Log($"{Config.Name}: can't watch folder: {ex.Message}");
        }
    }

    void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        var name = e.Name ?? "";
        if (name.Equals(MetaDir, StringComparison.OrdinalIgnoreCase) || name.StartsWith(MetaDir + "\\", StringComparison.OrdinalIgnoreCase))
            return;
        debounce.Change(1000, Timeout.Infinite);
    }

    void DropWatcher()
    {
        watcher?.Dispose();
        watcher = null;
    }

    void CreateMeta()
    {
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

    static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
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
        DropWatcher();
        try { SaveIndex(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
