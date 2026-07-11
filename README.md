<p align="center">
  <h1 align="center">ClaudeCliGui</h1>
  <p align="center">Windows 原生 Claude Code CLI 图形外壳<br>
  <sub>无需 CC-switch 等代理工具，打开即用，全程国内镜像自动部署</sub></p>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet" alt=".NET 8">
  <img src="https://img.shields.io/badge/platform-Windows%2010%2B-blue?logo=windows" alt="Windows 10+">
  <img src="https://img.shields.io/badge/release-v1.0.0-64ffda" alt="v1.0.0">
  <img src="https://img.shields.io/badge/license-MIT-green" alt="MIT">
</p>

---

**ClaudeCliGui** 是 [Claude Code CLI](https://github.com/anthropics/claude-code) 的 Windows 桌面伴侣。不替代 Claude Code，而是在它之上提供**项目切换、历史回看、一键环境部署、右键菜单集成**等 CLI 难以实现的交互体验。

单个 `claudeg.exe`（~70 MB），免安装，U 盘带走。

## 为什么需要它？

Claude Code CLI 在终端里功能强大，但上手门槛不低——需要自行安装 Node.js、配置代理、搞定 npm 镜像。**Claude Desktop** 甚至还需要额外工具（如 CC-switch）才能在国内正常使用。

ClaudeCliGui 把这些全自动化了：

| 痛点 | 怎么解决 |
|------|---------|
| 新电脑没装 Node.js / Claude | 启动即检测，**一键全自动安装**，国内镜像下载，全程零手动 |
| Claude Desktop 需要 CC-switch 等工具 | **不需要任何代理/加速器**，内置国内 npm 镜像源 |
| 多项目切换，终端 cd 来 cd 去 | 左侧项目列表，**点一下切换** |
| 历史对话在 JSONL 里，难翻阅 | **可视化历史**，上滚加载更多 |
| 想右键文件夹直接开始对话 | **一键安装右键菜单**，右键即开 |
| 用别人电脑，用完想清理干净 | **清零按钮**，删 API Key / 项目 / nodejs |

## 功能

### 首次启动

三步引导：安装 Node.js → 安装 Claude CLI → 输入 API Key。全程国内镜像，无需魔法上网。  
<sub>ⓘ 当前仅支持 **DeepSeek API Key**（`platform.deepseek.com/api_keys`）</sub>

### 对话区

- 流式输出，实时显示 Claude 回复
- Markdown 渲染：表格、粗斜体、代码块、引用
- 历史快照：上滚自动加载更多，超过 200 块自动裁剪
- 拖拽文件到窗口 → 自动填入绝对路径

工具栏位于输入框下方：

| 按钮 | 功能 |
|------|------|
| `⚡ 快捷指令` | 10 个 prompt 模板：代码审计、性能优化、安全检查、重构建议等 |
| `💡 提示技巧` | 10 条质量指令：逐步推理、专家会诊、自检纠错等 |

### 快捷键

| 按键 | 动作 |
|------|------|
| `Ctrl + Enter` | 发送消息 |
| `Ctrl + L` | 清空对话区 |
| `Ctrl + S` | 导出对话为 HTML |
| `Ctrl + F` | 搜索当前对话 |

### 项目管理

- **新建**：输入名称 + 父目录
- **添加已有**：浏览文件夹，自动继承 Claude 历史
- **重命名 / 删除**：三点菜单操作
- **清零**：卸载时一键清除所有痕迹（API Key、配置、nodejs、注册表）

### 自动更新

后台查询 GitHub Release，有新版本下载为 `claudeg.new.exe`，下次启动自动替换。

## 快速开始

1. 从 [Releases](https://github.com/WooMonlee/ClaudeCliGui/releases) 下载 `claudeg.exe`
2. 放到任意目录（建议和 `claude.exe` 同目录）
3. 双击运行 → 按引导完成三步配置
4. 新建或添加项目 → 开始对话

## 构建

```bash
dotnet publish ClaudeGuiWpf -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## 技术栈

- .NET 8 WPF
- Claude Code CLI（`--output-format stream-json`）
- 注册表右键菜单集成

## 许可证

MIT
