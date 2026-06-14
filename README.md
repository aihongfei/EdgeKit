# EdgeKit

**语言 / Language:** 中文 | [English](README.en.md)

EdgeKit 是一个面向 Windows 的桌面效率工具箱。它基于 WinUI 3 构建，常驻屏幕边缘，提供快速启动、命令搜索、剪贴板历史、文本工具、图片工具和常用系统操作。

> 当前状态：早期开发版本。主界面和核心工具链已经可用，部分工具仍在完善中。

## 功能

- 屏幕边缘抽屉：支持边缘触发、置顶、自动隐藏、托盘图标和全局热键。
- 命令面板：快速搜索系统设置、系统工具、Shell 文件夹、电源操作、自定义命令和本机应用。
- 剪贴板历史：记录文本、URL、JSON、文件路径、文件列表和图片内容。
- 快速启动：本机应用索引、最近项目、快捷入口和自定义命令。
- Everything 文件搜索：内置 x64 / ARM64 `es.exe` 桥接程序，在本机 Everything 服务运行时提供快速文件搜索。
- 文本工具：JSON 编辑/格式化、URL/Base64 等常用转换流程。
- 图片工具：基于 ImageSharp 的图片转换和压缩流程。
- 系统工具：网络/系统信息、hosts 文件、环境变量和窗口管理。
- 本地优先：运行数据默认写入 `%LOCALAPPDATA%\EdgeKit`。

## 运行要求

- Windows 10 1809 或更高版本，推荐 Windows 11。
- .NET 10 Desktop Runtime 或 .NET 10 SDK。
- Windows App Runtime 2.2。
- 可选：Everything，用于启用命令面板中的本地文件快速搜索。EdgeKit 随应用分发 `es.exe` 命令行桥接程序，但不负责安装 Everything 主程序或索引服务。
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

先发布 x64 和 ARM64：

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

`artifacts/`、`bin/`、`obj/`、`AppPackages/`、签名证书和本地发布配置都不应提交到 Git。

## 项目结构

```text
EdgeKit/
  Directory.Build.props
  EdgeKit.slnx
  installer/          Inno Setup 安装器脚本和安装器资源
  src/
    EdgeKit.App/      WinUI 3 应用、窗口、页面、视图模型、搜索和命令
    EdgeKit.Core/     共享领域模型和接口
    EdgeKit.Data/     SQLite 仓储实现
    EdgeKit.Native/   Win32 / Windows 互操作封装
    EdgeKit.Services/ 剪贴板、设置、诊断、图片、文本和工具服务
```

## 开发说明

- 新增工具入口：`src/EdgeKit.Services/Tools/ToolCatalog.cs`
- 新增命令面板内置命令：`src/EdgeKit.App/Commands/CommandRegistry.cs`
- 运行日志：`%LOCALAPPDATA%\EdgeKit\logs`
- 本地数据库和运行状态：`%LOCALAPPDATA%\EdgeKit`

提交 PR 前建议运行：

```powershell
dotnet restore .\EdgeKit.slnx
dotnet build .\EdgeKit.slnx
```

## 第三方组件

本项目使用并可能随应用分发多个第三方组件或资源，例如 Windows App SDK、WinUI、Monaco Editor、Everything CLI、SQLite、ImageSharp 等。

Everything 集成通过 `src/EdgeKit.App/Assets/Everything/<rid>/es.exe` 调用本机 Everything 索引服务；如果 Everything 未运行，相关文件搜索能力会自动降级为不可用状态。EdgeKit 自身代码使用 MIT License；第三方组件仍遵循各自许可证。

## 许可证

本项目基于 [MIT License](LICENSE) 开源。
