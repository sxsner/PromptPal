# PromptPal — 提示词快捷工具

Windows 桌面应用：管理提示词、按分类/标签收藏、一键复制、全局热键唤起、系统托盘常驻；另带一个 MCP stdio 服务，供 Trae 等 AI 客户端直接读写同一份提示词库。

当前版本：**v1.1.0**（.NET 10 / WinUI 3，x64，仅 Windows 10 1809+）。

> 本 README 兼作接力开发交接文档。**「构建与发布」「已知坑（勿回退）」两节是踩坑固化的硬约束**，改动前必读。

## 功能概览

- 两栏 Fluent 风格界面，浅色/深色/跟随系统 + 三档字号，运行时切换即时生效；窗口位置尺寸自动记忆。
- 分类（拖拽排序/重命名/删除解绑）+ 标签（大小写不敏感复用、孤儿标签自动清理）+ 收藏置顶 + 使用次数统计 + 三种排序（频率/更新/名称）。
- 全局热键 `Ctrl+Shift+P` 呼出/隐藏；关窗驻留托盘（固定 GUID 身份，Explorer 重启自愈）；复制默认写剪贴板并自动隐藏；双击列表项快速复制。
- 设置窗：外观、行为开关、热键、开机自启（注册表 Run）、数据管理（导出/导入 .db、重置所有数据）、分类拖拽卡片、MCP 接入、关于。
- MCP sidecar：13 个工具，stdio 协议，与 App 共用同一 SQLite（WAL）。
- 首次启动写入内置示例：**系统 / 编程 / 文档 3 分类 × 3 条提示词（共 9 条）+ 8 个常用标签**；「重置所有数据」重放这套内容。

## 技术栈

| 组件 | 版本 | 说明 |
|---|---|---|
| .NET | 10.0 | App TFM `net10.0-windows10.0.26100.0`；Core/Mcp 为 `net10.0` |
| WinUI 3 | WindowsAppSDK 2.4.0 | 非打包（unpackaged）self-contained |
| EF Core | 10.0.12 | Microsoft.EntityFrameworkCore.Sqlite |
| MVVM | CommunityToolkit.Mvvm 8.4.2 | |
| MCP | ModelContextProtocol 2.2.0 | `AddMcpServer().WithStdioServerTransport()` |
| DI/Hosting | Microsoft.Extensions 10.0.x | Scoped DbContext，MCP 每次调用独立 scope |

## 目录结构

```
src/
  PromptPal.Core/        实体、AppDbContext、服务（Category/Prompt/DbTransfer）、DatabaseInitializer
  PromptPal.App/         WinUI 3 应用：XAML、ViewModel、P/Invoke（热键/托盘/DWM/单实例）、Logger
  PromptPal.Mcp/         MCP stdio sidecar：Program.cs 宿主 + PromptPalTools.cs 13 工具 + SidecarLog
tests/
  PromptPal.Core.Tests/  xUnit（内存 SQLite），21 个用例：服务层 16 + 导入导出 5
docs/
  使用说明.txt           面向最终用户的说明（与发布包内同名文件保持一致）
  提示词实例文档.txt      9 条内置示例提示词的文案来源
design/                  早期 UI 原型（参考用）
publish/
  PromptPal-win-x64/     当前唯一可分发 Release 产物（539 文件，展开约 275MB）
  PromptPal-win-x64.zip  分发包（约 112MB，顶层保留同名文件夹）
run-promptpal.cmd        一键 Debug 自包含构建并启动
```

## 开发运行

本机未开开发者模式，打包式调试 / `dotnet run` 走不通。直接构建自包含 Debug 后跑散装 exe：

```powershell
# 或直接双击根目录 run-promptpal.cmd
dotnet build src/PromptPal.App -c Debug -r win-x64 --self-contained true `
  -p:WindowsAppSDKSelfContained=true
