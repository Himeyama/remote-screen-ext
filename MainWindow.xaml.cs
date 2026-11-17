using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace RemoteScreen;

public sealed partial class MainWindow : Window
{
    static readonly string DriverSetupPath =
        Path.Combine(AppContext.BaseDirectory, "VirtualDisplayDriver", "VDD Control.exe");

    readonly ResourceLoader _resources = new();
    ProjectionSession? _session;
    string _statusMessage = "";
    bool _ready;
    bool _busy;
    bool _closed;
    bool _stoppingSharing;

    public MainWindow()
    {
        InitializeComponent();
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
        }
        else
        {
            AppTitleBar.Visibility = Visibility.Collapsed;
        }

        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon))
        {
            AppWindow.SetIcon(icon);
        }

        ResizeToWorkArea();
        bool hasDriverSetup = RuntimeInformation.OSArchitecture == Architecture.X64 && File.Exists(DriverSetupPath);
        InstallDriverButton.Visibility = hasDriverSetup ? Visibility.Visible : Visibility.Collapsed;
        DriverDownloadLink.Visibility = hasDriverSetup ? Visibility.Collapsed : Visibility.Visible;
        Closed += OnClosed;
        Activated += OnActivated;
        _ready = true;
        UpdateMode();
        UpdateControls();
    }

    bool IsExtended => ModePicker.SelectedIndex == 1;

    void ResizeToWorkArea()
    {
        uint dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
        double scale = dpi == 0 ? 1 : dpi / 96d;
        int width = (int)Math.Round(960 * scale);
        int height = (int)Math.Round(640 * scale);
        DisplayArea? area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        if (area is null)
        {
            AppWindow.Resize(new SizeInt32(width, height));
            return;
        }

        RectInt32 work = area.WorkArea;
        width = Math.Min(width, work.Width);
        height = Math.Min(height, work.Height);
        AppWindow.MoveAndResize(new RectInt32(
            work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
    }

    void UpdateMode()
    {
        DisplayActions.Visibility = IsExtended ? Visibility.Visible : Visibility.Collapsed;
        ModeHint.Visibility = DisplayActions.Visibility;
        ModeHint.Text = _resources.GetString("ExtendedModeHint");
        LoadDisplays();
    }

    void LoadDisplays()
    {
        string? selected = (DisplayPicker.SelectedItem as Display)?.Name;
        List<Display> displays = DesktopCapture.ListDisplays()
            .Where(display => !IsExtended || !display.IsPrimary)
            .OrderByDescending(display => display.IsPrimary)
            .ToList();
        DisplayPicker.ItemsSource = displays;
        DisplayPicker.SelectedItem = displays.Find(display => display.Name == selected)
            ?? displays.FirstOrDefault();
        DisplayPicker.PlaceholderText = _resources.GetString(IsExtended ? "NoExtendedDisplays" : "NoDisplays");
    }

    void UpdateControls()
    {
        if (!_ready || _closed)
        {
            return;
        }

        bool sharing = _session is not null;
        ModePicker.IsEnabled = !_busy && !sharing;
        DisplayActions.IsHitTestVisible = !_busy && !sharing;
        DisplayPicker.IsEnabled = !_busy && !sharing;
        DisplaySettingsButton.IsEnabled = !_busy && !sharing;
        RefreshButton.IsEnabled = !_busy && !sharing;
        InstallDriverButton.IsEnabled = !_busy && !sharing;
        ClearDevicesButton.IsEnabled = !_busy;
        StartButton.IsEnabled = !_busy && !sharing && DisplayPicker.SelectedItem is Display;
        StopButton.IsEnabled = !_busy && sharing;
        AddressPicker.Visibility = sharing ? Visibility.Visible : Visibility.Collapsed;
        ConnectionLink.Visibility = sharing ? Visibility.Visible : Visibility.Collapsed;
        ConnectionCode.Visibility = ConnectionLink.Visibility;
        ConnectionActions.Visibility = ConnectionLink.Visibility;
        ConnectionCode.Text = _session?.Code ?? "";
        ConnectionHint.Text = sharing
            ? "初回は共有リンクを開くか、認証コードを入力してください。次回は同じブラウザで接続先を開くだけで投影できます。"
            : "共有を開始すると接続用のリンクが表示されます。";
        UpdateSharingStatus();
    }

    void UpdateSharingStatus()
    {
        if (_closed)
        {
            return;
        }

        string status = _session is { } session
            ? string.Format(CultureInfo.CurrentCulture, _resources.GetString("SharingActive"),
                session.DisplayLabel, session.ViewerCount)
            : _resources.GetString(_stoppingSharing ? "SharingStopping" : "SharingInactive");
        if (_statusMessage.Length != 0)
        {
            status = $"{status}　｜　{_statusMessage}";
        }

        if (SharingStatus.Text != status)
        {
            SharingStatus.Text = status;
            ToolTipService.SetToolTip(SharingStatus, status);
        }
    }

    void SetStatusMessage(string message)
    {
        _statusMessage = message;
        UpdateSharingStatus();
    }

    void ModeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_ready)
        {
            UpdateMode();
            UpdateControls();
        }
    }

    void DisplayChanged(object sender, SelectionChangedEventArgs args) => UpdateControls();

    void RefreshDisplays(object sender, RoutedEventArgs args)
    {
        LoadDisplays();
        UpdateControls();
    }

    void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated
            && _ready && !_closed && !_busy && _session is null)
        {
            LoadDisplays();
            UpdateControls();
        }
    }

    async void OpenDisplaySettings(object sender, RoutedEventArgs args) =>
        await Launcher.LaunchUriAsync(new Uri("ms-settings:display"));

    void OpenDriverSetup(object sender, RoutedEventArgs args)
    {
        try
        {
            using Process? setup = Process.Start(new ProcessStartInfo(DriverSetupPath)
            {
                WorkingDirectory = Path.GetDirectoryName(DriverSetupPath)!,
                UseShellExecute = true
            });
        }
        catch (Win32Exception error) when (error.NativeErrorCode != 1223)
        {
            SetStatusMessage(FormatMessage("DriverSetupFailed", error.Message));
        }
        catch (Win32Exception)
        {
            SetStatusMessage(_resources.GetString("DriverSetupCanceled"));
        }
    }

    async void StartSharing(object sender, RoutedEventArgs args)
    {
        if (DisplayPicker.SelectedItem is not Display display)
        {
            return;
        }

        _busy = true;
        UpdateControls();
        SetStatusMessage("共有を開始しています…");
        ProjectionSession? started = null;
        try
        {
            IPAddress[] addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Select(address => address.Address)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                .Distinct()
                .Append(IPAddress.Loopback)
                .ToArray();

            started = new ProjectionSession(display);
            ProjectionSession session = started;
            string? captureError = null;
            session.CaptureFailed += message => DispatcherQueue.TryEnqueue(async () =>
            {
                captureError = message;
                if (_session == session)
                {
                    await EndSharing($"共有を停止しました。{message}");
                }
            });
            session.ViewerCountChanged += () => DispatcherQueue.TryEnqueue(() =>
            {
                if (_session == session && !_closed)
                {
                    UpdateSharingStatus();
                }
            });
            await session.StartAsync();
            if (_closed)
            {
                return;
            }
            if (captureError is not null)
            {
                throw new InvalidOperationException(captureError);
            }

            _session = session;
            started = null;
            AddressPicker.ItemsSource = addresses;
            AddressPicker.SelectedIndex = 0;
            UpdateLink();
            SetStatusMessage("");
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                SetStatusMessage($"共有を開始できませんでした。{exception.Message}");
            }
        }
        finally
        {
            if (started is not null)
            {
                await DisposeSession(started);
            }

            _busy = false;
            UpdateControls();
        }
    }

    void AddressChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_ready)
        {
            UpdateLink();
        }
    }

    void UpdateLink() => ConnectionLink.Text =
        _session?.GetLink(AddressPicker.SelectedItem as IPAddress ?? IPAddress.Loopback) ?? "";

    void CopyLink(object sender, RoutedEventArgs args)
    {
        DataPackage data = new();
        data.SetText(ConnectionLink.Text);
        Clipboard.SetContent(data);
        SetStatusMessage("共有リンクをコピーしました。");
    }

    async void OpenViewer(object sender, RoutedEventArgs args)
    {
        if (_session is not null)
        {
            await Launcher.LaunchUriAsync(new Uri(_session.GetLink(IPAddress.Loopback)));
        }
    }

    async void StopSharing(object sender, RoutedEventArgs args) => await EndSharing("共有を停止しました。");

    async void ClearDevices(object sender, RoutedEventArgs args)
    {
        ContentDialog confirm = new()
        {
            XamlRoot = ClearDevicesButton.XamlRoot,
            Title = _resources.GetString("ClearDevicesTitle"),
            Content = new TextBlock
            {
                Text = _resources.GetString("ClearDevicesMessage"),
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = _resources.GetString("ClearDevicesConfirm"),
            CloseButtonText = _resources.GetString("Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() == ContentDialogResult.Primary && !_closed && !_busy)
        {
            await EndSharing(_resources.GetString("DevicesCleared"), clearDevices: true);
        }
    }

    string FormatMessage(string key, string detail) =>
        string.Format(CultureInfo.CurrentCulture, _resources.GetString(key), detail);

    async Task EndSharing(string status, bool clearDevices = false)
    {
        ProjectionSession? session = _session;
        _session = null;
        _busy = true;
        _stoppingSharing = session is not null;
        UpdateControls();
        string? stopError = session is null ? null : await DisposeSession(session);
        _stoppingSharing = false;
        UpdateSharingStatus();
        if (stopError is not null)
        {
            status = clearDevices ? FormatMessage("ClearDevicesFailed", stopError) : $"{status} {stopError}";
        }
        else if (clearDevices)
        {
            try
            {
                await Task.Run(RememberedDevices.ClearSavedApprovals);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                status = FormatMessage("ClearDevicesFailed", error.Message);
            }
        }

        _busy = false;
        if (!_closed)
        {
            ConnectionLink.Text = "";
            SetStatusMessage(status);
            UpdateControls();
        }
    }

    async void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        await EndSharing("共有を停止しました。");
    }

    static async Task<string?> DisposeSession(ProjectionSession session)
    {
        try
        {
            await session.DisposeAsync();
            return null;
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
    }

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(nint handle);
}
