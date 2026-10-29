using Microsoft.Win32;

namespace TrayUsage;

/// <summary>Per-user on/off switches for the activity island, kept in the registry (completion notices default to on; the always-visible progress island defaults to off).</summary>
internal static class ActivitySettings
{
    public const string ShowProgress = "ShowProgress";
    public const string NotifyCompletion = "NotifyCompletion";
    public const string Theme = "Theme";
    public const string HoverOpen = "HoverOpen";
    public const string HoverDelayMs = "HoverDelayMs";
    public const string FullscreenQuiet = "FullscreenQuiet";
    public const string ShowClock = "ShowClock";
    public const string NotifyQuestion = "NotifyQuestion";
    public const string NotifyQuota = "NotifyQuota";
    private const string KeyPath = @"Software\TrayUsage";

    public static bool Get(string name)
        => Get(name, name != ShowProgress);

    private static bool Get(string name, bool fallback)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(name) is int value ? value != 0 : fallback;
    }

    public static int GetInt(string name, int fallback)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(name) is int value ? value : fallback;
    }

    public static void SetInt(string name, int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true)
            ?? throw new IOException("設定を保存できませんでした。");
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    public static void Set(string name, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true)
            ?? throw new IOException("設定を保存できませんでした。");
        key.SetValue(name, enabled ? 1 : 0, RegistryValueKind.DWord);
    }
}
