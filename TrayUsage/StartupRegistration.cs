using Microsoft.Win32;

namespace TrayUsage;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TrayUsage";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string command &&
            string.Equals(command, Command, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true)
            ?? throw new IOException("Windows の自動起動設定を開けませんでした。");
        if (enabled)
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        else
            key.DeleteValue(ValueName, false);
    }

    private static string Command
    {
        get
        {
            var path = Environment.ProcessPath ?? throw new IOException("実行ファイルの場所を取得できませんでした。");
            return $"\"{path}\"";
        }
    }
}
