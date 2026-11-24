using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using FolderBridge.Core;
using FolderBridge.Core.Sync;

namespace FolderBridge;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

sealed class RateMeter
{
    long last = -1;
    DateTime at;
    double rate;

    public double Update(long total)
    {
        var now = DateTime.UtcNow;
        if (last >= 0 && total >= last)
        {
            var seconds = (now - at).TotalSeconds;
            if (seconds > 0.05)
                rate = rate * 0.5 + (total - last) / seconds * 0.5;
        }
        last = total;
        at = now;
        return rate < 1 ? 0 : rate;
    }
}

public sealed class ActivityVm
{
    public ActivityVm(ActivityEntry e)
    {
        Entry = e;
        Time = e.Time.ToString("HH:mm");
        (Action, Icon) = e.Kind switch
        {
            ActivityKind.Received => ("Received", Icons.Down),
            ActivityKind.Sent => ("Sent", Icons.Up),
            ActivityKind.Trashed => ("To trash", Icons.Trash),
            ActivityKind.Conflict => ("Conflict copy", Icons.Warn),
            ActivityKind.Error => ("Error", Icons.Error),
            _ => (e.Text, (ImageSource)Icons.Monitor),
        };
        File = e.Path?.Replace('/', '\\') ?? "";
        Device = e.DeviceName ?? "";
        Size = e.Size > 0 && e.Kind != ActivityKind.Device ? Format.Bytes(e.Size) : "";
        Details = e.Kind == ActivityKind.Error ? e.Text : File;
    }

    public ActivityEntry Entry { get; }
    public string Time { get; }
    public string Action { get; }
    public ImageSource Icon { get; }
    public string File { get; }
    public string Device { get; }
    public string Size { get; }
    public string Details { get; }
}

// Newest first and capped; a burst of entries lands as one change so the list lays out once.
public sealed class ActivityList : ObservableCollection<ActivityVm>
{
    const int Limit = 300;

    public void Prepend(IEnumerable<ActivityVm> newestLast)
    {
        var added = newestLast.Reverse().ToList();
        if (added.Count == 0)
            return;
        if (added.Count == 1 && Count < Limit)
        {
            Insert(0, added[0]);
            return;
        }
        var merged = added.Concat(Items).Take(Limit).ToList();
        Items.Clear();
        foreach (var item in merged)
            Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}

public sealed class ShareVm : Observable
{
    readonly Engine engine;
    readonly string folderId;
    bool isShared;
    string name;

    public ShareVm(Engine engine, string folderId, DeviceId device, string name, bool shared)
    {
        this.engine = engine;
        this.folderId = folderId;
        Device = device;
        this.name = name;
        isShared = shared;
    }

    public DeviceId Device { get; }

    public string Name
    {
        get => name;
        set => Set(ref name, value);
    }

    public bool IsShared
    {
        get => isShared;
        set
        {
            if (Set(ref isShared, value))
                engine.SetShared(folderId, Device, value);
        }
    }

    public void Refresh(bool shared)
    {
        if (isShared != shared)
        {
            isShared = shared;
            Raise(nameof(IsShared));
        }
    }
}

public sealed class FolderVm : Observable
{
    readonly Engine engine;
    readonly RateMeter rate = new();
    string name = "", path = "", statusLine = "", files = "", size = "", remaining = "", speed = "", percentText = "";
    double percent;
    bool paused, hasNoShares;
    ImageSource statusIcon = Icons.Offline;

    public FolderVm(Engine engine, string id)
    {
        this.engine = engine;
        Id = id;
    }

    public string Id { get; }
    public ObservableCollection<ShareVm> Shares { get; } = [];
    public ActivityList Activity { get; } = new();

    public string Name { get => name; private set => Set(ref name, value); }
    public string Path { get => path; private set => Set(ref path, value); }
    public string StatusLine { get => statusLine; private set => Set(ref statusLine, value); }
    public string Files { get => files; private set => Set(ref files, value); }
    public string Size { get => size; private set => Set(ref size, value); }
    public string Remaining { get => remaining; private set => Set(ref remaining, value); }
    public string Speed { get => speed; private set => Set(ref speed, value); }
    public string PercentText { get => percentText; private set => Set(ref percentText, value); }
    public double Percent { get => percent; private set => Set(ref percent, value); }
    public ImageSource StatusIcon { get => statusIcon; private set => Set(ref statusIcon, value); }
    public bool HasNoShares { get => hasNoShares; private set => Set(ref hasNoShares, value); }

    public bool Paused
    {
        get => paused;
        private set
        {
            if (Set(ref paused, value))
            {
                Raise(nameof(PauseText));
                Raise(nameof(PauseIcon));
            }
        }
    }

    public string PauseText => Paused ? "Resume" : "Pause";
    public ImageSource PauseIcon => Paused ? Icons.Play : Icons.Pause;


