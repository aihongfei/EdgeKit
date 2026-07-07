# EdgeKit

**语言 / Language:** 中文 | [English](README.en.md)

EdgeKit 是一个面向 Windows 的本地优先桌面效率工具箱。它基于 WinUI 3 构建，常驻屏幕边缘，通过边缘抽屉、全局快捷键和托盘菜单提供快速启动、命令搜索、剪贴板历史、待办任务、桌面便签、翻译、文本/图片处理、系统诊断和智能体助手。

> 当前状态：功能迭代版本。边缘抽屉、工具导航、SQLite 持久化、托盘、热键、命令面板、剪贴板、任务、便签、翻译、智能体和系统工具链已接入；部分工具仍会持续打磨体验和边界能力。

## 功能概览

### 入口与桌面交互

- 屏幕边缘抽屉：支持左侧、右侧或双侧触发，可配置呼出长条宽高、抽屉宽度、动画时长和自动收起延迟。
- 全局快捷键：默认 `Ctrl+Alt+K`，可在设置中心重新录制，触发后打开抽屉并聚焦搜索框。
- 托盘常驻：提供显示 EdgeKit、搜索、剪贴板历史、图片 OCR、新建便签、便签管理、设置和退出入口。
- 全屏保护：可在全屏应用中禁用边缘触发，避免游戏、视频或演示场景误触。
- 开机自启：可在设置中心开启，登录 Windows 后后台启动 EdgeKit。

### 首页与快速启动

- 最近窗口：记录最近切换过的活动窗口，可在首页快速回到目标窗口。
- 最近工具：展示最近使用过的工具入口。
- 快速启动：支持固定应用、文件、文件夹和常用入口。
- 每日摘录：本地摘录资源，可在首页显示或关闭。
- 首页隐私：最近窗口、最近工具、剪贴板摘要和每日摘录都可在设置中心控制显示范围。

### 搜索与命令

- 统一搜索框：聚合本机应用、内置命令、自定义命令、快速入口和可选 Everything 文件搜索。
- 命令面板：内置 Windows 设置、系统工具、Shell 文件夹、电源操作和 EdgeKit 内部导航命令。
- 自定义命令：支持命令文本、参数、工作目录、关键字、管理员运行和执行确认。
- Everything 集成：随应用分发 x64 / ARM64 `es.exe` 桥接程序；需要本机 Everything 主程序和索引服务运行。

### 剪贴板

- 剪贴板历史：记录文本、URL、JSON、文件路径、文件列表和图片内容。
- 搜索与分组：支持历史搜索、分组、置顶、删除和清空。
- 图片持久化：剪贴板图片保存到本地数据目录，数据库只保存索引信息。
- 本地优先：剪贴板数据默认写入 `%LOCALAPPDATA%\EdgeKit`。

### 任务与便签

- 待办任务：支持新增、编辑、删除、优先级、截止时间和状态流转。
- 任务视图：可按状态筛选，并按时间分组查看任务。
- 任务提醒：后台提醒服务随应用启动。
- 桌面便签：支持创建独立便签窗口，并持久化内容、颜色、位置和大小。

### 翻译、文本与图片

- 翻译：支持有道翻译和大模型翻译结果并排展示。
- 文本工具：提供 JSON 格式化/压缩/校验、树形预览、路径复制、URL 编解码和 Base64 编解码。
- Monaco 编辑器：JSON 编辑体验基于本地 Monaco 静态资源，不依赖外部 CDN。
- 图片转换：基于 ImageSharp 提供格式转换和压缩流程。
- 图片 OCR：基于 Windows OCR 识别图片文字。

### 系统工具

- 系统诊断：网络信息、系统信息、DNS 解析、TCP 探测、端口占用等诊断能力。
- Hosts 管理：读取、校验、备份和保存 Windows hosts 文件；需要提权的保存操作走受控内部命令。
- 环境变量：查看、分析、设置和删除用户/系统环境变量。
- 文件锁定检测：定位占用文件或目录的进程，支持回收站删除和终止占用后删除。
- 窗口管理：查看和管理当前窗口。
- 资源仪表盘：实时展示 CPU、内存、磁盘和网络采样，图表使用 Syncfusion WinUI 组件。

### 智能体

- 多轮对话：支持会话列表、消息流式输出、上下文状态和历史持久化。
- OpenAI 兼容配置：可配置 Base URL、模型、API Key、温度、上下文窗口和默认模式。
- 工具调用：按设置开关启用剪贴板、文件、Shell、Web、MCP、Windows 设置、Hosts、环境变量、文件锁和端口处理能力。
- 操作安全：敏感动作支持确认模式；文件和 Shell 能力可通过可信目录、命令白名单和开关限制作用域。
- 密钥保护：AI、有道翻译、搜索和 Syncfusion License 等密钥使用 Windows DPAPI 加密后落盘。

## 运行要求

- Windows 10 1809 或更高版本，推荐 Windows 11。
- .NET 10 Desktop Runtime 或 .NET 10 SDK。
- Windows App Runtime 2.2。
- WebView2 Runtime：用于承载本地 Monaco 编辑器资源。
- 可选：Everything，用于启用搜索框中的本地文件快速搜索。
- 可选：有道翻译 AppKey / AppSecret，用于启用有道翻译。
- 可选：OpenAI 兼容 API Key，用于启用智能体和大模型翻译。
- 可选：Syncfusion License Key，用于资源仪表盘图表组件授权。
- 构建安装包需要 Inno Setup 6。

检查 .NET 版本：

```powershell
dotnet --version
```

## 从源码运行

