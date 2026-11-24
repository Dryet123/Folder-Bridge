using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FolderBridge.Core;

// Keeps a byte rate under a limit; a limit of 0 means no limit.
public sealed class RateLimiter
{
    readonly object gate = new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    TimeSpan nextFree;

    public long BytesPerSecond { get; set; }

    public async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        var rate = BytesPerSecond;
        if (rate <= 0 || bytes <= 0)
            return;
        TimeSpan delay;
        lock (gate)
        {
            var now = clock.Elapsed;
            if (nextFree < now)
                nextFree = now;
            delay = nextFree - now;
            nextFree += TimeSpan.FromSeconds(bytes / (double)rate);
        }
        if (delay > TimeSpan.FromMilliseconds(1))
            await Task.Delay(delay, ct);
    }
}

// Duty-cycle throttle for long loops: after each slice of work it rests in proportion to it.
// A rest ratio of 1 keeps the loop busy about half of the time, 3 about a quarter.
sealed class Pacer
{
    static readonly long SliceTicks = Stopwatch.Frequency / 40;
    static readonly long GapTicks = Stopwatch.Frequency / 4;
    readonly Func<double> restRatio;
    long sliceStart, lastCall;

    public Pacer(Func<double> restRatio) => this.restRatio = restRatio;

    public void Pace()
    {
        var rest = Rest();
        if (rest > TimeSpan.Zero)
        {
            Thread.Sleep(rest);
            sliceStart = lastCall = Stopwatch.GetTimestamp();
        }
    }

    public async ValueTask PaceAsync(CancellationToken ct)
    {
        var rest = Rest();
        if (rest > TimeSpan.Zero)
        {
            await Task.Delay(rest, ct);
            sliceStart = lastCall = Stopwatch.GetTimestamp();
        }
    }

    TimeSpan Rest()
    {
        var ratio = restRatio();
        var now = Stopwatch.GetTimestamp();
        // A long gap since the last call means the work stopped in between, so a new slice starts.
        if (ratio <= 0 || now - lastCall > GapTicks)
            sliceStart = now;
        lastCall = now;
        var worked = now - sliceStart;
        if (ratio <= 0 || worked < SliceTicks)
            return TimeSpan.Zero;
        return TimeSpan.FromSeconds(worked * ratio / Stopwatch.Frequency);
    }
}

// Power and network conditions that can hold syncing back.
public static class SystemState
{
    static readonly object gate = new();
    static (bool Value, DateTime At) metered;
    static INetworkCostManager? costManager;
    static bool costUnavailable;

    public static bool OnBattery() => GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;

    public static bool OnMeteredNetwork()
    {
        lock (gate)
        {
            if (DateTime.UtcNow - metered.At < TimeSpan.FromSeconds(30))
                return metered.Value;
            metered = (QueryMetered(), DateTime.UtcNow);
            return metered.Value;
        }
    }

    static bool QueryMetered()
    {
        if (costUnavailable)
            return false;
        try
        {
            costManager ??= (INetworkCostManager)new NetworkListManager();
            if (costManager.GetCost(out var cost, IntPtr.Zero) != 0)
                return false;
            const uint Fixed = 0x2, Variable = 0x4, OverDataLimit = 0x10000, Roaming = 0x40000;
            return (cost & (Fixed | Variable | OverDataLimit | Roaming)) != 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or PlatformNotSupportedException)
        {
            costUnavailable = true;
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [ComImport, Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface INetworkCostManager
    {
        [PreserveSig]
        int GetCost(out uint cost, IntPtr destination);
    }

    [ComImport, Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")]
    class NetworkListManager;
}
