using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.UI.ViewManagement;
using Microsoft.Win32;
using WinRT.Interop;
using VoicesDesktop.Services;

namespace VoicesDesktop;

public sealed partial class MainWindow : Window
{
    private readonly IntPtr _hWnd;
    private readonly AppWindow _appWindow;
    private readonly TtsService _ttsService;
    private readonly HotkeyManager _hotkeyManager;
    private readonly DispatcherTimer _waveformTimer;
    private readonly DispatcherTimer _healthCheckTimer;
    private readonly Random _rand = new();
    private readonly UISettings _uiSettings = new();

    private string _currentVoice = "alba";
    private double _currentSpeed = 1.0;
    private double _wavePhase = 0.0;
    private bool _trayIconRegistered = false;

    // Win32 Taskbar & Window Interop
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // Win32 Subclassing
    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    // Shell_NotifyIcon (System Tray) Interop
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private const uint WM_SETICON = 0x0080;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const IntPtr ICON_SMALL = (IntPtr)0;
    private const IntPtr ICON_BIG = (IntPtr)1;

    private const uint WM_USER = 0x0400;
    private const uint WM_TRAYICON = WM_USER + 102;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;
    private const int NIIF_INFO = 0x00000001;

    private const int TRAY_SUBCLASS_ID = 9002;
    private readonly SubclassProc _traySubclassProc;
    private NOTIFYICONDATA _nid;

    public MainWindow()
    {
        this.InitializeComponent();

        _hWnd = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        _traySubclassProc = TrayWndProc;
        if (_hWnd != IntPtr.Zero)
        {
            SetWindowSubclass(_hWnd, _traySubclassProc, (UIntPtr)TRAY_SUBCLASS_ID, UIntPtr.Zero);
        }

        // Configure window size, icon, system tray, and native title bar
        SetupWindowAndIcons();

        // Initialize TTS Service
        _ttsService = new TtsService("http://127.0.0.1:8000");
        _ttsService.StatusChanged += OnTtsStatusChanged;
        _ttsService.ErrorOccurred += OnTtsErrorOccurred;

        // Initialize Dual-Channel System-Wide Hotkey (Alt + C)
        _hotkeyManager = new HotkeyManager(_hWnd);
        _hotkeyManager.HotkeyPressed += OnHotkeyPressed;
        _hotkeyManager.Register();

        // Waveform Animation Timer (~30 FPS)
        _waveformTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _waveformTimer.Tick += OnWaveformTimerTick;

        // Server Health Check Timer (runs every 6 seconds)
        _healthCheckTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(6)
        };
        _healthCheckTimer.Tick += async (s, e) => await CheckServerHealthAsync();
        _healthCheckTimer.Start();
        _ = CheckServerHealthAsync();

        // Dynamic System Theme Adapter (Auto Light / Dark Mode)
        _uiSettings.ColorValuesChanged += OnColorValuesChanged;
        ApplyCurrentTheme();

