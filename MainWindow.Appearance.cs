using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ATTM.Core.Midi;
using ATTM.Core.Playback;
using ATTM.Core.Syntax;
using ATTM.Services;
using ATTM.Views;
using Microsoft.Win32;

namespace ATTM;

public partial class MainWindow
{
    // ------------------------------------------------------------ 主题

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        WindowTheme.Apply(this, ThemeService.IsDark);
        Editor.ApplyTheme();
        UpdateThemeChrome();
        UpdateStatus();
    }

    private void UpdateThemeChrome()
    {
        var dark = ThemeService.IsDark;
        // 图标表示当前主题：深色=月亮，浅色=太阳
        ThemeButton.Content = dark ? "\uE51C" : "\uE518";

        var modeText = ThemeService.Mode switch
        {
            AppTheme.System => "跟随系统",
            AppTheme.Light => "浅色",
            _ => "深色"
        };

        ThemeButton.ToolTip = $"当前：{modeText} · 点击切换到{(dark ? "浅色" : "深色")}";
    }

    // ------------------------------------------------------------ 窗口位置

    private void RestoreWindowPlacement()
    {
        if (_config.WindowWidth is not { } width || _config.WindowHeight is not { } height) return;
        if (width < MinWidth || height < MinHeight) return;

        var left = _config.WindowLeft ?? Left;
        var top = _config.WindowTop ?? Top;
        var bounds = new Rect(left, top, width, height);

        // 上次的显示器可能已经拔掉，落在屏幕外就回退到居中
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        if (!bounds.IntersectsWith(virtualScreen)) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;

        if (_config.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void SaveWindowPlacement()
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;

        if (bounds.Width < MinWidth || bounds.Height < MinHeight) return;

        _config.WindowLeft = (int)Math.Round(bounds.Left);
        _config.WindowTop = (int)Math.Round(bounds.Top);
        _config.WindowWidth = (int)Math.Round(bounds.Width);
        _config.WindowHeight = (int)Math.Round(bounds.Height);
        _config.WindowMaximized = WindowState == WindowState.Maximized;
        _config.Save();
    }
}
