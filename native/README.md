# Game Activity Tracker — Rust 后台轻量引擎

本目录是游戏时长记录器的原生 Rust 实现：它承载领域逻辑、SQLite 持久化与 Windows 平台
采集，是**唯一的记录者**，并提供**无主窗口、低内存的托盘后台轻量模式**。原有 WPF 界面
保留为查看/编辑界面（C# 跟踪引擎已删除），通过共享 SQLite 与本地控制通道读取实时状态。

## 工作区结构

| crate | 职责 |
| --- | --- |
| `gat-core` | 领域逻辑：模型、进程匹配规则、Minecraft 识别、Steam 库读取、会话状态机、统计。无平台依赖，可在任意平台测试。 |
| `gat-data` | SQLite 持久化（rusqlite + bundled）。schema 与 C# 完全一致，可读写同一个 `activity.db`；后台专属设置存于独立表 `DaemonSettings`。 |
| `gat-platform` | Windows 平台层：进程枚举、前台窗口、最后输入时间、XInput 手柄、注册表开机启动、命名互斥量、托盘消息循环、电源/会话通知。非 Windows 提供 stub，使工作区可在 Linux 编译并测试核心逻辑。 |
| `gat-app` | `gat` 可执行文件：托盘后台轻量模式（引擎独占托盘，左键唤起 WPF 查看器）、无界面模式、冒烟测试、CLI 子命令与本地控制通道。 |

## 构建与测试

需要 Rust 1.82 及以上。Windows：

    cargo test --manifest-path native/Cargo.toml --workspace
    cargo build --release --manifest-path native/Cargo.toml -p gat-app

Linux（验证核心逻辑；用 mingw 目标对 Windows 平台层做类型检查）：

    cargo test --manifest-path native/Cargo.toml --workspace
    cargo check --manifest-path native/Cargo.toml -p gat-platform --target x86_64-pc-windows-gnu

release 二进制约 4 MB，无 .NET 运行时依赖。v2 默认使用完整扫描和关联进程发现；只有显式传入 `--lightweight` 才会降低扫描频率并关闭关联进程目录扫描。

Windows release 使用 GUI 子系统，后台启动和开机自启不会创建控制台窗口。`--headless` 适合诊断时使用，错误会写入数据目录的日志文件。

## 运行

    gat                 # 托盘后台轻量模式（默认）
    gat --background    # 同上
    gat --headless      # 无托盘、无界面运行
    gat --lightweight   # 最轻配置：关闭关联可执行文件目录扫描
    gat --smoke-test --smoke-seconds 4 --data-dir <目录>

所有命令都支持 `--data-dir`；未指定时使用 `%LOCALAPPDATA%\GameActivityTracker`
（可用环境变量 `GAT_DATA_DIR` 覆盖）。

CLI 子命令：

    gat status      # 查询后台状态与当前游戏
    gat snapshot    # 查询当前正在运行的游戏
    gat games       # 列出已登记游戏（直接读库，后台未运行也可用）
    gat stats       # 本年度统计（直接读库）
    gat reload      # 让后台重新加载规则与设置
    gat suspend     # 暂停记录
    gat resume      # 恢复记录
    gat shutdown    # 退出后台进程

## 后台轻量模式

- 单进程、无主窗口、无 WPF/XAML；Windows 上只有一个隐藏窗口、一个托盘图标和 250ms 定时器。
- **唯一托盘**：托盘图标只由引擎持有，左键单击打开（或激活已有）WPF 查看器，右键菜单提供
  “打开/退出”。WPF 自身不再有托盘，关闭窗口即完全退出。
- **托盘图标来源**：优先使用 `gat.exe` 同目录的 `App.ico`（替换该文件即可换图标），否则从同目录的
  `GameActivityTracker.exe` 提取内嵌图标，都没有时回退系统默认图标。
- `--lightweight` 会关闭关联可执行文件目录扫描、降低进程扫描频率，并限制日志大小（5 MB/天）
  与保留天数（30 天）。
