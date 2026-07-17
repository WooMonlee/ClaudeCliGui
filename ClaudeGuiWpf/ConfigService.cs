using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeGui;

/// <summary>
/// 管理 claudeCliGui.json 配置文件（与 claudeCliGui.exe 同目录）
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
        _configPath = Path.Combine(ExeDir, "claudeCliGui.json");

        // 迁移旧配置文件
        var oldConfigPath = Path.Combine(ExeDir, "claudeg.json");
        if (!File.Exists(_configPath) && File.Exists(oldConfigPath))
        {
            try { File.Move(oldConfigPath, _configPath); } catch { }
        }

        _profileDir = Path.Combine(ExeDir, "ProFile");
        _config = Load();
        EnsurePresetProviders(); // 首次启动填充预设
    }

    public List<ProjectEntry> GetProjects()
    {
        // 修复 L2/L10：加锁 + 全面补齐 SortOrder
        lock (_lock)
        {
            var projects = _config.Projects;
            bool needsInit = projects.Count > 0 && projects.All(p => p.SortOrder == 0);
            if (needsInit)
                for (int i = 0; i < projects.Count; i++)
                    projects[i].SortOrder = i;
            return projects.OrderBy(p => p.SortOrder).ToList();
        }
    }

    public ProjectEntry? GetProject(string name)
    {
        lock (_lock) // 修复 L3：防止读-写竞争
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
            LastAccessedAt = DateTime.Now,
            SortOrder = _config.Projects.Count // 排在最后
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

    /// <summary>拖拽排序：交换两个项目的 SortOrder 并持久化</summary>
    public void SwapSortOrder(string nameA, string nameB)
    {
        lock (_lock)
        {
            var a = GetProject(nameA);
            var b = GetProject(nameB);
            if (a == null || b == null || a == b) return;
            (a.SortOrder, b.SortOrder) = (b.SortOrder, a.SortOrder);
            Save();
        }
    }

    public void UpdateAccessTime(string name)
    {
        lock (_lock)
        {
            var project = GetProject(name);
            if (project != null)
            {
                project.LastAccessedAt = DateTime.Now;
                project.AccessCount++;
            }
        }
    }

    /// <summary>按项目路径更新权限模式（TerminalControl 切换时调用）</summary>
    public void SetPermissionModeByPath(string projectPath, string mode)
    {
        lock (_lock)
        {
            var project = _config.Projects.FirstOrDefault(p =>
                p.Path.Equals(projectPath, StringComparison.OrdinalIgnoreCase));
            if (project != null)
            {
                project.PermissionMode = mode;
                Save();
            }
        }
    }

    public bool IsSetupCompleted() => _config.SetupCompleted;

    /// <summary>根据当前策略获取应预载入的项目列表（按优先级排序）</summary>
    public List<ProjectEntry> GetPreloadList()
    {
        var projects = _config.Projects.ToList();
        if (projects.Count == 0) return projects;

        // 检查是否启用
        if (PreloadStrategy == "off") return new List<ProjectEntry>();

        var n = Math.Max(1, PreloadCount);
        // 总数少 → 直接全开
        if (projects.Count <= n) return projects.OrderByDescending(p => p.LastAccessedAt).ToList();

        return PreloadStrategy switch
        {
            "all" => projects.OrderByDescending(p => p.LastAccessedAt).ToList(),
            _ => projects.OrderByDescending(p => p.LastAccessedAt).Take(n).ToList() // "lastN"
        };
    }

    public string? GetClaudeDir() => _config.ClaudeDir;

    public string GetConfigValue(string key, string defaultValue = "") => key switch
    {
        "preloadStrategy" => _config.PreloadStrategy == "off" ? "off" :
                              string.IsNullOrWhiteSpace(_config.PreloadStrategy) ? defaultValue : _config.PreloadStrategy,
        "preloadCount" => _config.PreloadCount > 0 ? _config.PreloadCount.ToString() : defaultValue,
        "lastOpenDir" => _config.LastOpenDir ?? defaultValue,
        "lastActiveProject" => _config.LastActiveProject ?? defaultValue,
        _ => defaultValue
    };

    public void SetConfigValue(string key, string value)
    {
        lock (_lock)
        {
            switch (key)
            {
                case "preloadStrategy": _config.PreloadStrategy = value; break;
                case "preloadCount": if (int.TryParse(value, out var n)) _config.PreloadCount = Math.Clamp(n, 1, 10); break;
                case "lastOpenDir": _config.LastOpenDir = value; break;
                case "lastActiveProject": _config.LastActiveProject = value; break;
            }
            Save();
        }
    }

    // 便捷访问
    public string PreloadStrategy => _config.PreloadStrategy;
    public int PreloadCount => _config.PreloadCount > 0 ? _config.PreloadCount : 3;

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
            // 修复 A2：写入临时文件再 Move，防止写入中断导致配置损坏
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            var tmp = _configPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _configPath, overwrite: true);
        }
    }

    // ===== API 提供商管理 =====

    public List<ApiProviderConfig> GetProviders() => _config.ApiProviders;

    public ApiProviderConfig? GetActiveProvider()
    {
        lock (_lock) // 修复 L3：防止读-写竞争
        {
            if (!string.IsNullOrWhiteSpace(_config.ActiveProviderName))
            {
                var p = _config.ApiProviders.FirstOrDefault(x =>
                    x.Name.Equals(_config.ActiveProviderName, StringComparison.OrdinalIgnoreCase));
                if (p != null && !string.IsNullOrWhiteSpace(p.ApiKey)) return p;
            }
            return _config.ApiProviders
                .Where(x => x.Priority == 0 && !string.IsNullOrWhiteSpace(x.ApiKey))
                .MinBy(x => x.Priority);
        }
    }

    public List<ApiProviderConfig> GetFailoverChain()
        => _config.ApiProviders
            .Where(x => !string.IsNullOrWhiteSpace(x.ApiKey))
            .OrderBy(x => x.Priority)
            .Take(3) // 最多3级 fallback
            .ToList();

    public ApiProviderConfig? GetProvider(string name)
        => _config.ApiProviders.FirstOrDefault(x =>
            x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public void SetActiveProvider(string name)
    {
        lock (_lock) { _config.ActiveProviderName = name; Save(); }
    }

    public void SaveProvider(ApiProviderConfig config)
    {
        lock (_lock)
        {
            var existing = _config.ApiProviders.FirstOrDefault(x =>
                x.Name.Equals(config.Name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                // 原地更新，保持顺序不变
                existing.BaseUrl = config.BaseUrl;
                existing.ApiKey = config.ApiKey;
                existing.Model = config.Model;
                existing.SmallFastModel = config.SmallFastModel;
                existing.DefaultOpusModel = config.DefaultOpusModel;
                existing.DefaultSonnetModel = config.DefaultSonnetModel;
                existing.DefaultHaikuModel = config.DefaultHaikuModel;
                existing.DefaultFableModel = config.DefaultFableModel;
                existing.Priority = config.Priority;
                existing.Tags = config.Tags;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(config.DefaultOpusModel)) config.DefaultOpusModel = config.Model;
                if (string.IsNullOrWhiteSpace(config.DefaultSonnetModel)) config.DefaultSonnetModel = config.Model;
                if (string.IsNullOrWhiteSpace(config.DefaultHaikuModel)) config.DefaultHaikuModel = config.SmallFastModel;
                if (string.IsNullOrWhiteSpace(config.DefaultFableModel)) config.DefaultFableModel = config.Model;
                _config.ApiProviders.Add(config);
            }
            Save();
        }
    }

    public bool DeleteProvider(string name)
    {
        lock (_lock)
        {
            var removed = _config.ApiProviders.RemoveAll(x =>
                x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) { Save(); return true; }
        }
        return false;
    }

    /// <summary>首次启动填充预设提供商（仅在列表为空时）</summary>
    public void EnsurePresetProviders()
    {
        if (_config.ApiProviders.Count > 0) return;

        // 尝试从已有环境变量读取 DeepSeek 的 Key
        var existingKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY", EnvironmentVariableTarget.User)
                       ?? Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", EnvironmentVariableTarget.User)
                       ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY", EnvironmentVariableTarget.Process)
                       ?? "";

        var presets = new List<ApiProviderConfig>
        {
            new() { Name = "DeepSeek", BaseUrl = "https://api.deepseek.com/anthropic",
                Model = "deepseek-chat", SmallFastModel = "deepseek-chat", DefaultOpusModel = "deepseek-reasoner",
                DefaultSonnetModel = "deepseek-chat", DefaultHaikuModel = "deepseek-chat", DefaultFableModel = "deepseek-chat",
                Priority = 0, ApiKey = existingKey, Tags = "文本" },
            new() { Name = "阿里百练", BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/anthropic",
                Model = "qwen-plus", SmallFastModel = "qwen-plus", DefaultOpusModel = "qwen-max",
                Priority = 1, Tags = "多模态" },
            new() { Name = "智谱 GLM", BaseUrl = "https://open.bigmodel.cn/api/anthropic",
                Model = "glm-4-plus", SmallFastModel = "glm-4-flash", DefaultOpusModel = "glm-4-plus",
                Priority = 2, Tags = "文本" },
            new() { Name = "KIMI", BaseUrl = "https://api.moonshot.cn/anthropic",
                Model = "kimi-latest", SmallFastModel = "kimi-latest", DefaultOpusModel = "kimi-latest",
                Priority = 3, Tags = "长文本" },
            new() { Name = "MiniMax", BaseUrl = "https://api.minimax.chat/anthropic",
                Model = "minimax-m2", SmallFastModel = "minimax-m2", DefaultOpusModel = "minimax-m2",
                Priority = 4, Tags = "文本" },
            new() { Name = "小米 Mino", BaseUrl = "https://api.xiaomimimo.com/anthropic",
                Model = "mimo-v2-flash", SmallFastModel = "mimo-v2-flash", DefaultOpusModel = "mimo-v2-flash",
                Priority = 5, Tags = "文本" },
        };

        lock (_lock)
        {
            _config.ApiProviders = presets;
            if (!string.IsNullOrWhiteSpace(existingKey))
                _config.ActiveProviderName = "DeepSeek";
            Save();
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
                if (!config.SetupCompleted && config.Projects.Count > 0)
                    config.SetupCompleted = true;
                return config;
            }
        }
        catch (Exception ex)
        {
            // 修复 F3：配置损坏时备份原文件，防止数据永久丢失
            try
            {
                var backupPath = _configPath + ".bak";
                if (File.Exists(_configPath))
                {
                    File.Copy(_configPath, backupPath, overwrite: true);
                    Logger.Warn($"配置读取失败，已备份到 {backupPath}: {ex.Message}");
                }
            }
            catch { }
        }
        return new ProjectConfig();
    }
}
