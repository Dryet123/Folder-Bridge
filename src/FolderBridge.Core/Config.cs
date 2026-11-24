using System.Text.Json;

namespace FolderBridge.Core;

public sealed class AppConfig
{
    public string DeviceName { get; set; } = Environment.MachineName;
    public int ListenPort { get; set; } = 22000;
    public List<DeviceConfig> Devices { get; set; } = [];
    public List<FolderConfig> Folders { get; set; } = [];
    public List<string> IgnoredOffers { get; set; } = [];

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
