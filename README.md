# PathMan

面向 Windows 的环境变量与程序别名管理器，使用 C# 构建核心、桌面界面、CLI 和后台程序，使用 C++ 实现独立运行的垫片。

> **开发阶段：pre-alpha。** 已提供环境变量编辑、分组、Path 清理预览、变更监控与基础程序别名管理。功能、命令、配置格式与界面仍可能调整。

## 当前进度

已实现：

- CLI 程序别名管理：创建、列出、删除和刷新垫片，同卷直接硬链接，跨卷复制一份本地模板后复用硬链接；透传参数、标准输入输出和退出码。
- 环境变量管理：左侧分别展示用户变量和系统变量，分组可折叠；右侧为等宽的文本框或列表，操作按钮位于内容框外。
- 分号列表：PATH、PATHEXT 以及其他含分号的变量按条目显示，保留顺序、重复项、空项和未展开的 `%变量名%` 引用。
- Win11 Fluent 桌面界面：变量管理、健康检测、程序别名、版本管理、变量监控、关于六项导航，跟随系统浅色／深色主题；版本管理目前仅提供占位页。
- 变量及分组的新建、重命名和删除；列表条目的新增、编辑、目录浏览、排序及文本编辑。值的编辑先暂存，保存时写入注册表；检测到外部修改时拒绝覆盖，修改系统变量时请求管理员权限。
- 可配置分组：默认路径变量、系统变量、其他变量，启用监控后显示外部变量；支持增加任意数量的自定义分组。
- 行内编辑、置顶与拖拽：变量名在分组标题右侧缩进，行尾提供置顶、编辑和删除；双击或 F2 原地修改名称，列表条目也可原地编辑。选中分组时右侧显示组内变量及其值。
- 递归路径编辑：纯引用可逐层展开，路径条目和引用组可混合排序、移入其他引用组；修改保留原始引用，保存实际所属变量。
- 健康检测与通知：启动和刷新后检查引用回环、未定义变量、展开后的重复项与空路径条目；侧栏通知和健康页链接可直接定位变量。
- 外部变更监控：后台记录用户与系统环境变量的新增、修改和删除，将外部变更的变量归入外部分类，区分 PathMan 自身保存。
- 普通后台进程：单实例、启动、停止和状态查询，默认不自启。
- Path 清理：展开引用后去重，将系统 Path 中的软件条目迁移到独立变量；应用前以统一 diff 预览，保存原始值备份。健康问题集中在健康检测页展示。
- NSIS 安装与卸载：从启动起全局互斥，检测运行中的桌面、后台、CLI 与垫片，提示保存并正常关闭；支持同版本覆盖、版本升级及包类型／架构切换，阻止降级。

后续规划：完整的程序别名管理界面、变量监控工作流、多版本切换。当前 Path 清理由用户手动预览、应用，不随监控自动执行。

## 模块与入口

| 模块 | 语言 / 技术 | 职责 / 入口 |
| --- | --- | --- |
| Pathman.Core | C# / .NET 10 | 环境变量读写、分类与分组配置、变更监控、目录布局、别名管理、后台控制、自启设置，共享类库 |
| Pathman.Gui | C# / WPF | 桌面启动器 `PathMan.exe` |
| Pathman.Cli | C# | 命令行入口 `bin\pathman.exe` |
| Pathman.Service | C# | 用户会话后台程序 `internal\PathMan.Service.exe`，默认不自启 |
| Pathman.Shim | C++20 / Win32 | 通用垫片 `internal\PathMan.Shim.exe` |

`Pathman.Service` 是普通后台 EXE，不注册 Windows 系统服务（因为会话问题）。

## 安装布局

```text
%LocalAppData%\Programs\PathMan\   # 仅为自己安装；所有用户安装默认 %ProgramFiles%\PathMan
├── PathMan.exe                    # 桌面入口
├── LICENSE
├── bin\
│   └── pathman.exe                # bin 只包含 CLI
└── internal\
    ├── PathMan.Service.exe        # 后台程序，无控制台窗口
    ├── PathMan.Shim.exe           # 垫片本体
    ├── PathMan.SetupActions.dll   # 原生安装维护辅助库
    ├── install.ini                # 安装范围
    └── Uninstall.exe              # NSIS 生成的卸载器

%LocalAppData%\PathMan\
├── environment.json               # 分组、分配规则、监控设置及最近的变更记录
└── shims\
    ├── pm-pwsh.exe                # 硬链接 → 程序目录\internal\PathMan.Shim.exe
    └── pm-pwsh.shim               # 这个别名的配置
```