    public void Update(FolderSnapshot s, IReadOnlyList<DeviceSnapshot> devices)
    {
        Name = s.Name;
        Path = s.Path;
        Paused = s.Paused;
        Files = Format.Count(s.LocalFiles);
        Size = Format.Bytes(s.LocalBytes);
        var syncing = s.State == FolderState.Syncing;
        Remaining = Format.Bytes(syncing ? Math.Max(0, s.NeedBytes - s.DoneBytes) : 0);
        Speed = Format.Speed(rate.Update(s.BytesIn + s.BytesOut));

        string Names(IEnumerable<DeviceId> ids) =>
            string.Join(", ", ids.Select(id => devices.FirstOrDefault(d => d.Id == id)?.Name ?? id.Grouped[..7]));

        var waiting = s.SharedWith.Where(id => !s.ActivePeers.Contains(id)).ToList();
        var notAccepted = waiting.Where(id => devices.Any(d => d.Id == id && d.Connected)).ToList();
        double pct = 0;
        (ImageSource icon, string line) = s switch
        {
            { Paused: true } => (Icons.Paused, "Paused. Changes on this computer are not sent or received."),
            { State: FolderState.Error } => (Icons.Error, s.Error ?? "Something went wrong."),
            { State: FolderState.Syncing } => (Icons.Sync, s.NeedFiles > 0
                ? $"Receiving {Math.Min(s.DoneFiles + 1, s.NeedFiles)} of {s.NeedFiles} files from {Names(s.CurrentPeer is { } p ? [p] : s.ActivePeers)}"
                : "Applying folder changes"),
            { State: FolderState.Scanning } => (Icons.Sync, "Looking for changes..."),
            { Sending: true } => (Icons.Sync, $"Sending files to {Names(s.ActivePeers)}"),
            { SharedWith.Count: 0 } => (Icons.Offline, "Not shared yet. Tick a computer under Shared with."),
            { ActivePeers.Count: 0 } when notAccepted.Count > 0 => (Icons.Offline, $"Waiting for {Names(notAccepted)} to accept the folder"),
            { ActivePeers.Count: 0 } => (Icons.Offline, $"Waiting for {Names(waiting)} to come online"),
            { FailedFiles: > 0 } => (Icons.Warn, $"{s.FailedFiles} files could not be synced. Retrying every minute."),
            { Conflicts: > 0 } => (Icons.Warn, $"Up to date. {s.Conflicts} conflict {(s.Conflicts == 1 ? "copy needs" : "copies need")} a look."),
            _ => (Icons.Ok, $"Up to date with {Names(s.ActivePeers)}"),
        };
        if (syncing)
            pct = s.NeedBytes > 0 ? 100.0 * s.DoneBytes / s.NeedBytes : 0;
        else if (s.ActivePeers.Count > 0 && !s.Paused && s.State != FolderState.Error)
            pct = 100;
        StatusIcon = icon;
        StatusLine = line;
        Percent = Math.Clamp(pct, 0, 100);
        PercentText = $"{Math.Floor(Percent)}%";

        var paired = devices.ToList();
        for (int i = Shares.Count - 1; i >= 0; i--)
            if (paired.All(d => d.Id != Shares[i].Device))
                Shares.RemoveAt(i);
        foreach (var d in paired)
        {
            var share = Shares.FirstOrDefault(x => x.Device == d.Id);
            if (share == null)
                Shares.Add(new ShareVm(engine, Id, d.Id, d.Name, s.SharedWith.Contains(d.Id)));
            else
            {
                share.Name = d.Name;
                share.Refresh(s.SharedWith.Contains(d.Id));
            }
        }
        HasNoShares = Shares.Count == 0;
    }
}

public sealed class DeviceVm : Observable
{
    string name = "", sub = "", status = "", address = "", folders = "";
    bool isPaired, connected;
    ImageSource icon = Icons.Monitor;

    public DeviceVm(DeviceId id) => Id = id;

    public DeviceId Id { get; }
    public string IdText => Id.Grouped;
    public IPEndPoint? EndPoint { get; private set; }
    public ActivityList Activity { get; } = new();

