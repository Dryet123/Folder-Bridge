using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using FolderBridge.Core.Net;
using FolderBridge.Core.Sync;

namespace FolderBridge.Core;

public sealed class EngineOptions
{
    public IPAddress ListenAddress { get; init; } = IPAddress.Any;
    public bool Discovery { get; init; } = true;
}

public sealed class Engine : IDisposable
{
    const int IndexBatch = 1000;

    readonly object gate = new();
    readonly object logGate = new();
    readonly string configPath;
    readonly string logPath;
    readonly EngineOptions options;
    readonly AppConfig config;
    readonly Dictionary<string, FolderSync> folders = [];
    readonly Dictionary<DeviceId, Connection> connections = [];
    readonly Dictionary<DeviceId, HashSet<string>> peerFolders = [];
    readonly Dictionary<DeviceId, HashSet<string>> activeFolders = [];
    readonly Dictionary<DeviceId, NearbyDevice> nearby = [];
    readonly HashSet<DeviceId> connecting = [];
    readonly List<FolderOffer> offers = [];
    readonly CancellationTokenSource cts = new();
    TcpListener? listener;
    Discovery? discovery;
    Timer? timer;
    PairingSession? incomingSession;
    long bytesIn, bytesOut;
    int ticks;

    sealed record NearbyDevice(DeviceId Id, string Name, IPEndPoint EndPoint, DateTime Seen);

    public string DataDir { get; }
    public Identity Identity { get; }
    public DeviceId Id => Identity.Id;
    public bool FirstRun { get; }
    public ActivityLog Activity { get; } = new();
    public string? ListenError { get; private set; }

    public event Action<PairingSession>? PairingRequested;
    public event Action<FolderOffer>? FolderOffered;

