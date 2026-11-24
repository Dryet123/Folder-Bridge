using System.Diagnostics;
using FolderBridge.Core;

namespace FolderBridge.Tests;

public class ThrottleTests
{
    [Fact]
    public async Task RateLimiterSpreadsBytesOverTime()
    {
        var limiter = new RateLimiter { BytesPerSecond = 200 * 1024 };
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++)
            await limiter.WaitAsync(100 * 1024, CancellationToken.None);
        // 500 KB at 200 KB/s: the first block is free, the next four wait half a second each.
        Assert.InRange(clock.Elapsed.TotalSeconds, 1.8, 4);
    }

    [Fact]
    public async Task NoLimitMeansNoWaiting()
    {
        var limiter = new RateLimiter();
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
            await limiter.WaitAsync(1024 * 1024, CancellationToken.None);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void PowerAndNetworkStateCanBeRead()
    {
        // This desktop has no battery and a normal network; the point is that both calls work.
        Assert.False(SystemState.OnBattery());
        Assert.False(SystemState.OnMeteredNetwork());
    }

    [Fact]
    public void ScheduleOptionsAreChecked()
    {
        Assert.Throws<ArgumentException>(() => new SyncOptions { IntervalMinutes = 0 }.Validate());
        Assert.Throws<ArgumentException>(() => new SyncOptions { FromMinute = 60, ToMinute = 60 }.Validate());
        Assert.Throws<ArgumentException>(() => new SyncOptions { UploadLimitKBps = -1 }.Validate());
        new SyncOptions().Validate();
        Assert.Equal("07:05", SyncOptions.Time(7 * 60 + 5));
    }
}
