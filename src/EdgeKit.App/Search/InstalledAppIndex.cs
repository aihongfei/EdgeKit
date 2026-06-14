using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EdgeKit.Native;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;

namespace EdgeKit.App.Search;

/// <summary>
/// 本机已安装应用索引。启动后在后台线程枚举一次 shell:AppsFolder
/// （同时覆盖 Win32 桌面程序与 UWP/商店应用）并缓存，供搜索引擎合并匹配。
/// 图标在 UI 线程惰性转换为 <see cref="BitmapImage"/>，失败则保持 null 由 UI 占位。
/// </summary>
public sealed class InstalledAppIndex
{
    // 图标抓取的目标尺寸（物理像素）。
    private const int IconSize = 32;

    private readonly object _gate = new();
    private volatile IReadOnlyList<InstalledApp> _apps = Array.Empty<InstalledApp>();
    private Task? _loadTask;

    // 暂存原始 HBITMAP，待 UI 线程转换。枚举发生在后台线程，不能在那里创建 BitmapImage。
    private readonly List<(InstalledApp App, nint Hbitmap)> _pendingIcons = new();

    /// <summary>当前已索引到的应用快照（线程安全读取）。</summary>
    public IReadOnlyList<InstalledApp> Apps => _apps;

    /// <summary>
    /// 按常见名称在已索引应用中查找目标。
    /// 用于诸如 Everything 这类需要动态发现安装位置的程序。
    /// </summary>
    public bool TryFindAppByName(string appName, out InstalledApp? app)
    {
        app = null;
        var candidateName = NormalizeLookupKey(appName);
        if (candidateName.Length == 0)
        {
            return false;
        }

        var bestScore = 0;
        foreach (var current in Apps)
        {
            var score = GetMatchScore(current, candidateName);
            if (score > bestScore)
            {
                bestScore = score;
                app = current;
            }
        }

        return app is not null;
    }

    /// <summary>是否已完成首次枚举。</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>首次应用枚举完成后触发。事件可能来自后台线程，调用方需要自行切回 UI 线程。</summary>
    public event EventHandler? Loaded;

    /// <summary>
    /// 触发后台预热（幂等）。枚举本身不创建 BitmapImage，只暂存 HBITMAP，
    /// 待 <see cref="MaterializeIconsAsync"/> 在 UI 线程转换。
    /// </summary>
    public void Prewarm()
    {
        lock (_gate)
        {
            _loadTask ??= Task.Run(Enumerate);
        }
    }

