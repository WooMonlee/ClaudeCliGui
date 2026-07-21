using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace ClaudeGui;

public partial class App : System.Windows.Application
{
    // 单实例检测
    [DllImport("user32.dll")] private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    private const int SW_RESTORE = 9;
    private const string WindowTitle = "ClaudeCodeCli项目管理专家";

    static App()
    {
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "claudeCliGui-boot.log"),
                $"[{DateTime.Now:HH:mm:ss}] App 静态构造开始\n");
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例检测：如果已有窗口在运行（可能隐藏），恢复它
        var existingHwnd = FindWindow(null, WindowTitle);
        if (existingHwnd != IntPtr.Zero)
        {
            // 传递 --add-project 参数给运行中的实例（写临时文件做 IPC）
            for (int i = 0; i < e.Args.Length; i++)
            {
                if (e.Args[i] == "--add-project" && i + 1 < e.Args.Length)
                {
                    try
                    {
                        File.WriteAllText(
                            Path.Combine(Path.GetTempPath(), "claudeCliGui-add-project.txt"),
                            e.Args[i + 1]);
                    }
                    catch { }
                    break;
                }
            }

            // 发取消信号 → 旧实例停止退出计时器
            try
            {
                using var cancelEvent = new EventWaitHandle(false, EventResetMode.ManualReset, "ClaudeCliGui_CancelExit");
                cancelEvent.Set();
            }
            catch { }
            ShowWindow(existingHwnd, SW_RESTORE);
            SetForegroundWindow(existingHwnd);
            Environment.Exit(0);
            return;
        }

        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "claudeCliGui-boot.log"),
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
        var newPath = Path.Combine(Path.GetDirectoryName(exePath)!, "claudeCliGui.new.exe");
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
