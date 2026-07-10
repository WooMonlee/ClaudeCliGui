using System.Diagnostics;

namespace ClaudeGui;

/// <summary>
/// 简易文件日志（调试期使用，稳定后通过命令行开关控制）
/// </summary>
public static class Logger
{
    private static readonly string _logPath;
    private static readonly object _lock = new();
    private static bool _enabled = true;  // 始终开启直到稳定

    static Logger()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();
        _logPath = Path.Combine(exeDir, "claudeg-debug.log");

        try
        {
            // 超过 10MB 轮转
            if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 10 * 1024 * 1024)
            {
                var old = _logPath + ".old";
                try { File.Delete(old); } catch { }
                File.Move(_logPath, old);
            }

            File.AppendAllText(_logPath,
                $"\n=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} 启动 ({Environment.ProcessId}) ===\n");
        }
        catch { }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null)
    {
        var text = ex != null ? $"{msg}\n{ex}" : msg;
        Write("ERROR", text);
    }

    private static void Write(string level, string msg)
    {
        if (!_enabled) return;

        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {msg}";
        Debug.WriteLine(line);

        lock (_lock)
        {
            try
            {
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
            catch { /* 记录日志本身不应导致崩溃 */ }
        }
    }

    public static string LogPath => _logPath;
}
