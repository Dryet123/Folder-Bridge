using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FolderBridge.Core;

namespace FolderBridge.Dialogs;

public partial class PairDialog : XpWindow
{
    enum Stage { Choose, Connecting, Code, Done, Error }

    const int DefaultPort = 22000;
    readonly Engine engine;
    readonly DispatcherTimer timer;
    readonly List<DeviceVm> nearby = [];
    PairingSession? session;
    Stage stage;

    public PairDialog(Engine engine, DeviceVm? target)
    {
        this.engine = engine;
        InitializeComponent();
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => RefreshNearby();
        timer.Start();
        engine.LookAround();
        RefreshNearby();
        SetStage(Stage.Choose);
        if (target?.EndPoint is { } ep)
            _ = ConnectAsync(ep, target.Name);
    }

    public PairDialog(Engine engine, PairingSession incoming)
    {
        this.engine = engine;
        InitializeComponent();
        timer = new DispatcherTimer();
        Title = "Pairing request";
        // An unexpected request must not be accepted by a stray Enter key.
        PrimaryButton.IsDefault = false;
        Attach(incoming);
    }

    void RefreshNearby()
    {
        var found = engine.Snapshot().Nearby;
        var selected = (NearbyList.SelectedItem as DeviceVm)?.Id;
        nearby.RemoveAll(d => found.All(n => n.Id != d.Id));
        foreach (var n in found)
        {
            var vm = nearby.FirstOrDefault(d => d.Id == n.Id);
            if (vm == null)
                nearby.Add(vm = new DeviceVm(n.Id));
            vm.Update(n);
        }
        if (NearbyList.ItemsSource == null || NearbyList.Items.Count != nearby.Count)
        {
            NearbyList.ItemsSource = null;
            NearbyList.ItemsSource = nearby;
            NearbyList.SelectedItem = nearby.FirstOrDefault(d => d.Id == selected);
        }
        NoneFound.Visibility = nearby.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateNextButton();
    }

    void NearbyList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NearbyList.SelectedItem != null)
            AddressBox.Text = "";
        UpdateNextButton();
    }

    void NearbyList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (NearbyList.SelectedItem != null)
            Primary_Click(sender, e);
    }

    void AddressBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (AddressBox.Text.Length > 0)
            NearbyList.SelectedItem = null;
        UpdateNextButton();
    }

    void UpdateNextButton()
    {
        if (stage == Stage.Choose)
            PrimaryButton.IsEnabled = NearbyList.SelectedItem != null || AddressBox.Text.Trim().Length > 0;
    }

    async void Primary_Click(object sender, RoutedEventArgs e)
    {
        switch (stage)
        {
            case Stage.Choose when NearbyList.SelectedItem is DeviceVm { EndPoint: { } ep } device:
                await ConnectAsync(ep, device.Name);
                break;
            case Stage.Choose:
                var text = AddressBox.Text.Trim();
                var endPoint = await ResolveAsync(text);
                if (endPoint == null)
                    ShowError($"\"{text}\" is not an address Folder Bridge can reach. Type it like 192.168.1.20 or 192.168.1.20:22000.");
                else
                    await ConnectAsync(endPoint, text);
                break;
            case Stage.Code when session != null:
                PrimaryButton.IsEnabled = false;
                await session.ConfirmAsync();
                break;
            case Stage.Error:
                SetStage(Stage.Choose);
                break;
            case Stage.Done:
                Close();
                break;
        }
    }

    static async Task<IPEndPoint?> ResolveAsync(string text)
    {
        if (IPEndPoint.TryParse(text, out var ep))
            return ep.Port == 0 ? new IPEndPoint(ep.Address, DefaultPort) : ep;
        var host = text;
        int port = DefaultPort;
        var colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out var p))
        {
            host = text[..colon];
            port = p;
        }
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host);
            var v4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return v4 == null ? null : new IPEndPoint(v4, port);
        }
        catch (SocketException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    async Task ConnectAsync(IPEndPoint endPoint, string name)
    {
        SetStage(Stage.Connecting);
        MessageText.Text = $"Connecting to {name}...";
        try
        {
            Attach(await engine.StartPairingAsync(endPoint, name));
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or InvalidOperationException
                                   or System.Security.Authentication.AuthenticationException)
        {
            var reason = ex is InvalidOperationException ? ex.Message : "Make sure Folder Bridge is open there and that Windows Firewall lets it through.";
            ShowError($"Couldn't connect to {name} at {endPoint}. {reason}");
        }
    }

    void Attach(PairingSession s)
    {
        session = s;
        s.Changed += _ => Dispatcher.BeginInvoke(UpdateFromSession);
        UpdateFromSession();
    }

    void UpdateFromSession()
    {
        if (session == null)
            return;
        var s = session;
        var who = s.PeerAddress != null ? $"{s.PeerName} ({s.PeerAddress.Address})" : s.PeerName;
        switch (s.State)
        {
            case PairingState.Paired:
                SetStage(Stage.Done);
                MessageText.Text = $"Paired with {s.PeerName}.\n\nYou can now share folders with it: add a folder, or tick {s.PeerName} under Shared with on a folder you already have.";
                return;
            case PairingState.Rejected:
                ShowError($"Pairing with {s.PeerName} was cancelled. Nothing was changed.");
                return;
            case PairingState.Failed:
                ShowError($"The connection to {s.PeerName} was lost before pairing finished.");
                return;
        }
        if (s.Code == null)
        {
            SetStage(Stage.Connecting);
            MessageText.Text = $"Waiting for {s.PeerName}...";
            return;
        }
        SetStage(Stage.Code);
        CodeIntro.Text = s.Incoming ? $"{who} wants to pair with this computer." : $"Pairing with {who}.";
        CodeText.Text = string.Join(" ", s.Code.Replace(" ", "").ToCharArray()).Insert(5, "  ");
        PrimaryButton.Content = "Pair";
        PrimaryButton.IsEnabled = !s.LocalConfirmed;
        CodeStatus.Text = s.LocalConfirmed
            ? $"Waiting for {s.PeerName} to press Pair..."
            : s.RemoteConfirmed
                ? $"{s.PeerName} has already confirmed. Press Pair if the codes match."
                : "If the codes match, press Pair on both computers. If they don't, press Cancel.";
    }

    void ShowError(string message)
    {
        SetStage(Stage.Error);
        MessageText.Text = message;
    }

    void SetStage(Stage value)
    {
        stage = value;
        ChoosePage.Visibility = value == Stage.Choose ? Visibility.Visible : Visibility.Collapsed;
        CodePage.Visibility = value == Stage.Code ? Visibility.Visible : Visibility.Collapsed;
        MessageText.Visibility = value is Stage.Connecting or Stage.Done or Stage.Error ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = value == Stage.Done ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButton.Visibility = value == Stage.Connecting ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButton.IsEnabled = true;
        PrimaryButton.Content = value switch
        {
            Stage.Choose => "Next",
            Stage.Error => session?.Incoming == true ? "Close" : "Back",
            Stage.Done => "Close",
            _ => "Pair",
        };
        if (value == Stage.Error && session?.Incoming == true)
            stage = Stage.Done;
        if (value == Stage.Choose)
        {
            session = null;
            UpdateNextButton();
        }
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        timer.Stop();
        if (session is { State: PairingState.Waiting })
            session.Cancel();
        base.OnClosed(e);
    }
}
