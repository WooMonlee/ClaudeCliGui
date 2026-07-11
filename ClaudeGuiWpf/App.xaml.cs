using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace ClaudeGui;

public partial class App : System.Windows.Application
{
    static App()
    {
        // 最早期的启动诊断——在 Logger 初始化之前写入临时文件
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "claudeg-boot.log"),
                $"[{DateTime.Now:HH:mm:ss}] App 静态构造开始\n");
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "claudeg-boot.log"),
                $"[{DateTime.Now:HH:mm:ss}] OnStartup 进入\n");
        }
        catch { }

        Logger.Info("=== 应用启动 ===");

        // 检测是否有待更新文件
        SwapNewVersionIfExists();
        Logger.Info($"参数: {string.Join(" ", e.Args)}");
        Logger.Info($"日志路径: {Logger.LogPath}");

        // 捕获未处理异常
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error("UI 线程未处理异常", args.Exception);
            MessageBox.Show($"发生错误:\n{args.Exception.Message}\n\n详情已写入: {Logger.LogPath}",
                "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Logger.Error("非 UI 线程未处理异常", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Error("未观察到的 Task 异常", args.Exception);
            args.SetObserved();
        };

        Logger.Info("异常处理已注册");

        try
        {
            base.OnStartup(e);
            Logger.Info("启动完成");
        }
        catch (Exception ex)
        {
            Logger.Error("启动失败", ex);
            throw;
        }
    }

    private static void SwapNewVersionIfExists()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath)) return;
        var newPath = Path.Combine(Path.GetDirectoryName(exePath)!, "claudeg.new.exe");
        if (!File.Exists(newPath)) return;

        try
        {
            var oldPath = exePath + ".old";
            try { File.Delete(oldPath); } catch { }
            File.Move(exePath, oldPath);
            File.Move(newPath, exePath);
            // 启动新版本
            Process.Start(exePath);
            Environment.Exit(0);
        }
        catch { /* 交换失败不崩溃 */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info($"应用退出，退出码: {e.ApplicationExitCode}");
        base.OnExit(e);
    }
}
