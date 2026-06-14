using EdgeKit.App.Interaction;
using EdgeKit.Core.Recent;
using EdgeKit.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace EdgeKit.App.Views;

/// <summary>
/// 设置中心页。编辑各项设置，控件值变化即写入 <see cref="ISettingsService"/> 并落库；
/// 抽屉宽度变化通过设置服务的 Changed 事件被抽屉窗口实时响应。
/// </summary>
public sealed partial class SettingsPage : Page
{
    private ISettingsService? _settings;
    private readonly IStartupLaunchService _startupLaunch;
    private bool _isRecordingHotkey;
    private HotkeyRecorder? _hotkeyRecorder;

    // 初始填充控件时为 true，避免赋值反过来触发保存。
    private bool _loading;
    private const int MinDrawerWidth = 360;
    private const int MinQuickLaunchVisibleRows = 1;
    private const int MaxQuickLaunchVisibleRows = 4;
    private const int MinRecentToolsVisibleRows = 1;
    private const int MaxRecentToolsVisibleRows = 4;
    private const double MaxDrawerScreenRatio = 0.8;
    private const double OpenPaneLengthDip = 200;
    private const double CompactPaneLengthDip = 48;

    public SettingsPage()
    {
        InitializeComponent();
        _startupLaunch = App.Services.GetRequiredService<IStartupLaunchService>();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is ISettingsService settings)
        {
            _settings = settings;
            LoadValues();

            // 页面可见期间订阅设置变更（如拖拽调宽时实时回填数值）。
            _settings.Changed += OnSettingsChanged;
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        if (_isRecordingHotkey)
        {
            StopGlobalHotkeyRecording(saveCurrentText: false);
        }

        if (_settings is not null)
        {
            _settings.Changed -= OnSettingsChanged;
        }
    }

    private void OnSettingsChanged(object? sender, System.EventArgs e)
    {
        // 外部变更（拖拽调宽等）回填控件；_loading 守卫避免反向触发保存。
        DispatcherQueue.TryEnqueue(LoadValues);
    }

    private void LoadValues()
    {
        if (_settings is null)
        {
            return;
        }

        _loading = true;

        var maxDrawerWidth = GetMaxDrawerWidth();
        DrawerWidthBox.Maximum = maxDrawerWidth;
        DrawerWidthBox.Value = ClampDrawerWidth(_settings.DrawerWidth);
        StartupLaunchSwitch.IsOn = _startupLaunch.GetStatus().IsEnabled;
        UseEverythingSearchSwitch.IsOn = _settings.UseEverythingSearch;
        MenuExpandedSwitch.IsOn = _settings.MenuExpanded;
        SelectTriggerSides(_settings.TriggerSides);
        EdgeHandleWidthBox.Value = _settings.EdgeHandleWidth;
        EdgeHandleHeightBox.Maximum = GetMaxEdgeHandleHeight();
        EdgeHandleHeightBox.Value = ClampEdgeHandleHeight(_settings.EdgeHandleHeight);
        AutoHideDelayBox.Value = _settings.AutoHideDelayMs;
        AnimationDurationBox.Value = _settings.AnimationDurationMs;
        DisableInFullscreenSwitch.IsOn = _settings.DisableInFullscreen;
        if (!_isRecordingHotkey)
        {
            GlobalHotkeyBox.Text = _settings.GlobalHotkeyText;
        }
        TrackRecentWindowsSwitch.IsOn = _settings.TrackRecentWindows;
        TrackRecentToolsSwitch.IsOn = _settings.TrackRecentTools;
        RecentToolsVisibleRowsBox.Value = _settings.RecentToolsVisibleRows;
        ShowHomeClipboardHistorySwitch.IsOn = _settings.ShowHomeClipboardHistory;
        QuickLaunchVisibleRowsBox.Value = _settings.QuickLaunchVisibleRows;

        _loading = false;
    }

