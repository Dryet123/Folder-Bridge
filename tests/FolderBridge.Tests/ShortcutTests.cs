using FolderBridge.Core;

namespace FolderBridge.Tests;

public class ShortcutTests
{
    [Fact]
    public void ShortcutPointsAtItsTarget()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "folderbridge-tests", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var target = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            var link = Path.Combine(dir, "Folder Bridge.lnk");
            ShellShortcut.Create(link, target, "test");
            Assert.True(File.Exists(link));
            Assert.Equal(target, ShellShortcut.ReadTarget(link), ignoreCase: true);
            Assert.Null(ShellShortcut.ReadTarget(Path.Combine(dir, "missing.lnk")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
