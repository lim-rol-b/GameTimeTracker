# 交付验证记录

## 游戏本体与 Minecraft 进程识别

Windows 路径查询改为 PROCESS_QUERY_LIMITED_INFORMATION + QueryFullProcessImageName，并从同一有限信息句柄读取进程创建时间，保留 PID 重用保护。后台为简单完整路径规则生成游戏安装目录内的低优先级候选，跳过启动器／辅助工具，不放宽高级规则。Steam 导入保存安装目录，旧游戏可从 Steam 元数据或原 exe 目录补全。Minecraft 匹配 java/javaw、客户端入口和 gameDir，支持 PCL 版本隔离；编辑页提供目录选择。发现任务使用规则快照，过期结果丢弃，不阻塞记录锁进行目录遍历。62 项测试全部通过，包含 Apex 变体、2077 本体与启动器、Minecraft 多版本和误匹配排除。Windows 发布成功；未在 Windows 实机验证 Apex 权限、PCL/Forge/其他启动器实际行为。

## 每日详情布局

每日详情移除 Sessions 标题、说明及列表，游戏详情的会话展示保留。各游戏当日活跃／运行时长改为右侧汇总卡，与左侧小时方块区域顶部对齐，长列表独立滚动；无记录显示空状态。小时详情仍在方块下方。Windows 发布编译成功，检查 XAML 布局与包结构；未在 Windows 实机验证。

## 设置文字主题与右上导航

设置内容区显式使用主题文字色，避免分类选中态白字继承到浅色内容；游戏表格普通行与交替行背景改为主题资源。移除设置页内返回按钮，右上角按钮按当前页切换齿轮／返回箭头，同时更新提示与无障碍名称。Windows 发布编译成功，检查主题绑定和导航入口；未在 Windows 实机验证。

## 设置分类导航

设置页面主标题固定为“设置”，返回主页面后恢复实时游戏名。将游戏管理、外观、记录设置、手柄、系统、数据与隐私分为左侧纵向分类，右侧内容独立滚动，统一保存按钮保持可见。导入后选中游戏管理分类，其他设置绑定保持原样。Windows 发布成功，已检查 XAML 分类顺序、命令保留及包结构；未在 Windows 实机验证。

## 主页面精简与统一设置入口

移除右上标语、Currently Playing 卡片、热力图时段汇总及操作说明；保留年度统计、年份侧栏及颜色图例。顶部导航隐藏，右上齿轮进入合并后的游戏管理／外观／记录设置页，提供返回按钮。导入后仍停留游戏管理，原有添加、编辑、删除、统计和设置命令保留。Windows 发布编译成功，已检查 XAML、命令归属和发布包结构；未执行 Windows 实机交互验证。

## 三种主题与年份侧栏

新增浅色／深色／跟随系统，默认跟随系统，设置即时保存。共享颜色资源随 Windows 应用颜色设置更新，已打开的日期和小时颜色同步变化。年份下拉框改为右侧可纵向滚动列表，蓝色选中态；从首次使用当年开始，跨年自动追加，保留旧数据年份，不显示未来年份。主题及起始年份在原有 Settings JSON 中保存，无数据库表结构迁移。Windows 发布成功，49 项测试通过，新增覆盖主题持久化、旧设置默认值、跨年及历史年份。已检查 XAML、主题资源引用和 exe + runtime 包结构。尚未实机验证 Windows 配色、系统主题通知、键盘及滚动交互。

## 日期浮层与横向小时热力图

全年日期提示改为方块上方的深色圆角浮层，保留日期及原有统计；24 小时改成横向一排，00–23 标签在方块下方，与全年共用 16×16、5px 间距、2px 圆角和悬停样式。340×340 小时记录卡移至整排下方，保留悬停、键盘选择及卡片内滚动。Windows 发布编译通过，已检查 XAML 和包结构；未在 Windows 实机验证视觉与交互。

## 固定依赖目录发布

已取消单文件自解压，根目录仅有 GameActivityTracker.exe 与 runtime 文件夹；使用 SDK CreateAppHost 将入口绑定到 runtime/GameActivityTracker.dll。运行库、WPF、SQLite 和资源均放在 runtime，说明在 runtime/docs；原有 ProcessPath 开机启动仍指向根目录 exe。Windows x64 发布成功，检查入口内嵌路径、未设置 bundle 偏移、自包含运行配置及关键依赖。更新发布脚本与 Windows CI，并扩展 Windows 冒烟脚本以检查从不同工作目录启动且不生成解压缓存。本次未执行 Windows 实机启动或 PowerShell 脚本；现有 45 项核心测试结果属于前次验证。旧缓存不自动删除，数据位置不变。

