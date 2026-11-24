using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace FolderBridge.Core.Net;

// Announces this device on the LAN over multicast and broadcast and reports announcements from others.
// Announcements are frequent for a while after start, a network change or a request to look around,
// and slow otherwise; a device that shows up for the first time gets an immediate answer.
public sealed class Discovery : IDisposable
{
    public const int Port = 22027;
    public static readonly TimeSpan SlowInterval = TimeSpan.FromSeconds(15);
    static readonly TimeSpan FastInterval = TimeSpan.FromSeconds(5);
    static readonly TimeSpan FastPeriod = TimeSpan.FromMinutes(2);
    static readonly IPAddress Group = IPAddress.Parse("239.255.70.66");

    readonly DeviceId me;
    readonly Func<(string Name, int Port)> info;
    readonly Action<string> log;
    readonly CancellationTokenSource cts = new();
    readonly SemaphoreSlim nudge = new(0);
    readonly object gate = new();
    List<(IPAddress Address, IPAddress Mask)> interfaces = [];
    DateTime fastUntil, lastSent;
    Socket? receiver;
    UdpClient? sender;

    public event Action<DeviceId, string, IPEndPoint>? Seen;

    public Discovery(DeviceId me, Func<(string Name, int Port)> info, Action<string> log)
    {
        this.me = me;
        this.info = info;
        this.log = log;
    }

    public void Start()
    {
        interfaces = LocalInterfaces();
        try
        {
            receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = false };
            receiver.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            receiver.Bind(new IPEndPoint(IPAddress.Any, Port));
            JoinGroup();
            _ = ReceiveLoop(receiver);
        }
        catch (Exception ex)
        {
            log($"Discovery can't listen on UDP {Port}: {ex.Message}");
        }
        sender = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true, MulticastLoopback = true };
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        LookAround();
        _ = SendLoop();
    }

    // Announce often for a while, e.g. while the user is looking for a computer to pair with.
    public void LookAround()
    {
        lock (gate)
            fastUntil = DateTime.UtcNow + FastPeriod;
        nudge.Release();
    }

    public void AnnounceNow()
    {
        lock (gate)
        {
            if (DateTime.UtcNow - lastSent < TimeSpan.FromSeconds(1))
                return;
            lastSent = DateTime.UtcNow;
        }
        try { Send(); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { log($"Discovery send failed: {ex.Message}"); }
    }

    void OnNetworkChanged(object? sender, EventArgs e)
    {
        // A new Wi-Fi or cable connection brings new addresses; the group has to be joined on them again.
        interfaces = LocalInterfaces();
        JoinGroup();
        LookAround();
    }

    async Task SendLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            AnnounceNow();
            TimeSpan wait;
            lock (gate)
                wait = DateTime.UtcNow < fastUntil ? FastInterval : SlowInterval;
            try { await nudge.WaitAsync(wait, cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    void JoinGroup()
    {
        if (receiver == null)
            return;
        foreach (var (address, _) in interfaces)
        {
            try { receiver.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, address)); }
            catch (SocketException) { }
        }
    }

    void Send()
    {
        if (sender == null)
            return;
        var (name, port) = info();
        var data = JsonSerializer.SerializeToUtf8Bytes(new Announcement("folderbridge", 1, me.Value, name, port), Json.Options);
        var targets = new HashSet<IPEndPoint> { new(IPAddress.Broadcast, Port) };
        foreach (var (address, mask) in interfaces)
        {
            try
            {
                sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                sender.Send(data, data.Length, new IPEndPoint(Group, Port));
            }
            catch (SocketException) { }
            targets.Add(new IPEndPoint(DirectedBroadcast(address, mask), Port));
        }
        foreach (var target in targets)
        {
            try { sender.Send(data, data.Length, target); }
            catch (SocketException) { }
        }
    }

    async Task ReceiveLoop(Socket socket)
    {
        var buffer = new byte[2048];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!cts.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, cts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            try
            {
                var a = JsonSerializer.Deserialize<Announcement>(buffer.AsSpan(0, result.ReceivedBytes), Json.Options);
                if (a is not { App: "folderbridge" } || a.Port is <= 0 or > 65535 || a.Id == me.Value || !DeviceId.TryParse(a.Id, out var id))
                    continue;
                var from = ((IPEndPoint)result.RemoteEndPoint).Address;
                Seen?.Invoke(id, a.Name.Length > 64 ? a.Name[..64] : a.Name, new IPEndPoint(from, a.Port));
            }
            catch (JsonException) { }
        }
    }

    static List<(IPAddress Address, IPAddress Mask)> LocalInterfaces()
    {
        var result = new List<(IPAddress, IPAddress)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork)
                        result.Add((u.Address, u.IPv4Mask));
            }
        }
        catch (NetworkInformationException)
        {
        }
        return result;
    }

    static IPAddress DirectedBroadcast(IPAddress address, IPAddress mask)
    {
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        for (int i = 0; i < 4; i++)
            a[i] = (byte)(a[i] | ~m[i]);
        return new IPAddress(a);
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        cts.Cancel();
        receiver?.Dispose();
        sender?.Dispose();
    }
}