```powershell
git clone <repo-url>
cd EdgeKit
dotnet restore .\EdgeKit.slnx
dotnet build .\EdgeKit.slnx
dotnet run --project .\src\EdgeKit.App\EdgeKit.App.csproj
```

也可以用 Visual Studio 打开 `EdgeKit.slnx`，将 `EdgeKit.App` 设为启动项目后运行。

## 构建安装包

先发布目标运行时：

```powershell
dotnet publish .\src\EdgeKit.App\EdgeKit.App.csproj -c Release -r win-x64 -o .\artifacts\publish\EdgeKit-win-x64
dotnet publish .\src\EdgeKit.App\EdgeKit.App.csproj -c Release -r win-arm64 -o .\artifacts\publish\EdgeKit-win-arm64
```

WinUI 的 XAML 编译资源需要随安装包一起发布。打包前确认发布目录中包含：

- `EdgeKit.App.pri`
- `App.xbf`
- `Styles\*.xbf`
- `Views\*.xbf`
- `Windows\*.xbf`

如果发布目录缺少 `.xbf` 文件，可以从对应架构的构建输出同步：

```powershell
function Copy-XamlResources($rid) {
  $source = ".\src\EdgeKit.App\bin\Release\net10.0-windows10.0.26100.0\$rid"
  $target = ".\artifacts\publish\EdgeKit-$rid"

  Get-ChildItem -Path $source -Recurse -Filter *.xbf | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath((Resolve-Path $source), $_.FullName)
    $dest = Join-Path $target $relative
    New-Item -ItemType Directory -Path (Split-Path $dest) -Force | Out-Null
    Copy-Item $_.FullName $dest -Force
  }
}

Copy-XamlResources win-x64
Copy-XamlResources win-arm64
```

然后使用 Inno Setup 编译安装器：

```powershell
& "C:\Users\<you>\AppData\Local\Programs\Inno Setup 6\ISCC.exe" /DMyRuntimeTag=win-x64 .\installer\EdgeKit.iss
& "C:\Users\<you>\AppData\Local\Programs\Inno Setup 6\ISCC.exe" /DMyRuntimeTag=win-arm64 /DMyArchitecturesAllowed=arm64 /DMyArchitecturesInstallIn64BitMode=arm64 .\installer\EdgeKit.iss
```

生成的安装包位于：

```text
artifacts/installer/
```

安装器默认安装到当前用户的 `%LOCALAPPDATA%\Programs\EdgeKit`。卸载时会询问是否同时删除 `%LOCALAPPDATA%\EdgeKit` 用户数据。

`artifacts/`、`bin/`、`obj/`、`AppPackages/`、签名证书和本地发布配置都不应提交到 Git。

## 项目结构

```text
EdgeKit/
  Directory.Build.props
  EdgeKit.slnx
  installer/          Inno Setup 安装器脚本和安装器资源
  src/
    EdgeKit.App/      WinUI 3 应用入口、窗口、页面、视图模型、搜索、命令、托盘和交互
    EdgeKit.Core/     领域模型、接口、工具描述符、设置契约和状态定义
    EdgeKit.Data/     SQLite 仓储实现，保存设置、剪贴板、命令、最近项、任务、便签和智能体历史
    EdgeKit.Native/   Win32 / Windows Shell / 屏幕 / 剪贴板 / 窗口互操作封装
    EdgeKit.Services/ 文本、图片、OCR、翻译、智能体、诊断、系统操作、设置和工具目录服务
```

## 数据与配置位置

```text
%LOCALAPPDATA%\EdgeKit\edgekit.db          SQLite 主数据库
%LOCALAPPDATA%\EdgeKit\clipboard-images\  剪贴板图片缓存
%LOCALAPPDATA%\EdgeKit\logs\              运行日志，默认保留 7 天
%LOCALAPPDATA%\EdgeKit\backups\           hosts / environment 等操作备份
```

敏感配置不会以明文保存；应用通过 Windows DPAPI 加密后写入本地数据库。

## 开发说明

- 新增工具入口：`src/EdgeKit.Services/Tools/ToolCatalog.cs`
- 新增工具分类：`src/EdgeKit.Core/Tools/ToolCategory.cs`
- 新增命令面板内置命令：`src/EdgeKit.App/Commands/CommandRegistry.cs`
- 新增设置项：`src/EdgeKit.Core/Services/ISettingsService.cs` 与 `src/EdgeKit.Services/Settings/SettingsService.cs`
- 新增持久化仓储：优先放在 `src/EdgeKit.Data/<domain>/`，并在 `src/EdgeKit.App/App.xaml.cs` 注册依赖。
- 新增系统级能力：领域契约放 `EdgeKit.Core`，Windows 互操作放 `EdgeKit.Native`，业务编排放 `EdgeKit.Services`，UI 只负责展示和用户确认。

提交 PR 前建议运行：

```powershell
dotnet restore .\EdgeKit.slnx
dotnet build .\EdgeKit.slnx
```

## 第三方组件

本项目使用并可能随应用分发多个第三方组件或资源，例如 Windows App SDK、WinUI、WebView2、Monaco Editor、Everything CLI、SQLite、ImageSharp、Markdig、Syncfusion WinUI、Microsoft.Extensions.AI、Microsoft Agents AI、Model Context Protocol SDK、Serilog 等。

Everything 集成通过 `src/EdgeKit.App/Assets/Everything/<rid>/es.exe` 调用本机 Everything 索引服务；如果 Everything 未运行，相关文件搜索能力会自动降级为不可用状态。

EdgeKit 自身代码使用 MIT License；第三方组件仍遵循各自许可证。

## 许可证

本项目基于 [MIT License](LICENSE) 开源。
