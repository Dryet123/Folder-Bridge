using System.Net;
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

public sealed class ActivityLog
{
    const int Capacity = 1000;
    readonly Queue<ActivityEntry> recent = new();
    readonly object gate = new();

    public event Action<ActivityEntry>? Added;

    public void Add(ActivityEntry entry)
    {
        lock (gate)
        {
            recent.Enqueue(entry);
            while (recent.Count > Capacity)
                recent.Dequeue();
        }
        Added?.Invoke(entry);
    }

    public IReadOnlyList<ActivityEntry> Recent()
    {
        lock (gate)
            return recent.ToList();
    }
}

public sealed record FolderOffer(DeviceId DeviceId, string DeviceName, string FolderId, string FolderName);

public sealed record FolderSnapshot(
    string Id,
    string Name,
    string Path,
    FolderState State,
    string? Error,
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
