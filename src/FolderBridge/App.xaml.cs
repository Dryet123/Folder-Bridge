using System.IO;
using System.Net;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FolderBridge.Core;
using FolderBridge.Dialogs;

namespace FolderBridge;

public partial class App : Application
{
    readonly Dictionary<string, OfferDialog> openOffers = [];
    Mutex? instance;
    Engine engine = null!;
    Tray? tray;
    MainWindow? main;
    bool memoryDirty = true;
    long activityCursor;
    SettingsDialog? settings;
    long lastWork;

    public static ImageSource WindowIcon { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Icons.Register(Resources);
        var args = e.Args;

        if (args is ["--export-icon", var target])
        {
            File.WriteAllBytes(target, Icons.IcoFile(Icons.App, 16, 20, 24, 32, 40, 48, 64, 256));
            Shutdown();
            return;
        }

        var dataDir = Environment.GetEnvironmentVariable("FOLDERBRIDGE_DATA_DIR") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderBridge");
        // Scoped by data folder, so a test copy with its own data runs next to the installed one.
        var key = "FolderBridge-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDir.ToLowerInvariant())))[..16];
        instance = new Mutex(true, $"Local\\{key}", out var first);
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\{key}-show");
        var exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\{key}-exit");
        if (!first)
        {
            (args.Contains("--exit") ? exitSignal : showSignal).Set();
            Shutdown();
            return;
        }
        if (args.Contains("--exit"))
        {
            Shutdown();
            return;
        }

        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        WindowIcon = BitmapFrame.Create(new MemoryStream(Icons.IcoFile(Icons.App, 16, 24, 32, 48)));

        var listen = IPAddress.TryParse(Environment.GetEnvironmentVariable("FOLDERBRIDGE_LISTEN"), out var address) ? address : IPAddress.Any;
        try
        {
            engine = new Engine(dataDir, new EngineOptions { ListenAddress = listen, Discovery = !IPAddress.IsLoopback(listen) });
            engine.Start();
        }
        catch (Exception ex)
        {
            if (Snapshots.Enabled)
                File.WriteAllText(Path.Combine(dataDir, "startup-error.txt"), ex.ToString());
            else
                MessageBox.Show($"Folder Bridge couldn't start.\n\n{ex.Message}", "Folder Bridge", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        if (!Autostart.IsDevCopy)
        {
            if (engine.FirstRun)
                Autostart.Set(true);
            else
                Autostart.FollowMovedExe();
        }

        var logPath = Path.Combine(dataDir, "folderbridge.log");
        void LogCrash(string kind, object error)
        {
            try { File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {kind}: {error}{Environment.NewLine}"); }
            catch (IOException) { }
        }
        DispatcherUnhandledException += (_, ex) =>
        {
            LogCrash("UI error", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => LogCrash("Crash", ex.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            LogCrash("Background error", ex.Exception);
            ex.SetObserved();
        };

        tray = new Tray(this, engine);
        engine.PairingRequested += s => Dispatcher.BeginInvoke(() =>
        {
            var dialog = new PairDialog(engine, s) { ShowActivated = false, Owner = VisibleMain };
            dialog.Show();
            if (VisibleMain == null)
                tray.Notify("Pairing request", $"{s.PeerName} wants to pair with this computer.");
        });
        engine.FolderOffered += o => Dispatcher.BeginInvoke(() =>
        {
            ShowOffer(o, false);
            if (VisibleMain == null)
                tray.Notify("Shared folder", $"{o.DeviceName} wants to share \"{o.FolderName}\".");
        });
        OnSignal(showSignal, ShowMain);
        OnSignal(exitSignal, Quit);
        Snapshots.Start();

        engine.OptionsChanged += () => Dispatcher.BeginInvoke(ApplyLoad);
        engine.Activity.Since(ref activityCursor);
        ApplyLoad();

        var housekeeping = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        housekeeping.Tick += (_, _) => Housekeep();
        housekeeping.Start();

        if (!args.Contains("--tray"))
            ShowMain();
    }

    MainWindow? VisibleMain => main is { IsVisible: true } ? main : null;

    void OnSignal(EventWaitHandle signal, Action action) =>
        ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => Dispatcher.BeginInvoke(action), null, Timeout.Infinite, false);

    public void ShowMain()
    {
        if (main == null)
        {
            // The window only exists while it is open; closing it frees the whole visual tree.
            main = new MainWindow(engine);
            main.Closed += (_, _) =>
            {
                main = null;
                memoryDirty = true;
                ApplyLoad();
            };
            ApplyLoad();
        }
        main.Show();
        if (main.WindowState == WindowState.Minimized)
            main.WindowState = WindowState.Normal;
        if (!Snapshots.Enabled)
            main.Activate();
    }

    public void ShowOffer(FolderOffer offer, bool activate)
    {
        if (openOffers.TryGetValue(offer.FolderId, out var open))
        {
            open.Activate();
            return;
        }
        var dialog = new OfferDialog(engine, offer) { ShowActivated = activate && !Snapshots.Enabled, Owner = VisibleMain };
        openOffers[offer.FolderId] = dialog;
        dialog.Closed += (_, _) => openOffers.Remove(offer.FolderId);
        dialog.Show();
    }

    public void ShowSettings(int tab)
    {
        if (settings != null)
        {
            settings.Activate();
            return;
        }
        settings = new SettingsDialog(engine, tab) { Owner = VisibleMain };
        if (settings.Owner == null)
            settings.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        settings.Closed += (_, _) => settings = null;
        settings.ShowDialog();
    }

    // The process priority follows the chosen load. Windows' background mode would also slow the open
    // window down, so it is only used while the window is closed.
    void ApplyLoad()
    {
        var mode = engine.Options.Cpu;
        var process = GetCurrentProcess();
        SetPriorityClass(process, ProcessModeBackgroundEnd);
        SetPriorityClass(process, mode switch
        {
            CpuMode.Full => NormalPriority,
            CpuMode.Background when main == null => IdlePriority,
            _ => BelowNormalPriority,
        });
        if (mode == CpuMode.Background && main == null)
            SetPriorityClass(process, ProcessModeBackgroundBegin);
    }

    // Conflicts and failures get a notification when nobody is looking at the window.
    void NotifyProblems()
    {
        var entries = engine.Activity.Since(ref activityCursor);
        if (VisibleMain != null || tray == null)
            return;
        int conflicts = entries.Count(e => e.Kind == ActivityKind.Conflict);
        int errors = entries.Count(e => e.Kind == ActivityKind.Error);
        if (conflicts > 0)
            tray.Notify("Changed on both computers", conflicts == 1
                ? "A file was edited on both computers. Both versions were kept."
                : $"{conflicts} files were edited on both computers. Both versions were kept.");
        else if (errors > 0)
            tray.Notify("Some files didn't sync", errors == 1
                ? "A file couldn't be synced. Open Folder Bridge to see which one."
                : $"{errors} files couldn't be synced. Open Folder Bridge to see which ones.");
    }

    // With no window open, memory left over from a sync is handed back once things are quiet again.
    void Housekeep()
    {
        NotifyProblems();
        if (main != null || Windows.Count > 0)
            return;
        var work = engine.WorkDone;
        if (work != lastWork)
        {
            lastWork = work;
            memoryDirty = true;
        }
        if (memoryDirty && !engine.IsBusy)
        {
            memoryDirty = false;
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
            SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        }
    }

    public void Quit()
    {
        tray?.Dispose();
        engine.Dispose();
        main?.Close();
        instance?.ReleaseMutex();
        Shutdown();
    }

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    const uint NormalPriority = 0x20, IdlePriority = 0x40, BelowNormalPriority = 0x4000;
    const uint ProcessModeBackgroundBegin = 0x00100000, ProcessModeBackgroundEnd = 0x00200000;

    [DllImport("kernel32.dll")]
    static extern bool SetPriorityClass(IntPtr process, uint priority);
}