## Windows 启动错误排查更新

收到 TypeConverterMarkupExtension 异常，尚缺少内部异常堆栈，不能确认最终根因。移除窗口 XAML 的 ICO 转换，改为代码加载内嵌 PNG，并为窗口／托盘图标提供失败回退；ICO 重编码为 32 位 DIB 多尺寸格式。启动失败弹窗显示具体阶段、完整内部异常链和日志目录。单文件编译成功，已检查 7 个 ICO 帧的头部、长度及 EXE 内嵌图标；尚未在 Windows 实机复现或验证修复，数据库保持原样。

## Steam 自动扫描与筛选

自动扫描默认位置及登记库，按 AppID 去重；优先推荐唯一同名 exe，过滤辅助工具，保留 Engine 下的游戏程序和手动选择入口。单文件发布成功，45 项测试全部通过，新增 3 项覆盖多库发现与去重、同名候选歧义和工具过滤。Windows 注册表及 UI 尚未实机验证。

## 应用图标

用户提供的 PNG 转为含 16/24/32/48/64/128/256 尺寸的 ICO，保留原图外观。配置 exe 原生图标、主窗口图标和托盘图标，图标作为内嵌资源随单文件发布。Windows x64 发布成功，并检查 exe 的 RT_ICON/RT_GROUP_ICON 资源及包结构；未在 Windows 实机验证显示。

## 热力图圆角调整

参考 GitHub 贡献日历的小圆角外观，将 HeatmapTile 圆角从 5px 改为 2px（日期与小时方框共用此样式）。年度方框仍为 16×16、间距 5px。Windows x64 单文件发布成功；未在 Windows 实机验证界面。

## 热力图方框缩小

年度热力图日期方框从 32×32 改为 16×16，间隔从 10px 同步减半为 5px，周行高和月份定位同步对齐；颜色、悬停、点击和滚动行为保持原样。Windows x64 单文件发布成功，已检查 XAML 和发布包结构；未在 Windows 实机验证界面。

## 添加游戏入口与实时标题

“添加游戏”移到 Games 页的“导入 SteamLibrary”旁边；顶部大标题绑定当前记录游戏，每秒更新，无游戏时显示“等待游戏启动”，多个游戏同时记录时并列显示，ACTIVE 优先。长标题省略显示，悬停可查看完整名称。Windows x64 单文件发布成功，已检查 XAML 入口归属和发布目录结构；未执行 Windows 实机 UI 验证。

## 单文件发布更新

已成功交叉编译 Windows x64 自包含单文件程序。发布目录仅包含 GameActivityTracker.exe 和 docs/，运行库、SQLite 及资源内嵌，调试符号嵌入程序集；发布脚本使用全新暂存目录并检查单 exe 输出，避免旧依赖混入。已核对 ZIP 条目；未在 macOS 上执行 Windows 启动测试。日志见 artifacts/publish.log。

## 最新主页面布局与滚轮修复

主页面垂直滚动条移至窗口最右侧，内容内边距独立设置；移除 Overview 的 Recent sessions 列表和无用视图集合，历史会话不变。年度热力图普通滚轮事件显式转交外层垂直页面，Shift + 滚轮保留横向滚动，横向指示器不会因普通滚轮被唤醒。Windows x64 自包含发布成功，退出码 0，日志见 `artifacts/publish.log`。未运行测试或 Windows 实机交互验证。数据库位置及格式不变。

## 最新统一滚动条更新

新增全局滚动条模板与操作状态管理：闲置隐藏，滚动时显示约 4px 灰色位置滑块，悬停／拖动时约 8px 并加深，显示半透明轨道；停止操作 1.2 秒后淡出。移除了年度热力图旧有的单独滚动条样式。Windows x64 自包含发布成功，退出码 0，日志见 `artifacts/publish.log`。未运行测试，未在 Windows 实机验证视觉与鼠标交互。数据库位置及格式不变。

## 最新热力图交互更新

年度日期方块改为 32px、间隔 10px，加入横向拖动、自动淡出滚动条和悬停缩放；定时刷新保留滚动位置。日期详情新增竖向 24 小时热力图和右侧方形记录卡，小时区间从原有数据计算。已编译并发布 Windows x64 自包含包，退出码 0，日志见 `artifacts/publish.log`。未运行测试，Windows 上的鼠标交互和视觉效果未在 macOS 开发主机实机验证。数据路径及格式保持兼容。

## 最新外观与兼容更新

