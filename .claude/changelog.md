# ClaudeWebGui 开发日志

## 2026-07-08 — 多会话并行 + 快照保存 + 目录浏览 + 初始化检测

### 1. 多会话并行（项目→会话映射）
- **ClaudeService.cs** — 新增 `_projectSessions` (workDir→sessionId) 映射
  - `GetOrResumeProjectSessionAsync(workDir)` — 复用已有活跃会话或创建新会话
  - `IsProjectSessionActive(workDir)` — 检查项目是否已有活跃进程
  - 切换项目时**不杀旧进程**，只断开 WebSocket；重连时自动复用
- **index.html** — `activeProjects` Set 追踪活跃项目
  - 项目卡片显示绿色活跃圆点
  - 聊天头部显示"活跃"标记
  - 点击活跃项目直接重连，不重新创建

### 2. 会话快照保存/恢复
- **ClaudeService.cs** — `ChatEntry`/`ChatSnapshotEntry` 数据结构
  - `SaveSessionSnapshot(sessionId)` — 保存到 `.claude/claudeg-snapshot.json`
  - `GetSessionSnapshot(workDir)` — 从内存或文件加载
  - `SaveAllSnapshots()` — 关机前批量保存
  - WebSocket 断开、进程退出、手动停止时自动保存
- **index.html** — 活跃会话重连时先加载快照显示，再接入实时流
- **Program.cs** — `POST /api/projects/{name}/snapshot` 保存、`GET` 加载

### 3. 聊天记录.md 自动生成与归档
- 项目首次使用时自动创建 `聊天记录.md`
- 用户消息使用 `<span style="color:#64ffda;">` 标绿区分
- 超过 500KB 自动归档到 `.claude/聊天记录历史备份YYYYMMDD.md`

### 4. 目录浏览功能
- 前端：File System Access API (`showDirectoryPicker`) 优先
- 回退：服务端 `GET /api/browse-directories?path=...` 树形导航
- "浏览..."按钮在新建项目、添加已有项目的路径输入框旁
- 目录条目显示"有记忆"标记（检测 `.claude/` 目录存在）

### 5. 首次初始化检测
- `POST /api/projects/{name}/init-claude-if-needed`：
  - 检测项目 `.claude/` 目录是否存在
  - 自动创建 `聊天记录.md`
  - 写入 `claudeg.json`（项目元数据）到项目 `.claude/` 目录
  - 新建/添加项目时自动调用

### 6. 连接关闭快照
- `/api/shutdown` 端点调用 `SaveAllSnapshots()` 后退出
- 切换项目前自动触发 snapshot 保存

---

## 2026-07-08 — 实时会话恢复

### 点击项目时实时恢复 claude --continue 会话
- **问题**：点击项目时只显示 JSONL 静态历史，与 CMD 中 `claude --continue` 看到的实时会话不一致
- **修复**：点击项目时自动启动 `claude --continue --permission-mode bypassPermissions`（无 `-p`，stdin 保持打开）
  - 用户立即看到与命令行相同的实时会话状态
  - 用户第一条消息通过 stdin 直接写入运行中的 claude 进程
  - 后续消息恢复原有 `--resume` 机制
- **ClaudeService.cs** — 新增 `ResumeSessionAsync(workDir)` 和 `StartResumeProcess(workDir)`
  - `StartResumeProcess` 不传 `-p`，保持 stdin 打开（首次使用除外）
  - 新增 `WriteToStdinAsync(sessionId, prompt)` 向运行中进程写入用户消息
  - `SendMessageAsync` 自动检测 `StdinOpen`，优先使用 stdin 写入
  - `SessionRuntime` 新增 `StdinOpen` 字段
- **Program.cs** — 新增 `POST /api/projects/{name}/resume` 端点
- **index.html** — `selectProject` 改为调用 resume 端点
  - 新增 `resumeProjectSession()` 函数，2 秒超时回退到静态历史
  - 保留 `loadHistory()` 作为回退方案

---

## 2026-06-18 — 会话继续，双页面重构

### 项目管理重构（ConfigService + 双页面前端）
- **ConfigService.cs** — 新增。项目配置文件 `claudeg.json` 管理（存储在 exe 同目录，非 %APPDATA%）
  - `AddProject(name, path)` / `AddExistingProject(folderPath)` — 新建/添加已有项目
  - `RenameProject(old, new)` / `DeleteProject(name)` — 重命名/删除
  - `UpdateAccessTime(name)` — 记录最近访问时间
