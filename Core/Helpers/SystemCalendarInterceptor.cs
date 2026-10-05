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
    private Action? _showPopupCallback;
    private Action? _showSystemCalendarCallback;
    private bool _disposed;
    private bool _suppressClockLeftButtonUp;
    private bool _suppressClockRightButtonUp;
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

    public void Start(Action showPopupCallback, Action showSystemCalendarCallback)
    {
        _showPopupCallback = showPopupCallback;
        _showSystemCalendarCallback = showSystemCalendarCallback;

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
        }
        else
        {
            Log("WinCal: ✓ Direct taskbar clock mouse hook started, hook=" + _mouseHook);
        }
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
                    var isClock = IsPointInsideAnyTaskbarClock(mouse.pt, logDiagnostics: true);
                    if (isClock)
                    {
                        _suppressClockLeftButtonUp = true;
                        Log($"Taskbar clock left-clicked at ({mouse.pt.X},{mouse.pt.Y}); opening WinCal directly");
                        _dispatcher.BeginInvoke(new Action(() => _showPopupCallback?.Invoke()));
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
                    var isClock = IsPointInsideAnyTaskbarClock(mouse.pt, logDiagnostics: true);
                    if (isClock)
                    {
                        _suppressClockRightButtonUp = true;
                        Log($"Taskbar clock right-clicked at ({mouse.pt.X},{mouse.pt.Y}); opening Windows notification center");
                        _dispatcher.BeginInvoke(new Action(() => _showSystemCalendarCallback?.Invoke()));
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
            Log($"WinCal: MouseHookProc error: {ex.Message}");
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static bool IsPointInsideAnyTaskbarClock(POINT point, bool logDiagnostics)
    {
        if (IsPointInsideWin11TaskbarClock(point, logDiagnostics))
            return true;

        var primaryTaskbar = FindWindow("Shell_TrayWnd", null);
        if (primaryTaskbar != IntPtr.Zero && IsPointInsideTaskbarClock(primaryTaskbar, point))
            return true;

        var foundOnSecondaryTaskbar = false;
        EnumWindows((window, _) =>
        {
            var className = GetWindowClassName(window);
            if (!string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!IsPointInsideTaskbarClock(window, point))
                return true;

            foundOnSecondaryTaskbar = true;
            return false;
        }, IntPtr.Zero);

        if (foundOnSecondaryTaskbar)
            return true;

        return IsPointInsideTaskbarClockFallback(point, logDiagnostics);
    }

    private static bool IsPointInsideWin11TaskbarClock(POINT point, bool logDiagnostics)
    {
        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
            var walker = TreeWalker.RawViewWalker;
            var diagnostics = new List<string>();

            for (var depth = 0; element != null && depth < 16; depth++)
            {
                string className;
                string automationId;
                string name;
                ControlType? controlType;
                try
                {
                    className = element.Current.ClassName ?? string.Empty;
                    automationId = element.Current.AutomationId ?? string.Empty;
                    name = element.Current.Name ?? string.Empty;
                    controlType = element.Current.ControlType;
                }
                catch (ElementNotAvailableException)
                {
                    return false;
                }

                if (logDiagnostics)
                {
                    diagnostics.Add(
                        $"depth={depth} Class='{className}' AutomationId='{automationId}' " +
                        $"Name='{name}' ControlType='{controlType?.ProgrammaticName ?? ""}'");
                }

                if (LooksLikeClockElement(className, automationId, name, controlType))
                {
                    Log($"Taskbar clock matched via UI Automation: Class='{className}' AutomationId='{automationId}' Name='{name}'");
                    return true;
                }

                element = walker.GetParent(element);
            }

            if (logDiagnostics && diagnostics.Count > 0)
                Log($"UIA chain at ({point.X},{point.Y}): {string.Join(" || ", diagnostics)}");
        }
        catch (ElementNotAvailableException)
        {
            // Taskbar UI can rebuild its XAML tree while hit-testing.
        }
        catch (COMException ex)
        {
            Log($"WinCal: UI Automation clock hit-test failed: 0x{ex.HResult:X8}");
        }
        catch (InvalidOperationException ex)
        {
            Log($"WinCal: UI Automation clock hit-test failed: {ex.Message}");
        }

        return false;
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

    private static bool IsPointInsideTaskbarClockFallback(POINT point, bool logDiagnostics)
    {
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

        if (IsPointInsideClockDescendant(root, point, logDiagnostics))
            return true;

        if (!GetWindowRect(root, out var taskbarRect))
            return false;

        var width = taskbarRect.Right - taskbarRect.Left;
        var height = taskbarRect.Bottom - taskbarRect.Top;
        if (width <= height)
            return false;

        var dpi = GetDpiForWindow(root);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var fallbackWidth = (int)Math.Round(ClockFallbackWidthDip * scale);
        var rightInset = (int)Math.Round(ClockFallbackRightInsetDip * scale);
        var left = taskbarRect.Right - rightInset - fallbackWidth;
        var right = taskbarRect.Right - rightInset;

        var hit = point.X >= left &&
                  point.X < right &&
                  point.Y >= taskbarRect.Top &&
                  point.Y < taskbarRect.Bottom;

        if (logDiagnostics)
        {
            Log(
                $"Taskbar geometry fallback at ({point.X},{point.Y}): rootClass='{rootClass}' " +
                $"taskbar=({taskbarRect.Left},{taskbarRect.Top})-({taskbarRect.Right},{taskbarRect.Bottom}) " +
                $"dpi={dpi} clockRangeX=[{left},{right}) hit={hit}");
        }

        return hit;
    }

    private static bool IsPointInsideClockDescendant(IntPtr taskbar, POINT point, bool logDiagnostics)
    {
        try
        {
            var taskbarElement = AutomationElement.FromHandle(taskbar);
            var descendants = taskbarElement.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            var containingElements = new List<string>();

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

                if (bounds.IsEmpty ||
                    point.X < bounds.Left || point.X >= bounds.Right ||
                    point.Y < bounds.Top || point.Y >= bounds.Bottom)
                {
                    continue;
                }

                if (logDiagnostics && containingElements.Count < 12)
                {
                    containingElements.Add(
                        $"Class='{className}' AutomationId='{automationId}' Name='{name}' " +
                        $"ControlType='{controlType?.ProgrammaticName ?? ""}' Rect={bounds}");
                }

                if (!LooksLikeClockElement(className, automationId, name, controlType))
                    continue;

                Log(
                    $"Taskbar clock matched via descendant search: Class='{className}' " +
                    $"AutomationId='{automationId}' Name='{name}' Rect={bounds}");
                return true;
            }

            if (logDiagnostics && containingElements.Count > 0)
                Log($"UIA taskbar descendants containing click: {string.Join(" || ", containingElements)}");
        }
        catch (ElementNotAvailableException)
        {
        }
        catch (COMException ex)
        {
            Log($"WinCal: UI Automation descendant search failed: 0x{ex.HResult:X8}");
        }
        catch (InvalidOperationException ex)
        {
            Log($"WinCal: UI Automation descendant search failed: {ex.Message}");
        }

        return false;
    }

    private static bool IsPointInsideTaskbarClock(IntPtr taskbar, POINT cursor)
    {
        var trayNotify = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (trayNotify == IntPtr.Zero)
            return false;

        var clock = FindWindowEx(trayNotify, IntPtr.Zero, "TrayClockWClass", null);
        if (clock == IntPtr.Zero || !GetWindowRect(clock, out var clockRect))
            return false;

        return cursor.X >= clockRect.Left &&
               cursor.X < clockRect.Right &&
               cursor.Y >= clockRect.Top &&
               cursor.Y < clockRect.Bottom;
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
