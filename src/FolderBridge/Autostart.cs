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

    static string Command => $"\"{Environment.ProcessPath}\" --tray";

    // Keeps the entry pointing at this exe if the user moved it after turning autostart on.
    public static void FollowMovedExe()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (key?.GetValue(Name) is string current && current != Command)
            key.SetValue(Name, Command);
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(Name, Command);
        else
            key.DeleteValue(Name, false);
    }
}
