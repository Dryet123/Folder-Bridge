using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderBridge.Core;

static class Base32
{
    const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0)
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static bool IsValid(string s) => s.Length > 0 && s.All(c => Alphabet.Contains(c));
}

static class Json
{
    // Source-generated metadata: no reflection or emitted code when (de)serializing.
    public static readonly JsonSerializerOptions Options = new(JsonContext.Default.Options);

    public static readonly JsonSerializerOptions Pretty = new(Options) { WriteIndented = true };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(List<FileEntry>))]
[JsonSerializable(typeof(ActivityEntry))]
[JsonSerializable(typeof(Net.HelloMessage))]
[JsonSerializable(typeof(Net.ClusterConfigMessage))]
[JsonSerializable(typeof(Net.IndexMessage))]
[JsonSerializable(typeof(Net.RequestMessage))]
[JsonSerializable(typeof(Net.PairRequestMessage))]
[JsonSerializable(typeof(Net.PairNonceMessage))]
[JsonSerializable(typeof(Net.EmptyMessage))]
[JsonSerializable(typeof(Net.Announcement))]
partial class JsonContext : JsonSerializerContext;

static class AtomicFile
{
    public static void Write(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, true);
    }
}

public static class Format
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        // The UI is English, so numbers use a dot whatever the Windows region says.
        var number = (v >= 100 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.#", CultureInfo.InvariantCulture));
        return i == 0 ? $"{bytes} B" : $"{number} {units[i]}";
    }

    public static string Speed(double bytesPerSecond) => bytesPerSecond < 1024 ? "0 KB/s" : Bytes((long)bytesPerSecond) + "/s";

    public static string Count(long n) => n.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', ' ');
}
