<p align="center">
  <img src="src/GameActivityTracker.Windows/Assets/App.png" width="96" alt="Game Activity Tracker 图标">
</p>

# Game Activity Tracker

一个本地运行的 Windows 游戏时长记录器。通过游戏进程、前台窗口和近期输入，区分实际活跃、空闲和后台运行时间，用年度热力图展示游玩记录。

无需账号、API Key 或在线服务。游戏规则、设置和历史记录保存在本机 SQLite 数据库中。

## 功能

- **时长统计**：记录 Active、Idle、Background、Unknown 与 Running，提供年度统计、每日详情和小时记录。
- **热力图**：年度日期方块、横向 24 小时视图、悬停提示及按游戏汇总；支持年份切换。
- **游戏管理**：手动添加 exe，自动扫描 Steam 默认位置与登记库，预览确认后批量导入。
- **进程识别**：支持名称、完整路径、命令行等组合规则；可后台发现游戏目录中的本体 exe，排除常见启动器和辅助工具。
- **Minecraft / PCL**：根据客户端入口与游戏目录匹配 Java 进程，可按整个 `.minecraft` 或单个版本目录记录。
- **外观与后台运行**：浅色、深色、跟随系统；系统托盘、登录启动和手柄输入检测。

## 技术栈

| 部分 | 技术 |
| --- | --- |
| 语言与运行时 | C# 12、.NET 8 |
| Windows 界面 | WPF、XAML、MVVM |
| 数据持久化 | SQLite、Microsoft.Data.Sqlite |
| 系统集成 | Win32、WMI、XInput、WinForms 托盘图标 |
| 测试 | xUnit |
| 持续集成 | GitHub Actions，Windows 构建、测试、发布及启动冒烟检查 |

## 运行要求

- Windows 10 / 11，x64。
- 从源码构建需要 .NET 8 SDK；可用安装了“.NET 桌面开发”工作负载的 Visual Studio 2022。
- 自包含发布包不需要另行安装 .NET Runtime。

macOS / Linux 可以交叉编译 Windows 目标并运行核心测试，但不能在本机验证 WPF 界面与 Windows 输入 API。

## 从源码构建

在仓库根目录执行：

```powershell
dotnet restore
dotnet build -c Release
dotnet test tests/GameActivityTracker.Tests -c Release
```

Windows 上运行：

```powershell
dotnet run --project src/GameActivityTracker.Windows -c Release
```

首次还原 NuGet 依赖需要网络；应用的记录与统计功能在本地运行。

## 打包

Windows PowerShell 中执行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/publish-win.ps1
```

脚本执行构建、测试和发布，生成 `artifacts/GameActivityTracker-win-x64.zip`。构建产物不纳入 Git。

完整解压后目录结构如下：

```text
GameActivityTracker.exe
runtime/
  GameActivityTracker.dll
  ...运行库、依赖和资源
  docs/
```

请保留 exe 旁边的 `runtime` 文件夹。程序直接从该文件夹加载依赖，不采用单文件临时解压方案。

Windows 启动冒烟检查：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/smoke-test.ps1
```

该检查使用独立临时数据目录，验证启动、SQLite 初始化、记录循环和正常退出。

## 使用方法

1. 点击右上角齿轮进入设置，在左侧选择 **游戏管理**。
2. 点击 **添加游戏** 指定游戏 exe，或通过 **导入 SteamLibrary** 扫描游戏库；也可手动选库。
3. 确认游戏本体 exe。默认启用目录内相关进程识别，复杂情况可通过 **编辑游戏 / 规则** 配置。
4. Minecraft / PCL 可在编辑页选择 `.minecraft` 目录以统计多个版本，或选择某个版本目录单独统计。
5. 启动游戏后，在主页面查看当前游戏、年度统计和热力图；点击日期查看小时记录及当天各游戏汇总。
6. 关闭窗口默认收起到托盘；完全退出请使用托盘菜单的 **Exit / 退出**。

设置页提供游戏管理、外观、记录设置、手柄、系统、数据与隐私分类；右上角返回箭头可回到主页面。

## 统计口径

| 状态 | 含义 |
| --- | --- |
| Active | 游戏在前台，且近期有有效键鼠或手柄输入 |
| Idle | 游戏在前台，但输入已超过空闲阈值 |
| Background | 游戏在运行，但不在前台或系统已锁屏 |
| Unknown | 因睡眠、观察中断或信息不可用，无法确定状态 |
| Running | 上述四种状态的总和 |

默认空闲阈值为 60 秒。记录从本地首次观察到进程开始，不导入或推算 Steam 历史时长。日期统计按当前系统本地时区拆分。

## 数据与隐私

```text
%LOCALAPPDATA%\GameActivityTracker\
  activity.db
  logs\
```

只检测是否发生输入，不保存按键文本、鼠标轨迹或手柄原始输入。进程路径和必要的命令行用于规则匹配。

备份前请从托盘完全退出，再复制整个数据目录。程序文件与用户数据分开存放；升级时完整替换 exe 和 `runtime`，不要删除用户数据目录。

升级及旧版缓存清理说明见 [UPGRADE.md](UPGRADE.md)。

## 项目结构

```text
src/
  GameActivityTracker.Core/       # 数据模型、匹配规则、记录与统计逻辑
  GameActivityTracker.Data/       # SQLite 持久化
  GameActivityTracker.Windows/    # WPF 界面、Windows 进程与输入检测
tests/GameActivityTracker.Tests/  # 核心逻辑与数据库测试
scripts/                         # 发布、目录布局与 Windows 冒烟检查
.github/workflows/               # Windows CI
```

## 验证与限制

[验证记录](VALIDATION.md)中最近一次功能测试为 **62 项通过**，包括进程匹配、Minecraft 目录识别、统计、持久化和设置兼容。该数字是历史记录；当前版本的测试结果请以本地运行或 GitHub Actions 为准。

- Windows 实机的 Apex、PCL、反作弊权限、主题切换和鼠标交互仍需验证；交叉编译成功不代表实机运行验证完成。
- 进程检测采用轮询，短暂进程可能遗漏；后台运行时间不等于实际游玩时间。
- 无法读取受保护进程的信息时，不绕过保护，也不会自动请求管理员权限。
- 手柄检测支持 XInput；不直接支持所有 DirectInput 或未映射手柄。
- 没有安装器、自动更新、代码签名、云同步或 Steam 历史时长导入。

详细记录见 [VALIDATION.md](VALIDATION.md)，人工验收步骤见 [WINDOWS-ACCEPTANCE.md](WINDOWS-ACCEPTANCE.md)。

## 许可证

本项目尚未指定开源许可证。公开仓库不等于授予开源许可；如需按开源方式分发，请先由项目所有者选择并添加许可证。
