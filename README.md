# ClaudeG — Claude Code CLI 桌面外壳 v1.0

[.NET 8 WPF] 为 [Claude Code CLI](https://github.com/anthropics/claude-code) 打造的 Windows 原生图形界面。单文件便携，无需安装。

## 核心功能

### 一键部署环境
- 首次启动自动检测缺失组件，**一键安装** Node.js + Claude CLI
- Node.js 从国内镜像下载，Claude CLI 通过 npm 安装，全程零手动
- 环境完整后直接进入对话界面，不再闪现安装页

### 项目管理
- **新建/添加/重命名/删除**项目，每个项目独立文件夹和会话
- 右键菜单集成：右键任意文件夹 → "在当前文件夹使用ClaudeGui"
- 左上角一键开关右键菜单（`右键菜单 ✓` / `右键菜单 ✗`）

### 对话体验
- **流式实时输出** Claude 回复，支持 markdown 渲染（表格、粗体、代码块）
- **快捷指令**：10 个常用 prompt 模板（代码审计、性能优化、安全检查等），下拉即选
- **提示技巧**：10 条质量指令（逐步推理、专家会诊、自检纠错等），选中插入输入框
- **拖拽文件**直接插入绝对路径
- 历史对话**上滚加载更多**，超过 200 块自动裁剪保持流畅
- `Ctrl+L` 清屏 · `Ctrl+S` 导出 HTML · `Ctrl+F` 搜索对话

### API 配置
- 自动写入 `settings.json` + 全局 `CLAUDE.md` 行为规则
- 支持 DeepSeek/Anthropic 兼容端点，模型映射自动配置
- API Key 保存后立即生效，无需重启

### 升级与维护
- **自动检测更新**：后台查 GitHub Release，下载 `claudeg.new.exe`，下次启动自动替换
- **清零按钮**：一键清除所有配置/API Key/Node.js/注册表，适合卸载或在他人电脑上退出
- 独立日志文件，512KB 自动裁剪

## 构建

```bash
dotnet publish ClaudeGuiWpf -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

输出：`ClaudeGuiWpf/bin/Release/net8.0-windows/win-x64/publish/claudeg.exe`

## 部署

单文件 `claudeg.exe` 复制到任意目录即可运行。如需右键菜单，启动后在左上角点击"右键菜单 ✗"安装。

## 许可证

MIT