# 产物：src/PromptPal.App/bin/Debug/net10.0-windows10.0.26100.0/win-x64/PromptPal.App.exe
```

- 数据目录：`%LOCALAPPDATA%\PromptPal\`（首次启动自动建库、ACL 收敛为当前用户 + SYSTEM）。
- MCP 调试：直接运行 `PromptPal.Mcp.exe` 即 stdio 宿主；F5 配置模板见 [mcp.json.template](src/PromptPal.Mcp/mcp.json.template)。
- 跑测试：`dotnet test tests/PromptPal.Core.Tests`（21/21）。

## 数据与日志

| 内容 | 位置 |
|---|---|
| 数据库 | `%LOCALAPPDATA%\PromptPal\promptpal.db`（SQLite，WAL 模式，`Default Timeout=5`） |
| 应用设置 | `%LOCALAPPDATA%\PromptPal\settings.json`（原子写；主题/字号/热键/窗口边界等） |
| 应用日志 | `%LOCALAPPDATA%\PromptPal\logs\PromptPal-yyyy-MM-dd.log` |
| MCP 日志 | `%LOCALAPPDATA%\PromptPal\logs\mcp-yyyy-MM-dd.log` |

- 日志按天一个文件，文件名按写入时刻实时计算，跨午夜自动换新文件；目录创建失败降级 `%TEMP%\PromptPal\logs`。设置「关于」页展示完整路径。
- 关闭日志：exe 同目录放空文件 `nolog.txt`，或环境变量 `PROMPTPAL_LOG=0`（MCP 侧为 `PROMPTPAL_MCP_LOG=0`）。
- schema 由 `EnsureCreatedAsync` 生成（**不用** EF Migration）；实体 Category / Prompt / Tag / PromptTag（强类型连接表）。
- 播种只发生在**全新数据库文件**（建库前 `File.Exists` 判定）；已存在的库清空后也不会自动回填，避免覆盖用户意图。
- 备份换机：用设置页「导出 .db」（`VACUUM INTO` 一致性快照，运行中可导出）；手动复制必须先彻底退出 App **和 MCP 客户端**，且不能只覆盖 db 而留下旧的 `-wal/-shm`。详见 docs/使用说明.txt 第四部分。

## MCP sidecar（PromptPal.Mcp）

- net10.0 控制台，引用 Core；App 无需运行即可独立服务，但二者共享同一 db 文件（WAL 保证并发一致；sidecar 写入后 App 列表下次刷新可见）。
- **stdout 只允许 JSON-RPC 帧**：所有日志走 stderr（`LogToStandardErrorThreshold = Trace`，最低级别 Warning），SidecarLog 只写文件，绝不碰 stdout。
- 注册到 Trae：应用「设置 → MCP 接入 → 复制注册配置（mcp.json）」，粘贴到 MCP 面板（stdio）或存为项目 `.trae/mcp.json`，再 Reload Window。

| 工具 | 说明 |
|---|---|
| list_categories / list_tags | 列分类/标签及各自提示词数 |
| search_prompts | keyword（标题/正文）/category/tag/favorite/limit（1-100，默认 20），返摘要 |
| get_prompt | 按 Id 取完整正文与元数据 |
| create_prompt / update_prompt | title/content 必填校验；update 省略=不变、tags 空数组=清空、category="未分类"=移出 |
| copy_prompt | **返回正文供对话使用并 +1 计数/记最近使用时间；不碰系统剪贴板** |
| toggle_favorite / delete_prompt | 切收藏 / 按 Id 删除 |
| create_category / rename_category / move_category / delete_category | 分类增改排序删；move 用 `direction: "up"/"down"`；删除仅解绑提示词 |

业务校验异常统一转 `McpException`（客户端收到 isError 中文文本，含可用分类候选），不冒内部错误。

## 构建与发布（硬约束，照做）

### App 发布命令（已验证）

```powershell
dotnet publish src/PromptPal.App/PromptPal.App.csproj -c Release -r win-x64 `
  --self-contained true -p:WindowsAppSDKSelfContained=true -p:PublishSingleFile=false `
  -p:PublishReadyToRun=false -p:PublishTrimmed=false -p:CsWinRTAotOptimizerEnabled=false `
  -o publish/PromptPal-win-x64
```

