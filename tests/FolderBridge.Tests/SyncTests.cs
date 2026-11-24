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
        var config = Path.Combine(dataDir, "config.json");
        if (!File.Exists(config))
            File.WriteAllText(config, $$"""{"deviceName":"{{name}}","listenPort":0}""");
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
    public async Task RenamedFolderMovesOnTheOtherSide()
    {
        Directory.CreateDirectory(Path.Combine(dirA, "old", "inner"));
        File.WriteAllText(Path.Combine(dirA, "old", "a.txt"), "a");
        File.WriteAllText(Path.Combine(dirA, "old", "inner", "b.txt"), "b");
        await WaitUntil(() => HasText(Path.Combine(dirB, "old", "inner", "b.txt"), "b"));

        Directory.Move(Path.Combine(dirA, "old"), Path.Combine(dirA, "new"));
        await WaitUntil(() => HasText(Path.Combine(dirB, "new", "inner", "b.txt"), "b") && HasText(Path.Combine(dirB, "new", "a.txt"), "a"));
        await WaitUntil(() => !Directory.Exists(Path.Combine(dirB, "old")));
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
    public async Task ManySmallFilesArrive()
    {
        for (int i = 0; i < 300; i++)
            File.WriteAllText(Path.Combine(dirA, $"small{i:D3}.txt"), $"file {i}");
        await WaitUntil(() => Directory.GetFiles(dirB, "small*.txt").Length == 300, 60);
        Assert.Equal("file 123", File.ReadAllText(Path.Combine(dirB, "small123.txt")));
        await WaitUntil(() => b.Snapshot().Folders.Single().LocalFiles == 300);
    }

    [Fact]
    public async Task AnEditBeatsADeletion()
    {
        File.WriteAllText(Path.Combine(dirA, "keep.txt"), "v1");
        await WaitUntil(() => HasText(Path.Combine(dirB, "keep.txt"), "v1"));
        var folderId = a.Snapshot().Folders.Single().Id;
        b.SetPaused(folderId, true);
        await WaitUntil(() => b.Snapshot().Folders.Single().State == Core.Sync.FolderState.Paused);

        File.Delete(Path.Combine(dirA, "keep.txt"));
        File.WriteAllText(Path.Combine(dirB, "keep.txt"), "v2 from B");
        await WaitUntil(() => a.Snapshot().Folders.Single().LocalFiles == 0);
        await Task.Delay(1500);
        b.SetPaused(folderId, false);

        await WaitUntil(() => HasText(Path.Combine(dirA, "keep.txt"), "v2 from B"));
        Assert.Equal("v2 from B", File.ReadAllText(Path.Combine(dirB, "keep.txt")));
    }

    [Fact]
    public async Task RestartKeepsEverythingInSync()
    {
        File.WriteAllText(Path.Combine(dirA, "before.txt"), "before");
        await WaitUntil(() => HasText(Path.Combine(dirB, "before.txt"), "before"));

        b.Dispose();
        b = CreateEngine("B");
        b.Start();
        await WaitUntil(() => b.Snapshot().Devices.Any(d => d.Connected));

        File.WriteAllText(Path.Combine(dirA, "after.txt"), "after");
        File.WriteAllText(Path.Combine(dirB, "before.txt"), "edited after restart");
        await WaitUntil(() => HasText(Path.Combine(dirB, "after.txt"), "after"));
        await WaitUntil(() => HasText(Path.Combine(dirA, "before.txt"), "edited after restart"));
        Assert.Empty(Directory.GetFiles(dirA, "*.conflict-*"));
        Assert.Empty(Directory.GetFiles(dirB, "*.conflict-*"));
    }

    [Fact]
    public async Task ManualModeWaitsForSyncNow()
    {
        b.UpdateOptions(b.Options with { Mode = SyncMode.Manual });
        await WaitUntil(() => b.Snapshot().Folders.Single().State == Core.Sync.FolderState.Waiting);

        File.WriteAllText(Path.Combine(dirA, "later.txt"), "later");
        await Task.Delay(4000);
        Assert.False(File.Exists(Path.Combine(dirB, "later.txt")));
        Assert.Equal("Syncs when you press Sync now", b.Snapshot().Folders.Single().WaitReason);

        b.Rescan();
        await WaitUntil(() => HasText(Path.Combine(dirB, "later.txt"), "later"));
    }

    [Fact]
    public async Task OutsideSyncHoursNothingMovesUntilAllowed()
    {
        var now = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
        b.UpdateOptions(b.Options with { Mode = SyncMode.Hours, FromMinute = (now + 120) % 1440, ToMinute = (now + 180) % 1440 });
        await WaitUntil(() => b.Snapshot().Folders.Single().State == Core.Sync.FolderState.Waiting);

        File.WriteAllText(Path.Combine(dirA, "night.txt"), "night");
        await Task.Delay(4000);
        Assert.False(File.Exists(Path.Combine(dirB, "night.txt")));
        Assert.StartsWith("Syncs between", b.Snapshot().Folders.Single().WaitReason);

        b.UpdateOptions(b.Options with { Mode = SyncMode.Instant });
        await WaitUntil(() => HasText(Path.Combine(dirB, "night.txt"), "night"));
    }

    [Fact]
    public async Task OptionsSurviveRestart()
    {
        var wanted = b.Options with
        {
            Mode = SyncMode.Interval,
            IntervalMinutes = 30,
            FromMinute = 0,
            Cpu = CpuMode.Full,
            Notifications = false,
            PauseOnBattery = true,
            DownloadLimitKBps = 500,
        };
        b.UpdateOptions(wanted);
        b.Dispose();
        b = CreateEngine("B");
        Assert.Equal(wanted, b.Options);
        b.Start();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task DownloadLimitIsRespected()
    {
        b.UpdateOptions(b.Options with { DownloadLimitKBps = 256 });
        var data = RandomNumberGenerator.GetBytes(1024 * 1024);
        var started = DateTime.UtcNow;
        File.WriteAllBytes(Path.Combine(dirA, "slow.bin"), data);
        var dst = Path.Combine(dirB, "slow.bin");
        await WaitUntil(() => File.Exists(dst) && new FileInfo(dst).Length == data.Length, 30);
        // Four 256 KB blocks at 256 KB/s: the first goes at once, the other three take about a second each.
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromSeconds(2.5));
        Assert.Equal(data, File.ReadAllBytes(dst));
    }

    [Fact]
    public async Task IgnoredFolderCanBeOfferedAgain()
    {
        var offers = new List<FolderOffer>();
        b.FolderOffered += o =>
        {
            lock (offers)
                offers.Add(o);
        };
        a.AddFolder("Second", Directory.CreateDirectory(Path.Combine(root, "secondA")).FullName, [b.Id]);
        await WaitUntil(() => { lock (offers) return offers.Count == 1; });

        b.IgnoreOffer(offers[0]);
        Assert.Empty(b.Snapshot().Offers);
        Assert.Equal(1, b.IgnoredOffers);

        b.AskAgainAboutIgnored();
        await WaitUntil(() => { lock (offers) return offers.Count == 2; });
        Assert.Equal("Second", b.Snapshot().Offers.Single().FolderName);
        Assert.Equal(0, b.IgnoredOffers);
    }

    [Fact]
    public async Task DeclinedPairingChangesNothing()
    {
        using var c = CreateEngine("C");
        c.Start();
        var incoming = new TaskCompletionSource<PairingSession>();
        a.PairingRequested += s => incoming.TrySetResult(s);
        var outgoing = await c.StartPairingAsync(new IPEndPoint(IPAddress.Loopback, a.ListenPort), "A");
        var atA = await incoming.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await outgoing.ConfirmAsync();
        atA.Cancel();

        await WaitUntil(() => outgoing.State == PairingState.Rejected);
        Assert.Equal(PairingState.Rejected, atA.State);
        Assert.DoesNotContain(a.Snapshot().Devices, d => d.Id == c.Id);
        Assert.Empty(c.Snapshot().Devices);
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
