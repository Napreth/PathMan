# PathMan

PathMan 是 Windows 环境变量与程序别名管理器，也可为同一个环境变量保存多个版本，通过桌面界面或命令行切换。

目前处于 Beta 测试阶段。安装包见 [Releases](https://github.com/Napreth/PathMan/releases)。

- 分组管理用户和系统变量，支持置顶、拖拽和行内编辑。
- 逐行编辑 PATH、PATHEXT 等分号列表，展开引用并定位被引用变量。
- 检测引用错误、重复项和空条目，预览并清理 PATH。
- 为程序或文件创建命令别名，管理 Java、Python 等工具的环境变量版本。
- 在后台记录外部修改，按用户或系统范围配置自动整理。

界面采用 Win11 Fluent 风格和 Mica 云母材质，提供简体中文、English、浅色与深色主题。

## 安装

选择对应系统架构的安装包：`x86`、`x64` 或 `arm64`。

| 安装包 | 运行时 |
| --- | --- |
| `PathMan-<版本名>-<架构>.exe` | 需要对应架构的 .NET 10 Desktop Runtime |
| `PathMan-<版本名>-<架构>-SelfContained.exe` | 自带运行时 |

向导支持仅当前用户或所有用户安装，并可设置安装目录、开始菜单与桌面快捷方式。默认目录分别为 `%LocalAppData%\Programs\PathMan` 和 `%ProgramFiles%\PathMan`；所有用户安装需要管理员权限。

安装器默认使用系统语言，不支持时使用英文。可用 `/LANG=1033` 指定英文，`/LANG=2052` 指定简体中文。

升级和覆盖安装会沿用已登记的目录与安装范围，保留用户配置。检测到主窗口或后台程序正在运行时，向导会列出进程；保存工作并点击“继续”后，安装器终止这些进程，等待 CLI 任务完成。安装结束后恢复此前运行的后台程序，通过别名启动的目标程序可继续运行。

更换安装目录或范围前需先卸载。卸载保留 `%LocalAppData%\PathMan` 中的用户数据。

安装器按 `X.Y.Z` 数字版本号拦截降级，自动更新也执行相同检查；同版本允许覆盖安装。

## 桌面界面

### 变量管理

左栏按用户和系统两个作用范围显示环境变量，可拖动分界线调整高度。选中变量后在右栏编辑；选中分组时，右栏列出组内变量，双击进入编辑。

默认分类如下。分类只影响 PathMan 内的显示，自定义分组和手动归属优先于自动分类。

| 分类 | 内容 |
| --- | --- |
| 路径变量 | 名称以 `PATH` 结尾，内容为多个路径；或为单个路径，且被 PATH 作为完整条目引用 |
| 系统变量 | Windows 内置名称，如 TEMP、TMP、PATHEXT，在用户和系统作用范围中都可能出现 |
| 其他变量 | 未匹配其他分类的变量；PSModulePath 默认归入此类 |
| 外部变量 | 启用对应监控选项后接收外部新增的变量 |

标题旁的加号可新建分组或变量。双击名称或按 F2 重命名，Enter 或失焦提交，Esc 取消。变量可以拖入其他分组，分组及置顶项目可以拖动排序。

PATH、PATHEXT 等分号列表支持多行与文本两种编辑模式。多行模式会保留空条目；新建行后直接输入，编辑已有行并清空内容会删除该行，未修改的空行仍保留。变量值编辑后点击“保存”写入注册表，“撤销”恢复草稿。

`%变量名%` 引用会高亮显示，可通过行末按钮跳转。纯引用（如 `%TOOLS_PATH%` 或 `%TOOLS_PATH%\`）指向路径变量时，可递归展开并拖动其中的条目。

主窗口实时监听环境变量。未编辑时直接更新显示；存在未保存编辑时，在右栏显示差异：红色为外部删除，绿色为外部新增，蓝色为本地修改。可撤回或清理外部更改，也可在本次编辑期间开启临时自动清理。

### PATH 清理

变量管理页右上角的“清理”会打开双栏差异预览，点击“应用清理”后写入。

- 用户 PATH：展开引用后去重。
- 系统 PATH：去重后保留 Windows 系统目录与纯引用，将其余软件路径移入 `SOFTWARE_PATH` 并添加引用。名称已占用时使用 `SOFTWARE1_PATH`、`SOFTWARE2_PATH` 等。
- 直接条目与被引用变量中的路径重复时，优先保留引用，被引用变量的内容不变。

预览会提示搜索顺序是否改变，因为 PATH 顺序可能影响实际运行的命令。写入前备份原值和注册表类型；源值或引用发生变化后需重新预览。

### 健康检测

应用启动和环境变量变化时自动检测，发现问题后在侧栏通知。检测包括引用回环、未定义变量、空条目、展开后的重复项、作用范围错误、注册表类型，以及命令行和垫片目录的 PATH 配置。

“查看详情”显示相关变量、引用链与问题行，并提供适用的修复操作。系统变量引用仅存在于用户范围的变量属于错误；含引用的值应使用 `REG_EXPAND_SZ`。

“忽略”可隐藏相同内容的问题，通过“显示已忽略”恢复。来源内容或类型变化后会重新报告。

### 程序别名

开启“启用程序别名”后，PathMan 将垫片目录加入用户 PATH 末尾。关闭开关会移除该目录并保留别名配置。PATH 更改在新终端中生效。

点击别名名称或目标路径可分别编辑，Enter、失焦或切换页面时保存，Esc 取消。页面提供新建、重命名、编辑、浏览、复制和删除操作；功能关闭时可以选择条目，编辑操作禁用。

别名名称须符合 Windows 文件名规则。目标可以是完整路径，也可以是 PATH 中的文件名，例如 `powershell.exe` 或 `npx.cmd`。允许登记尚不存在的目标，实际调用时报告错误；浏览可选择任意文件类型。

原生可执行程序继承调用方的工作目录、环境和标准输入输出，并透传参数与退出码。其他文件由 Windows 默认关联程序打开。

### 版本管理

每个项目绑定一个环境变量及其作用范围，可以保存多个命名版本，多个版本也可以使用相同的值。例如将 `java` 绑定到系统变量 `JAVA_HOME`，登记 `8`、`25`、`lts` 三个版本，再切换当前值。

左栏显示项目和目标变量，右栏显示版本和值。双击名称或值编辑，双击目标变量打开选择器。新建后直接在空白行中输入，Enter 或失焦保存配置；点击“切换”才修改实际环境变量。删除项目或版本配置会保留环境变量当前值。

PATH 不可作为版本目标。内置系统变量默认不可选，可在“设置 → 高级设置”中放开，启用前会显示风险警告。桌面界面与 CLI 共用版本配置和目标限制。

### 变量监控

后台监控需要同时开启“在后台运行监控程序”和“登录时自动运行后台程序”。关闭后台运行会关闭登录自启，其余选项保留并置灰。主窗口的实时监听独立运行，不执行后台自动整理。

用户和系统范围分别提供外部变量归类、PATH 去重与迁移选项，也可排除系统环境编辑器或 PowerToys 的修改。用户外部路径迁入 `EXTERNAL_USER_PATH`，系统外部路径迁入 `EXTERNAL_PATH`。

“精确识别修改来源”首次启用时需要管理员授权，之后由计划任务启动只读的来源采集程序，登录时不会重复弹出 UAC。监控主进程仍以当前用户权限运行。来源无法确认时保留原值，系统 PATH 自动整理仍需单独授权。通过“外部更改记录”查看更改前后内容、时间、作用范围和写入进程。

### 设置与关于

设置页提供语言、主题、启动时检测更新、接收测试版更新和高级设置。语言与主题支持跟随系统，切换后立即生效；未支持的系统语言使用英文。

默认只接收正式版更新，开启“接收测试版更新”后也接收 Beta 和公开测试版。更新检测从有效的 `release.json` 中选择最大的 `X.Y.Z` 数字版本号，仅在高于当前版本时提供更新，版本名和 tag 不参与比较。检测结果会保留；自动检测发现新版时发送系统通知，每个版本只提醒一次。点击通知可查看发行说明。

关于页提供源代码、手动检查更新和许可证入口。“安装更新”会下载匹配当前架构与运行时类型的安装包，验证后运行；没有匹配安装包时打开 GitHub 发布页。

### 键盘与选择

列表支持 Ctrl+点击多选、Shift+点击范围选择，以及 Ctrl+A 全选。复制和删除作用于选中项目，需要单个目标的操作在多选时禁用。

| 快捷键 | 操作 |
| --- | --- |
| Ctrl+Z | 撤销 |
| Ctrl+Y / Ctrl+Shift+Z | 重做 |
| Delete | 删除选中项 |
| Ctrl+C | 复制 |
| F2 | 行内重命名或编辑 |
| Insert / Ctrl+N | 新建列表项 |
| Ctrl+Shift+N | 在变量树中新建分组 |
| Ctrl+S | 保存变量值或提交当前配置编辑 |
| Enter / Esc | 提交或取消行内编辑 |

输入框内使用原生文字编辑快捷键。

## 版本管理 CLI

在版本管理页开启“启用命令行版本切换”后，打开新终端使用 `pathman`。该功能默认关闭，主窗口按安装范围将 `bin` 加入用户或系统 PATH；相应范围启用自动整理时，写入 `SOFTWARE_PATH` 或已有的 `SOFTWARE数字_PATH`。PATH 缺少该变量的纯引用时，健康检测会报告错误。

关闭开关会移除命令行目录并保留版本配置。未启用时直接运行版本命令会报错退出，`--help` 始终可用。

| 命令 | 用途 |
| --- | --- |
| `pathman list [项目]` | 查看项目、目标变量、PATH 引用和当前值 |
| `pathman add <项目> <版本> <值> [--variable <变量名>] [--type User\|Machine] [--replace]` | 登记或更新版本 |
| `pathman switch <项目> <版本>` | 将版本值写入目标环境变量 |
| `pathman delete <项目> [版本]` | 删除项目或指定版本的配置 |

```powershell
pathman add java 8 "D:/Java/jdk-8/" --variable JAVA_HOME --type Machine
pathman add java 25 "D:/Java/jdk-25/"
pathman add java lts "D:/Java/jdk-25/"
pathman list java
sudo.exe pathman switch java 25
pathman delete java 8
```

首次登记项目需指定 `--variable`，`--type` 默认为 `User`，后续版本沿用项目绑定。已有版本可用 `--replace` 更新。版本值可以是路径或普通文本，允许登记尚不存在的目录。

`add` 只登记配置，`switch` 修改环境变量，`delete` 保留环境变量及其当前值。`list` 从注册表读取当前值，`RefsByPATH` 表示 PATH 是否直接或间接引用目标变量。

CLI 使用调用者权限。修改系统变量需在管理员终端运行，或自行使用 `sudo.exe`；权限不足时命令报错退出。

### 终端切换脚本

CLI 目录同时提供三个脚本。`name`、`version` 支持命名参数或位置参数，`scope` 默认为 `shell`：`reg` 只修改注册表，`shell` 只修改当前终端，`all` 同时修改两者。当前终端切换会更新目标变量及 PATH 中对应旧版本的路径，保留其余条目。

```powershell
Switch-PathMan -Name java -Version 25 -Scope reg
Switch-PathMan java 25
```

```cmd
switch-pathman /n node /v 24
switch-pathman node 24 /s all
```

```bash
. switch-pathman --name python --version 3.12 --scope all
. switch-pathman python 3.12
```

Git Bash 必须用 `.` 在当前 shell 中执行。批处理脚本内使用 CMD 版本时需用 `call switch-pathman ...`。CMD 的延迟展开需关闭，且单个变量赋值不能超过 8000 个字符或包含换行；不满足时切换报错，不截断内容。

## 权限与数据

桌面界面首次修改系统变量时请求 UAC，后续操作复用管理员助手。关闭窗口或闲置 15 分钟后助手退出。取消授权会保留编辑内容。

环境变量值保存在 Windows 注册表，PathMan 的用户数据位于 `%LocalAppData%\PathMan`：

| 路径 | 内容 |
| --- | --- |
| `environment.json` | 分组、置顶、版本、设置与变更记录 |
| `backups\` | 环境变量修改备份 |
| `shims\` | 程序别名垫片与目标配置 |

安装目录根部为 `PathMan.exe`，`bin\` 放置 CLI，`internal\` 放置后台程序、垫片和安装维护组件。同卷时，别名使用安装目录垫片的硬链接；跨卷时复制一份到用户数据目录，再复用本地硬链接。用户数据所在卷需支持硬链接。

## 模块

| 模块 | 技术 | 职责 |
| --- | --- | --- |
| Pathman.Core | C# / .NET 10 | 环境变量、分类、检测、监控、别名与版本配置 |
| Pathman.Gui | C# / WPF | 桌面界面 |
| Pathman.Cli | C# | 版本管理命令行 |
| Pathman.Service | C# | 用户会话后台监控与安装维护 |
| Pathman.Shim | C++20 / Win32 | 程序别名调用 |

后台程序在用户会话中运行。

## 构建与测试

构建需要 Windows、PowerShell 7、.NET 10 SDK、CMake 3.24+、Visual Studio C++ 桌面工具集、Windows SDK 和 NSIS 3.12+。ARM64 需要对应的 C++ 工具集，`-Test` 需要 PowerShell 7.6+。可通过 `NSIS_COMPILER` 指定 `makensis.exe`。

```powershell
.\build.ps1
.\build.ps1 -Arch x64,arm64
.\build.ps1 -Arch x64 -PackageType SelfContained -Test
.\build.ps1 -Plan
```

`-Arch` 接受 `x86`、`x64`、`arm64`，`-PackageType` 接受 `FrameworkDependent`、`SelfContained`，均支持数组。默认生成六份安装包，输出到 `artifacts\setup`；应用文件位于 `artifacts\publish\win-<架构>\<类型>`。`-Plan` 只列出构建计划，`-SkipInstaller` 跳过打包。

### 构建编号

版本名与数字版本号分开记录。`Directory.Build.props` 保存版本名、发行类型和公共 X、Y；被 Git 忽略的 `.pathman/build-state.json` 保存上一次成功构建的完整 `X.Y.Z` 编号。

| 参数 | 编号变化 |
| --- | --- |
| `-X` | X 递增，Y、Z 归零 |
| `-Y` | Y 递增，Z 归零；X 变化时先将 Y 归零 |
| `-Z`（默认） | Z 递增；X 或 Y 变化时先将 Z 归零 |

三个参数互斥，构建计划和失败构建不消耗编号。每段范围为 0–65535，Windows 文件版本为 `X.Y.Z.0`，产品版本使用版本名。

可显式指定版本名、编号和发行类型，用于可重复构建：

```powershell
.\build.ps1 -VersionName 1.0.0-beta.1 -VersionNumber 1.0.0 -ReleaseType beta
```

`-VersionName` 接受 SemVer，`-Version` 是其别名。`-ReleaseType` 为 `official` 或 `beta`，默认读取公共配置。显式指定 `-VersionNumber` 会跳过本地计数，不能与 `-X`、`-Y`、`-Z` 同用。

### 测试与 CI

`build.ps1 -Test` 运行核心测试和本机可运行架构的运行时测试。核心测试也可单独运行：

```powershell
dotnet run --project tests/Pathman.Core.Tests -c Release
```

[CI](.github/workflows/ci.yml) 在推送、Pull Request 和手动触发时调用共用的[构建与验收工作流](.github/workflows/build.yml)。x64 和 x86 在 Windows x64 runner 上运行，x86 使用 WoW64；ARM64 使用原生 Windows ARM64 runner。每个架构构建两种安装包，执行 CLI、垫片及安装、升级、覆盖、降级拦截和卸载验收。

Actions 缓存 NuGet 包和 NSIS、PowerShell、目标运行时的下载文件，工具文件在使用前校验哈希。[tests](tests/) 中的安装器与桌面回归脚本需在隔离 Windows 环境中运行，完成后恢复测试环境。

本地可使用 act 的原生 Windows 执行器：

```powershell
act push -P windows-2025=-self-hosted --matrix arch:x64
act push -P windows-2025=-self-hosted --matrix arch:x86
```

ARM64 任务需要 Windows ARM64 主机，并映射 `windows-11-vs2026-arm`。act 使用便携运行时，跳过安装器验收。

### 标签发布

[Release 工作流](.github/workflows/release.yml) 支持标签发行和手动发布公开测试版。发布前需配置仓库 secret `PATHMAN_VARIABLES_TOKEN`，使用具有本仓库 Variables 读写权限的细粒度访问令牌；编号变量 `PATHMAN_BUILD_STATE` 由工作流创建。配置写回与 Release 发布使用工作流的 `GITHUB_TOKEN`，构建分支需允许工作流提交配置。

标签发行使用主线配置中的 X。准备新发行时递增 X、将 Y 归零，提交后创建附注标签或签名标签。标签名为 SemVer，可带 `v` 前缀：

```powershell
git tag -a v1.0.0-beta.1 -F release-notes.txt
git push origin v1.0.0-beta.1
```

标签说明第一行用于 Release 标题，标题格式为“标签名 - 第一行说明”。正文从后续第一个非空行开始，签名块不会写入发行说明。轻量标签不可用于发布。

手动运行 Release 工作流会发布 Y 版，标签为 `test/X.Y.0`。工作流读取主线 X 与仓库变量中的 Y，递增 Y 后将 X、Y 写回所选构建分支，再构建该提交。构建分支需包含当前主线 X 与最新发行提交。不同分支共用仓库编号，Fork 使用自己的编号空间；失败或取消的公开构建可能留下编号空缺。普通 CI 不发布版本。

三个架构的构建和验收全部通过后，发布六份 EXE 与 `release.json`。清单包含 `versionName`、`versionNumber`、`type` 和 `installers`；每个安装包记录文件名、架构、是否自带运行时、字节大小与 SHA-256。`type` 为 `official`、`beta` 或 `development`，Y 版属于 `development`。Beta 和 Y 版标记为 GitHub 预发布，本地 Z 构建不可发布。每个安装包的 `.exe.json` 校验清单保留为内部产物。

## 开发计划

- PATH 变量卡片视图。

## 许可证

[MIT](LICENSE)。第三方组件许可见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) 和 [licenses](licenses/)。
