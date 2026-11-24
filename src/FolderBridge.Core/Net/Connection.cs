using System.Buffers;
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
    readonly ConcurrentDictionary<int, TaskCompletionSource<Chunk?>> pending = new();
    readonly CancellationTokenSource cts = new();
    int nextRequestId;
    int closed;

    public DeviceId PeerId { get; }
    public IPEndPoint RemoteEndPoint { get; }
    public bool IsClient { get; }
    public string PeerName { get; set; } = "";
    public DateTime LastReceived { get; private set; } = DateTime.UtcNow;
    public bool IsClosed => closed != 0;

    public Func<Connection, MessageType, ReadOnlyMemory<byte>, Task>? OnMessage { get; set; }
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
                var payload = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
                bool handedOver = false;
                try
                {
                    await ssl.ReadExactlyAsync(payload.AsMemory(0, length), cts.Token);
                    LastReceived = DateTime.UtcNow;
                    var type = (MessageType)header[4];
                    if (type == MessageType.Response)
                        handedOver = CompleteResponse(payload, length);
                    else if (type != MessageType.Ping && OnMessage != null)
                        await OnMessage(this, type, payload.AsMemory(0, length));
                }
                finally
                {
                    if (!handedOver)
                        ArrayPool<byte>.Shared.Return(payload);
                }
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

    public Task SendFrameAsync(MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var header = new byte[5];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length + 1);
        header[4] = (byte)type;
        return WriteAsync(header, payload, ct);
    }

    async Task WriteAsync(ReadOnlyMemory<byte> head, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct);
        try
        {
            await ssl.WriteAsync(head, ct);
            if (body.Length > 0)
                await ssl.WriteAsync(body, ct);
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

    public static T Read<T>(ReadOnlyMemory<byte> payload) =>
        JsonSerializer.Deserialize<T>(payload.Span, Json.Options) ?? throw new InvalidDataException("Empty message");

    // Returns null when the other side no longer has that exact file content.
    // The caller owns the returned chunk and must dispose it to give the buffer back.
    public async Task<Chunk?> RequestAsync(string folder, string path, ContentHash hash, long offset, int size, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref nextRequestId);
        var tcs = new TaskCompletionSource<Chunk?>(TaskCreationOptions.RunContinuationsAsynchronously);
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

    public Task RespondAsync(int id, ReadOnlyMemory<byte> data, bool found)
    {
        var head = new byte[10];
        BinaryPrimitives.WriteInt32BigEndian(head, data.Length + 6);
        head[4] = (byte)MessageType.Response;
        BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(5), id);
        head[9] = found ? (byte)0 : (byte)1;
        return WriteAsync(head, found ? data : ReadOnlyMemory<byte>.Empty, CancellationToken.None);
    }

    bool CompleteResponse(byte[] payload, int length)
    {
        if (length < 5)
            return false;
        var id = BinaryPrimitives.ReadInt32BigEndian(payload);
        if (!pending.TryRemove(id, out var tcs))
            return false;
        if (payload[4] != 0)
        {
            tcs.TrySetResult(null);
            return false;
        }
        return tcs.TrySetResult(new Chunk(payload, 5, length - 5));
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

public sealed class Chunk : IDisposable
{
    byte[]? array;
    readonly int offset;

    public Chunk(byte[] array, int offset, int length)
    {
        this.array = array;
        this.offset = offset;
        Length = length;
    }

    public int Length { get; }
    public ReadOnlyMemory<byte> Memory => array.AsMemory(offset, Length);

    public void Dispose()
    {
        var a = Interlocked.Exchange(ref array, null);
        if (a != null)
            ArrayPool<byte>.Shared.Return(a);
    }
}
