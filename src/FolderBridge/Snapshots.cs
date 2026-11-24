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

    // Menus normally open at the mouse pointer and take focus; here they are shown in hidden windows instead.
    public static void ShowMenu(System.Windows.Controls.ContextMenu menu, string name)
    {
        // A context menu can't live inside a window, so its items move to a plain vertical menu.
        var host = new System.Windows.Controls.Menu
        {
            ItemsPanel = new System.Windows.Controls.ItemsPanelTemplate(new FrameworkElementFactory(typeof(System.Windows.Controls.StackPanel))),
            Background = Brushes.White,
        };
        foreach (var item in menu.Items.Cast<object>().ToList())
        {
            menu.Items.Remove(item);
            host.Items.Add(item);
        }
        var window = new Window
        {
            Title = name,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            FontFamily = (FontFamily)Application.Current.Resources["UiFont"],
            FontSize = 11,
            Content = new System.Windows.Controls.Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xAC, 0xA8, 0x99)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(2),
                Child = host,
            },
        };
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
        MoveOffScreen(window);
        window.Show();
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
                File.WriteAllBytes(Path.Combine(Dir!, (w is XpWindow ? w.GetType().Name : w.Title) + ".png"), Icons.Png(bitmap));
            }
            catch (IOException)
            {
            }
        }
    }
}
