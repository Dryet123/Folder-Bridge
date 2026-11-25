using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FolderBridge.Core;

namespace FolderBridge.Dialogs;

public partial class SettingsDialog : XpWindow
{
    readonly Engine engine;

    public SettingsDialog(Engine engine, int tab = 0)
    {
        this.engine = engine;
        InitializeComponent();
        Tabs.SelectedIndex = tab;

        var s = engine.Snapshot();
        var o = engine.Options;
        NameBox.Text = s.DeviceName;
        PortBox.Text = s.Port.ToString(CultureInfo.InvariantCulture);
        IdBox.Text = s.DeviceId.Grouped;
        AutostartBox.IsChecked = Autostart.IsEnabled;
        ShortcutBox.IsChecked = DesktopShortcut.Exists;
        NotifyBox.IsChecked = o.Notifications;

        ModeInstant.IsChecked = o.Mode == SyncMode.Instant;
        ModeInterval.IsChecked = o.Mode == SyncMode.Interval;
        ModeManual.IsChecked = o.Mode == SyncMode.Manual;
        ModeHours.IsChecked = o.Mode == SyncMode.Hours;
        IntervalBox.Text = o.IntervalMinutes.ToString(CultureInfo.InvariantCulture);
        FromBox.Text = SyncOptions.Time(o.FromMinute);
        ToBox.Text = SyncOptions.Time(o.ToMinute);

        CpuFull.IsChecked = o.Cpu == CpuMode.Full;
        CpuBalanced.IsChecked = o.Cpu == CpuMode.Balanced;
        CpuBackground.IsChecked = o.Cpu == CpuMode.Background;
        BatteryBox.IsChecked = o.PauseOnBattery;
        MeteredBox.IsChecked = o.PauseOnMetered;

        UploadLimitBox.IsChecked = o.UploadLimitKBps > 0;
        DownloadLimitBox.IsChecked = o.DownloadLimitKBps > 0;
        UploadBox.Text = (o.UploadLimitKBps > 0 ? o.UploadLimitKBps : 1000).ToString(CultureInfo.InvariantCulture);
        DownloadBox.Text = (o.DownloadLimitKBps > 0 ? o.DownloadLimitKBps : 1000).ToString(CultureInfo.InvariantCulture);

        var ignored = engine.IgnoredOffers;
        if (ignored > 0)
        {
            IgnoredText.Text = ignored == 1 ? "1 shared folder was ignored." : $"{ignored} shared folders were ignored.";
            IgnoredRow.Visibility = Visibility.Visible;
        }
    }

    // Typing into a field picks the option it belongs to.
    void Interval_Focus(object sender, RoutedEventArgs e) => ModeInterval.IsChecked = true;

    void Hours_Focus(object sender, RoutedEventArgs e) => ModeHours.IsChecked = true;

    void Limit_Focus(object sender, RoutedEventArgs e)
    {
        if (sender == UploadBox)
            UploadLimitBox.IsChecked = true;
        else
            DownloadLimitBox.IsChecked = true;
    }

    void Copy_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(IdBox.Text);

    void AskAgain_Click(object sender, RoutedEventArgs e)
    {
        engine.AskAgainAboutIgnored();
        IgnoredRow.Visibility = Visibility.Collapsed;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var mode = ModeInterval.IsChecked == true ? SyncMode.Interval
                : ModeManual.IsChecked == true ? SyncMode.Manual
                : ModeHours.IsChecked == true ? SyncMode.Hours
                : SyncMode.Instant;
            var current = engine.Options;
            // A field only has to be valid when its option is the one chosen.
            var options = current with
            {
                Mode = mode,
                IntervalMinutes = mode == SyncMode.Interval ? Number(IntervalBox, "The interval", 1, 1440) : Try(() => Number(IntervalBox, "", 1, 1440), current.IntervalMinutes),
                FromMinute = mode == SyncMode.Hours ? Minute(FromBox) : Try(() => Minute(FromBox), current.FromMinute),
                ToMinute = mode == SyncMode.Hours ? Minute(ToBox) : Try(() => Minute(ToBox), current.ToMinute),
                Cpu = CpuFull.IsChecked == true ? CpuMode.Full : CpuBackground.IsChecked == true ? CpuMode.Background : CpuMode.Balanced,
                PauseOnBattery = BatteryBox.IsChecked == true,
                PauseOnMetered = MeteredBox.IsChecked == true,
                UploadLimitKBps = UploadLimitBox.IsChecked == true ? Number(UploadBox, "The sending limit", 1, 0) : 0,
                DownloadLimitKBps = DownloadLimitBox.IsChecked == true ? Number(DownloadBox, "The receiving limit", 1, 0) : 0,
                Notifications = NotifyBox.IsChecked == true,
            };
            options.Validate();
            engine.UpdateSettings(NameBox.Text, Number(PortBox, "The port", 1024, 65535));
            engine.UpdateOptions(options);
            if (AutostartBox.IsChecked != Autostart.IsEnabled)
                Autostart.Set(AutostartBox.IsChecked == true);
            if (ShortcutBox.IsChecked != DesktopShortcut.Exists)
                DesktopShortcut.Set(ShortcutBox.IsChecked == true);
            DialogResult = true;
        }
        catch (ArgumentException ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    static int Number(TextBox box, string what, int min, int max)
    {
        if (!int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || (max > 0 && value > max))
            throw new ArgumentException(max > 0 ? $"{what} must be a number from {min} to {max}." : $"{what} must be a whole number.");
        return value;
    }

    static int Try(Func<int> parse, int fallback)
    {
        try { return parse(); }
        catch (ArgumentException) { return fallback; }
    }

    static int Minute(TextBox box)
    {
        var text = box.Text.Trim();
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour) && hour is >= 0 and < 24)
            return hour * 60;
        if (TimeOnly.TryParseExact(text, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return time.Hour * 60 + time.Minute;
        throw new ArgumentException($"\"{text}\" is not a time. Write it like 22:00.");
    }
}
