using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderBridge.Core;

public enum Ordering { Equal, Greater, Lesser, Concurrent }

[JsonConverter(typeof(VersionVectorConverter))]
public sealed class VersionVector
{
    public static readonly VersionVector Empty = new(new Dictionary<string, long>());

    readonly Dictionary<string, long> counters;

    VersionVector(Dictionary<string, long> counters) => this.counters = counters;

    public IReadOnlyDictionary<string, long> Counters => counters;

    public VersionVector Increment(string device)
    {
        var c = new Dictionary<string, long>(counters);
        c[device] = c.GetValueOrDefault(device) + 1;
        return new VersionVector(c);
    }

    public VersionVector Merge(VersionVector other)
    {
        var c = new Dictionary<string, long>(counters);
        foreach (var (k, v) in other.counters)
            c[k] = Math.Max(v, c.GetValueOrDefault(k));
        return new VersionVector(c);
    }

    public Ordering Compare(VersionVector other)
    {
        bool greater = false, lesser = false;
        foreach (var key in counters.Keys.Union(other.counters.Keys))
        {
            var a = counters.GetValueOrDefault(key);
            var b = other.counters.GetValueOrDefault(key);
            if (a > b) greater = true;
            else if (a < b) lesser = true;
        }
        return (greater, lesser) switch
        {
            (false, false) => Ordering.Equal,
            (true, false) => Ordering.Greater,
            (false, true) => Ordering.Lesser,
            _ => Ordering.Concurrent,
        };
    }

    public static VersionVector Of(Dictionary<string, long> counters) => new(new Dictionary<string, long>(counters));

    public override string ToString() => string.Join(",", counters.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}"));

    sealed class VersionVectorConverter : JsonConverter<VersionVector>
    {
        public override VersionVector Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(JsonSerializer.Deserialize<Dictionary<string, long>>(ref reader) ?? []);

        public override void Write(Utf8JsonWriter writer, VersionVector value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.counters);
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
    public string? Hash { get; init; }
    public VersionVector Version { get; init; } = VersionVector.Empty;

    [JsonIgnore]
    public bool IsFile => !IsDirectory && !Deleted;

    public bool SameContent(FileEntry other) =>
        Deleted == other.Deleted && IsDirectory == other.IsDirectory && (Deleted || IsDirectory || Hash == other.Hash);
}
