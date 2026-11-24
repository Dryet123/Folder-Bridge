using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderBridge.Core;

public enum Ordering { Equal, Greater, Lesser, Concurrent }

// SHA-256 kept inline (32 bytes) instead of a 64-char string per file; hex on the wire and on disk.
[JsonConverter(typeof(ContentHashConverter))]
public readonly record struct ContentHash(ulong A, ulong B, ulong C, ulong D) : IComparable<ContentHash>
{
    public static ContentHash From(ReadOnlySpan<byte> sha256) => new(
        BinaryPrimitives.ReadUInt64BigEndian(sha256),
        BinaryPrimitives.ReadUInt64BigEndian(sha256[8..]),
        BinaryPrimitives.ReadUInt64BigEndian(sha256[16..]),
        BinaryPrimitives.ReadUInt64BigEndian(sha256[24..]));

    public static ContentHash Parse(string hex)
    {
        if (hex.Length != 64)
            throw new FormatException("A content hash has 64 hex digits");
        return From(Convert.FromHexString(hex));
    }

    public int CompareTo(ContentHash other)
    {
        int c = A.CompareTo(other.A);
        if (c == 0) c = B.CompareTo(other.B);
        if (c == 0) c = C.CompareTo(other.C);
        return c != 0 ? c : D.CompareTo(other.D);
    }

    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[32];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, A);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], B);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[16..], C);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[24..], D);
        return Convert.ToHexStringLower(bytes);
    }

    internal sealed class ContentHashConverter : JsonConverter<ContentHash>
    {
        public override ContentHash Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            try { return Parse(reader.GetString() ?? ""); }
            catch (FormatException ex) { throw new JsonException(ex.Message); }
        }

        public override void Write(Utf8JsonWriter writer, ContentHash value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}

// One per file version, so it is kept small: a sorted array of counters with shared device keys.
[JsonConverter(typeof(VersionVectorConverter))]
public sealed class VersionVector
{
    public static readonly VersionVector Empty = new([]);
    static readonly ConcurrentDictionary<string, string> Keys = new();

    readonly record struct Counter(string Key, long Value);

    readonly Counter[] counters;

    VersionVector(Counter[] counters) => this.counters = counters;

    static string Intern(string key) => Keys.GetOrAdd(key, key);

    public VersionVector Increment(string device)
    {
        for (int i = 0; i < counters.Length; i++)
        {
            if (counters[i].Key != device)
                continue;
            var copy = (Counter[])counters.Clone();
            copy[i] = copy[i] with { Value = copy[i].Value + 1 };
            return new VersionVector(copy);
        }
        var grown = new Counter[counters.Length + 1];
        counters.CopyTo(grown, 0);
        grown[^1] = new Counter(Intern(device), 1);
        Array.Sort(grown, (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return new VersionVector(grown);
    }

    public VersionVector Merge(VersionVector other)
    {
        var a = counters;
        var b = other.counters;
        var result = new List<Counter>(Math.Max(a.Length, b.Length));
        int i = 0, j = 0;
        while (i < a.Length || j < b.Length)
        {
            int cmp = i == a.Length ? 1 : j == b.Length ? -1 : string.CompareOrdinal(a[i].Key, b[j].Key);
            if (cmp == 0)
                result.Add(new Counter(a[i].Key, Math.Max(a[i++].Value, b[j++].Value)));
            else if (cmp < 0)
                result.Add(a[i++]);
            else
                result.Add(b[j++]);
        }
        return new VersionVector(result.ToArray());
    }

    public Ordering Compare(VersionVector other)
    {
        var a = counters;
        var b = other.counters;
        bool greater = false, lesser = false;
        int i = 0, j = 0;
        while (i < a.Length || j < b.Length)
        {
            int cmp = i == a.Length ? 1 : j == b.Length ? -1 : string.CompareOrdinal(a[i].Key, b[j].Key);
            long x = cmp <= 0 ? a[i].Value : 0;
            long y = cmp >= 0 ? b[j].Value : 0;
            if (cmp <= 0) i++;
            if (cmp >= 0) j++;
            if (x > y) greater = true;
            else if (x < y) lesser = true;
        }
        return (greater, lesser) switch
        {
            (false, false) => Ordering.Equal,
            (true, false) => Ordering.Greater,
            (false, true) => Ordering.Lesser,
            _ => Ordering.Concurrent,
        };
    }

    public override string ToString() => string.Join(",", counters.Select(c => $"{c.Key}:{c.Value}"));

    internal sealed class VersionVectorConverter : JsonConverter<VersionVector>
    {
        public override VersionVector Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Version must be an object");
            var list = new List<Counter>(2);
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var key = Intern(reader.GetString()!);
                reader.Read();
                list.Add(new Counter(key, reader.GetInt64()));
            }
            if (list.Count == 0)
                return Empty;
            list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return new VersionVector(list.ToArray());
        }

        public override void Write(Utf8JsonWriter writer, VersionVector value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var c in value.counters)
                writer.WriteNumber(c.Key, c.Value);
            writer.WriteEndObject();
        }
    }
}

public sealed record FileEntry
{
    // Relative to the folder root, always with '/' separators.
    public required string Path { get; init; }
    public bool IsDirectory { get; init; }
    public bool Deleted { get; init; }
    public long Size { get; init; }
    public long ModifiedTicks { get; init; }
    public ContentHash? Hash { get; init; }
    public VersionVector Version { get; init; } = VersionVector.Empty;

    [JsonIgnore]
    public bool IsFile => !IsDirectory && !Deleted;

    public bool SameContent(FileEntry other) =>
        Deleted == other.Deleted && IsDirectory == other.IsDirectory && (Deleted || IsDirectory || Hash == other.Hash);
}
