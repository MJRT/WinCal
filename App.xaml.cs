using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Hardcodet.Wpf.TaskbarNotification;
using WinCal.Core.Helpers;
using WinCal.Core.Services;
using WinCal.Views;

namespace WinCal;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private PopupWindow? _popup;
    private SettingsWindow? _settingsWindow;
    private SystemCalendarInterceptor? _interceptor;

    private const byte VK_LWIN = 0x5B;
    private const byte VK_N = 0x4E;
    private const byte VK_ESCAPE = 0x1B;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局异常处理
        DispatcherUnhandledException += (s, args) =>
        {
            System.Diagnostics.Debug.WriteLine($"WinCal: Unhandled exception: {args.Exception}");
            args.Handled = true;
        };

        // 初始化托盘图标
        _trayIcon = (TaskbarIcon)FindResource("TrayIcon")!;

        // 左键点击弹出日历
        _trayIcon.TrayLeftMouseDown += (s, args) => TogglePopup();

        // 动态生成带今日日期数字的图标
        _trayIcon.Icon = TrayIconGenerator.Generate(DateTime.Today.Day);

        // 更新托盘提示文本
        _trayIcon.ToolTipText = $"miniCal - {DateTime.Now:yyyy年M月d日 dddd}";

        // 应用保存的主题设置
        var settings = AppSettings.Load();
        ThemeHelper.ApplyTheme(settings.ThemeMode);

        // 动态创建右键菜单
        var menu = new ContextMenu();

        var settingsItem = new MenuItem { Header = "设置" };
        settingsItem.Click += (s, args) => OpenSettings();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (s, args) => Shutdown();
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenu = menu;

        // 启动系统日历拦截器：点击任务栏时钟时替换为我们的面板
        try
        {
            _interceptor = new SystemCalendarInterceptor(Dispatcher);
            _interceptor.Start(TogglePopup, ToggleWindowsNotificationCenter);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WinCal: Interceptor failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 显示设置窗口（独立顶层窗口，单例模式）
    /// 可从右键菜单或齿轮按钮调用
    /// </summary>
    public static void ShowSettings()
    {
        try
        {
            var app = (App)Current;
            if (app._settingsWindow != null && app._settingsWindow.IsVisible)
            {
                // 已打开则激活
                app._settingsWindow.Activate();
                return;
            }

            app._settingsWindow = new SettingsWindow();
            app._settingsWindow.Closed += (_, _) => app._settingsWindow = null;
            app._settingsWindow.Show();
            app._settingsWindow.Activate();
        }
        catch (Exception ex)
        {
            var logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "wincal_error.log");
            System.IO.File.WriteAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{ex}\n\n--- InnerException ---\n{ex.InnerException}");
            MessageBox.Show($"错误已写入桌面 minical_error.log", "miniCal 错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenSettings()
    {
        // 使用 Dispatcher 延迟打开，避免与右键菜单的弹出窗口冲突
        Dispatcher.BeginInvoke(new Action(() =>
        {
            System.Diagnostics.Debug.WriteLine("WinCal: OpenSettings dispatcher callback executing");
            ShowSettings();
        }));
    }

    private void ToggleWindowsNotificationCenter()
    {
        try
        {
            // WinCal 和原生通知中心互斥：右键切到原生面板前先隐藏 WinCal。
            if (_popup != null && _popup.IsVisible)
                _popup.Hide();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Win+N 本身就是原生通知中心的 toggle：
                // 已显示时关闭，未显示时打开。
                keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
                keybd_event(VK_N, 0, 0, UIntPtr.Zero);
                keybd_event(VK_N, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WinCal: ToggleWindowsNotificationCenter error: {ex}");
        }
    }

    /// <summary>
    /// 切换 WinCal 面板。
    /// WinCal 已显示时再次左键会隐藏；若原生通知中心正在前台，则先关闭原生面板再显示 WinCal。
    /// </summary>
    private void TogglePopup()
    {
        try
        {
            if (_popup != null && _popup.IsVisible)
            {
                _popup.Hide();
                return;
            }

            CloseNativeShellFlyoutIfForeground();

            _popup?.Close();
            _popup = new PopupWindow();
            _popup.Show();
            WindowPositionHelper.PositionNearTaskbar(_popup);
            _popup.Activate();
            _popup.StartFocusTracking();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WinCal: TogglePopup error: {ex}");
            _popup = null;
        }
    }

    private static void CloseNativeShellFlyoutIfForeground()
    {
        if (!IsNativeShellFlyoutForeground())
            return;

        keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
        keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static bool IsNativeShellFlyoutForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return false;

        var className = new StringBuilder(256);
        if (GetClassName(hwnd, className, className.Capacity) <= 0)
            return false;

        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
            return false;

        string processName;
        try
        {
            processName = Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
            return false;
        }

        var classText = className.ToString();

        // Windows 11 24H2+.
        if (processName.Equals("ShellHost", StringComparison.OrdinalIgnoreCase) &&
            classText.Equals("ControlCenterWindow", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Windows 11 21H2-23H2 and compatible ShellExperienceHost paths.
        return processName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) &&
               classText.StartsWith("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _interceptor?.Dispose();
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
