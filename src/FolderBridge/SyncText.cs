using FolderBridge.Core;

namespace FolderBridge;

// Plain-English wording of the sync options, shared by the tray menu, status bar and dialogs.
static class SyncText
{
    public static readonly int[] Intervals = [5, 15, 30, 60];

    public static string Every(int minutes) => minutes switch
    {
        1 => "Every minute",
        60 => "Every hour",
        _ when minutes % 60 == 0 => $"Every {minutes / 60} hours",
        _ => $"Every {minutes} minutes",
    };

    public static string Hours(SyncOptions o) => $"Only between {SyncOptions.Time(o.FromMinute)} and {SyncOptions.Time(o.ToMinute)}";

    public static string Mode(SyncOptions o) => o.Mode switch
    {
        SyncMode.Interval => Every(o.IntervalMinutes),
        SyncMode.Manual => "Only when I press Sync now",
        SyncMode.Hours => Hours(o),
        _ => "As soon as something changes",
    };

    public static string Cpu(CpuMode mode) => mode switch
    {
        CpuMode.Full => "Full speed",
        CpuMode.Background => "Background",
        _ => "Balanced",
    };

    public static string Summary(SyncOptions o)
    {
        var mode = o.Mode switch
        {
            SyncMode.Interval => Every(o.IntervalMinutes).ToLowerInvariant(),
            SyncMode.Manual => "manual",
            SyncMode.Hours => $"{SyncOptions.Time(o.FromMinute)} to {SyncOptions.Time(o.ToMinute)}",
            _ => "instant",
        };
        var load = o.Cpu == CpuMode.Full ? "full speed" : $"{Cpu(o.Cpu).ToLowerInvariant()} load";
        return $"Sync: {mode}, {load}";
    }
}
