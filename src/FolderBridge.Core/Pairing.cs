using System.Net;
using System.Security.Cryptography;
using FolderBridge.Core.Net;

namespace FolderBridge.Core;

public enum PairingState { Waiting, Paired, Rejected, Failed }

// Numeric comparison pairing. The initiator commits to its nonce before seeing the other one,
// so a device in the middle can't steer both screens to the same code.
public sealed class PairingSession
{
    readonly Engine engine;
    readonly Connection conn;
    readonly object gate = new();
    readonly byte[] myNonce = RandomNumberGenerator.GetBytes(32);
    byte[]? peerCommitment;
    byte[]? peerNonce;
    bool localOk, remoteOk;

    public bool Incoming { get; }
    public DeviceId PeerId => conn.PeerId;
    public string PeerName { get; private set; }
    public IPEndPoint? PeerAddress { get; private set; }
    public string? Code { get; private set; }
    public PairingState State { get; private set; } = PairingState.Waiting;
    public bool LocalConfirmed => localOk;
    public bool RemoteConfirmed => remoteOk;

    public event Action<PairingSession>? Changed;

    internal PairingSession(Engine engine, Connection conn, bool incoming, string peerName, IPEndPoint? peerAddress)
    {
        this.engine = engine;
        this.conn = conn;
        Incoming = incoming;
        PeerName = peerName;
        PeerAddress = peerAddress;
    }

    internal Task BeginAsync() =>
        conn.SendAsync(MessageType.PairRequest, new PairRequestMessage(engine.DeviceName, engine.ListenPort, SHA256.HashData(myNonce)));

    internal async Task HandleAsync(MessageType type, byte[] payload)
    {
        switch (type)
        {
            case MessageType.PairRequest when Incoming && peerCommitment == null:
            {
                var m = Connection.Read<PairRequestMessage>(payload);
                PeerName = Trim(m.Name);
                PeerAddress = new IPEndPoint(conn.RemoteEndPoint.Address, m.Port);
                peerCommitment = m.Commitment;
                await conn.SendAsync(MessageType.PairNonce, new PairNonceMessage(myNonce, engine.DeviceName));
                break;
            }
            case MessageType.PairNonce when !Incoming && peerNonce == null:
            {
                var m = Connection.Read<PairNonceMessage>(payload);
                if (!string.IsNullOrWhiteSpace(m.Name))
                    PeerName = Trim(m.Name);
                peerNonce = m.Nonce;
                await conn.SendAsync(MessageType.PairReveal, new PairNonceMessage(myNonce));
                SetCode();
                break;
            }
            case MessageType.PairReveal when Incoming && peerCommitment != null && peerNonce == null:
            {
                var nonce = Connection.Read<PairNonceMessage>(payload).Nonce;
                if (!SHA256.HashData(nonce).AsSpan().SequenceEqual(peerCommitment))
                {
                    Finish(PairingState.Failed);
                    conn.Close();
                    return;
                }
                peerNonce = nonce;
                SetCode();
                engine.RaisePairingRequested(this);
                break;
            }
            case MessageType.PairConfirm:
                remoteOk = true;
                TryComplete();
                Changed?.Invoke(this);
                break;
            case MessageType.PairReject:
                Finish(PairingState.Rejected);
                conn.Close();
                break;
        }
    }

    public async Task ConfirmAsync()
    {
        lock (gate)
        {
            if (Code == null || State != PairingState.Waiting || localOk)
                return;
            localOk = true;
        }
        await conn.TrySendAsync(MessageType.PairConfirm, new { });
        TryComplete();
        Changed?.Invoke(this);
    }

    public void Cancel()
    {
        if (State != PairingState.Waiting)
            return;
        _ = conn.TrySendAsync(MessageType.PairReject, new { }).ContinueWith(_ => conn.Close());
        Finish(PairingState.Rejected);
    }

    internal void OnClosed()
    {
        if (State == PairingState.Waiting)
            Finish(PairingState.Failed);
    }

    void SetCode()
    {
        Code = Identity.PairingCode(engine.Id, PeerId, myNonce, peerNonce!);
        Changed?.Invoke(this);
    }

    void TryComplete()
    {
        lock (gate)
        {
            if (!localOk || !remoteOk || State != PairingState.Waiting)
                return;
            State = PairingState.Paired;
        }
        engine.CompletePairing(this);
        // Give the other side a moment to read our confirmation before hanging up.
        _ = Task.Delay(3000).ContinueWith(_ => conn.Close());
    }

    void Finish(PairingState state)
    {
        lock (gate)
        {
            if (State != PairingState.Waiting)
                return;
            State = state;
        }
        Changed?.Invoke(this);
    }

    static string Trim(string name) => name.Length > 64 ? name[..64] : name;
}