    public string Name { get => name; private set => Set(ref name, value); }
    public string Sub { get => sub; private set => Set(ref sub, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public string Address { get => address; private set => Set(ref address, value); }
    public string Folders { get => folders; private set => Set(ref folders, value); }
    public bool IsPaired { get => isPaired; private set => Set(ref isPaired, value); }
    public bool Connected { get => connected; private set => Set(ref connected, value); }
    public ImageSource Icon { get => icon; private set => Set(ref icon, value); }


    public void Update(DeviceSnapshot d, IEnumerable<FolderSnapshot> allFolders)
    {
        SetName(d.Name);
        IsPaired = true;
        Connected = d.Connected;
        Address = d.Address ?? "Unknown";
        Sub = d.Connected ? $"Online, {d.Address}" : "Offline";
        Status = d.Connected ? "Connected" : "Offline. It connects by itself when both computers are on the same network.";
        var shared = allFolders.Where(f => f.SharedWith.Contains(d.Id)).Select(f => f.Name).ToList();
        Folders = shared.Count > 0 ? string.Join(", ", shared) : "None yet";
    }

    public void Update(NearbySnapshot n)
    {
        SetName(n.Name);
        IsPaired = false;
        Connected = false;
        EndPoint = n.EndPoint;
        Address = n.EndPoint.Address.ToString();
        Sub = "Nearby, not paired";
        Status = "Found on this network. Pair with it to share folders.";
        Folders = "None";
    }

    void SetName(string value)
    {
        Name = value;
        var portable = value.Contains("LAPTOP", StringComparison.OrdinalIgnoreCase)
            || value.Contains("NOTEBOOK", StringComparison.OrdinalIgnoreCase)
            || value.Contains("BOOK", StringComparison.OrdinalIgnoreCase);
        Icon = portable ? Icons.Laptop : Icons.Monitor;
    }
}

public sealed class OfferVm(FolderOffer offer)
{
    public FolderOffer Offer { get; } = offer;
    public string Text => $"Accept \"{Offer.FolderName}\" from {Offer.DeviceName}";
}

public sealed class WelcomeVm;

public sealed class MainViewModel : Observable
{
    readonly Engine engine;
    readonly RateMeter down = new(), up = new();
    string statusText = "", downText = "", upText = "", deviceName = "";
    bool hasNoFolders, hasNoDevices;
    object? detail;

    public MainViewModel(Engine engine) => this.engine = engine;

    public ObservableCollection<FolderVm> Folders { get; } = [];
    public ObservableCollection<DeviceVm> Devices { get; } = [];
    public ObservableCollection<OfferVm> Offers { get; } = [];
    public WelcomeVm Welcome { get; } = new();
    public EngineSnapshot? Last { get; private set; }

    public object? Detail { get => detail; set => Set(ref detail, value); }
    public string StatusText { get => statusText; private set => Set(ref statusText, value); }
    public string DownText { get => downText; private set => Set(ref downText, value); }
    public string UpText { get => upText; private set => Set(ref upText, value); }
    public string DeviceName { get => deviceName; private set => Set(ref deviceName, value); }
    public bool HasNoFolders { get => hasNoFolders; private set => Set(ref hasNoFolders, value); }
    public bool HasNoDevices { get => hasNoDevices; private set => Set(ref hasNoDevices, value); }

    public void Update(EngineSnapshot s)
    {
        Last = s;
        DeviceName = s.DeviceName;
        StatusText = s.ListenError ?? $"Ready on port {s.Port}";
        DownText = "Down: " + Format.Speed(down.Update(s.BytesIn));
        UpText = "Up: " + Format.Speed(up.Update(s.BytesOut));

        SyncList(Folders, s.Folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            f => f.Id, vm => vm.Id, f => new FolderVm(engine, f.Id), (vm, f) => vm.Update(f, s.Devices));

        var devices = s.Devices.OrderBy(d => d.Name).Select(d => (Key: d.Id, Paired: (DeviceSnapshot?)d, Nearby: (NearbySnapshot?)null))
            .Concat(s.Nearby.Select(n => (Key: n.Id, Paired: (DeviceSnapshot?)null, Nearby: (NearbySnapshot?)n)))
            .ToList();
        SyncList(Devices, devices, d => d.Key.Value, vm => vm.Id.Value, d => new DeviceVm(d.Key), (vm, d) =>
        {
            if (d.Paired != null)
                vm.Update(d.Paired, s.Folders);
            else
                vm.Update(d.Nearby!);
        });

        SyncList(Offers, s.Offers.ToList(), o => o.FolderId + o.DeviceId, vm => vm.Offer.FolderId + vm.Offer.DeviceId, o => new OfferVm(o), (_, _) => { });

        HasNoFolders = Folders.Count == 0;
        HasNoDevices = Devices.Count == 0;
        if (Detail is FolderVm f && !Folders.Contains(f) || Detail is DeviceVm d && !Devices.Contains(d) || Detail == null
            || Detail is WelcomeVm && Folders.Count > 0)
            Detail = Folders.FirstOrDefault() ?? (object)Welcome;
    }

    public void AddActivity(IReadOnlyList<ActivityEntry> entries)
    {
        if (entries.Count == 0)
            return;
        var items = entries.Select(e => new ActivityVm(e)).ToList();
        foreach (var folder in Folders)
            folder.Activity.Prepend(items.Where(i => i.Entry.FolderId == folder.Id));
        foreach (var device in Devices)
            device.Activity.Prepend(items.Where(i => i.Entry.Device == device.Id));
    }

    static void SyncList<TVm, TItem>(ObservableCollection<TVm> target, IList<TItem> source, Func<TItem, string> key,
        Func<TVm, string> vmKey, Func<TItem, TVm> create, Action<TVm, TItem> update)
    {
        var keys = source.Select(key).ToHashSet();
        for (int i = target.Count - 1; i >= 0; i--)
            if (!keys.Contains(vmKey(target[i])))
                target.RemoveAt(i);
        for (int i = 0; i < source.Count; i++)
        {
            var k = key(source[i]);
            int index = -1;
            for (int j = 0; j < target.Count; j++)
                if (vmKey(target[j]) == k)
                {
                    index = j;
                    break;
                }
            TVm vm;
            if (index < 0)
            {
                vm = create(source[i]);
                target.Insert(i, vm);
            }
            else
            {
                vm = target[index];
                if (index != i)
                    target.Move(index, i);
            }
            update(vm, source[i]);
        }
    }
}
