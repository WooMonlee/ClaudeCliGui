using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeGui;

/// <summary>
/// 管理 claudeg.json 配置文件（与 claudeg.exe 同目录）
/// </summary>
public class ConfigService
{
    private readonly string _configPath;
    private readonly string _profileDir;
    private readonly object _lock = new();
    private ProjectConfig _config;

    public string ExeDir { get; }
    public string ProfileDir => _profileDir;

    public ConfigService()
    {
        ExeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();
        _configPath = Path.Combine(ExeDir, "claudeg.json");
        _profileDir = Path.Combine(ExeDir, "ProFile");
        _config = Load();
    }

    public List<ProjectEntry> GetProjects() => _config.Projects;

    public ProjectEntry? GetProject(string name)
    {
        return _config.Projects.FirstOrDefault(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public ProjectEntry AddProject(string name, string path)
    {
        var entry = new ProjectEntry
        {
            Name = name,
            Path = path,
            CreatedAt = DateTime.Now,
            LastAccessedAt = DateTime.Now
        };

        lock (_lock)
        {
            // 不允许重名
            var existing = GetProject(name);
            if (existing != null)
                throw new InvalidOperationException($"项目 '{name}' 已存在");

            _config.Projects.Add(entry);
            Save();
        }
        return entry;
    }

    public ProjectEntry AddExistingProject(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"目录不存在: {folderPath}");

        var name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return AddProject(name, folderPath);
    }

    public ProjectEntry? RenameProject(string oldName, string newName)
    {
        lock (_lock)
        {
            var project = GetProject(oldName);
            if (project == null) return null;

            if (GetProject(newName) != null)
                throw new InvalidOperationException($"项目 '{newName}' 已存在");

            project.Name = newName;
            Save();
        }
        return GetProject(newName);
    }

    public bool DeleteProject(string name)
    {
        lock (_lock)
        {
            var removed = _config.Projects.RemoveAll(p =>
                p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                Save();
                return true;
            }
        }
        return false;
    }

    public void UpdateAccessTime(string name)
    {
        lock (_lock)
        {
            var project = GetProject(name);
            if (project != null)
            {
                project.LastAccessedAt = DateTime.Now;
                // 不立即保存，等会话结束时统一保存
            }
        }
    }

    public bool IsSetupCompleted() => _config.SetupCompleted;

    public string? GetClaudeDir() => _config.ClaudeDir;

    public void MarkSetupCompleted(string? claudeDir = null)
    {
        lock (_lock)
        {
            _config.SetupCompleted = true;
            if (!string.IsNullOrWhiteSpace(claudeDir))
                _config.ClaudeDir = claudeDir;
            Save();
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        }
    }

    private ProjectConfig Load()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var config = JsonSerializer.Deserialize<ProjectConfig>(json) ?? new ProjectConfig();
                // 修复 L2：已有项目列表但无 setupCompleted 字段 → 视为已设置
                if (!config.SetupCompleted && config.Projects.Count > 0)
                {
                    config.SetupCompleted = true;
                }
                return config;
            }
        }
        catch { }
        return new ProjectConfig();
    }
}
