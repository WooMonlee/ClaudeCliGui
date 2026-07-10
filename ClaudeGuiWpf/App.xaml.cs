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

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info($"应用退出，退出码: {e.ApplicationExitCode}");
        base.OnExit(e);
    }
}
