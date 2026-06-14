# EdgeKit

**Language / 语言:** [中文](README.md) | English

EdgeKit is a Windows desktop productivity toolbox built with WinUI 3. It sits near the screen edge and provides quick access to app launching, command search, clipboard history, text tools, image tools, and common system actions.

> Status: early development. The shell and core workflows are usable, while some tools are still being refined.

## Features

- Edge drawer with edge trigger, pinning, auto-hide, tray icon, and global hotkey support.
- Command palette for Windows settings, system tools, shell folders, power actions, custom commands, and installed apps.
- Clipboard history for text, URLs, JSON-like content, file paths, file lists, and images.
- Quick launch with installed app indexing, recent items, shortcuts, and custom commands.
- Everything file search using bundled x64 / ARM64 `es.exe` bridge binaries when the local Everything service is running.
- Text utilities such as JSON editing/formatting, URL conversion, and Base64 conversion.
- Image conversion and compression powered by ImageSharp.
- System utilities for network/system information, hosts file editing, environment variables, and window management.
- Local-first storage under `%LOCALAPPDATA%\EdgeKit`.

## Requirements

- Windows 10 1809 or later. Windows 11 is recommended.
- .NET 10 Desktop Runtime or .NET 10 SDK.
- Windows App Runtime 2.2.
- Optional: Everything, used for fast local file search in the command palette. EdgeKit ships the `es.exe` command-line bridge, but it does not install the Everything app or indexing service.
- Inno Setup 6 for building installers.

Check the installed .NET version:

```powershell
dotnet --version
```

## Run From Source

```powershell
git clone <repo-url>
cd EdgeKit
dotnet restore .\EdgeKit.slnx
dotnet build .\EdgeKit.slnx
dotnet run --project .\src\EdgeKit.App\EdgeKit.App.csproj
```

You can also open `EdgeKit.slnx` in Visual Studio, set `EdgeKit.App` as the startup project, and run it from there.

## Build Installers

Publish x64 and ARM64 builds:

```powershell
dotnet publish .\src\EdgeKit.App\EdgeKit.App.csproj -c Release -r win-x64 -o .\artifacts\publish\EdgeKit-win-x64
dotnet publish .\src\EdgeKit.App\EdgeKit.App.csproj -c Release -r win-arm64 -o .\artifacts\publish\EdgeKit-win-arm64
```

WinUI compiled XAML resources must be included in the installer. Before compiling the installer, make sure the publish directory contains:

- `EdgeKit.App.pri`
- `App.xbf`
- `Styles\*.xbf`
- `Views\*.xbf`
- `Windows\*.xbf`

If `.xbf` files are missing from the publish directory, copy them from the matching runtime build output:

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

Compile installers with Inno Setup:

```powershell
& "C:\Users\<you>\AppData\Local\Programs\Inno Setup 6\ISCC.exe" /DMyRuntimeTag=win-x64 .\installer\EdgeKit.iss
& "C:\Users\<you>\AppData\Local\Programs\Inno Setup 6\ISCC.exe" /DMyRuntimeTag=win-arm64 /DMyArchitecturesAllowed=arm64 /DMyArchitecturesInstallIn64BitMode=arm64 .\installer\EdgeKit.iss
```

Generated installers are written to:

```text
artifacts/installer/
```

Do not commit `artifacts/`, `bin/`, `obj/`, `AppPackages/`, signing certificates, or local publish profiles.

## Project Structure

```text
EdgeKit/
  Directory.Build.props
  EdgeKit.slnx
  installer/          Inno Setup script and installer assets
  src/
    EdgeKit.App/      WinUI 3 app, windows, pages, view models, search, and commands
    EdgeKit.Core/     shared domain models and contracts
    EdgeKit.Data/     SQLite repository implementations
    EdgeKit.Native/   Win32 / Windows interop wrappers
    EdgeKit.Services/ clipboard, settings, diagnostics, image, text, and tool services
```

## Development Notes

- Add new tool entries in `src/EdgeKit.Services/Tools/ToolCatalog.cs`.
- Add built-in command palette entries in `src/EdgeKit.App/Commands/CommandRegistry.cs`.
- Runtime logs are written to `%LOCALAPPDATA%\EdgeKit\logs`.
- Local database and runtime state are written to `%LOCALAPPDATA%\EdgeKit`.

Before submitting a pull request, run:

```powershell
dotnet restore .\EdgeKit.slnx
dotnet build .\EdgeKit.slnx
```

## Third-Party Components

This project uses and may redistribute third-party components or assets such as Windows App SDK, WinUI, Monaco Editor, Everything CLI, SQLite, and ImageSharp.

The Everything integration calls the local Everything index service through `src/EdgeKit.App/Assets/Everything/<rid>/es.exe`. If Everything is not running, file search automatically degrades to an unavailable state. EdgeKit code is licensed under the MIT License; third-party components remain under their own licenses.

## License

This project is licensed under the [MIT License](LICENSE).
