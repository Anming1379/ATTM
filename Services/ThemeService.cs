using System.Windows;
using Microsoft.Win32;

namespace ATTM.Services;

public enum AppTheme
{
    Dark,
    Light,
    System
}

/// <summary>
/// 主题解析与切换：深色 / 浅色 / 跟随系统。
/// 资源字典替换是应用级的，所有界面通过 DynamicResource 自动跟随。
/// </summary>
public static class ThemeService
{
    private const string DarkUri = "pack://application:,,,/ATTM;component/Themes/Palette.Dark.xaml";
    private const string LightUri = "pack://application:,,,/ATTM;component/Themes/Palette.Light.xaml";
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static bool _watcherAttached;

    public static AppTheme Mode { get; private set; } = AppTheme.Dark;

    /// <summary>当前实际生效的是不是深色。</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>调色板替换后触发，界面用它刷新标题栏、编辑器配色、按钮图标。</summary>
    public static event EventHandler? ThemeChanged;

    public static AppTheme ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "light" => AppTheme.Light,
        "system" => AppTheme.System,
        _ => AppTheme.Dark
    };

    public static string ToConfigString(AppTheme mode) => mode switch
    {
        AppTheme.Light => "Light",
        AppTheme.System => "System",
        _ => "Dark"
    };

    public static void SetMode(AppTheme mode)
    {
        Mode = mode;
        ApplyPalette();
        EnsureSystemWatcher();
    }

    /// <summary>系统主题变化时调用（跟随系统模式才真正重绘）。</summary>
    public static void RefreshSystemTheme()
    {
        if (Mode == AppTheme.System) ApplyPalette();
    }

    public static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (key?.GetValue("AppsUseLightTheme") is int value) return value != 0;
        }
        catch
        {
            // 读不到就按深色处理
        }

        return false;
    }

    private static void ApplyPalette()
    {
        var dark = Mode switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => !IsSystemLightTheme()
        };

        IsDark = dark;

        var app = Application.Current;
        if (app is null) return;

        var palette = new ResourceDictionary
        {
            Source = new Uri(dark ? DarkUri : LightUri, UriKind.Absolute)
        };

        var dictionaries = app.Resources.MergedDictionaries;
        for (var i = 0; i < dictionaries.Count; i++)
        {
            var source = dictionaries[i].Source?.OriginalString ?? string.Empty;
            if (!source.Contains("Palette.", StringComparison.OrdinalIgnoreCase)) continue;

            dictionaries[i] = palette;
            ThemeChanged?.Invoke(null, EventArgs.Empty);
            return;
        }

        dictionaries.Add(palette);
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void EnsureSystemWatcher()
    {
        if (_watcherAttached) return;
        _watcherAttached = true;

        try
        {
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color)) return;

                // SystemEvents 在自己的线程上触发，必须切回 UI 线程改资源字典
                var app = Application.Current;
                app?.Dispatcher.BeginInvoke(new Action(RefreshSystemTheme));
            };
        }
        catch
        {
            // 极少数环境没有 SystemEvents，忽略即可
        }
    }
}
