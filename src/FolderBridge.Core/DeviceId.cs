using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FolderBridge.Core;

public readonly record struct DeviceId(string Value)
{
    public static DeviceId FromCertificate(X509Certificate cert) => new(Base32.Encode(SHA256.HashData(cert.GetRawCertData())));

    public static bool TryParse(string? s, out DeviceId id)
    {
        id = new DeviceId(s ?? "");
        return s is { Length: 52 } && Base32.IsValid(s);
    }

    // Used as the key in version vectors, long enough to never collide between a handful of devices.
    public string Short => Value[..12];

    public string Grouped => string.Join('-', Value.Chunk(7).Select(c => new string(c)));

    public override string ToString() => Value;
}

public sealed class Identity
{
    public X509Certificate2 Certificate { get; }
    public DeviceId Id { get; }

    Identity(X509Certificate2 cert)
    {
        Certificate = cert;
        Id = DeviceId.FromCertificate(cert);
    }

    public static Identity LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=folderbridge", key, HashAlgorithmName.SHA256);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(50));
            AtomicFile.Write(path, cert.Export(X509ContentType.Pfx));
        }
        // SChannel can't use ephemeral keys for TLS, so the key goes through a PFX import.
        return new Identity(X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.UserKeySet));
    }

    public static string PairingCode(DeviceId a, DeviceId b, ReadOnlySpan<byte> nonceA, ReadOnlySpan<byte> nonceB)
    {
        if (string.CompareOrdinal(a.Value, b.Value) > 0)
        {
            (a, b) = (b, a);
            var t = nonceA;
            nonceA = nonceB;
            nonceB = t;
        }
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(System.Text.Encoding.ASCII.GetBytes(a.Value + b.Value));
        h.AppendData(nonceA);
        h.AppendData(nonceB);
        var hash = h.GetHashAndReset();
        var v = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash) % 1_000_000;
        return $"{v / 1000:000} {v % 1000:000}";
    }
}