csproj 必须保持 `PublishTrimmed=false`、`PublishReadyToRun=false`、`CsWinRTAotOptimizerEnabled=false`：WinUI3 + .NET 10 + WindowsAppSDK 2.4 下裁剪/AOT 会让启动引导代码与随包 WinRT.Runtime 不兼容，启动即 `TypeLoadException` 且被吞掉，表现为"进程在、无窗口"。

- 打包瘦身已固化为 csproj 的 `StripUnusedWinML` Target（默认开）：自动剔除 onnxruntime.dll / DirectML.dll / Microsoft.ML.OnnxRuntime.dll / Microsoft.Windows.AI.MachineLearning.dll（省约 39.6MB；`Microsoft.Windows.AI.MachineLearning.Projection.dll` 保留）。需完整输出排查加 `-p:StripWinML=false`。
- 发布验证不能只看进程存活：断言 `MainWindowHandle != 0` 且窗口标题为 PromptPal，或日志走到 `Activate called`。
- 体积：散装目录约 275MB / zip 约 112MB（运行时 + WinAppSDK 全内置）。framework-dependent 与 MSIX 两条瘦包路线评估过但**未做**（MSIX 需改自启方式、数据目录与依赖模型）。

### MCP sidecar 发布命令

```powershell
dotnet publish src/PromptPal.Mcp/PromptPal.Mcp.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish/PromptPal-win-x64/mcp
```

**`IncludeNativeLibrariesForSelfExtract=true` 不可省**，否则单文件包缺 e_sqlite3 原生库，启动即 DllNotFound。产物约 85.8MB 单文件，跨机免 .NET 环境。

### 打包 zip

`Compress-Archive -Path publish/PromptPal-win-x64 -DestinationPath publish/PromptPal-win-x64.zip -Force`（顶层保留同名文件夹；包根放「使用说明.txt」）。

## 已知坑（勿回退）

1. **SQLite 不能在库端按 DateTimeOffset 排序**：EF 编译期即抛 `NotSupportedException`（表现为"添加失败"但数据已入库）。所有按时间排序一律"取回后内存排序"，禁止在 IQueryable 上 `OrderBy(时间列)`。根因有固化测试 `Sqlite_DoesNotSupport_DateTimeOffset_OrderBy_RootCauseGuard`。
2. **单实例 Main**：必须用 csproj `<DefineConstants>DISABLE_XAML_GENERATED_MAIN</DefineConstants>` 关 XAML 生成 Main（`DisableXamlGeneratedMain` 属性在本工程无效）；自定义 Main 走 `AppInstance.FindOrRegisterForKey("PromptPal")` + 重复实例重定向激活。
3. **托盘 NOTIFYICONDATA**：x64 下 cbSize = **984**，带 `NIF_GUID` 固定 GUID 并 `NIM_SETVERSION(4)`；注册三级降级（GUID → 清幽灵重试 → 无 GUID 句柄模式），监听 `TaskbarCreated` 自愈。
4. **代码后置取不到 ThemeResource**：`Application.Current.Resources[key]` 只反映应用级（跟随系统）主题；系统深色 + App 强制浅色时必须经 `AppPrefs.ThemeBrush(key, ActualTheme)` 取色。ContentDialog 弹层同样要显式设 `RequestedTheme = 宿主.ActualTheme`（共 8 处）。
5. **工具窗不进任务栏**：只挂 owner（GWL_HWNDPARENT）+ 摘 WS_EX_APPWINDOW；**不要加 WS_EX_TOOLWINDOW**——那会得到 Win10 矮一截的非原生标题栏。
6. **WAL 换机静默丢数据**：目标机残留自己的 db-wal/-shm 时只覆盖 promptpal.db，旧 WAL 会重放回新库，且无文件锁报错。文档（使用说明第四部分）必须保留这条警告。
7. **导入 .db 备份曾长期失效**：不能用 `Data Source=` 截断连接串取路径（串带 `;Default Timeout=5`），必须按键值解析；备份用 `VACUUM INTO`（事务外），导入 ATTACH 必须在事务外、DELETE/INSERT 在事务内并同步 `sqlite_sequence`。
8. **WinUI 编译器限制**：不支持 `FlyoutBase.ContextFlyout` 附加属性（右键菜单用 `MenuFlyout.ShowAt`）；`DisplayArea.GetAll()` 在 WindowsAppSDK 2.3.6 投影中不存在。
9. **MCP 自测脚本竞态**：客户端启动后立刻发帧并半关闭 stdin 会丢响应；脚本需等 initialize 响应后再发（Trae 正式宿主无此问题）。

