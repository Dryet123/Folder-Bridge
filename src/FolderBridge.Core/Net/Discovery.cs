using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace FolderBridge.Core.Net;

// Announces this device on the LAN every few seconds over multicast and broadcast,
// and reports announcements from other devices.
public sealed class Discovery : IDisposable
{
    public const int Port = 22027;
    static readonly IPAddress Group = IPAddress.Parse("239.255.70.66");

    readonly DeviceId me;
    readonly Func<(string Name, int Port)> info;
    readonly Action<string> log;
    readonly CancellationTokenSource cts = new();
    Socket? receiver;
    UdpClient? sender;

    public event Action<DeviceId, string, IPEndPoint>? Seen;

    public Discovery(DeviceId me, Func<(string Name, int Port)> info, Action<string> log)
    {
        this.me = me;
        this.info = info;
        this.log = log;
    }

    sealed record Announcement(string App, int V, string Id, string Name, int Port);

    public void Start()
    {
        try
        {
            receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = false };
            receiver.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            receiver.Bind(new IPEndPoint(IPAddress.Any, Port));
            foreach (var (address, _) in LocalInterfaces())
            {
                try { receiver.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, address)); }
                catch (SocketException) { }
            }
            _ = ReceiveLoop(receiver);
        }
        catch (Exception ex)
        {
            log($"Discovery can't listen on UDP {Port}: {ex.Message}");
        }
        sender = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true, MulticastLoopback = true };
        _ = SendLoop();
    }

    public void AnnounceNow()
    {
        try { Send(); }
        catch (Exception ex) { log($"Discovery send failed: {ex.Message}"); }
    }

    async Task SendLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            AnnounceNow();
            try { await Task.Delay(5000, cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    void Send()
    {
        if (sender == null)
            return;
        var (name, port) = info();
        var data = JsonSerializer.SerializeToUtf8Bytes(new Announcement("folderbridge", 1, me.Value, name, port), Json.Options);
        var targets = new HashSet<IPEndPoint> { new(IPAddress.Broadcast, Port) };
        foreach (var (address, mask) in LocalInterfaces())
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

    static IEnumerable<(IPAddress Address, IPAddress Mask)> LocalInterfaces()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
                if (u.Address.AddressFamily == AddressFamily.InterNetwork)
                    yield return (u.Address, u.IPv4Mask);
        }
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
        cts.Cancel();
        receiver?.Dispose();
        sender?.Dispose();
    }
}
