using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FolderBridge.Core;

namespace FolderBridge;

public partial class App : Application
{
    Mutex? instance;
    Engine? engine;
    Tray? tray;
    MainWindow? main;

    public static bool IsExiting { get; private set; }
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

        if (engine.FirstRun && !Autostart.IsDevCopy)
            Autostart.Set(true);

        DispatcherUnhandledException += (_, ex) =>
        {
            File.AppendAllText(Path.Combine(dataDir, "folderbridge.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} UI error: {ex.Exception}{Environment.NewLine}");
            ex.Handled = true;
        };

        main = new MainWindow(engine);
        tray = new Tray(this, engine);
        engine.PairingRequested += s => Dispatcher.BeginInvoke(() =>
        {
            main.ShowIncomingPairing(s);
            if (!main.IsVisible)
                tray.Notify("Pairing request", $"{s.PeerName} wants to pair with this computer.");
        });
        engine.FolderOffered += o => Dispatcher.BeginInvoke(() =>
        {
            main.ShowOffer(o, false);
            if (!main.IsVisible)
                tray.Notify("Shared folder", $"{o.DeviceName} wants to share \"{o.FolderName}\".");
        });
        Listen(showSignal, ShowMain);
        Listen(exitSignal, Quit);
        Snapshots.Start();

        if (!args.Contains("--tray"))
            ShowMain();
    }

    void Listen(EventWaitHandle signal, Action action)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                signal.WaitOne();
                Dispatcher.BeginInvoke(action);
            }
        }) { IsBackground = true };
        thread.Start();
    }

    public void ShowMain()
    {
        if (main == null)
            return;
        main.Show();
        if (main.WindowState == WindowState.Minimized)
            main.WindowState = WindowState.Normal;
        if (!Snapshots.Enabled)
            main.Activate();
    }

    public void Quit()
    {
        IsExiting = true;
        tray?.Dispose();
        engine?.Dispose();
        main?.Close();
        instance?.ReleaseMutex();
        Shutdown();
    }
}