    public Engine(string dataDir, EngineOptions? options = null)
    {
        DataDir = dataDir;
        this.options = options ?? new EngineOptions();
        Directory.CreateDirectory(Path.Combine(dataDir, "index"));
        logPath = Path.Combine(dataDir, "folderbridge.log");
        try
        {
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 2_000_000)
                File.Delete(logPath);
        }
        catch (IOException)
        {
        }
        Identity = Identity.LoadOrCreate(Path.Combine(dataDir, "device.pfx"));
        configPath = Path.Combine(dataDir, "config.json");
        config = AppConfig.Load(configPath, out var created);
        FirstRun = created;
        if (created)
            SaveConfig();
    }

    public string DeviceName
    {
        get { lock (gate) return config.DeviceName; }
    }

    public int ListenPort => (listener?.LocalEndpoint as IPEndPoint)?.Port ?? config.ListenPort;

    public void Start()
    {
        lock (gate)
            foreach (var fc in config.Folders)
                StartFolder(fc);
        StartListener();
        if (options.Discovery)
        {
            discovery = new Discovery(Id, () => (DeviceName, ListenPort), Log);
            discovery.Seen += OnSeen;
            discovery.Start();
        }
        timer = new Timer(_ => Tick(), null, 500, 5000);
        Log($"Started as {DeviceName} ({Id.Grouped}) on port {ListenPort}");
    }

    internal void Log(string message)
    {
        lock (logGate)
        {
            try { File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}"); }
            catch (IOException) { }
        }
    }

    void SaveConfig()
    {
        lock (gate)
            config.Save(configPath);
    }

    // ---------- Folders ----------

    void StartFolder(FolderConfig fc)
    {
        var folder = new FolderSync(this, fc, Path.Combine(DataDir, "index", fc.Id + ".json"));
        folders[fc.Id] = folder;
        folder.Start();
    }

    public FolderConfig AddFolder(string name, string path, IEnumerable<DeviceId> devices) =>
        AddFolder(new FolderConfig
        {
            Id = NewFolderId(),
            Name = name.Trim(),
            Path = path,
            Devices = devices.Select(d => d.Value).ToList(),
        });

    FolderConfig AddFolder(FolderConfig fc)
    {
        if (string.IsNullOrWhiteSpace(fc.Name))
            throw new ArgumentException("Give the folder a name.");
        if (string.IsNullOrWhiteSpace(fc.Path) || !Path.IsPathFullyQualified(fc.Path))
            throw new ArgumentException("Choose a folder on this computer.");
        fc.Path = Path.GetFullPath(fc.Path).TrimEnd('\\');
        if (Path.GetPathRoot(fc.Path)!.TrimEnd('\\') == fc.Path)
            throw new ArgumentException("Choose a folder, not a whole drive.");
        var path = Norm(fc.Path);
        if (path.StartsWith(Norm(DataDir), StringComparison.OrdinalIgnoreCase) || Norm(DataDir).StartsWith(path, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("That folder holds Folder Bridge's own settings.");
        lock (gate)
        {
            if (folders.ContainsKey(fc.Id))
                throw new ArgumentException("This folder is already synced.");
            foreach (var other in config.Folders)
            {
                var o = Norm(other.Path);
                if (o.Equals(path, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"This folder is already synced as \"{other.Name}\".");
                if (path.StartsWith(o, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"This folder is inside \"{other.Name}\", which is already synced.");
                if (o.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"This folder contains \"{other.Name}\", which is already synced.");
            }
            Directory.CreateDirectory(fc.Path);
            config.Folders.Add(fc);
            offers.RemoveAll(o => o.FolderId == fc.Id);
            SaveConfig();
            StartFolder(fc);
        }
        Log($"Added folder {fc.Name} at {fc.Path}");
        ConfigChanged();
        return fc;
    }

    static string Norm(string path) => Path.GetFullPath(path).TrimEnd('\\') + '\\';

    static string NewFolderId() => Base32.Encode(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant().Insert(8, "-");

    public void RemoveFolder(string id)
    {
        FolderSync? folder;
        lock (gate)
        {
            if (!folders.Remove(id, out folder))
                return;
            config.Folders.Remove(folder.Config);
            foreach (var set in activeFolders.Values)
                set.Remove(id);
            SaveConfig();
        }
        folder.Dispose();
        try { File.Delete(Path.Combine(DataDir, "index", id + ".json")); }
        catch (IOException) { }
        Log($"Removed folder {folder.Config.Name}");
        ConfigChanged();
    }

    public void SetPaused(string id, bool paused)
    {
        FolderSync? folder;
        lock (gate)
        {
            if (!folders.TryGetValue(id, out folder))
                return;
            folder.Config.Paused = paused;
            SaveConfig();
        }
        folder.RequestScan();
    }

    public void SetAllPaused(bool paused)
    {
        List<string> ids;
        lock (gate)
            ids = folders.Keys.ToList();
        foreach (var id in ids)
            SetPaused(id, paused);
    }

    public void SetShared(string folderId, DeviceId device, bool shared)
    {
        lock (gate)
        {
            if (!folders.TryGetValue(folderId, out var folder))
                return;
            var list = folder.Config.Devices;
            if (shared && !list.Contains(device.Value))
                list.Add(device.Value);
            else if (!shared)
                list.Remove(device.Value);
            else
                return;
            SaveConfig();
        }
        ConfigChanged();
    }

    public void Rescan(string? folderId = null)
    {
        List<FolderSync> targets;
        lock (gate)
            targets = folders.Values.Where(f => folderId == null || f.Id == folderId).ToList();
        foreach (var f in targets)
            f.RequestScan();
        if (folderId == null)
            Tick();
    }

    public void AcceptOffer(FolderOffer offer, string path)
    {
        AddFolder(new FolderConfig
        {
            Id = offer.FolderId,
            Name = offer.FolderName,
            Path = path,
            Devices = [offer.DeviceId.Value],
        });
    }

    public void IgnoreOffer(FolderOffer offer)
    {
        lock (gate)
        {
            offers.RemoveAll(o => o.FolderId == offer.FolderId);
            if (!config.IgnoredOffers.Contains(offer.FolderId))
                config.IgnoredOffers.Add(offer.FolderId);
            SaveConfig();
        }
    }

    // ---------- Devices and settings ----------

    public void UpdateSettings(string deviceName, int port)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new ArgumentException("The computer name can't be empty.");
        if (port is < 1024 or > 65535)
            throw new ArgumentException("The port must be between 1024 and 65535.");
        bool portChanged;
        List<Connection> conns;
        lock (gate)
        {
            portChanged = config.ListenPort != port;
            config.DeviceName = deviceName.Trim();
            config.ListenPort = port;
            SaveConfig();
            conns = connections.Values.ToList();
        }
        if (portChanged)
        {
            listener?.Stop();
            StartListener();
        }
        foreach (var c in conns)
            _ = c.TrySendAsync(MessageType.Hello, new HelloMessage(DeviceName, ListenPort));
        discovery?.AnnounceNow();
    }

    public void Unpair(DeviceId id)
    {
        RemoveDevice(id);
        Connection? conn;
        lock (gate)
            connections.TryGetValue(id, out conn);
        if (conn != null)
            _ = conn.TrySendAsync(MessageType.Unpair, new { }).ContinueWith(_ => conn.Close());
    }

    void RemoveDevice(DeviceId id)
    {
        string? name;
        lock (gate)
        {
            var device = config.Devices.FirstOrDefault(d => d.Id == id.Value);
            if (device == null)
                return;
            name = device.Name;
            config.Devices.Remove(device);
            foreach (var f in config.Folders)
                f.Devices.Remove(id.Value);
            offers.RemoveAll(o => o.DeviceId == id);
            SaveConfig();
        }
        Activity.Add(new ActivityEntry(DateTime.Now, ActivityKind.Device, "Unpaired", Device: id, DeviceName: name));
        ConfigChanged();
    }

    bool IsPaired(DeviceId id)
    {
        lock (gate)
            return config.Devices.Any(d => d.Id == id.Value);
    }

    bool Shares(FolderConfig fc, DeviceId peer) => fc.Devices.Contains(peer.Value) && IsPaired(peer);

    internal bool IsConnected(DeviceId id)
    {
        lock (gate)
            return connections.ContainsKey(id);
    }

    internal Connection? GetConnection(DeviceId id)
    {
        lock (gate)
            return connections.GetValueOrDefault(id);
    }

    internal string PeerName(DeviceId id)
    {
        lock (gate)
            return config.Devices.FirstOrDefault(d => d.Id == id.Value)?.Name ?? id.Grouped[..7];
    }

    internal void CountIn(int bytes) => Interlocked.Add(ref bytesIn, bytes);

    // ---------- Pairing ----------

    public async Task<PairingSession> StartPairingAsync(IPEndPoint endPoint, string? knownName = null, CancellationToken ct = default)
    {
        var conn = await Connection.ConnectAsync(endPoint, Identity.Certificate, ct);
        if (conn.PeerId == Id)
        {
            conn.Close();
            throw new InvalidOperationException("That address is this computer.");
        }
        var session = new PairingSession(this, conn, false, knownName ?? endPoint.Address.ToString(), endPoint);
        conn.OnMessage = (_, type, payload) => session.HandleAsync(type, payload);
        conn.Closed += _ => session.OnClosed();
        _ = conn.RunAsync();
        await session.BeginAsync();
        return session;
    }

    void WaitForPairing(Connection conn)
    {
        PairingSession? session = null;
        conn.OnMessage = async (c, type, payload) =>
        {
            if (session == null)
            {
                if (type != MessageType.PairRequest)
                {
                    c.Close();
                    return;
                }
                session = BeginIncoming(c);
                if (session == null)
                    return;
            }
            await session.HandleAsync(type, payload);
        };
        _ = conn.RunAsync();
        _ = Task.Delay(30000).ContinueWith(_ =>
        {
            if (session == null)
                conn.Close();
        });
    }

    PairingSession? BeginIncoming(Connection conn)
    {
        lock (gate)
        {
            if (incomingSession is { State: PairingState.Waiting })
            {
                conn.Close();
                return null;
            }
            incomingSession = new PairingSession(this, conn, true, "", null);
        }
        var session = incomingSession;
        conn.Closed += _ => session.OnClosed();
        _ = Task.Delay(TimeSpan.FromMinutes(3)).ContinueWith(_ =>
        {
            if (session.State == PairingState.Waiting)
                session.Cancel();
        });
        return session;
    }

    internal void RaisePairingRequested(PairingSession session) => PairingRequested?.Invoke(session);

    internal void CompletePairing(PairingSession session)
    {
        lock (gate)
        {
            config.Devices.RemoveAll(d => d.Id == session.PeerId.Value);
            config.Devices.Add(new DeviceConfig
            {
                Id = session.PeerId.Value,
                Name = session.PeerName,
                LastAddress = session.PeerAddress?.ToString(),
            });
            SaveConfig();
        }
        Log($"Paired with {session.PeerName} ({session.PeerId.Grouped})");
        Activity.Add(new ActivityEntry(DateTime.Now, ActivityKind.Device, "Paired", Device: session.PeerId, DeviceName: session.PeerName));
        _ = Task.Delay(500).ContinueWith(_ => Tick());
    }

    // ---------- Connections ----------

    void StartListener()
    {
        try
        {
            var l = new TcpListener(options.ListenAddress, config.ListenPort);
            l.Start();
            listener = l;
            ListenError = null;
            _ = AcceptLoop(l);
        }
        catch (SocketException ex)
        {
            listener = null;
            ListenError = $"Port {config.ListenPort} is in use by another program";
            Log($"Can't listen on port {config.ListenPort}: {ex.Message}");
        }
    }

    async Task AcceptLoop(TcpListener l)
    {
        while (!cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await l.AcceptTcpClientAsync(cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = HandleIncoming(client);
        }
    }

    async Task HandleIncoming(TcpClient client)
    {
        Connection conn;
        try
        {
            conn = await Connection.AcceptAsync(client, Identity.Certificate, cts.Token);
        }
        catch (Exception ex)
        {
            Log($"Handshake with {client.Client.RemoteEndPoint} failed: {ex.Message}");
            client.Dispose();
            return;
        }
        if (IsPaired(conn.PeerId))
            Attach(conn);
        else
            WaitForPairing(conn);
    }

    async Task ConnectAsync(DeviceId id, List<IPEndPoint> endPoints)
    {
        try
        {
            foreach (var ep in endPoints)
            {
                try
                {
                    var conn = await Connection.ConnectAsync(ep, Identity.Certificate, cts.Token);
                    if (conn.PeerId != id || !IsPaired(id))
                    {
                        conn.Close();
                        continue;
                    }
                    Attach(conn);
                    return;
                }
                catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
                {
                }
            }
        }
        finally
        {
            lock (gate)
                connecting.Remove(id);
        }
    }

    void Attach(Connection conn)
    {
        Connection? replaced = null;
        lock (gate)
        {
            var device = config.Devices.FirstOrDefault(d => d.Id == conn.PeerId.Value);
            if (device == null)
            {
                conn.Close();
                return;
            }
            conn.PeerName = device.Name;
            if (connections.TryGetValue(conn.PeerId, out var existing))
            {
                // Both sides may dial at once. Keep the connection opened by the device with the smaller ID,
                // which both sides agree on without talking.
                if (Preferred(existing) && !Preferred(conn))
                {
                    conn.Close();
                    return;
                }
                replaced = existing;
            }
            connections[conn.PeerId] = conn;
        }
        if (replaced != null)
        {
            Detach(replaced);
            replaced.Close();
        }
        conn.OnMessage = HandleMessage;
        conn.Closed += OnClosed;
        _ = conn.RunAsync();
        _ = Greet(conn);
        Log($"Connected to {conn.PeerName} at {conn.RemoteEndPoint}");
        if (replaced == null)
            Activity.Add(new ActivityEntry(DateTime.Now, ActivityKind.Device, "Connected", Device: conn.PeerId, DeviceName: conn.PeerName));
    }

    bool Preferred(Connection c) => c.IsClient == (string.CompareOrdinal(Id.Value, c.PeerId.Value) < 0);

    async Task Greet(Connection conn)
    {
        try
        {
            await conn.SendAsync(MessageType.Hello, new HelloMessage(DeviceName, ListenPort));
            await SendClusterConfig(conn);
        }
        catch
        {
        }
    }

    Task SendClusterConfig(Connection conn)
    {
        List<SharedFolder> shared;
        lock (gate)
            shared = config.Folders.Where(f => Shares(f, conn.PeerId)).Select(f => new SharedFolder(f.Id, f.Name)).ToList();
        return conn.SendAsync(MessageType.ClusterConfig, new ClusterConfigMessage(shared));
    }

    void OnClosed(Connection conn)
    {
        bool removed = Detach(conn);
        if (removed)
        {
            Log($"Disconnected from {conn.PeerName}");
            Activity.Add(new ActivityEntry(DateTime.Now, ActivityKind.Device, "Disconnected", Device: conn.PeerId, DeviceName: conn.PeerName));
        }
    }

    bool Detach(Connection conn)
    {
        List<FolderSync> affected;
        lock (gate)
        {
            if (!connections.TryGetValue(conn.PeerId, out var current) || current != conn)
                return false;
            connections.Remove(conn.PeerId);
            peerFolders.Remove(conn.PeerId);
            activeFolders.Remove(conn.PeerId);
            affected = folders.Values.ToList();
        }
        foreach (var f in affected)
            f.RemovePeer(conn.PeerId);
        return true;
    }

    async Task HandleMessage(Connection conn, MessageType type, byte[] payload)
    {
        try
        {
            switch (type)
            {
                case MessageType.Hello:
                    OnHello(conn, Connection.Read<HelloMessage>(payload));
                    break;
                case MessageType.ClusterConfig:
                    await OnClusterConfig(conn, Connection.Read<ClusterConfigMessage>(payload));
                    break;
                case MessageType.Index:
                    OnIndex(conn, Connection.Read<IndexMessage>(payload));
                    break;
                case MessageType.Request:
                    var request = Connection.Read<RequestMessage>(payload);
                    _ = Task.Run(() => Serve(conn, request));
                    break;
                case MessageType.Unpair:
                    RemoveDevice(conn.PeerId);
                    conn.Close();
                    break;
                case MessageType.PairRequest:
                    // The other side forgot us and wants to pair again over this connection.
                    Detach(conn);
                    var session = BeginIncoming(conn);
                    if (session != null)
                    {
                        conn.OnMessage = (_, t, p) => session.HandleAsync(t, p);
                        await session.HandleAsync(type, payload);
                    }
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            Log($"Bad message from {conn.PeerName}: {ex.Message}");
            conn.Close();
        }
    }

    void OnHello(Connection conn, HelloMessage m)
    {
        var name = m.Name.Length > 64 ? m.Name[..64] : m.Name;
        conn.PeerName = name;
        lock (gate)
        {
            var device = config.Devices.FirstOrDefault(d => d.Id == conn.PeerId.Value);
            if (device == null)
                return;
            var address = new IPEndPoint(conn.RemoteEndPoint.Address, m.Port).ToString();
            if (device.Name == name && device.LastAddress == address)
                return;
            device.Name = name;
            device.LastAddress = address;
            SaveConfig();
        }
    }

    async Task OnClusterConfig(Connection conn, ClusterConfigMessage m)
    {
        var peer = conn.PeerId;
        var fresh = new List<FolderOffer>();
        lock (gate)
        {
            var ids = m.Folders.Select(f => f.Id).ToHashSet();
            peerFolders[peer] = ids;
            offers.RemoveAll(o => o.DeviceId == peer && !ids.Contains(o.FolderId));
            foreach (var sf in m.Folders)
            {
                if (folders.ContainsKey(sf.Id) || config.IgnoredOffers.Contains(sf.Id) || offers.Any(o => o.FolderId == sf.Id))
                    continue;
                var offer = new FolderOffer(peer, conn.PeerName, sf.Id, sf.Name.Length > 100 ? sf.Name[..100] : sf.Name);
                offers.Add(offer);
                fresh.Add(offer);
            }
        }
        foreach (var offer in fresh)
            FolderOffered?.Invoke(offer);
        await RefreshActive(conn);
    }

    async Task RefreshActive(Connection conn)
    {
        var peer = conn.PeerId;
        List<FolderSync> added, dropped;
        lock (gate)
        {
            if (connections.GetValueOrDefault(peer) != conn)
                return;
            var theirs = peerFolders.GetValueOrDefault(peer) ?? [];
            var now = folders.Values.Where(f => Shares(f.Config, peer) && theirs.Contains(f.Id)).ToList();
            var before = activeFolders.GetValueOrDefault(peer) ?? [];
            added = now.Where(f => !before.Contains(f.Id)).ToList();
            dropped = folders.Values.Where(f => before.Contains(f.Id) && !now.Contains(f)).ToList();
            activeFolders[peer] = now.Select(f => f.Id).ToHashSet();
        }
        foreach (var f in dropped)
            f.RemovePeer(peer);
        foreach (var f in added)
            await SendIndex(conn, f.Id, f.SnapshotIndex());
    }

    void ConfigChanged()
    {
        List<Connection> conns;
        lock (gate)
            conns = connections.Values.ToList();
        foreach (var c in conns)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await SendClusterConfig(c);
                    await RefreshActive(c);
                }
                catch
                {
                }
            });
        }
    }

    void OnIndex(Connection conn, IndexMessage m)
    {
        FolderSync? folder;
        lock (gate)
        {
            if (!folders.TryGetValue(m.Folder, out folder) || !Shares(folder.Config, conn.PeerId))
                return;
        }
        folder.OnRemoteIndex(conn.PeerId, m.Files);
    }

    async Task SendIndex(Connection conn, string folderId, List<FileEntry> entries)
    {
        try
        {
            foreach (var batch in entries.Chunk(IndexBatch))
                await conn.SendAsync(MessageType.Index, new IndexMessage(folderId, batch.ToList()));
        }
        catch
        {
        }
    }

    internal void Broadcast(FolderSync folder, List<FileEntry> changed)
    {
        List<Connection> targets;
        lock (gate)
            targets = connections.Values.Where(c => activeFolders.GetValueOrDefault(c.PeerId)?.Contains(folder.Id) == true).ToList();
        foreach (var c in targets)
            _ = SendIndex(c, folder.Id, changed);
    }

    async Task Serve(Connection conn, RequestMessage req)
    {
        byte[]? data = null;
        FolderSync? folder;
        lock (gate)
            folder = folders.GetValueOrDefault(req.Folder);
        if (folder != null && Shares(folder.Config, conn.PeerId) && req.Size is > 0 and <= FolderSync.ChunkSize)
        {
            try
            {
                var (block, fileSize) = folder.ServeBlock(req.Path, req.Hash, req.Offset, req.Size);
                data = block;
                if (block != null && req.Offset + block.Length >= fileSize)
                    Activity.Add(new ActivityEntry(DateTime.Now, ActivityKind.Sent, "", folder.Id, req.Path, conn.PeerId, conn.PeerName, fileSize));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log($"Can't read {req.Path}: {ex.Message}");
            }
        }
        try
        {
            await conn.RespondAsync(req.Id, data);
            if (data != null)
                Interlocked.Add(ref bytesOut, data.Length);
        }
        catch
        {
        }
    }

    // ---------- Discovery and housekeeping ----------

    void OnSeen(DeviceId id, string name, IPEndPoint endPoint)
    {
        bool connectNow;
        lock (gate)
        {
            nearby[id] = new NearbyDevice(id, name, endPoint, DateTime.UtcNow);
            connectNow = config.Devices.Any(d => d.Id == id.Value) && !connections.ContainsKey(id) && connecting.Add(id);
        }
        if (connectNow)
            _ = ConnectAsync(id, [endPoint]);
    }

    void Tick()
    {
        var now = DateTime.UtcNow;
        var toConnect = new List<(DeviceId Id, List<IPEndPoint> EndPoints)>();
        List<Connection> conns;
        lock (gate)
        {
            foreach (var stale in nearby.Values.Where(n => now - n.Seen > TimeSpan.FromSeconds(20)).ToList())
                nearby.Remove(stale.Id);
            foreach (var d in config.Devices)
            {
                var id = new DeviceId(d.Id);
                if (connections.ContainsKey(id) || connecting.Contains(id))
                    continue;
                var endPoints = new List<IPEndPoint>();
                if (nearby.TryGetValue(id, out var n))
                    endPoints.Add(n.EndPoint);
                if (IPEndPoint.TryParse(d.LastAddress ?? "", out var last) && !endPoints.Contains(last))
                    endPoints.Add(last);
                if (endPoints.Count == 0)
                    continue;
                connecting.Add(id);
                toConnect.Add((id, endPoints));
            }
            conns = connections.Values.ToList();
        }
        foreach (var (id, endPoints) in toConnect)
            _ = ConnectAsync(id, endPoints);

        bool ping = Interlocked.Increment(ref ticks) % 3 == 0;
        foreach (var c in conns)
        {
            if (now - c.LastReceived > TimeSpan.FromSeconds(60))
                c.Close();
            else if (ping)
                _ = c.SendFrameAsync(MessageType.Ping, ReadOnlyMemory<byte>.Empty).ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    public EngineSnapshot Snapshot()
    {
        lock (gate)
        {
            var devices = config.Devices.Select(d =>
            {
                var id = new DeviceId(d.Id);
                var conn = connections.GetValueOrDefault(id);
                var address = conn?.RemoteEndPoint.Address.ToString() ?? (IPEndPoint.TryParse(d.LastAddress ?? "", out var ep) ? ep.Address.ToString() : null);
                return new DeviceSnapshot(id, d.Name, conn != null, address);
            }).ToList();
            var near = nearby.Values
                .Where(n => config.Devices.All(d => d.Id != n.Id.Value))
                .OrderBy(n => n.Name)
                .Select(n => new NearbySnapshot(n.Id, n.Name, n.EndPoint))
                .ToList();
            var folderSnapshots = folders.Values
                .Select(f => f.Snapshot(connections.Keys.Where(p => activeFolders.GetValueOrDefault(p)?.Contains(f.Id) == true).ToList()))
                .ToList();
            return new EngineSnapshot(config.DeviceName, Id, ListenPort, ListenError,
                Interlocked.Read(ref bytesIn), Interlocked.Read(ref bytesOut),
                folderSnapshots, devices, near, offers.ToList());
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        timer?.Dispose();
        listener?.Stop();
        discovery?.Dispose();
        List<Connection> conns;
        List<FolderSync> all;
        lock (gate)
        {
            conns = connections.Values.ToList();
            all = folders.Values.ToList();
        }
        foreach (var c in conns)
            c.Close();
        foreach (var f in all)
            f.Dispose();
    }
}