        // Cleanup on window closing
        this.Closed += OnWindowClosed;
    }

    private void SetupWindowAndIcons()
    {
        const int width = 380;
        const int height = 500;
        _appWindow.Resize(new SizeInt32(width, height));

        var presenter = _appWindow.Presenter as OverlappedPresenter;
        if (presenter != null)
        {
            presenter.IsMaximizable = false;
            presenter.IsResizable = false;
        }

        // Center on primary display
        var displayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        if (displayArea != null)
        {
            var centeredPosition = new PointInt32(
                (displayArea.WorkArea.Width - width) / 2,
                (displayArea.WorkArea.Height - height) / 2
            );
            _appWindow.Move(centeredPosition);
        }

        // Set App & Taskbar Icons
        string icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        IntPtr hIconSmall = IntPtr.Zero;

        if (File.Exists(icoPath))
        {
            try
            {
                _appWindow.SetIcon(icoPath);
                IntPtr hIconBig = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
                hIconSmall = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);

                if (hIconBig != IntPtr.Zero) SendMessage(_hWnd, WM_SETICON, ICON_BIG, hIconBig);
                if (hIconSmall != IntPtr.Zero) SendMessage(_hWnd, WM_SETICON, ICON_SMALL, hIconSmall);
            }
            catch { }
        }

        // Initialize System Tray Icon
        InitSystemTray(hIconSmall);

        // Native Title Bar Integration
        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(AppTitleBar);

        ApplyCurrentTheme();
    }

    private bool IsWindowsInLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int val)
            {
                return val == 1;
            }
        }
        catch { }

        try
        {
            var bg = _uiSettings.GetColorValue(UIColorType.Background);
            return (bg.R + bg.G + bg.B) > 384;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyCurrentTheme()
    {
        bool isLight = IsWindowsInLightTheme();
        if (RootGrid != null)
        {
            RootGrid.RequestedTheme = isLight ? ElementTheme.Light : ElementTheme.Dark;
        }

        if (AppLogoImage != null)
        {
            string logoFile = isLight ? "logo_black.png" : "logo_white.png";
            string logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", logoFile);
            if (File.Exists(logoPath))
            {
                AppLogoImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(logoPath));
            }
        }

        if (AppWindowTitleBar.IsCustomizationSupported() && _appWindow?.TitleBar != null)
        {
            var titleBar = _appWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

            if (isLight)
            {
                titleBar.ButtonForegroundColor = ColorHelper.FromArgb(255, 13, 13, 13);
                titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(255, 230, 230, 230);
                titleBar.ButtonHoverForegroundColor = Colors.Black;
                titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(255, 210, 210, 210);
                titleBar.ButtonPressedForegroundColor = Colors.Black;
            }
            else
            {
                titleBar.ButtonForegroundColor = ColorHelper.FromArgb(255, 236, 236, 236);
                titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(255, 47, 47, 47);
                titleBar.ButtonHoverForegroundColor = Colors.White;
                titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(255, 60, 60, 60);
                titleBar.ButtonPressedForegroundColor = Colors.White;
            }
        }
    }

    private void OnColorValuesChanged(UISettings sender, object args)
    {
        DispatcherQueue.TryEnqueue(ApplyCurrentTheme);
    }

    private void InitSystemTray(IntPtr hIcon)
    {
        _nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hWnd,
            uID = 1001,
            uFlags = (int)(NIF_MESSAGE | NIF_ICON | NIF_TIP),
            uCallbackMessage = (int)WM_TRAYICON,
            hIcon = hIcon,
            szTip = "Voices — Neural Speech (Alt + C)"
        };

        _trayIconRegistered = Shell_NotifyIcon(NIM_ADD, ref _nid);
    }

    public void ShowTrayNotification(string title, string message)
    {
        if (!_trayIconRegistered) return;
        try
        {
            _nid.uFlags = (int)(NIF_INFO | NIF_TIP);
            _nid.szInfoTitle = title;
            _nid.szInfo = message.Length > 200 ? message.Substring(0, 197) + "…" : message;
            _nid.dwInfoFlags = NIIF_INFO;
            Shell_NotifyIcon(NIM_MODIFY, ref _nid);
        }
        catch { }
    }

    private IntPtr TrayWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (uMsg == WM_TRAYICON)
        {
            int action = lParam.ToInt32();
            if (action == WM_LBUTTONUP || action == WM_LBUTTONDBLCLK)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    _appWindow.Show();
                    var presenter = _appWindow.Presenter as OverlappedPresenter;
                    presenter?.Restore();
                    SetForegroundWindow(_hWnd);
                });
                return IntPtr.Zero;
            }
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private async Task CheckServerHealthAsync()
    {
        bool isOnline = await _ttsService.CheckHealthAsync();
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateServerStatusUi(isOnline);
        });
    }

    private void UpdateServerStatusUi(bool isOnline)
    {
        if (ServerStatusDot != null)
        {
            ServerStatusDot.Background = isOnline
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(ColorHelper.FromArgb(255, 16, 185, 129))
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(ColorHelper.FromArgb(255, 239, 68, 68));
            ToolTipService.SetToolTip(ServerStatusDot, isOnline
                ? "TTS Server Online (127.0.0.1:8000)"
                : "TTS Server Offline: run py server\\server.py");
        }
    }

    private async void OnHotkeyPressed(object? sender, EventArgs e)
    {
        // If already speaking or synthesizing, stop immediately
        if (_ttsService.IsActive)
        {
            _ttsService.Stop();
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            PlaybackStateText.Text = "Capturing selection...";
        });

        // Capture highlighted text from active application
        string? text = await ClipboardHelper.CaptureSelectedTextAsync(350);

        if (!string.IsNullOrWhiteSpace(text))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                CapturedTextBox.Text = text;
            });

            ShowTrayNotification("Voices", $"Reading: {text}");
            await _ttsService.SpeakAsync(text, _currentVoice, _currentSpeed);
        }
        else
        {
            var tcs = new TaskCompletionSource<string>();
            DispatcherQueue.TryEnqueue(() =>
            {
                string existing = CapturedTextBox.Text.Trim();
                if (string.IsNullOrEmpty(existing))
                {
                    PlaybackStateText.Text = "No text selected";
                }
                tcs.SetResult(existing);
            });

            string fallbackText = await tcs.Task;
            if (!string.IsNullOrEmpty(fallbackText))
            {
                ShowTrayNotification("Voices", $"Reading: {fallbackText}");
                await _ttsService.SpeakAsync(fallbackText, _currentVoice, _currentSpeed);
            }
        }
    }

    private void QuickStopButton_Click(object sender, RoutedEventArgs e)
    {
        _ttsService.Stop();
    }

    private async void PrimaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_ttsService.IsActive)
        {
            _ttsService.Stop();
            return;
        }

        string text = CapturedTextBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            text = "Voices brings text to life with ultra-low latency neural speech.";
            CapturedTextBox.Text = text;
        }

        await _ttsService.SpeakAsync(text, _currentVoice, _currentSpeed);
    }

    private void OnTtsStatusChanged(object? sender, TtsStatus status)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (status)
            {
                case TtsStatus.Speaking:
                    PlaybackStateText.Text = "Speaking...";
                    ActionBtnIcon.Glyph = "\uE71A"; // Stop square
                    ActionBtnText.Text = "Stop";
                    _waveformTimer.Start();
                    break;

                case TtsStatus.Synthesizing:
                    PlaybackStateText.Text = "Synthesizing...";
                    ActionBtnIcon.Glyph = "\uE895";
                    ActionBtnText.Text = "Generating...";
                    break;

                case TtsStatus.Idle:
                default:
                    PlaybackStateText.Text = "Ready";
                    ActionBtnIcon.Glyph = "\uE768"; // Play triangle
                    ActionBtnText.Text = "Speak Selection";
                    _waveformTimer.Stop();
                    ResetWaveform();
                    break;
            }
        });
    }

    private void OnTtsErrorOccurred(object? sender, string errorMessage)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateServerStatusUi(false);
            PlaybackStateText.Text = "Server offline: run py server\\server.py";
            ActionBtnIcon.Glyph = "\uE768";
            ActionBtnText.Text = "Speak Selection";
            ResetWaveform();
            ShowTrayNotification("Voices", "Server offline: run py server\\server.py");
        });
    }

    private void OnWaveformTimerTick(object? sender, object e)
    {
        _wavePhase += 0.28;

        double h1 = 6 + Math.Sin(_wavePhase) * 6 + _rand.Next(0, 4);
        double h2 = 8 + Math.Sin(_wavePhase + 0.8) * 8 + _rand.Next(0, 5);
        double h3 = 12 + Math.Sin(_wavePhase + 1.6) * 10 + _rand.Next(0, 6);
        double h4 = 8 + Math.Sin(_wavePhase + 2.4) * 8 + _rand.Next(0, 5);
        double h5 = 6 + Math.Sin(_wavePhase + 3.2) * 6 + _rand.Next(0, 4);

        WaveBar1.Height = Math.Clamp(h1, 4, 18);
        WaveBar2.Height = Math.Clamp(h2, 5, 20);
        WaveBar3.Height = Math.Clamp(h3, 6, 22);
        WaveBar4.Height = Math.Clamp(h4, 5, 20);
        WaveBar5.Height = Math.Clamp(h5, 4, 18);
    }

    private void ResetWaveform()
    {
        WaveBar1.Height = 6;
        WaveBar2.Height = 10;
        WaveBar3.Height = 14;
        WaveBar4.Height = 10;
        WaveBar5.Height = 6;
    }

    private void CopyTextButton_Click(object sender, RoutedEventArgs e)
    {
        string text = CapturedTextBox.Text;
        if (!string.IsNullOrEmpty(text))
        {
            ClipboardHelper.SetClipboardText(text);
            string prevStatus = PlaybackStateText.Text;
            PlaybackStateText.Text = "Copied to clipboard!";
            Task.Delay(1400).ContinueWith(_ => DispatcherQueue.TryEnqueue(() => PlaybackStateText.Text = prevStatus));
        }
    }

    private void ClearTextButton_Click(object sender, RoutedEventArgs e)
    {
        CapturedTextBox.Text = string.Empty;
    }

    private void VoiceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VoiceComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _currentVoice = tag;
        }
    }

    private void SpeedSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        _currentSpeed = Math.Round(e.NewValue, 2);
        if (SpeedLabel != null)
        {
            SpeedLabel.Text = $"{_currentSpeed:F2}x";
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_trayIconRegistered)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _nid);
            _trayIconRegistered = false;
        }

        if (_hWnd != IntPtr.Zero)
        {
            RemoveWindowSubclass(_hWnd, _traySubclassProc, (UIntPtr)TRAY_SUBCLASS_ID);
        }

        _waveformTimer.Stop();
        _healthCheckTimer.Stop();
        _uiSettings.ColorValuesChanged -= OnColorValuesChanged;
        _hotkeyManager.Dispose();
        _ttsService.Dispose();
    }
}
