using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace FolderBridge.Core.Net;

public sealed class Connection
{
    const int MaxFrame = 32 * 1024 * 1024;
    static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    readonly TcpClient tcp;
    readonly SslStream ssl;
    readonly SemaphoreSlim writeLock = new(1, 1);
    readonly ConcurrentDictionary<int, TaskCompletionSource<byte[]?>> pending = new();
    readonly CancellationTokenSource cts = new();
    int nextRequestId;
    int closed;

    public DeviceId PeerId { get; }
    public IPEndPoint RemoteEndPoint { get; }
    public bool IsClient { get; }
    public string PeerName { get; set; } = "";
    public DateTime LastReceived { get; private set; } = DateTime.UtcNow;
    public bool IsClosed => closed != 0;

    public Func<Connection, MessageType, byte[], Task>? OnMessage { get; set; }
    public event Action<Connection>? Closed;

    Connection(TcpClient tcp, SslStream ssl, bool isClient)
    {
        this.tcp = tcp;
        this.ssl = ssl;
        IsClient = isClient;
        RemoteEndPoint = (IPEndPoint)tcp.Client.RemoteEndPoint!;
        PeerId = ssl.RemoteCertificate is { } cert
            ? DeviceId.FromCertificate(cert)
            : throw new AuthenticationException("The other device sent no certificate");
    }

    public static async Task<Connection> ConnectAsync(IPEndPoint endPoint, X509Certificate2 cert, CancellationToken ct)
    {
        var tcp = new TcpClient(endPoint.AddressFamily) { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(HandshakeTimeout);
            await tcp.ConnectAsync(endPoint, timeout.Token);
            var ssl = new SslStream(tcp.GetStream(), false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "folderbridge",
                ClientCertificates = [cert],
                // Peers are self-signed; trust is decided by the device ID after the handshake.
                LocalCertificateSelectionCallback = (_, _, _, _, _) => cert,
                RemoteCertificateValidationCallback = (_, c, _, _) => c != null,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, timeout.Token);
            return new Connection(tcp, ssl, true);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public static async Task<Connection> AcceptAsync(TcpClient tcp, X509Certificate2 cert, CancellationToken ct)
    {
        tcp.NoDelay = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HandshakeTimeout);
        var ssl = new SslStream(tcp.GetStream(), false);
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = cert,
            ClientCertificateRequired = true,
            RemoteCertificateValidationCallback = (_, c, _, _) => c != null,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        }, timeout.Token);
        return new Connection(tcp, ssl, false);
    }

    public async Task RunAsync()
    {
        var header = new byte[5];
        try
        {
            while (true)
            {
                await ssl.ReadExactlyAsync(header, cts.Token);
                int length = BinaryPrimitives.ReadInt32BigEndian(header) - 1;
                if (length < 0 || length > MaxFrame)
                    throw new InvalidDataException("Bad frame length");
                var payload = new byte[length];
                await ssl.ReadExactlyAsync(payload, cts.Token);
                LastReceived = DateTime.UtcNow;

                var type = (MessageType)header[4];
                if (type == MessageType.Response)
                    CompleteResponse(payload);
                else if (type != MessageType.Ping && OnMessage != null)
                    await OnMessage(this, type, payload);
            }
        }
        catch
        {
        }
        finally
        {
            Close();
        }
    }

    public async Task SendFrameAsync(MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var header = new byte[5];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length + 1);
        header[4] = (byte)type;
        await writeLock.WaitAsync(ct);
        try
        {
            await ssl.WriteAsync(header, ct);
            await ssl.WriteAsync(payload, ct);
            await ssl.FlushAsync(ct);
        }
        catch
        {
            Close();
            throw;
        }
        finally
        {
            writeLock.Release();
        }
    }

    public Task SendAsync<T>(MessageType type, T message, CancellationToken ct = default) =>
        SendFrameAsync(type, JsonSerializer.SerializeToUtf8Bytes(message, Json.Options), ct);

    public async Task TrySendAsync<T>(MessageType type, T message)
    {
        try
        {
            await SendAsync(type, message);
        }
        catch
        {
        }
    }

    public static T Read<T>(byte[] payload) =>
        JsonSerializer.Deserialize<T>(payload, Json.Options) ?? throw new InvalidDataException("Empty message");

    // Returns null when the other side no longer has that exact file content.
    public async Task<byte[]?> RequestAsync(string folder, string path, string hash, long offset, int size, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref nextRequestId);
        var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;
        try
        {
            if (IsClosed)
                throw new IOException("Disconnected");
            await SendAsync(MessageType.Request, new RequestMessage(id, folder, path, hash, offset, size), ct);
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    public Task RespondAsync(int id, byte[]? data)
    {
        var payload = new byte[5 + (data?.Length ?? 0)];
        BinaryPrimitives.WriteInt32BigEndian(payload, id);
        payload[4] = data == null ? (byte)1 : (byte)0;
        data?.CopyTo(payload, 5);
        return SendFrameAsync(MessageType.Response, payload);
    }

    void CompleteResponse(byte[] payload)
    {
        if (payload.Length < 5)
            return;
        var id = BinaryPrimitives.ReadInt32BigEndian(payload);
        if (pending.TryRemove(id, out var tcs))
            tcs.TrySetResult(payload[4] == 0 ? payload[5..] : null);
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0)
            return;
        cts.Cancel();
        try { ssl.Dispose(); } catch { }
        tcp.Dispose();
        foreach (var tcs in pending.Values)
            tcs.TrySetException(new IOException("Disconnected"));
        Closed?.Invoke(this);
    }
}
