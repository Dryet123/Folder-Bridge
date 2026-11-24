using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FolderBridge;

// Debug aid: with FOLDERBRIDGE_SNAPSHOT_DIR set, windows open off-screen without taking focus
// and are saved as PNGs every second, so the UI can be checked without touching the desktop.
static class Snapshots
{
#if DEBUG
    static readonly string? Dir = Environment.GetEnvironmentVariable("FOLDERBRIDGE_SNAPSHOT_DIR");
#else
    static readonly string? Dir = null;
#endif

    public static bool Enabled => Dir != null;

    public static void Start()
    {
        if (Dir == null)
            return;
        Directory.CreateDirectory(Dir);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => CaptureAll();
        timer.Start();
    }

    public static void MoveOffScreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -12000;
        window.Top = 0;
    }

    static void CaptureAll()
    {
        foreach (Window w in Application.Current.Windows)
        {
            if (!w.IsVisible || w.ActualWidth < 1)
                continue;
            try
            {
                var bitmap = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(w);
                File.WriteAllBytes(Path.Combine(Dir!, w.GetType().Name + ".png"), Icons.Png(bitmap));
            }
            catch (IOException)
            {
            }
        }
    }
}