- **Program.cs** — 新增 6 个项目管理 API：
  - `GET /api/projects` — 列出所有项目
  - `POST /api/projects` — 新建项目（创建文件夹）
  - `POST /api/projects/add-existing` — 添加已有目录
  - `PUT /api/projects/{name}/rename` — 重命名
  - `DELETE /api/projects/{name}` — 删除（不影响实际文件）
  - `POST /api/projects/{name}/access` — 更新访问时间
  - `GET /api/exe-dir` — 获取 exe 所在目录
  - `GET /api/detect-output-path` — 自动检测编译输出目录
  - `POST /api/open-dir` — 在资源管理器打开目录
- **Models.cs** — 新增 `CreateProjectRequest`、`AddExistingRequest`、`RenameRequest`
- **wwwroot/index.html** — 完全重写为双页面设计
  - 页面1（项目列表）：项目卡片、新建/添加/重命名/删除、空状态提示
  - 页面2（聊天）：返回按钮、头部显示项目路径 + 编译输出路径（可点击打开）、聊天界面
  - 主题切换（深色/浅色）、localStorage 持久化

### 自动关闭机制
- 浏览器 `beforeunload` → `navigator.sendBeacon('/api/shutdown')` → `Environment.Exit(0)`
- WebSocket 连接追踪：客户端断开后 3 秒内无新连接则退出
- 连接计数 + 线程安全锁

---

## 2026-06-18 — 项目骨架与核心功能

### 项目初始化
- **ClaudeWebGui.csproj** — .NET 8 self-contained single-file publish
  - `OutputType=WinExe`（无黑窗）
  - `PublishSingleFile=true`, `SelfContained=true`, `IncludeNativeLibrariesForSelfExtract=true`
  - `AssemblyName=claudeg`（输出 `claudeg.exe`）
  - `wwwroot/**` 作为 EmbeddedResource 嵌入
- **Program.cs** — ASP.NET Core Minimal API 入口
  - 自动查找空闲端口
  - 启动后自动打开浏览器
  - 首页返回嵌入的 index.html
- **Models.cs** — `ClaudeStreamMessage`、`ClaudeMessage`、`ClaudeContentBlock`、`ClaudeUsage`、`SessionInfo`、`ClientMessage`、`WsOutgoingMessage`

### Claude 子进程管理（ClaudeService.cs）
- `StartSessionAsync(prompt, workDir)` — 启动 `claude` 子进程
- `SendMessageAsync(sessionId, prompt)` — 向已有会话追加消息（`--resume`）
- `StopSession(sessionId)` — 终止进程（`Kill(entireProcessTree: true)`）
- 并发读取 stdout/stderr（`ReadStdoutAsync` + `ReadStderrAsync` + `Task.WhenAll`）
- `BroadcastToSubscribersAsync` — 广播到所有 WebSocket 订阅者，自动清理死连接
- `EscapeArg` — 命令行参数转义

### 首次使用检测
- 检测 `.claude/` 目录是否存在
- 首次：发送初始化 prompt，引导 Claude 建立 `CLAUDE.md` 和本地记忆
- 非首次：`--continue --permission-mode bypassPermissions` 跳过权限确认
- `BuildInitPrompt(userPrompt, workDir)` — 构建初始化指令

### stdin 超时修复
- 启动进程后立即 `process.StandardInput.Close()`，避免 claude 等待管道输入 3 秒

### 系统消息中文翻译（TranslateClaudeMessage）
- stdin/stdout/stderr 相关术语
- 网络/API 错误（连接被拒绝、超时、频率限制、未授权等）
- 文件/权限错误
- 会话/进程状态（启动、完成、失败、取消等）

### WebSocket 流式推送
- `GET /ws/{sessionId}` — WebSocket 端点
- 客户端订阅/取消订阅机制（`ConcurrentBag<WebSocket>`）
- 支持多客户端同时监听同一会话

### HTTP API
- `POST /api/session` — 创建新会话
- `POST /api/session/{id}/msg` — 发送消息
- `POST /api/session/{id}/stop` — 停止会话
- `GET /api/sessions` — 列出所有会话
- `GET /api/session/{id}` — 获取单个会话
- `DELETE /api/session/{id}` — 删除会话

### 前端（index.html 初版 → 重写为双页面）
- 聊天界面：消息列表、流式渲染、Markdown + 代码高亮（marked.js + highlight.js CDN）
- 快捷操作按钮、输入框自适应高度
- Enter 发送 / Shift+Enter 换行
- 统计栏（输入/输出 token 数）
- tool_use / tool_result / thinking 内容块渲染

### 编译输出目录自动检测（DetectBuildOutputPath）
- 按优先级检测：`bin/Release` → `bin/Debug` → `dist` → `build` → `build/Release` → `build/Debug` → `out/Release` → `out/Debug` → `target` → `target/release`
- 兜底：检查 `bin/` 下的子目录

### 构建脚本（build.cmd）
- 终止运行中的 `claudeg.exe`
- 清理旧输出
- `dotnet publish` + 复制到 `D:\Prog\ProgIDE\Claude\bin\`
