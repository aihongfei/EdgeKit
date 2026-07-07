# EdgeKit

**Language / 语言:** [中文](README.md) | English

EdgeKit is a local-first Windows desktop productivity toolbox built with WinUI 3. It stays near the screen edge and provides quick access to app launching, command search, clipboard history, tasks, sticky notes, translation, text/image processing, system diagnostics, and an agent assistant through an edge drawer, global hotkey, and tray menu.

> Status: active feature iteration. The edge drawer, tool navigation, SQLite persistence, tray icon, global hotkey, command palette, clipboard, tasks, sticky notes, translation, agent, and system utility workflows are wired up. Some tools are still being refined for user experience and edge cases.

## Feature Overview

### Entry Points and Desktop Interaction

- Edge drawer: supports left, right, or both-side triggers, configurable handle width/height, drawer width, animation duration, and auto-hide delay.
- Global hotkey: defaults to `Ctrl+Alt+K`, can be re-recorded in Settings, and opens the drawer with the search box focused.
- Tray resident mode: provides entries for showing EdgeKit, search, clipboard history, image OCR, new sticky note, sticky note management, settings, and exit.
- Fullscreen protection: can disable edge triggering while fullscreen apps are active to avoid accidental activation during games, videos, or presentations.
- Startup launch: can be enabled in Settings so EdgeKit starts in the background after Windows sign-in.

### Home and Quick Launch

- Recent windows: tracks recently focused windows and lets you return to them from the home page.
- Recent tools: shows recently used tools.
- Quick launch: supports pinned apps, files, folders, and frequently used entries.
- Daily quote: local quote resources can be shown or hidden on the home page.
- Home privacy controls: recent windows, recent tools, clipboard snippets, and daily quote visibility can be configured in Settings.

### Search and Commands

- Unified search box: combines installed apps, built-in commands, custom commands, quick entries, and optional Everything file search.
- Command palette: includes Windows settings, system tools, shell folders, power actions, and internal EdgeKit navigation commands.
- Custom commands: support command text, arguments, working directory, keywords, run-as-administrator, and confirmation before execution.
- Everything integration: ships x64 / ARM64 `es.exe` bridge binaries; the local Everything app and indexing service must be running.

### Clipboard

- Clipboard history: records text, URLs, JSON, file paths, file lists, and images.
- Search and grouping: supports history search, groups, pinning, deletion, and clearing.
- Image persistence: clipboard images are saved under the local data directory while the database stores index metadata.
- Local-first: clipboard data is stored under `%LOCALAPPDATA%\EdgeKit` by default.

### Tasks and Sticky Notes

- Task board: supports creating, editing, deleting, prioritizing, due dates, and status transitions.
- Task view: supports filtering by status and grouping by time.
- Task reminders: background reminder service starts with the app.
- Desktop sticky notes: supports independent sticky note windows with persisted content, color, position, and size.

### Translation, Text, and Images

- Translation: shows Youdao translation and LLM translation side by side.
- Text tools: provide JSON formatting/minifying/validation, tree preview, path copy, URL encode/decode, and Base64 encode/decode.
- Monaco editor: JSON editing uses bundled local Monaco assets instead of an external CDN.
- Image conversion: format conversion and compression powered by ImageSharp.
- Image OCR: recognizes text from images using Windows OCR.

### System Tools

- System diagnostics: includes network information, system information, DNS resolution, TCP probing, and port ownership diagnostics.
- Hosts management: reads, validates, backs up, and saves the Windows hosts file; elevated writes go through controlled internal commands.
- Environment variables: view, analyze, set, and delete user/system environment variables.
- File lock detection: identifies processes locking files or directories, with recycle-bin deletion and kill-then-delete actions.
- Window management: inspect and manage current windows.
- Resource dashboard: samples CPU, memory, disk, and network usage in real time, with charts powered by Syncfusion WinUI.

### Agent

