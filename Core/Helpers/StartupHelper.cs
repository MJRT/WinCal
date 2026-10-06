using Microsoft.Win32;

namespace WinCal.Core.Helpers;

/// <summary>
/// 开机自启动工具：通过注册表当前用户 Run 键实现
/// </summary>
public static class StartupHelper
{
    private const string AppName = "miniCal";
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    public const string StartupArgument = "--startup";

    private static string StartupCommand =>
        $"\"{Environment.ProcessPath}\" {StartupArgument}";

    /// <summary>
    /// 启用开机自启动
    /// </summary>
    public static void Enable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.SetValue(AppName, StartupCommand);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WinCal: Failed to enable startup: {ex.Message}");
        }
    }

    /// <summary>
    /// 禁用开机自启动
    /// </summary>
    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(AppName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WinCal: Failed to disable startup: {ex.Message}");
        }
    }

    /// <summary>
    /// 检查是否已启用开机自启动
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(AppName) as string;
            return string.Equals(value, StartupCommand, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value?.Trim('"'), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