## 测试

`tests/PromptPal.Core.Tests`，xUnit + 内存 SQLite：

- `CategoryAndPromptServiceTests`（16）：分类增删改/重名/排序归一化/拖拽重排、标签复用与孤儿清理、输入校验、计数原子自增、DateTimeOffset 排序根因守卫、重置恢复 3 分类/9 提示词/8 标签/9 关联。
- `DbTransferServiceTests`（5）：导出快照自包含、导入整体替换保 Id 与自增序列、非库文件/缺表库拒绝且不留备份、覆盖导出。

## 更新日志

- **2026-09-16（当前）**
  - 内置示例重构为 系统/编程/文档 3 分类、9 条示例提示词（文案取自 docs/提示词实例文档.txt）、8 个标签；仅全新库播种，重置事务化重放；21/21 测试同步。
  - 日志全部迁移到 `%LOCALAPPDATA%\PromptPal\logs\`，App/MCP 各按天一个文件；桌面不再生成日志。
  - 评估过"同一 exe 带参数走 CLI、无参数走 MCP"的混合路线（实测冷启动 ~1.2s、热调用 ~14ms，MCP 在工具发现/Schema/结构化错误上更优），结论**保持纯 stdio MCP**；曾短暂试验的 CliRunner/PromptPalActions 已彻底移除，Debug/Release/发布包三处产物经重编与 stdio 冒烟（13 工具）确认一致。
  - 全量重编对齐 Debug/Release（0 警告 0 错误），App 散装产物 bin↔publish 哈希一致，MCP 单文件重发（85,768,174B），zip 重建（112.2MB/539 文件），使用说明与 README 同步。
- **2026-09-15 v1.1.0**：MCP sidecar（13 工具）与设置页 MCP 接入；.db 文件级导入导出（替换旧 JSON 方案）；分类拖拽卡片排序（`ReorderAsync`，MCP move_category 并存）；单实例保护、DbContext 页面 scope、取消令牌下传；系统原生体验 10 项（托盘 GUID 身份 984 字节、窗口/设置窗位置记忆、字号实时、浅色主题全链路审计、自制应用图标、状态栏反馈、工具窗 owner 化、关机放行）；第二批 UI（置顶按钮字形、原生标题栏、浅色对比）；左栏 5 区右键菜单；打包瘦身 Target 固化；多批稳定性修复（重置不播种 P0、WndProc 误 FreeHGlobal、UI 线程兜底、剪贴板占用重试、ContentDialog 防重入、自启对账、ACL 加 SYSTEM）。
- **更早**：SQLite DateTimeOffset 内存排序修复；GC 调优（ServerGC=false/ConcurrentGC=true）；编译告警清零。

## 待办（按优先级）

1. Fluent 观感增强（Mica/Acrylic、自定义 TitleBar、动效）。
2. 瘦包路线：framework-dependent 或 MSIX（需配套迁移自启/数据目录）。
3. 热键键位自定义（当前只有开关，Ctrl+Shift+P 硬编码）。
4. App 列表对 sidecar 写入的实时刷新（二期可走单实例通道命名管道通知）。
5. 数据量增大后的关键字搜索/分页 UI（MCP 侧 search_prompts 已先行）。