普通界面改为浅色背景与全黑文字，热力图卡片、标签、图例与提示使用深色背景和白字。沿用旧版 `%LOCALAPPDATA%\GameActivityTracker\activity.db` 和版本 1 数据结构；未改动持久化字段或执行清空操作。已直接执行 Windows x64 自包含发布并更新两个 ZIP，发布退出码 0；按用户要求未运行测试。升级说明随发布包提供，Windows 实机运行未在本机执行。

## SteamLibrary 导入增量

已新增库文件夹选择、ACF 清单读取、exe 候选选择、去重导入和原子批量保存。此增量已执行完整 Release 编译，结果为 **0 警告、0 错误**，日志见 `artifacts/steam-library-build.log`。按用户要求未运行测试，也未在 macOS 上执行 Windows UI。现已从最新源码重新发布自包含 Windows x64 包，并同步更新源码 ZIP 和 SHA256 校验值；发布命令成功退出，日志见 `artifacts/publish.log`。本次未运行测试。

以下 42 项测试结果属于初版交付记录，不代表本次新增导入功能已通过测试。

## 初版交付记录

验证日期：2026-09-16。

环境：macOS / Apple Silicon，.NET SDK 8.0.425；Windows 应用交叉编译到 `net8.0-windows` / `win-x64`，测试运行于 `net8.0` / ARM64。

| 项目 | 实际结果 | 证据 |
|---|---|---|
| `dotnet restore GameActivityTracker.sln` | 成功，退出码 0 | `artifacts/restore.log` |
| `dotnet build GameActivityTracker.sln -c Release --no-restore` | 成功，0 warnings，0 errors | `artifacts/build.log` |
| `dotnet test GameActivityTracker.sln -c Release --no-build --no-restore` | 42 passed，0 failed，0 skipped | `artifacts/test.log`、`artifacts/test-results/core-tests.trx` |
| `dotnet publish … -c Release -r win-x64 --self-contained true` | 成功，退出码 0 | `artifacts/publish.log` |
| PE 架构检查 | EXE 为 Windows GUI x86-64，原生 SQLite DLL 为 x86-64 | `file` 检查 |
| WPF 实际启动 | **未执行：开发主机不是 Windows** | 提供 `scripts/smoke-test.ps1` 和 Windows CI |
| 真正游戏／键鼠／XInput／睡眠／托盘人工验收 | **未执行：需要 Windows 实机** | `WINDOWS-ACCEPTANCE.md` |

本机为避免受限环境的 MSBuild 子进程阻塞，编译附加了 `-m:1 -p:UseSharedCompilation=false --disable-build-servers`；这是构建执行参数，不改变程序行为或编译目标。SDK 与 NuGet 缓存在临时目录中，不是项目运行时依赖。发布目录已带 Windows .NET Runtime 和 SQLite 原生依赖。

测试涵盖核心逻辑与真实 SQLite 持久化／事务，不将“测试通过”扩张解释为 Windows 全平台端到端验收通过。Windows CI 文件已创建，未声称已经在远程 CI 执行。

## 2026-09-20：体积与后台资源优化（本地）

- .NET SDK 8.0.425，macOS arm64，Windows x64 交叉编译：0 警告、0 错误。
- xUnit：65 项通过，0 失败。新增会话成员缓存、增量检查点片段边界与失败事务重试测试。
- 已发布并核验两种包的目录布局、apphost 相对路径绑定、runtimeconfig 和 ZIP 完整性。全部 XAML 与修改前字节一致。
- `artifacts/GameActivityTracker-optimized-win-x64.zip`：71,198,425 字节；解压 171,455,416 字节。
- `artifacts/GameActivityTracker-optimized-win-x64-lite.zip`：1,589,116 字节；解压 3,924,478 字节。另需系统安装 .NET 8 Desktop Runtime x64，运行库占用不包含在轻量包大小内。
- Windows 原生进程枚举、主窗口延迟创建/托盘恢复、压缩后启动检查已加入 CI，但本次尚未在 Windows 执行。UI 文件不变不等于 Windows 行为已经全面验证。
- 后台内存降幅与 NTFS 压缩后的磁盘占用尚无实机测量，不给出估算值作为结果。测量方式见 PERFORMANCE.md 和 scripts/measure-memory.ps1。

## 2026-09-20：v1.0.2 发布补充

- 用户反馈：后台内存已下降至约 96 MB。该数值来自用户实测；未提供测量口径和机器配置，不推算统一降幅或所有机器的上限。
- v1.0.2 同时提供免安装版和轻量版；轻量版要求 .NET 8 Desktop Runtime（Windows x64），发布说明、README 和包内说明均注明。
- Windows CI 同时构建两种包，发布资产采用对应提交的 CI 构建产物；最终运行结果见 GitHub Actions 和 Release。
