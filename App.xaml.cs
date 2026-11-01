using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ATTM;

/// <summary>
/// 应用入口：支持 "ATTM.exe 谱面.attm" 这种命令行参数，
/// 这样在资源管理器里双击 .attm（安装包已注册关联）就能直接打开。
/// 同时兜住所有未处理异常：写日志 + 提示，而不是让窗口一声不响地失效。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        var file = FindFileArgument(e.Args);

        var window = new MainWindow(file);
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 不吞掉现场：先记盘，再提示，最后让程序继续跑（用户还能另存/复制内容）
        Report(e.Exception);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception) WriteLog(exception);
    }

    private static void Report(Exception exception)
    {
        var path = WriteLog(exception);

        MessageBox.Show(
            $"出错了：{exception.Message}\n\n详细信息已写入：\n{path}",
            "ATTM",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    internal static string WriteLog(Exception exception)
    {
        // 先试 %APPDATA%，不行就写到程序目录（受限环境下 APPDATA 可能不可写）
        var candidates = new[]
        {
            Path.Combine(Services.AppConfig.ConfigDirectory, "last-error.log"),
            Path.Combine(AppContext.BaseDirectory, "last-error.log")
        };

        var path = candidates[0];

        foreach (var candidate in candidates)
        {
            try
            {
                var directory = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                path = candidate;
                break;
            }
            catch
            {
                // 试下一个
            }
        }

        try
        {
            File.AppendAllText(path,
                $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} v{typeof(App).Assembly.GetName().Version}\n" +
                exception + "\n\n");
        }
        catch
        {
            // 连日志都写不了就只能靠提示框了
        }

        return path;
    }

    private static string? FindFileArgument(string[] args)
    {
        foreach (var argument in args)
        {
            if (string.IsNullOrWhiteSpace(argument) || argument.StartsWith('-')) continue;

            var path = argument.Trim().Trim('"');
            if (File.Exists(path)) return path;
        }

        return null;
    }
}
