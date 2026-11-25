using System.IO;
using FolderBridge.Core;

namespace FolderBridge;

static class DesktopShortcut
{
    static string LinkPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Folder Bridge.lnk");

    public static bool Exists => File.Exists(LinkPath);

    public static void Set(bool enabled)
    {
        if (enabled)
            ShellShortcut.Create(LinkPath, Environment.ProcessPath!, "Keep a folder the same on your computers");
        else if (File.Exists(LinkPath))
            File.Delete(LinkPath);
    }

    // Keeps the shortcut pointing at this exe if the user moved it.
    public static void FollowMovedExe()
    {
        var target = ShellShortcut.ReadTarget(LinkPath);
        if (target != null && !string.Equals(target, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            Set(true);
    }
}
