using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;

namespace WinCal.Core.Helpers;

/// <summary>
/// 监听任务栏时钟的真实鼠标点击。
/// 左键直接打开 WinCal，右键打开 Windows 原生通知中心；通知和闹钟本身不会触发。
/// </summary>
public class SystemCalendarInterceptor : IDisposable
{
    private IntPtr _mouseHook;
    private GCHandle _mouseGcHandle;
    private readonly Dispatcher _dispatcher;
    private Action? _toggleWinCalCallback;
    private Action? _toggleSystemCalendarCallback;
    private bool _disposed;
    private bool _suppressClockLeftButtonUp;
    private bool _suppressClockRightButtonUp;
    private readonly object _clockRectsLock = new();
    private List<RECT> _clockRects = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCal");
    private static readonly string LogPath = Path.Combine(LogDirectory, "interceptor.log");

    // Win32 常量
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int GA_ROOT = 2;
    private const double ClockFallbackWidthDip = 100;
    private const double ClockFallbackRightInsetDip = 4;

    // Win32 API
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelMouseProc lpfn,
        IntPtr hMod,
        uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk,
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(
        IntPtr hWndParent,
        IntPtr hWndChildAfter,
        string? lpszClass,
        string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    public SystemCalendarInterceptor(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// 启动拦截器，传入弹出日历面板的回调
    /// </summary>
    private static void Log(string msg)
    {
        Debug.WriteLine(msg);
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never affect taskbar input.
        }
    }

    public bool Start(Action toggleWinCalCallback, Action toggleSystemCalendarCallback)
    {
        _toggleWinCalCallback = toggleWinCalCallback;
        _toggleSystemCalendarCallback = toggleSystemCalendarCallback;

        // Resolve the taskbar clock while Explorer is stable. UI Automation can be
        // relatively expensive, so it must never run inside WH_MOUSE_LL.
        RefreshClockHitRects();

        // 只监听真实鼠标点击。通知/闹钟等 Shell 窗口不再参与触发判断。
        _mouseGcHandle = GCHandle.Alloc(new LowLevelMouseProc(MouseHookProc));
        var mouseCallback = (LowLevelMouseProc)_mouseGcHandle.Target!;
        _mouseHook = SetWindowsHookEx(
            WH_MOUSE_LL,
            mouseCallback,
            GetModuleHandle(null),
            0);

        if (_mouseHook == IntPtr.Zero)
        {
            Log("WinCal: ✗ Failed to set low-level mouse hook!");
            _mouseGcHandle.Free();
            return false;
        }

        Log("WinCal: ✓ Direct taskbar clock mouse hook started, hook=" + _mouseHook);
        return true;
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var mouse = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (wParam == (IntPtr)WM_LBUTTONDOWN)
                {
                    var isClock = IsTaskbarClockClick(mouse.pt);
                    if (isClock)
                    {
                        _suppressClockLeftButtonUp = true;
                        _dispatcher.BeginInvoke(new Action(() =>
                        {
                            Log($"Taskbar clock left-clicked at ({mouse.pt.X},{mouse.pt.Y}); toggling WinCal");
                            _toggleWinCalCallback?.Invoke();
                        }));
                        return (IntPtr)1;
                    }
                }

                if (wParam == (IntPtr)WM_LBUTTONUP && _suppressClockLeftButtonUp)
                {
                    _suppressClockLeftButtonUp = false;
                    return (IntPtr)1;
                }

                if (wParam == (IntPtr)WM_RBUTTONDOWN)
                {
                    var isClock = IsTaskbarClockClick(mouse.pt);
                    if (isClock)
                    {
                        _suppressClockRightButtonUp = true;
                        _dispatcher.BeginInvoke(new Action(() =>
                        {
                            Log($"Taskbar clock right-clicked at ({mouse.pt.X},{mouse.pt.Y}); toggling Windows notification center");
                            _toggleSystemCalendarCallback?.Invoke();
                        }));
                        return (IntPtr)1;
                    }
                }

                if (wParam == (IntPtr)WM_RBUTTONUP && _suppressClockRightButtonUp)
                {
                    _suppressClockRightButtonUp = false;
                    return (IntPtr)1;
                }
            }
        }
        catch (Exception ex)
        {
            // Never perform file I/O from WH_MOUSE_LL.
            Debug.WriteLine($"WinCal: MouseHookProc error: {ex.Message}");
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private bool IsTaskbarClockClick(POINT point)
    {
        if (!TryGetTaskbarAtPoint(point, out var taskbar))
            return false;

        return IsPointInsideCachedClock(point) ||
               IsPointInsideTaskbarClockFallback(taskbar, point);
    }

    private static bool TryGetTaskbarAtPoint(POINT point, out IntPtr taskbar)
    {
        taskbar = IntPtr.Zero;

        var window = WindowFromPoint(point);
        if (window == IntPtr.Zero)
            return false;

        var root = GetAncestor(window, GA_ROOT);
        if (root == IntPtr.Zero)
            root = window;

        var rootClass = GetWindowClassName(root);
        if (!string.Equals(rootClass, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(rootClass, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        taskbar = root;
        return true;
    }

    private bool IsPointInsideCachedClock(POINT point)
    {
        lock (_clockRectsLock)
        {
            return _clockRects.Any(rect =>
                point.X >= rect.Left && point.X < rect.Right &&
                point.Y >= rect.Top && point.Y < rect.Bottom);
        }
    }

    private void RefreshClockHitRects()
    {
        var rects = new List<RECT>();
        var taskbars = new List<IntPtr>();

        var primaryTaskbar = FindWindow("Shell_TrayWnd", null);
        if (primaryTaskbar != IntPtr.Zero)
            taskbars.Add(primaryTaskbar);

        EnumWindows((window, _) =>
        {
            if (string.Equals(
                    GetWindowClassName(window),
                    "Shell_SecondaryTrayWnd",
                    StringComparison.OrdinalIgnoreCase))
            {
                taskbars.Add(window);
            }
            return true;
        }, IntPtr.Zero);

        foreach (var taskbar in taskbars.Distinct())
        {
            var countBefore = rects.Count;

            var trayNotify = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (trayNotify != IntPtr.Zero)
            {
                var legacyClock = FindWindowEx(trayNotify, IntPtr.Zero, "TrayClockWClass", null);
                if (legacyClock != IntPtr.Zero && GetWindowRect(legacyClock, out var legacyRect))
                    rects.Add(legacyRect);
            }

            try
            {
                var taskbarElement = AutomationElement.FromHandle(taskbar);
                var descendants = taskbarElement.FindAll(
                    TreeScope.Descendants,
                    System.Windows.Automation.Condition.TrueCondition);

                for (var i = 0; i < descendants.Count; i++)
                {
                    var element = descendants[i];
                    string className;
                    string automationId;
                    string name;
                    ControlType? controlType;
                    System.Windows.Rect bounds;

                    try
                    {
                        className = element.Current.ClassName ?? string.Empty;
                        automationId = element.Current.AutomationId ?? string.Empty;
                        name = element.Current.Name ?? string.Empty;
                        controlType = element.Current.ControlType;
                        bounds = element.Current.BoundingRectangle;
                    }
                    catch (ElementNotAvailableException)
                    {
                        continue;
                    }

                    if (!LooksLikeClockElement(className, automationId, name, controlType) ||
                        bounds.IsEmpty ||
                        bounds.Width <= 0 ||
                        bounds.Height <= 0)
                    {
                        continue;
                    }

                    rects.Add(new RECT
                    {
                        Left = (int)Math.Floor(bounds.Left),
                        Top = (int)Math.Floor(bounds.Top),
                        Right = (int)Math.Ceiling(bounds.Right),
                        Bottom = (int)Math.Ceiling(bounds.Bottom)
                    });
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
            {
                Log($"WinCal: Clock UIA cache refresh failed: {ex.Message}");
            }

            if (rects.Count == countBefore && TryGetTaskbarClockFallbackRect(taskbar, out var fallbackRect))
                rects.Add(fallbackRect);
        }

        lock (_clockRectsLock)
            _clockRects = rects;

        Log($"WinCal: Cached {rects.Count} taskbar clock hit region(s).");
    }

    private static bool LooksLikeClockElement(
        string className,
        string automationId,
        string name,
        ControlType? controlType)
    {
        if (ContainsClockToken(className) || ContainsClockToken(automationId))
            return true;

        // Some Windows 11 builds expose the clock as a generic SystemTrayIcon button.
        // Its accessible name still contains the rendered time (for example "14:39").
        return controlType == ControlType.Button &&
               automationId.Equals("SystemTrayIcon", StringComparison.OrdinalIgnoreCase) &&
               name.Any(char.IsDigit) &&
               name.Contains(':');
    }

    private static bool ContainsClockToken(string value) =>
        value.Contains("DateTime", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Clock", StringComparison.OrdinalIgnoreCase);

    private static bool IsPointInsideTaskbarClockFallback(IntPtr taskbar, POINT point)
    {
        if (!TryGetTaskbarClockFallbackRect(taskbar, out var fallbackRect))
            return false;

        return point.X >= fallbackRect.Left &&
               point.X < fallbackRect.Right &&
               point.Y >= fallbackRect.Top &&
               point.Y < fallbackRect.Bottom;
    }

    private static bool TryGetTaskbarClockFallbackRect(IntPtr taskbar, out RECT fallbackRect)
    {
        fallbackRect = default;

        if (!GetWindowRect(taskbar, out var taskbarRect))
            return false;

        var width = taskbarRect.Right - taskbarRect.Left;
        var height = taskbarRect.Bottom - taskbarRect.Top;
        if (width <= height)
            return false;

        var dpi = GetDpiForWindow(taskbar);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var fallbackWidth = (int)Math.Round(ClockFallbackWidthDip * scale);
        var rightInset = (int)Math.Round(ClockFallbackRightInsetDip * scale);

        fallbackRect = new RECT
        {
            Left = taskbarRect.Right - rightInset - fallbackWidth,
            Top = taskbarRect.Top,
            Right = taskbarRect.Right - rightInset,
            Bottom = taskbarRect.Bottom
        };
        return true;
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var className = new System.Text.StringBuilder(256);
        return GetClassName(hwnd, className, className.Capacity) > 0
            ? className.ToString()
            : string.Empty;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        if (_mouseGcHandle.IsAllocated)
        {
            _mouseGcHandle.Free();
        }

        Debug.WriteLine("WinCal: SystemCalendarInterceptor disposed");
    }
}
