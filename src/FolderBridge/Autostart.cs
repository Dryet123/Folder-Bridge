using Microsoft.Win32;

namespace FolderBridge;

static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Folder Bridge";

    public static bool IsEnabled => Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(Name) is string;

    // Builds from the source tree and test copies with their own data folder never register themselves.
    public static bool IsDevCopy =>
        Environment.GetEnvironmentVariable("FOLDERBRIDGE_DATA_DIR") != null
        || Environment.ProcessPath?.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) == true;

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
        else
            key.DeleteValue(Name, false);
    }
}
