namespace FolderBridge.Core.Net;

public enum MessageType : byte
{
    Hello = 1,
    ClusterConfig,
    Index,
    Request,
    Response,
    PairRequest,
    PairNonce,
    PairReveal,
    PairConfirm,
    PairReject,
    Ping,
    Unpair,
}

public sealed record HelloMessage(string Name, int Port);

public sealed record SharedFolder(string Id, string Name);

public sealed record ClusterConfigMessage(List<SharedFolder> Folders);

public sealed record IndexMessage(string Folder, List<FileEntry> Files);

public sealed record RequestMessage(int Id, string Folder, string Path, ContentHash Hash, long Offset, int Size);

public sealed record PairRequestMessage(string Name, int Port, byte[] Commitment);

public sealed record PairNonceMessage(byte[] Nonce, string? Name = null);

public sealed record EmptyMessage
{
    public static readonly EmptyMessage Value = new();
}

sealed record Announcement(string App, int V, string Id, string Name, int Port);
