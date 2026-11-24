using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderBridge.Core;

public sealed class AppConfig
{
    public string DeviceName { get; set; } = Environment.MachineName;
    public int ListenPort { get; set; } = 22000;
    public List<DeviceConfig> Devices { get; set; } = [];
    public List<FolderConfig> Folders { get; set; } = [];
    public List<string> IgnoredOffers { get; set; } = [];
    public SyncOptions Sync { get; set; } = new();

    public static AppConfig Load(string path, out bool created)
    {
        created = !File.Exists(path);
        if (created)
            return new AppConfig();
        // ReadAllText drops the BOM that Notepad may add when the file is edited by hand.
        return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Json.Options) ?? new AppConfig();
    }

    public void Save(string path) => AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(this, Json.Pretty));
}

public enum SyncMode { Instant, Interval, Manual, Hours }

public enum CpuMode { Full, Balanced, Background }

// Every value is always written, since several defaults are not zero and would otherwise be lost.
public sealed record SyncOptions
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public SyncMode Mode { get; init; } = SyncMode.Instant;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int IntervalMinutes { get; init; } = 15;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int FromMinute { get; init; } = 22 * 60;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int ToMinute { get; init; } = 8 * 60;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public CpuMode Cpu { get; init; } = CpuMode.Balanced;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public bool PauseOnBattery { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public bool PauseOnMetered { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int UploadLimitKBps { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int DownloadLimitKBps { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public bool Notifications { get; init; } = true;

    public static string Time(int minute) => $"{minute / 60:00}:{minute % 60:00}";

    public void Validate()
    {
        if (IntervalMinutes is < 1 or > 24 * 60)
            throw new ArgumentException("The interval must be between 1 minute and 24 hours.");
        if (FromMinute is < 0 or >= 24 * 60 || ToMinute is < 0 or >= 24 * 60)
            throw new ArgumentException("Times must be between 00:00 and 23:59.");
        if (FromMinute == ToMinute)
            throw new ArgumentException("The start and end of the sync hours can't be the same.");
        if (UploadLimitKBps < 0 || DownloadLimitKBps < 0)
            throw new ArgumentException("Speed limits can't be negative.");
    }
}

public sealed class DeviceConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? LastAddress { get; set; }
}

public sealed class FolderConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Paused { get; set; }
    public List<string> Devices { get; set; } = [];
}
