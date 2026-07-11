using System.Collections.Concurrent;
using System.Diagnostics;

namespace ClaudeGui;

/// <summary>
/// 无锁异步文件日志（修复 R1：彻底消除死锁）
/// </summary>
public static class Logger
{
    private static readonly string _logPath;
    private static readonly ConcurrentQueue<string> _queue = new();
    private static readonly Thread _writerThread;
    private static bool _enabled = true;

    static Logger()
    {
        _writerThread = new Thread(FlushLoop) { IsBackground = true, Name = "Logger" };
        _writerThread.Start();

        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();
        _logPath = Path.Combine(exeDir, "claudeg-debug.log");

        try
        {
            // 修复 P3：超过 512KB 裁剪到保留最后 256KB
            const int max = 512 * 1024, keep = 256 * 1024;
            if (File.Exists(_logPath) && new FileInfo(_logPath).Length > max)
            {
                var bytes = File.ReadAllBytes(_logPath);
                if (bytes.Length > keep)
                    File.WriteAllBytes(_logPath, bytes[(bytes.Length - keep)..]);
            }
            File.AppendAllText(_logPath, $"\n=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} 启动 ({Environment.ProcessId}) ===\n");
        }
        catch { }
    }

    public static string LogPath => _logPath;
    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null)
        => Write("ERROR", ex != null ? $"{msg}\n{ex}" : msg);

    private static void Write(string level, string msg)
    {
        if (!_enabled) return;
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {msg}";
        Debug.WriteLine(line);
        _queue.Enqueue(line);
    }

    private static void FlushLoop()
    {
        var batch = new List<string>();
        while (_enabled)
        {
            try
            {
                batch.Clear();
                while (_queue.TryDequeue(out var line))
                    batch.Add(line);
                if (batch.Count > 0)
                {
                    try { File.AppendAllLines(_logPath, batch); } catch { }
                }
            }
            catch { }
            Thread.Sleep(100);
        }
    }
}