    private void OnDrawerWidthChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || _settings is null || double.IsNaN(args.NewValue))
        {
            return;
        }

        var clamped = ClampDrawerWidth(args.NewValue);
        if (Math.Abs(clamped - args.NewValue) > 0.1)
        {
            _loading = true;
            sender.Value = clamped;
            _loading = false;
        }

        _settings.DrawerWidth = clamped;
        _settings.Save();
    }

    private void OnMenuExpandedToggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_loading || _settings is null)
        {
            return;
        }

        _settings.MenuExpanded = MenuExpandedSwitch.IsOn;
        var clampedWidth = ClampDrawerWidth(_settings.DrawerWidth);
        _loading = true;
        DrawerWidthBox.Maximum = GetMaxDrawerWidth();
        if (clampedWidth != _settings.DrawerWidth)
        {
            DrawerWidthBox.Value = clampedWidth;
        }
        _loading = false;

        if (clampedWidth != _settings.DrawerWidth)
        {
            _settings.DrawerWidth = clampedWidth;
        }

        _settings.Save();
    }

    private void OnUseEverythingSearchToggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_loading || _settings is null)
        {
            return;
        }

        _settings.UseEverythingSearch = UseEverythingSearchSwitch.IsOn;
        _settings.Save();
    }

    private void OnStartupLaunchToggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        StartupLaunchErrorBar.IsOpen = false;
        var requested = StartupLaunchSwitch.IsOn;
        var result = _startupLaunch.SetEnabled(requested);
        if (result.Succeeded)
        {
            return;
        }

        _loading = true;
        StartupLaunchSwitch.IsOn = _startupLaunch.GetStatus().IsEnabled;
        _loading = false;

        StartupLaunchErrorBar.Title = "开机自启设置失败";
        StartupLaunchErrorBar.Message = string.IsNullOrWhiteSpace(result.ErrorMessage)
            ? "请稍后重试。"
            : result.ErrorMessage;
        StartupLaunchErrorBar.IsOpen = true;
    }

    private int ClampDrawerWidth(double value)
        => Math.Clamp((int)Math.Round(value), MinDrawerWidth, GetMaxDrawerWidth());

    private static int ClampRecentToolsVisibleRows(double value)
        => Math.Clamp((int)Math.Round(value), MinRecentToolsVisibleRows, MaxRecentToolsVisibleRows);

    private static int ClampQuickLaunchVisibleRows(double value)
        => Math.Clamp((int)Math.Round(value), MinQuickLaunchVisibleRows, MaxQuickLaunchVisibleRows);

    private int ClampEdgeHandleHeight(double value)
        => Math.Clamp((int)Math.Round(value), 120, GetMaxEdgeHandleHeight());

    private int GetMaxEdgeHandleHeight()
    {
        var area = DisplayArea.Primary;
        return Math.Max(120, area?.WorkArea.Height ?? 720);
    }

    private int GetMaxDrawerWidth()
    {
        var area = DisplayArea.Primary;
        var workWidth = area?.WorkArea.Width ?? 1280;
        var maxEffectiveWidth = Math.Max(MinDrawerWidth, (int)Math.Floor(workWidth * MaxDrawerScreenRatio));
        var paneExtra = _settings?.MenuExpanded == true
            ? (int)Math.Round((OpenPaneLengthDip - CompactPaneLengthDip) * GetDpiScale())
            : 0;

        return Math.Max(MinDrawerWidth, maxEffectiveWidth - paneExtra);
    }

    private double GetDpiScale()
    {
        var scale = XamlRoot?.RasterizationScale ?? 0;
        return scale > 0 ? scale : 1.0;
    }

    private void OnTriggerSidesChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _settings is null)
        {
            return;
        }

        _settings.TriggerSides = GetSelectedTriggerSides();
        _settings.Save();
    }

    private void OnEdgeHandleWidthChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || _settings is null || double.IsNaN(args.NewValue))
        {
            return;
        }

        _settings.EdgeHandleWidth = (int)Math.Round(args.NewValue);
        _settings.Save();
    }

    private void OnEdgeHandleHeightChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || _settings is null || double.IsNaN(args.NewValue))
        {
            return;
        }

        var clamped = ClampEdgeHandleHeight(args.NewValue);
        if (Math.Abs(clamped - args.NewValue) > 0.1)
        {
            _loading = true;
            sender.Value = clamped;
            _loading = false;
        }

        _settings.EdgeHandleHeight = clamped;
        _settings.Save();
    }

    private void OnAutoHideDelayChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || _settings is null || double.IsNaN(args.NewValue))
        {
            return;
        }

        _settings.AutoHideDelayMs = (int)args.NewValue;
        _settings.Save();
    }

    private void OnAnimationDurationChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || _settings is null || double.IsNaN(args.NewValue))
        {
            return;
        }

        _settings.AnimationDurationMs = (int)args.NewValue;
        _settings.Save();
    }

    private void OnDisableInFullscreenToggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_loading || _settings is null)
        {
            return;
        }

        _settings.DisableInFullscreen = DisableInFullscreenSwitch.IsOn;
        _settings.Save();
    }

    private void SelectTriggerSides(EdgeTriggerSides sides)
    {
        var tag = sides == EdgeTriggerSides.Right
            ? "Right"
            : sides == EdgeTriggerSides.Both
                ? "Both"
                : "Left";

        foreach (var item in TriggerSidesBox.Items.OfType<ComboBoxItem>())
        {
            if ((item.Tag as string) == tag)
            {
                TriggerSidesBox.SelectedItem = item;
                return;
            }
        }
    }

    private EdgeTriggerSides GetSelectedTriggerSides()
    {
        var tag = (TriggerSidesBox.SelectedItem as ComboBoxItem)?.Tag as string;
        return tag switch
        {
            "Right" => EdgeTriggerSides.Right,
            "Both" => EdgeTriggerSides.Both,
            _ => EdgeTriggerSides.Left
        };
    }

    private void OnGlobalHotkeyBoxPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        StartGlobalHotkeyRecording();
        GlobalHotkeyBox.Focus(Microsoft.UI.Xaml.FocusState.Pointer);
        e.Handled = true;
    }

    private void OnGlobalHotkeyBoxGotFocus(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        StartGlobalHotkeyRecording();
    }

    private void OnGlobalHotkeyBoxLostFocus(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_isRecordingHotkey)
        {
            StopGlobalHotkeyRecording(saveCurrentText: false);
        }
    }

    private void OnGlobalHotkeyKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isRecordingHotkey)
        {
            StartGlobalHotkeyRecording();
        }

        e.Handled = true;

        if (_loading || _settings is null)
        {
            return;
        }

        if (HotkeyGesture.IsModifierKey(e.Key))
        {
            GlobalHotkeyBox.Text = GetPressedModifierPreview();
            return;
        }

        if (HotkeyGesture.TryCreateFromCurrentKeyboardState(e.Key, out var gesture))
        {
            SaveGlobalHotkeyGesture(gesture);
        }
    }

    private void OnGlobalHotkeyKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!_isRecordingHotkey)
        {
            return;
        }

        e.Handled = true;

        if (_settings is null || !HotkeyGesture.IsModifierKey(e.Key) || HotkeyGesture.IsAnyModifierDown())
        {
            return;
        }

        if (HotkeyGesture.TryCreateModifierOnly(e.Key, out var gesture))
        {
            SaveGlobalHotkeyGesture(gesture);
        }
    }

    private void StartGlobalHotkeyRecording()
    {
        if (_loading || _settings is null || _isRecordingHotkey)
        {
            return;
        }

        _isRecordingHotkey = true;
        App.IsRecordingGlobalHotkey = true;
        App.GlobalHotkey?.SuspendRegistration();
        GlobalHotkeyBox.Text = "按下快捷键...";
        GlobalHotkeyBox.SelectAll();

        _hotkeyRecorder?.Dispose();
        _hotkeyRecorder = new HotkeyRecorder(
            DispatcherQueue,
            SaveGlobalHotkeyGesture,
            preview => GlobalHotkeyBox.Text = preview);
        if (!_hotkeyRecorder.Start())
        {
            _hotkeyRecorder.Dispose();
            _hotkeyRecorder = null;
        }
    }

    private void StopGlobalHotkeyRecording(bool saveCurrentText)
    {
        if (!_isRecordingHotkey)
        {
            return;
        }

        _isRecordingHotkey = false;
        App.IsRecordingGlobalHotkey = false;
        _hotkeyRecorder?.Dispose();
        _hotkeyRecorder = null;
        App.GlobalHotkey?.ResumeRegistration();

        if (!saveCurrentText && _settings is not null)
        {
            GlobalHotkeyBox.Text = _settings.GlobalHotkeyText;
        }
    }

    private void SaveGlobalHotkeyGesture(HotkeyGesture gesture)
    {
        if (_settings is null)
        {
            StopGlobalHotkeyRecording(saveCurrentText: false);
            return;
        }

        _settings.GlobalHotkeyText = gesture.ToString();
        _settings.Save();
        GlobalHotkeyBox.Text = _settings.GlobalHotkeyText;
        StopGlobalHotkeyRecording(saveCurrentText: true);
    }

    private static string GetPressedModifierPreview()
    {
        var parts = new List<string>();
        if (HotkeyGesture.IsAnyModifierDown())
        {
            if ((EdgeKit.Native.NativeMethods.GetKeyState((int)VirtualKey.Control) & unchecked((short)0x8000)) != 0)
            {
                parts.Add("Ctrl");
            }

            if ((EdgeKit.Native.NativeMethods.GetKeyState((int)VirtualKey.Menu) & unchecked((short)0x8000)) != 0)
            {
                parts.Add("Alt");
            }

            if ((EdgeKit.Native.NativeMethods.GetKeyState((int)VirtualKey.Shift) & unchecked((short)0x8000)) != 0)
            {
                parts.Add("Shift");
            }

            if ((EdgeKit.Native.NativeMethods.GetKeyState((int)VirtualKey.LeftWindows) & unchecked((short)0x8000)) != 0
                || (EdgeKit.Native.NativeMethods.GetKeyState((int)VirtualKey.RightWindows) & unchecked((short)0x8000)) != 0)
            {
                parts.Add("Win");
            }
        }

        return parts.Count == 0
            ? "按下快捷键..."
            : string.Join("+", parts) + "+...";
    }

    private void OnTrackRecentWindowsToggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_loading || _settings is null)
        {
            return;
        }

        _settings.TrackRecentWindows = TrackRecentWindowsSwitch.IsOn;
        _settings.Save();
    }

    private void OnTrackRecentToolsToggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_loading || _settings is null)
        {
            return;
        }

        _settings.TrackRecentTools = TrackRecentToolsSwitch.IsOn;
        _settings.Save();
    }

    private void OnRecentToolsVisibleRowsChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || _settings is null || double.IsNaN(args.NewValue))
        {
            return;
        }

        var clamped = ClampRecentToolsVisibleRows(args.NewValue);
        if (Math.Abs(clamped - args.NewValue) > 0.1)
        {
            _loading = true;
            sender.Value = clamped;
            _loading = false;
        }

        _settings.RecentToolsVisibleRows = clamped;
        _settings.Save();
    }

    private void OnShowHomeClipboardHistoryToggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_loading || _settings is null)
        {
            return;
        }

        _settings.ShowHomeClipboardHistory = ShowHomeClipboardHistorySwitch.IsOn;
        _settings.Save();
    }

    private void OnQuickLaunchVisibleRowsChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || _settings is null || double.IsNaN(args.NewValue))
        {
            return;
        }

        var clamped = ClampQuickLaunchVisibleRows(args.NewValue);
        if (Math.Abs(clamped - args.NewValue) > 0.1)
        {
            _loading = true;
            sender.Value = clamped;
            _loading = false;
        }

        _settings.QuickLaunchVisibleRows = clamped;
        _settings.Save();
    }

    private void OnClearRecentClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // 从全局容器解析仓储，清除全部最近记录。
        var repo = App.Services.GetService<IRecentItemsRepository>();
        repo?.Clear(null);
    }
}
