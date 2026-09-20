# 体积与后台资源优化

本次保留 WPF/XAML、主题、图标、所有语言资源、数据库格式、输入采样频率（250 ms）和用户配置的进程扫描间隔。未改用其他 UI 框架，也未强制清空工作集来制造低内存读数。

## 两种发布包

- **免安装版**：`scripts/publish-win.ps1`。保留全部 .NET 8 桌面运行库，无需另装运行环境。
- **轻量版**：`scripts/publish-win.ps1 -FrameworkDependent`。输出 `artifacts/GameActivityTracker-win-x64-lite.zip`。需要系统已安装 **.NET 8 Desktop Runtime，Windows x64**；普通 .NET Runtime 不足以运行 WPF。程序界面、功能与数据目录相同。运行库官方下载：https://dotnet.microsoft.com/download/dotnet/8.0 。

轻量包省去的是应用目录中重复携带的运行库。若系统尚未安装桌面运行库，必须把另行安装的运行库也计入总磁盘成本，不能把轻量包大小称为完整独立应用大小。两种包之间切换时使用新的解压目录，避免旧运行库残留；用户记录仍保存在原来的 LocalAppData 目录。

WPF 不支持可靠的自动裁剪，因此未启用 PublishTrimmed 或 NativeAOT，也未按文件名猜测并删除运行库。参考：https://github.com/dotnet/wpf/issues/3811 。

## 免安装版无损磁盘压缩

在 Windows 10/11 的 NTFS 磁盘上解压后，先从托盘退出应用，再运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\runtime\docs\compact-runtime.ps1
```

使用 Windows 自带 `compact.exe /EXE:LZX` 压缩程序和运行库，保留全部文件内容。关注资源管理器的“占用空间”，逻辑“大小”不变。压缩属性不能通过 ZIP 保留；需要在最终解压目录执行。压缩率取决于运行库与磁盘，读取文件时存在解压成本，不能据此声称降低后台内存。恢复未压缩状态：

```powershell
powershell -ExecutionPolicy Bypass -File .\runtime\docs\compact-runtime.ps1 -Undo
```

只处理应用目录，不处理用户数据库。NTFS 和命令要求见 Microsoft 文档：https://learn.microsoft.com/windows-server/administration/windows-commands/compact 。

## 后台优化

1. `--background` 启动时不创建主窗口、视图模型、热力图或读取历史会话，第一次打开托盘窗口才创建。
2. 主窗口隐藏后停止界面刷新计时器，解除完整历史会话缓存的引用；显示时立即重新加载。保留窗口、选择、滚动位置和未保存设置，避免用户操作状态丢失。已打开的详情窗口可继续持有其自身数据。
3. 使用 Toolhelp 进程快照按名称筛选，再读取候选进程的路径和启动时间，避免每轮为所有系统进程构建 `Process` 对象。仍验证完整路径和启动时间，保留命令行/WMI、Steam 和 Minecraft 规则。枚举失败会报错重试，不当作所有游戏已退出。
4. 规则使用记录值比较与版本号失效，不再每轮序列化成 JSON。后台规则扩展完成时检查版本，丢弃过期结果。路径缓冲区通常只分配 512 字符，必要时扩展到 32768，保留长路径支持。
5. 无会话时跳过数据库检查点事务；记录期间每 5 秒持久化会话头、上一检查点的末尾片段及新增片段，避免重写整场游戏的全部历史片段。事务成功后才更新游标，失败后可以安全重试。
6. 活动会话成员列表仅在开始/结束时重建，托盘游戏名使用随规则加载更新的缓存。

历史对象解除引用后由 .NET 垃圾回收器回收；不承诺立即返还相同数目的物理内存页。主窗口曾经打开过与从未打开过的后台状态，其资源占用也不同。

## 验证和内存复测

2026-09-20 用户实测反馈：优化后后台内存约 **96 MB**。未提供工作集/Private Bytes 口径、机器配置与完整采样，因此该值作为用户反馈记录，不作为所有设备的上限或统一基准。

交叉编译与核心测试无法代替 Windows 界面/原生 API 的验证。Windows CI 已配置两种包的普通启动、后台延迟创建窗口、隐藏/恢复、进程规则缓存失效，以及磁盘压缩后的相同冒烟检查。macOS 本地不能执行这些 Windows 检查；发布时的 Windows 验证结果以对应提交的 GitHub Actions 和 Release 说明为准。

在同一 Windows 机器、同一游戏规则与数据库下分别运行旧版与新版，等待各场景稳定后采样：

```powershell
./scripts/measure-memory.ps1 -Seconds 60 -Output artifacts/background.csv
```

脚本附加到已有进程，不启动、终止或修改应用，不触碰数据，不强制 GC。分别测量后台启动未开窗口、开窗后收起、前台显示和实际记录游戏四个场景；固定游戏数量、历史量、系统进程数量与采样等待时间。记录工作集、Private Bytes、CPU 时间、句柄和线程；Private Bytes 不等于任务管理器的专用工作集。还需人工核对浅色/深色、Steam 导入、Minecraft、手柄、锁屏/睡眠、长路径及托盘恢复。