- **唯一记录者**：引擎使用自己的互斥量 `Local\GameActivityTracker.Engine`，始终负责记录；
  WPF 界面不再采集，只通过共享 `activity.db` 与控制通道展示实时状态，因此不会再出现重复计时。
- 与 WPF 版共享同一个 `activity.db`，不改变用户数据格式。

## 控制通道

后台进程会在数据目录写入 `control.json`：

    { "port": 45050, "token": "<随机令牌>", "pid": 1234, "startedAt": "..." }

服务仅绑定 `127.0.0.1`，每个请求都必须携带令牌。请求与响应都是单行 JSON：

    请求:  {"token":"...","command":"status"}
    响应:  {"ok":true,"data":{...}}

支持的命令：`ping`、`status`、`snapshot`、`games`、`stats`、`reload`、`suspend`、
`resume`、`shutdown`。C# 侧可用 `TcpClient` 读取 `control.json` 后连接，实现自定义集成。

## 测试

`gat-core` / `gat-data` 的测试覆盖空闲阈值、采样点切分、前台/后台/未知状态、跨午夜与
DST（23/25 小时）、连续天数、Minecraft 版本目录、关联可执行文件展开、Steam 库扫描与
去重、数据库 checkpoint 幂等与事务回滚，并包含读取 C# 风格 JSON 与时间戳的兼容性测试。
C# 跟踪引擎及其测试工程已删除，测试由本工作区接管。

## Windows 实机验证清单

以下项目需要在 Windows 实机确认（交叉编译通过不等于实机通过）：

- [ ] 托盘图标显示、左键单击唤起 WPF 查看器、右键“退出”
- [ ] WPF 查看器通过控制通道显示实时状态（无重复计时）
- [ ] WPF 无自带托盘、关闭即退出；重复点击托盘只激活已有窗口
- [ ] 开机自启 Run 键指向 §gat.exe --background§（由引擎读写）
- [ ] 前台窗口与 `GetLastInputInfo` 的 Active/Idle 判定
- [ ] XInput 手柄输入识别
- [ ] 注册表开机启动（`HKCU\...\Run`）写入与删除
- [ ] 休眠/唤醒、锁屏/解锁消息处理
- [ ] `gat --smoke-test` 退出码与 SQLite 初始化
- [ ] 与 WPF 版 `activity.db` 双向兼容

## 故障排查：托盘未出现

1. **`gat.exe` 是否与 `GameActivityTracker.exe` 同目录（包根）？**
   便携布局把托管程序放在 `runtime/`，`AppContext.BaseDirectory` 指向 `runtime/`；查看器按**可执行文件所在目录（包根）**查找，
   再回退 `runtime/`，并向上搜索 `native/target/{release,debug}/gat.exe` 与 `artifacts/native/gat.exe`。
   开发时先构建引擎：`cargo build --release --manifest-path native/Cargo.toml -p gat-app`。
   `dotnet publish` 现在会把 `gat.exe` / `App.ico` 一并放到包根（`scripts/publish-win.ps1` 也会兜底复制）。
2. **是否有残留的 `control.json`？**
   引擎崩溃或被杀后可能留下它。查看器现在会先 `ping` 端口确认引擎确实在运行，不可达时删除残留文件并重新拉起引擎。
3. **看日志**：`%LOCALAPPDATA%\GameActivityTracker\logs\` 下的 `gat-<日期>.log` 应包含
   `Starting native tray background mode`；启动失败会写 `startup-error.log`。
4. **手动运行排除法**：命令行执行 `gat --headless`（带控制台输出）或 `gat status`，可直接看到引擎报错。
5. **Windows 11 托盘收纳**：新通知图标默认折叠在任务栏“^”溢出区，展开并固定即可，这不代表引擎未运行。
6. **单实例**：已有引擎运行时新进程会立即退出并复用已有实例，属正常行为。
