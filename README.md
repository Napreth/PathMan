# PathMan

面向 Windows 的环境变量与程序别名管理器，使用 C# 构建核心、桌面界面、CLI 和后台程序，使用 C++ 实现独立运行的垫片。

> **开发阶段：pre-alpha。** 已提供环境变量的基本编辑、分组、变更监控与程序别名管理，高级自动整理功能仍在开发中。功能、命令、配置格式与界面仍可能调整。

## 当前进度

已实现：

- CLI 程序别名管理：创建、列出、删除和刷新硬链接垫片，透传参数、标准输入输出和退出码。
- 环境变量管理：左侧分别展示用户变量和系统变量，分组可折叠；右侧为等宽的文本框或列表，操作按钮位于内容框外。
- 分号列表：PATH、PATHEXT 以及其他含分号的变量按条目显示，保留顺序、重复项、空项和未展开的 `%变量名%` 引用。
- Win11 Fluent 桌面界面：变量管理、程序别名、变量监控、关于四项导航，跟随系统浅色／深色主题。
- 变量及分组的新建、重命名和删除；列表条目的新增、编辑、目录浏览、排序及文本编辑。值的编辑先暂存，保存时写入注册表；检测到外部修改时拒绝覆盖，修改系统变量时请求管理员权限。
- 可配置分组：默认路径变量、系统变量、其他变量，启用监控后显示外部变量；支持增加任意数量的自定义分组。
- 行内编辑、置顶与拖拽：变量名与分组名对齐，行尾提供置顶、编辑和删除；双击或 F2 原地修改名称，列表条目也可原地编辑。选中分组时右侧显示组内变量及其值。
- 外部变更监控：后台记录用户与系统环境变量的新增、修改和删除，将外部变更的变量归入外部分类，区分 PathMan 自身保存。
- 普通后台进程：单实例、启动、停止和状态查询，默认不自启。
- NSIS 安装与卸载：检测运行中的桌面、后台、CLI 与硬链接垫片，提示保存并正常关闭；支持同版本覆盖、版本升级及包类型／架构切换，阻止降级。

后续规划：PATH 引用的自动组织、多版本切换、外部 PATH 条目的自动迁移与去重。当前监控记录变动，自动整理尚未接入。

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

硬链接只连接文件且不能跨卷。安装向导会检查安装目录与当前用户 LocalAppData 是否同卷。
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

产物：

- `artifacts\setup\PathMan-0.1.0-<arch>.exe`：普通包。
- `artifacts\setup\PathMan-0.1.0-<arch>-SelfContained.exe`：自包含包。
- 同名 `.exe.json`：架构、类型、编译器版本及安装包和载荷 SHA-256。
- `artifacts\publish\win-<arch>\<PackageType>\`：四个应用入口及 LICENSE。

## 使用

安装后重新打开终端。以下别名示例使用默认安装路径下的 PowerShell 7，可按实际情况替换目标程序路径：

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

GUI 使用 Win11 Fluent 控件与 DWM Mica 云母材质，内容延伸到标题栏，保留系统原生窗口按钮。侧边栏采用设置页的宽导航、彩色图标与选中样式；西文使用 Segoe UI Variable，中文使用微软雅黑 UI，标题采用常规字重。旧版 Windows 使用主题背景作为回退。变量管理的两栏内容等宽，左右列表均为 24 逻辑像素的紧凑行，选中时显示矩形蓝底白字。

分类规则：

- 路径变量：名称以 `PATH` 结尾（不区分大小写，且不要求下划线），包含多个有效路径；或只包含一个路径，但在相应作用域 PATH 中以完整 `%变量名%` 条目被裸引用。`%变量名%\bin`、引号包裹的引用不属于裸引用。空条目不计为路径数量，分类检查不会改写原值。
- 系统变量：Windows 内置且通常无需手动修改的变量。名单位于 `src/Pathman.Core/EnvironmentVariables/Defaults/windows-variables.json`，以微软文档为来源；不把 USMT 专用 CSIDL 宏、CMD 动态伪变量或经常修改的 PATH、PATHEXT、TEMP、TMP 当作此类。
- 其他变量：未匹配以上规则的变量。PATHEXT 是分号列表，但不属于路径变量。
- 外部变量：启用监控后显示，包含监测到被外部程序新增或修改的变量。

分组定义由默认 JSON 和 `%LocalAppData%\PathMan\environment.json` 中的 `groups` 列表驱动，界面按配置生成，分类种数没有写死在代码中。新建分组只影响所在作用域；删除分组会保留变量并移入默认分组。默认接收未分类变量的分组需保留。

“用户变量”和“系统变量”标题右侧保留新建图标，支持悬停菜单并使用对应标题的作用域。每行最右侧提供置顶、编辑和删除；双击名称或按 F2 开始行内编辑，Enter／失去焦点提交，Esc 取消，名称无效时留在输入框内提示。列表条目使用相同的原地编辑方式。右栏标题是所选变量或分组的名称；选择分组时显示组内变量及其值，选择变量时单值显示为文本，PATH、PATHEXT 和含 `;` 的变量显示为列表。列表操作和文本编辑先暂存，点击保存才会写入；切换或关闭时会提示未保存的更改。复制已选条目取得单项，没有选中条目时复制完整值，空项、重复项、空白和顺序会保留。读取不展开 `%变量名%`。

置顶变量排在所属组前面，置顶分组排在所属作用域的分组前面；置顶状态和顺序保存到配置。变量可拖入同一作用域的其他分组，手动归属优先于所有自动分类规则。普通变量在组内按名称排列，置顶变量可以相互拖动排序；分组可在各自的置顶／非置顶区域内拖动排序。拖拽时原位置保留空位，浮层跟随鼠标，目标组高亮或显示黑色插入线；放到列表外取消，靠近列表上下边缘时自动滚动，离边缘越远滚动越快。

安装器沿用已有安装目录及安装范围，升级／覆盖保留用户分组、监控、自启、别名和 PATH 归属记录，并刷新硬链接；原先正在运行的后台程序在完成或取消维护后恢复。更换目录或安装范围须先卸载；卸载保留 `%LocalAppData%\PathMan` 用户数据。关闭运行中的 GUI 使用正常关闭请求，未保存的编辑仍可取消关闭，安装器可重试或取消；不会强制终止 GUI 或正在执行的别名命令。静默安装遇到运行中的程序会退出，避免弹出保存对话框。普通包仍会先检查对应架构的运行时。

在“变量监控”页启用监控会启动普通后台进程，通过注册表变化通知跟踪两种作用域。关闭 GUI 后仍可记录，关闭监控会隐藏外部分类。关于页的“登录时启动后台”使用当前用户 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`；切换自启不会立即启动或停止后台程序。
带单引号的别名在 PowerShell 中要使用调用运算符和双引号，例如 `& "p'w'sh"`。

可用 `pathman --data-root <目录> ...` 隔离别名数据。测试在临时目录执行，不修改真实 PATH 或自启设置。

## 许可证

本项目采用 [MIT 许可证](LICENSE)。
