using System.Net;
using System.Text.Json;
using FolderBridge.Core.Sync;

namespace FolderBridge.Core;

public enum ActivityKind { Received, Sent, Trashed, Conflict, Error, Device }

public sealed record ActivityEntry(
    DateTime Time,
    ActivityKind Kind,
    string Text,
    string? FolderId = null,
    string? Path = null,
    DeviceId? Device = null,
    string? DeviceName = null,
    long Size = 0);

// Keeps the latest entries in memory and appends new ones to disk in batches.
public sealed class ActivityLog : IDisposable
{
    const int Capacity = 1000;
    readonly Queue<ActivityEntry> recent = new();
    readonly List<ActivityEntry> unsaved = [];
    readonly object gate = new();
    readonly string? path;
    readonly Timer flushTimer;
    long total;
    int linesOnDisk;
    bool flushScheduled;

    public ActivityLog(string? path = null)
    {
        this.path = path;
        flushTimer = new Timer(_ => Flush());
        if (path == null || !File.Exists(path))
            return;
        try
        {
            var lines = File.ReadAllLines(path);
            foreach (var line in lines.TakeLast(Capacity))
            {
                try
                {
                    if (JsonSerializer.Deserialize<ActivityEntry>(line, Json.Options) is { } e)
                        recent.Enqueue(e);
                }
                catch (JsonException)
                {
                }
            }
            total = recent.Count;
            linesOnDisk = lines.Length;
            if (lines.Length > Capacity * 2)
            {
                File.WriteAllLines(path, lines.TakeLast(Capacity));
                linesOnDisk = Capacity;
            }
        }
        catch (IOException)
        {
        }
    }

    public void Add(ActivityEntry entry)
    {
        lock (gate)
        {
            recent.Enqueue(entry);
            if (recent.Count > Capacity)
                recent.Dequeue();
            total++;
            if (path == null)
                return;
            unsaved.Add(entry);
            if (!flushScheduled)
            {
                flushScheduled = true;
                flushTimer.Change(2000, Timeout.Infinite);
            }
        }
    }

    public IReadOnlyList<ActivityEntry> Recent()
    {
        lock (gate)
            return recent.ToList();
    }

    // Entries added since the cursor (at most the ones still in memory); moves the cursor to the end.
    public List<ActivityEntry> Since(ref long cursor)
    {
        lock (gate)
        {
            var count = (int)Math.Min(total - cursor, recent.Count);
            cursor = total;
            return count <= 0 ? [] : recent.Skip(recent.Count - count).ToList();
        }
    }

    void Flush()
    {
        List<string> lines;
        bool rewrite;
        lock (gate)
        {
            flushScheduled = false;
            if (unsaved.Count == 0)
                return;
            // A long session can log far more than is kept; rewrite the file with just the recent part now and then.
            rewrite = linesOnDisk + unsaved.Count > Capacity * 3;
            lines = (rewrite ? recent.ToList() : unsaved).Select(e => JsonSerializer.Serialize(e, Json.Options)).ToList();
            unsaved.Clear();
            linesOnDisk = rewrite ? lines.Count : linesOnDisk + lines.Count;
        }
        try
        {
            if (rewrite)
                File.WriteAllLines(path!, lines);
            else
                File.AppendAllLines(path!, lines);
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        flushTimer.Dispose();
        Flush();
    }
}

public sealed record FolderOffer(DeviceId DeviceId, string DeviceName, string FolderId, string FolderName);

public sealed record FolderSnapshot(
    string Id,
    string Name,
    string Path,
    FolderState State,
    string? Error,
    string? WaitReason,
    bool Paused,
    int LocalFiles,
    long LocalBytes,
    int Conflicts,
    int NeedFiles,
    int DoneFiles,
    long NeedBytes,
    long DoneBytes,
    string? CurrentFile,
    DeviceId? CurrentPeer,
    int FailedFiles,
    DateTime? LastSync,
    long BytesIn,
    long BytesOut,
    bool Sending,
    IReadOnlyList<DeviceId> SharedWith,
    IReadOnlyList<DeviceId> ActivePeers);

public sealed record DeviceSnapshot(DeviceId Id, string Name, bool Connected, string? Address);

public sealed record NearbySnapshot(DeviceId Id, string Name, IPEndPoint EndPoint);

public sealed record EngineSnapshot(
    string DeviceName,
    DeviceId DeviceId,
    int Port,
    string? ListenError,
    long BytesIn,
    long BytesOut,
    IReadOnlyList<FolderSnapshot> Folders,
    IReadOnlyList<DeviceSnapshot> Devices,
    IReadOnlyList<NearbySnapshot> Nearby,
    IReadOnlyList<FolderOffer> Offers);
