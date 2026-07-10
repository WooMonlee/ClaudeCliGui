# ClaudeG — Claude Code CLI 桌面外壳

[.NET 8 WPF] 为 [Claude Code CLI](https://github.com/anthropics/claude-code) 提供 Windows 原生图形界面，支持便携部署、一键安装运行环境。

## 功能

- **原生 WPF 界面** — 左侧项目列表 + 右侧 Claude 对话终端
- **右键菜单集成** — 在任意文件夹右键 "在当前文件夹使用 ClaudeGui"
- **一键安装环境** — 自动下载 Node.js + Claude CLI，无需手动配置
- **Markdown 渲染** — 表格/粗体/代码块/标题/引用，复制到 Word 保留格式
- **多项目管理** — 新建/添加/重命名/删除项目，每个项目独立会话
- **版本检测** — 自动检测 Claude CLI 新版本
- **便携设计** — Node.js 和 Claude CLI 装在 exe 同目录，复制即用
- **清零功能** — 一键清除所有配置和 API Key

## 构建

```bash
dotnet publish ClaudeGuiWpf -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

输出：`ClaudeGuiWpf/bin/Release/net8.0-windows/win-x64/publish/claudeg.exe`

## 部署

将 `claudeg.exe` 放到任意目录，双击运行即可。如需右键菜单，在安装向导中勾选。

## 许可证

MIT
