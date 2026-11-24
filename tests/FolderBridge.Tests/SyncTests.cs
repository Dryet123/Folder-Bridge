using System.Net;
using System.Security.Cryptography;
using FolderBridge.Core;

namespace FolderBridge.Tests;

public sealed class SyncTests : IAsyncLifetime
{
    readonly string root = Path.Combine(Path.GetTempPath(), "folderbridge-tests", Guid.NewGuid().ToString("N"));
    Engine a = null!, b = null!;
    string dirA = null!, dirB = null!;

    public async Task InitializeAsync()
    {
        a = CreateEngine("A");
        b = CreateEngine("B");
        a.Start();
        b.Start();

        string? codeA = null, codeB = null;
        var incoming = new TaskCompletionSource<PairingSession>();
        b.PairingRequested += s => incoming.TrySetResult(s);
        var outgoing = await a.StartPairingAsync(new IPEndPoint(IPAddress.Loopback, b.ListenPort), "B");
        var sessionB = await incoming.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntil(() => outgoing.Code != null);
        codeA = outgoing.Code;
        codeB = sessionB.Code;
        Assert.Equal(codeA, codeB);
        Assert.Equal("A", sessionB.PeerName);
        Assert.Equal("B", outgoing.PeerName);

        await outgoing.ConfirmAsync();
        await sessionB.ConfirmAsync();
        await WaitUntil(() => outgoing.State == PairingState.Paired && sessionB.State == PairingState.Paired);
        await WaitUntil(() => a.Snapshot().Devices.Any(d => d.Connected) && b.Snapshot().Devices.Any(d => d.Connected));

        dirA = Directory.CreateDirectory(Path.Combine(root, "folderA")).FullName;
        dirB = Path.Combine(root, "folderB");
        var offered = new TaskCompletionSource<FolderOffer>();
        b.FolderOffered += o => offered.TrySetResult(o);
        a.AddFolder("Docs", dirA, [b.Id]);
        var offer = await offered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Docs", offer.FolderName);
        b.AcceptOffer(offer, dirB);
    }

    Engine CreateEngine(string name)
    {
        var dataDir = Directory.CreateDirectory(Path.Combine(root, "data" + name)).FullName;
        File.WriteAllText(Path.Combine(dataDir, "config.json"), $$"""{"deviceName":"{{name}}","listenPort":0}""");
        return new Engine(dataDir, new EngineOptions { ListenAddress = IPAddress.Loopback, Discovery = false });
    }

    public Task DisposeAsync()
    {
        a.Dispose();
        b.Dispose();
        try { Directory.Delete(root, true); }
        catch (IOException) { }
        return Task.CompletedTask;
    }

    static async Task WaitUntil(Func<bool> condition, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("Condition was not met in time");
            await Task.Delay(100);
        }
    }

    static bool HasText(string path, string text)
    {
        try { return File.Exists(path) && File.ReadAllText(path) == text; }
        catch (IOException) { return false; }
    }

    [Fact]
    public async Task NewFileReachesTheOtherDevice()
    {
        var src = Path.Combine(dirA, "hello.txt");
        File.WriteAllText(src, "hello");
        var when = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(src, when);

        var dst = Path.Combine(dirB, "hello.txt");
        await WaitUntil(() => HasText(dst, "hello"));
        Assert.Equal(when, File.GetLastWriteTimeUtc(dst));
        Assert.Contains(b.Activity.Recent(), e => e.Kind == ActivityKind.Received && e.Path == "hello.txt");
    }

    [Fact]
    public async Task EditsGoBothWays()
    {
        File.WriteAllText(Path.Combine(dirA, "note.txt"), "one");
        await WaitUntil(() => HasText(Path.Combine(dirB, "note.txt"), "one"));

        File.WriteAllText(Path.Combine(dirB, "note.txt"), "two");
        await WaitUntil(() => HasText(Path.Combine(dirA, "note.txt"), "two"));

        File.WriteAllText(Path.Combine(dirA, "note.txt"), "three");
        await WaitUntil(() => HasText(Path.Combine(dirB, "note.txt"), "three"));
    }

    [Fact]
    public async Task FoldersAndLargeFilesSync()
    {
        var data = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 123);
        Directory.CreateDirectory(Path.Combine(dirA, "deep", "er"));
        Directory.CreateDirectory(Path.Combine(dirA, "empty"));
        File.WriteAllBytes(Path.Combine(dirA, "deep", "er", "big.bin"), data);

        var dst = Path.Combine(dirB, "deep", "er", "big.bin");
        await WaitUntil(() => File.Exists(dst) && new FileInfo(dst).Length == data.Length);
        Assert.Equal(data, File.ReadAllBytes(dst));
        await WaitUntil(() => Directory.Exists(Path.Combine(dirB, "empty")));

        Directory.Delete(Path.Combine(dirA, "empty"));
        await WaitUntil(() => !Directory.Exists(Path.Combine(dirB, "empty")));
    }

    [Fact]
    public async Task DeletedFilesGoToTrash()
    {
        File.WriteAllText(Path.Combine(dirA, "old.txt"), "bye");
        await WaitUntil(() => HasText(Path.Combine(dirB, "old.txt"), "bye"));

        File.Delete(Path.Combine(dirA, "old.txt"));
        await WaitUntil(() => !File.Exists(Path.Combine(dirB, "old.txt")));
        var trashed = Directory.GetFiles(Path.Combine(dirB, ".folderbridge", "trash"), "old.txt", SearchOption.AllDirectories);
        Assert.Single(trashed);
        Assert.Equal("bye", File.ReadAllText(trashed[0]));
    }

    [Fact]
    public async Task ConcurrentEditsKeepBothVersions()
    {
        File.WriteAllText(Path.Combine(dirA, "todo.txt"), "base");
        await WaitUntil(() => HasText(Path.Combine(dirB, "todo.txt"), "base"));
        var folderId = a.Snapshot().Folders.Single().Id;

        b.SetPaused(folderId, true);
        await WaitUntil(() => b.Snapshot().Folders.Single().State == Core.Sync.FolderState.Paused);

        var older = new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(Path.Combine(dirA, "todo.txt"), "from A");
        File.SetLastWriteTimeUtc(Path.Combine(dirA, "todo.txt"), older);
        File.WriteAllText(Path.Combine(dirB, "todo.txt"), "from B");
        File.SetLastWriteTimeUtc(Path.Combine(dirB, "todo.txt"), older.AddMinutes(5));
        await WaitUntil(() => a.Snapshot().Folders.Single().LocalFiles == 1 && a.Snapshot().Folders.Single().State == Core.Sync.FolderState.Idle);
        await Task.Delay(1500);

        b.SetPaused(folderId, false);

        // B's edit is newer, so it wins on both sides and A's text survives as a conflict copy.
        await WaitUntil(() => HasText(Path.Combine(dirA, "todo.txt"), "from B"));
        await WaitUntil(() => Directory.GetFiles(dirB, "todo.conflict-*").Length == 1);
        Assert.Equal("from B", File.ReadAllText(Path.Combine(dirB, "todo.txt")));
        var copyA = Directory.GetFiles(dirA, "todo.conflict-A-*.txt");
        Assert.Single(copyA);
        Assert.Equal("from A", File.ReadAllText(copyA[0]));
        Assert.Equal("from A", File.ReadAllText(Directory.GetFiles(dirB, "todo.conflict-*")[0]));
    }
}