安装目录可以与 LocalAppData 位于不同卷。同卷时别名直接硬链接到安装目录中的垫片；跨卷时在 `%LocalAppData%\PathMan\internal\PathMan.Shim.exe` 保留一份副本，所有别名硬链接到该副本。刷新时校验模板内容、原子替换变化的副本并重新链接别名；已有别名配置保持不变。LocalAppData 所在卷需支持硬链接。
启动位置不能确定安装目录时，读取 `HKCU`／`HKLM` 的 `Software\Microsoft\Windows\CurrentVersion\Uninstall\PathMan\InstallLocation`，兼容 32／64 位注册表视图。安装器也使用卸载登记识别正在运行的复制垫片。
垫片通过自身启动路径定位同名 `.shim`，继承调用方的工作目录、环境、标准输入输出，透传参数和目标退出码。

## 构建与打包

构建环境：Windows、PowerShell 7、.NET 10 SDK、CMake 3.24+、Visual Studio C++ 桌面工具集和 Windows SDK，以及 [NSIS 3.12+](https://nsis.sourceforge.io/Download)。ARM64 需要相应的 C++ 工具集。
安装 NSIS 后可以直接运行；便携编译器可设置环境变量 `NSIS_COMPILER` 指向 `makensis.exe`。脚本也会查找 `artifacts/tools/nsis-3.12/makensis.exe`。

```powershell
.\build.ps1                                       # 默认三种架构 × 两种类型，共六份 EXE
.\build.ps1 -Arch x64,arm64                       # 选择架构，默认 x86,x64,arm64
.\build.ps1 -PackageType FrameworkDependent       # 依赖系统运行时，文件名无类型后缀
.\build.ps1 -Arch x86 -PackageType SelfContained  # 自带运行时，后缀 -SelfContained
.\build.ps1 -Test                                 # 运行核心测试和本机匹配架构的应用测试
.\build.ps1 -Plan                                 # 只显示构建矩阵
.\build.ps1 -Version 0.2.0
.\build.ps1 -SkipInstaller                        # 仅生成发布目录
.\scripts\package.ps1                             # 使用已有发布目录重新打包六份 EXE
dotnet run --project tests/Pathman.Core.Tests -c Release  # 单独验证分号拆分与原始值读取
```

`-Arch` 和 `-PackageType` 都接受数组，省略时构建全部组合。普通包需要对应架构的 .NET 10 Desktop Runtime，自包含包不需要预装运行时。应用采用单文件发布，`bin` 只包含 `pathman.exe`。

`tests/gui-interactions.ps1` 和 `tests/installer-maintenance.ps1` 是隔离 Windows 虚拟机的回归脚本，需交互会话及测试安装目录 `C:\PathManTestApp`；前者的 `-PrepareFixture` 会重建测试分组和 `PATHMAN_*` 测试变量。请在可恢复检查点的虚拟机中运行，结束后恢复检查点。它们覆盖行内编辑、置顶／拖拽、自动滚动、运行检测、版本升级／覆盖、降级保护及配置保留。

`tests/gui-cleanup.ps1` 同样只允许在隔离虚拟机执行；先以管理员权限运行 `-PrepareFixture` 创建可恢复的测试 Path 和引用，再在普通用户交互会话运行脚本。它覆盖空草稿与空行删除、模式切换、引用跳转、清理预览／UAC 保存、健康页跳转及两个开关，运行时需确认测试虚拟机的 UAC。

`tests/gui-recursive-paths.ps1` 使用相同的隔离测试方式，覆盖递归展开、跨层移动、引用组嵌套、回环阻止、继承的系统变量提权保存、拖动自动滚动、引用着色、通知导航和 diff 滚轮。核心测试另外覆盖 OSC8 解析、内部链接校验、展开限制和 diff 重建。

产物：

- `artifacts\setup\PathMan-0.1.0-<arch>.exe`：普通包。
- `artifacts\setup\PathMan-0.1.0-<arch>-SelfContained.exe`：自包含包。
- 同名 `.exe.json`：架构、类型、编译器版本及安装包和载荷 SHA-256。
- `artifacts\publish\win-<arch>\<PackageType>\`：四个应用入口及 LICENSE。

## 使用

安装后先在“程序别名”页启用程序别名，再重新打开终端。以下别名示例使用默认安装路径下的 PowerShell 7，可按实际情况替换目标程序路径：

```powershell
pathman layout
pathman shim add pm-pwsh "C:\Program Files\PowerShell\7\pwsh.exe"
pathman shim list
pm-pwsh --version
pathman shim refresh
pathman shim remove pm-pwsh

pathman background start
pathman background status
pathman background stop
pathman startup enable
pathman startup status
pathman startup disable
```

GUI 使用 Win11 Fluent 控件与 DWM Mica 云母材质，内容延伸到标题栏，保留系统原生窗口按钮。侧边栏采用设置页的宽导航、彩色图标与选中样式；西文使用 Segoe UI Variable，中文使用微软雅黑 UI，标题采用常规字重。旧版 Windows 使用主题背景作为回退。变量管理的两栏内容等宽，左右列表均为 24 逻辑像素的紧凑行，文字垂直居中，选中时显示矩形蓝底白字。分类行没有常驻背景色，变量名略向右缩进。用户变量与系统变量之间的分界线可以上下拖动，调整两块列表的高度，右侧编辑区继续覆盖两块列表的整体高度。

分类规则：

- 路径变量：名称以 `PATH` 结尾（不区分大小写，且不要求下划线），包含多个有效路径；或只包含一个路径，但在相应作用域 PATH 中被纯引用。`%变量名%` 和 `%变量名%\` 都属于纯引用；`%变量名%\bin`、引号包裹的引用不属于纯引用。空条目不计为路径数量，分类检查不会改写原值。
- 系统变量：仅在系统作用域显示，包含 Windows 内置且通常无需手动修改的变量。名单位于 `src/Pathman.Core/EnvironmentVariables/Defaults/windows-variables.json`，以微软文档为来源；不把 USMT 专用 CSIDL 宏、CMD 动态伪变量或经常修改的 PATH、PATHEXT、TEMP、TMP 当作此类。用户作用域不显示此默认分类，自定义分组不受影响。
- 其他变量：未匹配以上规则的变量。PATHEXT 是分号列表，但不属于路径变量。
- 外部变量：启用监控后显示，包含监测到被外部程序新增或修改的变量。

分组定义由默认 JSON 和 `%LocalAppData%\PathMan\environment.json` 中的 `groups` 列表驱动，界面按配置生成，分类种数没有写死在代码中。新建分组只影响所在作用域；删除分组会保留变量并移入默认分组。默认接收未分类变量的分组需保留。

“用户变量”和“系统变量”标题右侧保留新建图标，点击展开标准菜单，使用对应标题的作用域。变量／分组行末提供置顶、编辑和删除；左侧双击名称或按 F2 开始行内编辑，Enter／失去焦点提交，Esc 取消，名称无效时留在输入框内提示。右栏标题是所选变量或分组的名称；选择分组时显示组内变量及其值，双击右侧变量进入该变量的编辑区，行尾的编辑按钮仍可改名。

选择变量时普通单值显示为文本，路径类变量、PATHEXT 和含 `;` 的变量显示为列表，单条路径也可使用引用跳转，两种编辑区的首字符位置一致。列表行不再放编辑和删除图标，使用框外按钮或双击编辑；“新建”在列表末尾生成空行并立即输入，填入内容后 Enter／失焦保留，空草稿失焦或 Esc 则移除；将已有条目编辑为空也会删除该行。“浏览…”单独选择文件夹。“复制”下面的“删除”移除选中条目，右下角“文本模式”／“多行模式”直接切换原始分号文本与列表，普通单值也可切换，切换本身不会改动值。删除最后一行后保存为空值，不会重新生成空白行。列表操作和文本编辑先暂存，点击保存才会写入；切换变量或关闭时会提示未保存的更改。复制已选条目取得单项，没有选中条目时复制完整值，未编辑的空项、重复项、空白和顺序均保留。路径变量行内有 `%变量名%` 引用时，行尾显示跳转按钮；多个引用通过菜单选择目标，按当前作用域解析用户覆盖。

路径列表中的纯引用（例如 `%DEVELOP_PATH%` 或 `%DEVELOP_PATH%\`）可像分组一样递归展开；仅展开名称以 `PATH` 结尾的已保存变量，回环和过深的引用停止展开。路径条目与引用组可以互相排序，也可以拖入其他引用组，原位置保留空位，浮层跟随鼠标，目标显示高亮或细插入线，边缘支持按距离加速滚动。拖动不跨越用户／系统作用域，产生回环的位置不能放置。编辑从用户 Path 展开的系统变量仍会在保存时请求管理员权限；保存会验证并写入各条目的实际所属变量，展开操作本身不展开写回原始值。所有值中的 `%变量名%` token 使用圆角淡黄色背景（由 `#DDF4FF` 只改色相得到），原生文本编辑仍保留光标、输入法和撤销。

“健康检测”参考 [Nextcloud 管理概览的检查汇总](https://docs.nextcloud.com/server/latest/admin_manual/configuration_server/security_setup_warnings.html)，展示引用回环、未定义变量、展开后的重复项、空路径条目与展开限制。应用打开及变量快照更新时自动检测，结果通过导航下方的通知提示；当前页面只展示结果和定位链接，尚无一键修复。关闭通知后，相同结果不会反复提醒，问题变化时会更新通知。通知支持红、黄、蓝、绿四种同饱和度／亮度的淡色，正文使用 OSC8 链接；`apppage:health` 和 `apppage:variables?scope=User&name=Path` 等内部链接在当前窗口导航。

右上角“清理”在页面内展示两种作用域的 Path 修改预览，点击“应用清理”才写入。预览使用类似 Git 的统一 diff：红色删除行、绿色新增行、上下文、行号和 `@@` 区段标题；鼠标位于 diff 行上也能滚动整个预览。清理页面不另行运行健康检查，健康问题在健康检测页和侧栏通知中展示。系统 Path 保留已有的 Windows、System32、Wbem、WindowsPowerShell\v1.0、OpenSSH 目录及纯引用，将其他条目保留原文移入 `SOFTWARE_PATH`，名称已占用时依次使用 `SOFTWARE1_PATH`、`SOFTWARE2_PATH`，然后追加纯引用。不会重建缺失的系统目录或将任意 Windows 子目录都当作系统项；目录清单位于 `Defaults/windows-paths.json`，附有微软组件文档来源。用户 Path 只去重，不迁移软件路径。

清理按保存的作用域递归展开引用，以大小写无关的规范路径比较；直接条目与被引用路径重复时优先保留引用。被引用变量的内容保持原样，部分重叠但仍有独有路径的条目不会整项删除。应用前在 `%LocalAppData%\PathMan\backups` 保存含原始值和注册表类型的 JSON；同一作用域的迁移与 Path 更新一起验证、保存，源值或引用值变化后拒绝旧预览，写入失败回滚本批次。系统保存需要 UAC；两个作用域分别保存，部分完成时会在页面说明并重新生成预览。

置顶变量排在所属组前面，置顶分组排在所属作用域的分组前面；置顶状态和顺序保存到配置。变量可拖入同一作用域的其他分组，手动归属优先于所有自动分类规则。普通变量在组内按名称排列，置顶变量可以相互拖动排序；分组可在各自的置顶／非置顶区域内拖动排序。拖拽时原位置保留空位，浮层跟随鼠标，目标组高亮或显示黑色插入线；放到列表外取消，靠近列表上下边缘时自动滚动，离边缘越远滚动越快。

安装器自动使用系统支持的界面语言，不支持时使用英文，也支持 `/LANG=1033`（英文）或 `/LANG=2052`（简体中文）覆盖。安装与卸载从启动起共享全局互斥，覆盖所有架构、包类型和用户会话。向导始终使用一个窗口，选择所有用户安装后，仅在写入时请求 UAC 并使用隐藏的提权进程执行，确认提示使用“确定／取消”。

安装器沿用已有安装目录及安装范围，升级／覆盖保留用户分组、监控、自启、别名和 PATH 归属记录，并刷新垫片及跨卷副本；原先正在运行的后台程序在完成或取消维护后恢复。更换目录或安装范围须先卸载；卸载保留 `%LocalAppData%\PathMan` 用户数据。关闭运行中的 GUI 使用正常关闭请求，未保存的编辑仍可取消关闭，安装器可重试或取消；不会强制终止 GUI 或正在执行的别名命令。静默安装遇到运行中的程序会退出，避免弹出保存对话框。普通包仍会先检查对应架构的运行时。

在“变量监控”页启用监控会启动普通后台进程，通过注册表变化通知跟踪两种作用域。关闭 GUI 后仍可记录，关闭监控会隐藏外部分类。同页的“登录时自动运行服务”使用当前用户 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`；这里的“服务”仍是用户会话中的普通后台程序，切换自启不会立即启动或停止它。

“程序别名”页的“启用程序别名”将垫片目录添加到用户 Path 的最后一个条目，移除同目录的旧条目，系统 Path 保持不变。Windows 将用户 Path 接在系统 Path 之后，因此别名排在其他命令之后。关闭开关仅移除垫片目录，不删除别名文件；新终端使用更新的 Path。新安装默认不再自动加入垫片目录，升级保留现有开关状态。
带单引号的别名在 PowerShell 中要使用调用运算符和双引号，例如 `& "p'w'sh"`。

可用 `pathman --data-root <目录> ...` 隔离别名数据。测试在临时目录执行，不修改真实 PATH 或自启设置。

## 许可证

本项目采用 [MIT 许可证](LICENSE)。
