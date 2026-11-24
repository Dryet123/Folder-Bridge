using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FolderBridge.Core;
using FolderBridge.Dialogs;

namespace FolderBridge;

public partial class MainWindow : XpWindow
{
    readonly Engine engine;
    readonly MainViewModel vm;
    readonly Dictionary<string, OfferDialog> openOffers = [];
    bool syncingSelection;

    public MainWindow(Engine engine)
    {
        this.engine = engine;
        InitializeComponent();
        Icon = App.WindowIcon;
        vm = new MainViewModel(engine);
        DataContext = vm;
        Refresh();
        foreach (var entry in engine.Activity.Recent())
            vm.AddActivity(entry);
        engine.Activity.Added += e => Dispatcher.BeginInvoke(() => vm.AddActivity(e));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => Refresh();
        timer.Start();
    }

    public EngineSnapshot? Snapshot => vm.Last;

    void Refresh()
    {
        vm.Update(engine.Snapshot());
        SyncSelection();
    }

    void SyncSelection()
    {
        syncingSelection = true;
        FolderList.SelectedItem = vm.Detail as FolderVm;
        DeviceList.SelectedItem = vm.Detail as DeviceVm;
        syncingSelection = false;
    }

    void Show(object detail)
    {
        vm.Detail = detail;
        SyncSelection();
    }

    void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!syncingSelection && FolderList.SelectedItem is FolderVm folder)
            Show(folder);
    }

    void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!syncingSelection && DeviceList.SelectedItem is DeviceVm device)
            Show(device);
    }

    static T? ContextOf<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

    void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddFolderDialog(engine, engine.Snapshot().Devices) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.AddedId != null)
        {
            Refresh();
            if (vm.Folders.FirstOrDefault(f => f.Id == dialog.AddedId) is { } folder)
                Show(folder);
        }
    }

    void Pair_Click(object sender, RoutedEventArgs e) => new PairDialog(engine, (DeviceVm?)null) { Owner = this }.ShowDialog();

    void PairNearby_Click(object sender, RoutedEventArgs e)
    {
        if (ContextOf<DeviceVm>(sender) is { } device)
            new PairDialog(engine, device) { Owner = this }.ShowDialog();
    }

    void SyncAll_Click(object sender, RoutedEventArgs e) => engine.Rescan();

    void Settings_Click(object sender, RoutedEventArgs e) => new SettingsDialog(engine) { Owner = this }.ShowDialog();

    void Offer_Click(object sender, RoutedEventArgs e)
    {
        if (ContextOf<OfferVm>(sender) is { } offer)
            ShowOffer(offer.Offer, true);
    }

    void PauseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ContextOf<FolderVm>(sender) is { } folder)
        {
            engine.SetPaused(folder.Id, !folder.Paused);
            Refresh();
        }
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ContextOf<FolderVm>(sender) is { } folder && Directory.Exists(folder.Path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder.Path}\"") { UseShellExecute = true });
    }

    void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ContextOf<FolderVm>(sender) is not { } folder)
            return;
        var text = $"Stop syncing \"{folder.Name}\" on this computer?\n\nNo files are deleted. They stay here and on the other computers.";
        if (MessageDialog.Confirm(this, "Remove folder", text, "Remove"))
        {
            engine.RemoveFolder(folder.Id);
            Refresh();
        }
    }

    void Unpair_Click(object sender, RoutedEventArgs e)
    {
        if (ContextOf<DeviceVm>(sender) is not { } device)
            return;
        var text = $"Unpair {device.Name}?\n\nFolders stop syncing with it on both computers. No files are deleted, and you can pair again later.";
        if (MessageDialog.Confirm(this, "Unpair", text, "Unpair"))
        {
            engine.Unpair(device.Id);
            Refresh();
        }
    }

    public void ShowIncomingPairing(PairingSession session)
    {
        var dialog = new PairDialog(engine, session) { ShowActivated = false };
        if (IsVisible)
            dialog.Owner = this;
        dialog.Show();
    }

    public void ShowOffer(FolderOffer offer, bool activate)
    {
        if (openOffers.TryGetValue(offer.FolderId, out var open))
        {
            open.Activate();
            return;
        }
        var dialog = new OfferDialog(engine, offer) { ShowActivated = activate && !Snapshots.Enabled };
        if (IsVisible)
            dialog.Owner = this;
        openOffers[offer.FolderId] = dialog;
        dialog.Closed += (_, _) => openOffers.Remove(offer.FolderId);
        dialog.Show();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
