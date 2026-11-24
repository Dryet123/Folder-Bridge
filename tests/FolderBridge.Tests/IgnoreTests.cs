using FolderBridge.Core.Sync;

namespace FolderBridge.Tests;

public class IgnoreTests
{
    [Theory]
    [InlineData(".folderbridge")]
    [InlineData("Thumbs.db")]
    [InlineData("desktop.ini")]
    [InlineData("~$report.docx")]
    [InlineData("~WRL0003.tmp")]
    [InlineData("movie.mkv.crdownload")]
    [InlineData("setup.exe.part")]
    [InlineData("photo.jpg.partial")]
    public void SkipsClutterAndUnfinishedDownloads(string name) => Assert.True(FolderSync.IsIgnored(name));

    [Theory]
    [InlineData("report.docx")]
    [InlineData("~notes.txt")]
    [InlineData("partial.txt")]
    [InlineData("backup.tmp")]
    [InlineData("archive.part1.rar")]
    public void KeepsOrdinaryFiles(string name) => Assert.False(FolderSync.IsIgnored(name));
}