    /// <summary>
    /// 在 UI 线程把后台抓到的 HBITMAP 转为 BitmapImage 并回填到应用条目。
    /// 须在 UI 线程调用（创建 BitmapImage）。重复调用安全。
    /// </summary>
    public void MaterializeIcons(DispatcherQueue dispatcher)
    {
        List<(InstalledApp App, nint Hbitmap)> pending;
        lock (_gate)
        {
            if (_pendingIcons.Count == 0)
            {
                return;
            }

            pending = new List<(InstalledApp, nint)>(_pendingIcons);
            _pendingIcons.Clear();
        }

        foreach (var (app, hbitmap) in pending)
        {
            try
            {
                app.IconImage = IconConverter.FromHBitmap(hbitmap);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "应用图标转换失败 app={App}", app.DisplayName);
            }
            finally
            {
                if (hbitmap != nint.Zero)
                {
                    NativeMethods.DeleteObject(hbitmap);
                }
            }
        }
    }

    private void Enumerate()
    {
        var result = new List<InstalledApp>();
        var pending = new List<(InstalledApp, nint)>();

        try
        {
            var iid = NativeMethods.IID_IShellItem;
            NativeMethods.SHCreateItemFromParsingName("shell:AppsFolder", nint.Zero, ref iid, out var folderObj);
            if (folderObj is not NativeMethods.IShellItem folder)
            {
                CompleteLoad(result, pending);
                return;
            }

            folder.BindToHandler(
                nint.Zero,
                NativeMethods.BHID_EnumItems,
                typeof(NativeMethods.IEnumShellItems).GUID,
                out var enumObj);

            if (enumObj is not NativeMethods.IEnumShellItems enumerator)
            {
                CompleteLoad(result, pending);
                return;
            }

            while (enumerator.Next(1, out var item, out var fetched) == 0 && fetched == 1)
            {
                try
                {
                    item.GetDisplayName(NativeMethods.SIGDN_NORMALDISPLAY, out var displayName);
                    item.GetDisplayName(NativeMethods.SIGDN_PARENTRELATIVEPARSING, out var parsingName);

                    if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(parsingName))
                    {
                        continue;
                    }

                    var app = new InstalledApp(displayName, parsingName, iconImage: null);
                    result.Add(app);

                    // 抓图标 HBITMAP（不在此处创建 BitmapImage）。
                    if (item is NativeMethods.IShellItemImageFactory factory)
                    {
                        var size = new NativeMethods.SIZE { cx = IconSize, cy = IconSize };
                        var flags = NativeMethods.SIIGBF_ICONONLY | NativeMethods.SIIGBF_BIGGERSIZEOK;
                        if (factory.GetImage(size, flags, out var hbitmap) == 0 && hbitmap != nint.Zero)
                        {
                            pending.Add((app, hbitmap));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "枚举单个应用项失败");
                }
                finally
                {
                    if (item is not null)
                    {
                        System.Runtime.InteropServices.Marshal.ReleaseComObject(item);
                    }
                }
            }

            System.Runtime.InteropServices.Marshal.ReleaseComObject(enumerator);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(folder);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "枚举本机应用失败");
        }

        MergeStartMenuShortcuts(result);

        CompleteLoad(result, pending);

        Log.Information("已索引本机应用 {Count} 个", result.Count);
    }

    private static void MergeStartMenuShortcuts(List<InstalledApp> apps)
    {
        foreach (var shortcut in EnumerateStartMenuShortcuts())
        {
            var app = FindMatchingApp(apps, shortcut);
            if (app is null)
            {
                var launchTarget = !string.IsNullOrWhiteSpace(shortcut.AppUserModelId)
                    ? shortcut.AppUserModelId
                    : shortcut.ExecutablePath;

                if (string.IsNullOrWhiteSpace(launchTarget))
                {
                    continue;
                }

                apps.Add(new InstalledApp(
                    shortcut.DisplayName,
                    launchTarget,
                    iconImage: null,
                    locationPath: shortcut.ShortcutPath,
                    executablePath: shortcut.ExecutablePath));
                continue;
            }

            if (string.IsNullOrWhiteSpace(app.LocationPath))
            {
                app.LocationPath = shortcut.ShortcutPath;
            }

            if (string.IsNullOrWhiteSpace(app.ExecutablePath))
            {
                app.ExecutablePath = shortcut.ExecutablePath;
            }
        }
    }

    private static InstalledApp? FindMatchingApp(List<InstalledApp> apps, StartMenuShortcut shortcut)
    {
        if (!string.IsNullOrWhiteSpace(shortcut.AppUserModelId))
        {
            var byAumid = apps.FirstOrDefault(a =>
                string.Equals(a.AppUserModelId, shortcut.AppUserModelId, StringComparison.OrdinalIgnoreCase));
            if (byAumid is not null)
            {
                return byAumid;
            }
        }

        if (!string.IsNullOrWhiteSpace(shortcut.ExecutablePath))
        {
            var byExe = apps.FirstOrDefault(a =>
                string.Equals(a.AppUserModelId, shortcut.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.ExecutablePath, shortcut.ExecutablePath, StringComparison.OrdinalIgnoreCase));
            if (byExe is not null)
            {
                return byExe;
            }
        }

        return apps.FirstOrDefault(a =>
            string.Equals(a.DisplayName, shortcut.DisplayName, StringComparison.OrdinalIgnoreCase));
    }

    private static List<StartMenuShortcut> EnumerateStartMenuShortcuts()
    {
        var result = new List<StartMenuShortcut>();
        foreach (var root in GetStartMenuRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var shortcutPath in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
            {
                if (TryReadShortcut(shortcutPath, out var shortcut))
                {
                    result.Add(shortcut);
                }
            }
        }

        return result;
    }

    private static IEnumerable<string> GetStartMenuRoots()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            "Programs");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs");
    }

    private static bool TryReadShortcut(string shortcutPath, out StartMenuShortcut shortcut)
    {
        shortcut = default;
        object? linkObject = null;

        try
        {
            linkObject = new NativeMethods.ShellLink();
            var persistFile = (NativeMethods.IPersistFile)linkObject;
            persistFile.Load(shortcutPath, NativeMethods.STGM_READ);

            var shellLink = (NativeMethods.IShellLinkW)linkObject;
            var targetBuilder = new StringBuilder(NativeMethods.MAX_PATH);
            shellLink.GetPath(targetBuilder, targetBuilder.Capacity, nint.Zero, NativeMethods.SLGP_RAWPATH);
            var executablePath = targetBuilder.ToString();

            var appUserModelId = string.Empty;
            if (linkObject is NativeMethods.IPropertyStore propertyStore)
            {
                var key = NativeMethods.PKEY_AppUserModel_ID;
                propertyStore.GetValue(ref key, out var value);
                try
                {
                    appUserModelId = value.GetString();
                }
                finally
                {
                    value.Clear();
                }
            }

            var displayName = Path.GetFileNameWithoutExtension(shortcutPath);
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return false;
            }

            shortcut = new StartMenuShortcut(displayName, shortcutPath, executablePath, appUserModelId);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "读取开始菜单快捷方式失败 path={Path}", shortcutPath);
            return false;
        }
        finally
        {
            if (linkObject is not null && Marshal.IsComObject(linkObject))
            {
                Marshal.FinalReleaseComObject(linkObject);
            }
        }
    }

    private readonly record struct StartMenuShortcut(
        string DisplayName,
        string ShortcutPath,
        string ExecutablePath,
        string AppUserModelId);

    private void CompleteLoad(List<InstalledApp> result, List<(InstalledApp App, nint Hbitmap)> pending)
    {
        lock (_gate)
        {
            _apps = result;
            _pendingIcons.AddRange(pending);
            IsLoaded = true;
        }

        Loaded?.Invoke(this, EventArgs.Empty);
    }

    private static int GetMatchScore(InstalledApp app, string candidateName)
    {
        var displayName = NormalizeLookupKey(app.DisplayName);
        var executableName = NormalizeLookupKey(Path.GetFileNameWithoutExtension(app.ExecutablePath));
        var locationName = NormalizeLookupKey(Path.GetFileNameWithoutExtension(app.LocationPath));
        var appUserModelName = NormalizeLookupKey(Path.GetFileNameWithoutExtension(app.AppUserModelId));

        if (displayName == candidateName
            || executableName == candidateName
            || locationName == candidateName
            || appUserModelName == candidateName)
        {
            return 100;
        }

        if (displayName.Contains(candidateName, StringComparison.OrdinalIgnoreCase)
            || executableName.Contains(candidateName, StringComparison.OrdinalIgnoreCase)
            || locationName.Contains(candidateName, StringComparison.OrdinalIgnoreCase)
            || appUserModelName.Contains(candidateName, StringComparison.OrdinalIgnoreCase))
        {
            return 60;
        }

        if ((displayName.Length > 0 && candidateName.Contains(displayName, StringComparison.OrdinalIgnoreCase))
            || (executableName.Length > 0 && candidateName.Contains(executableName, StringComparison.OrdinalIgnoreCase))
            || (locationName.Length > 0 && candidateName.Contains(locationName, StringComparison.OrdinalIgnoreCase))
            || (appUserModelName.Length > 0 && candidateName.Contains(appUserModelName, StringComparison.OrdinalIgnoreCase)))
        {
            return 30;
        }

        return 0;
    }

    private static string NormalizeLookupKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }
}
