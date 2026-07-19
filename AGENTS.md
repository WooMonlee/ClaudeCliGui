# ClaudeWebGui (claudeg.exe)

C# ASP.NET Core 8 实现的 Codex CLI Web GUI，自包含单文件发布（~46MB），放在 Codex.exe 同目录即可使用。

## 项目结构
- `Program.cs` — 入口：Minimal API 路由、WebSocket、自动关机、项目 CRUD
- `ClaudeService.cs` — 核心：Codex 子进程管理、流式 stdout/stderr 读取、WebSocket 广播、中文翻译
- `ConfigService.cs` — `claudeg.json` 项目管理（与 exe 同目录）
- `Models.cs` — ClaudeStreamMessage、SessionInfo、ClientMessage 等数据模型
- `wwwroot/index.html` — 前端：双页面（项目列表 + 聊天），marked.js + highlight.js CDN

## 构建
```
dotnet publish ClaudeWebGui -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```
或运行 `build.cmd`

## 发布输出
`ClaudeWebGui/bin/Release/net8.0/win-x64/publish/claudeg.exe`

## 部署
复制 `claudeg.exe` 到 Codex.exe 所在目录。启动后自动打开浏览器，关闭标签页后端自动退出。

## 开发日志
见 `.Codex/changelog.md`