- Multi-turn conversations: supports conversation list, streaming responses, context status, and persistent history.
- OpenAI-compatible configuration: configurable Base URL, model, API key, temperature, context window, and default mode.
- Tool calling: enables clipboard, file, Shell, Web, MCP, Windows settings, hosts, environment variable, file lock, and port handling tools through settings switches.
- Operation safety: sensitive actions support confirmation mode; file and Shell capabilities can be scoped with trusted directories, command allowlists, and feature switches.
- Secret protection: AI, Youdao, search, and Syncfusion license secrets are encrypted at rest with Windows DPAPI.

## Requirements

- Windows 10 1809 or later. Windows 11 is recommended.
- .NET 10 Desktop Runtime or .NET 10 SDK.
- Windows App Runtime 2.2.
- WebView2 Runtime, used to host local Monaco editor assets.
- Optional: Everything, used for fast local file search from the search box.
- Optional: Youdao AppKey / AppSecret, used for Youdao translation.
- Optional: OpenAI-compatible API key, used for the agent and LLM translation.
- Optional: Syncfusion License Key, used for resource dashboard chart component licensing.
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

Publish target runtime builds first:

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

The installer defaults to the current user's `%LOCALAPPDATA%\Programs\EdgeKit` directory. During uninstall, it asks whether `%LOCALAPPDATA%\EdgeKit` user data should also be removed.

Do not commit `artifacts/`, `bin/`, `obj/`, `AppPackages/`, signing certificates, or local publish profiles.

## Project Structure

```text
EdgeKit/
  Directory.Build.props
  EdgeKit.slnx
  installer/          Inno Setup script and installer assets
  src/
    EdgeKit.App/      WinUI 3 app entry, windows, pages, view models, search, commands, tray, and interaction
    EdgeKit.Core/     domain models, contracts, tool descriptors, settings contracts, and state definitions
    EdgeKit.Data/     SQLite repositories for settings, clipboard, commands, recent items, tasks, sticky notes, and agent history
    EdgeKit.Native/   Win32 / Windows Shell / screen / clipboard / window interop wrappers
    EdgeKit.Services/ text, image, OCR, translation, agent, diagnostics, system operation, settings, and tool catalog services
```

## Data and Configuration Locations

```text
%LOCALAPPDATA%\EdgeKit\edgekit.db          Main SQLite database
%LOCALAPPDATA%\EdgeKit\clipboard-images\  Clipboard image cache
%LOCALAPPDATA%\EdgeKit\logs\              Runtime logs, retained for 7 days by default
%LOCALAPPDATA%\EdgeKit\backups\           Backups for hosts / environment operations
```

Sensitive configuration is not stored in plain text. The app encrypts secrets with Windows DPAPI before writing them to the local database.

## Development Notes

- Add new tool entries in `src/EdgeKit.Services/Tools/ToolCatalog.cs`.
- Add new tool categories in `src/EdgeKit.Core/Tools/ToolCategory.cs`.
- Add built-in command palette entries in `src/EdgeKit.App/Commands/CommandRegistry.cs`.
- Add new settings in `src/EdgeKit.Core/Services/ISettingsService.cs` and `src/EdgeKit.Services/Settings/SettingsService.cs`.
- Add new persistence repositories under `src/EdgeKit.Data/<domain>/`, then register them in `src/EdgeKit.App/App.xaml.cs`.
- Add new system-level capabilities by putting domain contracts in `EdgeKit.Core`, Windows interop in `EdgeKit.Native`, business orchestration in `EdgeKit.Services`, and keeping UI responsible only for presentation and user confirmation.

Before submitting a pull request, run:

```powershell
dotnet restore .\EdgeKit.slnx
dotnet build .\EdgeKit.slnx
```

## Third-Party Components

This project uses and may redistribute third-party components or assets such as Windows App SDK, WinUI, WebView2, Monaco Editor, Everything CLI, SQLite, ImageSharp, Markdig, Syncfusion WinUI, Microsoft.Extensions.AI, Microsoft Agents AI, Model Context Protocol SDK, and Serilog.

The Everything integration calls the local Everything indexing service through `src/EdgeKit.App/Assets/Everything/<rid>/es.exe`. If Everything is not running, file search automatically degrades to an unavailable state.

EdgeKit code is licensed under the MIT License; third-party components remain under their own licenses.

## License

This project is licensed under the [MIT License](LICENSE).