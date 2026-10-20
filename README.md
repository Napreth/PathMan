# PathMan

面向 Windows 的环境变量与程序别名管理器，使用 C# 构建核心、桌面界面、CLI 和后台程序，使用 C++ 实现独立运行的垫片。

> **开发阶段：pre-alpha。** 当前版本主要用于验证模块架构、程序别名和安装流程，尚未实现完整的环境变量管理功能。功能、命令、配置格式与界面仍可能调整。

## 当前进度

已实现：

- CLI 程序别名管理：创建、列出、删除和刷新硬链接垫片，透传参数、标准输入输出和退出码。
- 基础桌面界面：查看与刷新别名列表，切换当前用户的登录自启设置。
- 普通后台进程：单实例、启动、停止和状态查询，默认不自启。
- NSIS 安装与卸载。

后续规划：环境变量编辑与作用域管理、变量分类、PATH 路径分组、多版本切换、外部 PATH 变更监控与自动整理。目前仅预留了部分数据模型，相关功能尚未接入。

## 模块与入口

| 模块 | 语言 / 技术 | 职责 / 入口 |
| --- | --- | --- |
| Pathman.Core | C# / .NET 10 | 配置模型、目录布局、别名管理、后台进程控制、自启设置，共享类库 |
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
.\build.ps1 -Test                                 # 同时运行本机匹配架构的应用测试
.\build.ps1 -Plan                                 # 只显示构建矩阵
.\build.ps1 -Version 0.2.0
.\build.ps1 -SkipInstaller                        # 仅生成发布目录
.\scripts\package.ps1                             # 使用已有发布目录重新打包六份 EXE
```

`-Arch` 和 `-PackageType` 都接受数组，省略时构建全部组合。普通包需要对应架构的 .NET 10 Desktop Runtime，自包含包不需要预装运行时。应用采用单文件发布，`bin` 只包含 `pathman.exe`。

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

GUI 提供别名列表与“登录 Windows 时自动启动”复选框。自启使用当前用户的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，无需管理员权限；切换自启不会立即启动或停止后台程序。
带单引号的别名在 PowerShell 中要使用调用运算符和双引号，例如 `& "p'w'sh"`。

可用 `pathman --data-root <目录> ...` 隔离别名数据。测试在临时目录执行，不修改真实 PATH 或自启设置。

## 许可证

本项目采用 [MIT 许可证](LICENSE)。
