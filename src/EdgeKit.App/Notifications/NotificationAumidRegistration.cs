using System.Runtime.InteropServices;
using EdgeKit.Native;
using Serilog;

namespace EdgeKit.App.Notifications;

/// <summary>
/// 为 unpackaged 应用注册 AUMID 快捷方式，使 ScheduledToastNotification 能被系统调度。
/// </summary>
public static class NotificationAumidRegistration
{
    public const string Aumid = "EdgeKit";

    public static void EnsureRegistered()
    {
        try
        {
            var processHr = NativeMethods.SetCurrentProcessExplicitAppUserModelID(Aumid);
            if (processHr != NativeMethods.S_OK)
            {
                Log.Warning("设置当前进程 AUMID 失败：0x{HResult:X8}", unchecked((uint)processHr));
            }

            var shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs",
                "EdgeKit.lnk");

            var exePath = Path.Combine(AppContext.BaseDirectory, "EdgeKit.App.exe");
            if (!File.Exists(exePath))
            {
                Log.Warning("找不到可执行文件，跳过 AUMID 注册：{ExePath}", exePath);
                return;
            }

            var directory = Path.GetDirectoryName(shortcutPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var shellLink = (object)new NativeMethods.ShellLink();
            var link = (NativeMethods.IShellLinkW)shellLink;
            link.SetPath(exePath);
            link.SetWorkingDirectory(AppContext.BaseDirectory);
            link.SetDescription("EdgeKit");

            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath))
            {
                link.SetIconLocation(iconPath, 0);
            }

            var persistFile = (NativeMethods.IPersistFile)shellLink;
            persistFile.Save(shortcutPath, false);

            WriteShortcutAumid(shortcutPath);

            Log.Information("已注册 AUMID 快捷方式：{Path}", shortcutPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "注册 AUMID 快捷方式失败。");
        }
    }

    private static void WriteShortcutAumid(string shortcutPath)
    {
        var errors = new List<Exception>();

        if (TryWriteWithParsingName(shortcutPath, errors))
        {
            return;
        }

        if (TryWriteWithShellItemPropertyStore(shortcutPath, errors))
        {
            return;
        }

        if (TryWriteWithPropertyStoreHandler(shortcutPath, errors))
        {
            return;
        }

        throw new AggregateException("写入快捷方式 AUMID 失败。", errors);
    }

    private static bool TryWriteWithParsingName(string shortcutPath, List<Exception> errors)
    {
        nint propertyStorePtr = nint.Zero;
        try
        {
            var propertyStoreId = NativeMethods.IID_IPropertyStore;
            var hr = NativeMethods.SHGetPropertyStoreFromParsingName(
                shortcutPath,
                nint.Zero,
                NativeMethods.GPS_READWRITE,
                ref propertyStoreId,
                out propertyStorePtr);

            if (hr != NativeMethods.S_OK || propertyStorePtr == nint.Zero)
            {
                throw new COMException($"SHGetPropertyStoreFromParsingName 返回 0x{hr:X8}。", hr);
            }

            WritePropertyStore(propertyStorePtr);
            Log.Information("已通过 ParsingName 写入 AUMID。路径：{Path}", shortcutPath);
            return true;
        }
        catch (Exception ex)
        {
            errors.Add(ex);
            return false;
        }
        finally
        {
            if (propertyStorePtr != nint.Zero)
            {
                Marshal.Release(propertyStorePtr);
            }
        }
    }

    private static bool TryWriteWithShellItemPropertyStore(string shortcutPath, List<Exception> errors)
    {
        object? shellItemObject = null;
        nint propertyStorePtr = nint.Zero;
        try
        {
            var shellItemId = NativeMethods.IID_IShellItem2;
            NativeMethods.SHCreateItemFromParsingName(
                shortcutPath,
                nint.Zero,
                ref shellItemId,
                out shellItemObject);

            var shellItem = (NativeMethods.IShellItem2)shellItemObject;
            var propertyStoreId = NativeMethods.IID_IPropertyStore;
            var hr = shellItem.GetPropertyStore(
                NativeMethods.GPS_READWRITE,
                ref propertyStoreId,
                out propertyStorePtr);

            if (hr != NativeMethods.S_OK || propertyStorePtr == nint.Zero)
            {
                throw new COMException($"IShellItem2.GetPropertyStore 返回 0x{hr:X8}。", hr);
            }

            WritePropertyStore(propertyStorePtr);
            Log.Information("已通过 IShellItem2.GetPropertyStore 写入 AUMID。路径：{Path}", shortcutPath);
            return true;
        }
        catch (Exception ex)
        {
            errors.Add(ex);
            return false;
        }
        finally
        {
            if (propertyStorePtr != nint.Zero)
            {
                Marshal.Release(propertyStorePtr);
            }

            if (shellItemObject is not null)
            {
                Marshal.FinalReleaseComObject(shellItemObject);
            }
        }
    }

    private static bool TryWriteWithPropertyStoreHandler(string shortcutPath, List<Exception> errors)
    {
        object? shellItemObject = null;
        object? propertyStoreObject = null;
        try
        {
            var shellItemId = NativeMethods.IID_IShellItem;
            NativeMethods.SHCreateItemFromParsingName(
                shortcutPath,
                nint.Zero,
                ref shellItemId,
                out shellItemObject);

            var shellItem = (NativeMethods.IShellItem)shellItemObject;
            var handlerId = NativeMethods.BHID_PropertyStore;
            var propertyStoreId = NativeMethods.IID_IPropertyStore;
            shellItem.BindToHandler(
                nint.Zero,
                handlerId,
                propertyStoreId,
                out propertyStoreObject);

            var propertyStore = (NativeMethods.IPropertyStore)propertyStoreObject;
            WritePropertyStore(propertyStore);
            Log.Information("已通过 PropertyStoreHandler 写入 AUMID。路径：{Path}", shortcutPath);
            return true;
        }
        catch (Exception ex)
        {
            errors.Add(ex);
            return false;
        }
        finally
        {
            if (propertyStoreObject is not null)
            {
                Marshal.FinalReleaseComObject(propertyStoreObject);
            }

            if (shellItemObject is not null)
            {
                Marshal.FinalReleaseComObject(shellItemObject);
            }
        }
    }

    private static void WritePropertyStore(nint propertyStorePtr)
    {
        var propertyStore = (NativeMethods.IPropertyStore)Marshal.GetObjectForIUnknown(propertyStorePtr);
        try
        {
            WritePropertyStore(propertyStore);
        }
        finally
        {
            Marshal.FinalReleaseComObject(propertyStore);
        }
    }

    private static void WritePropertyStore(NativeMethods.IPropertyStore propertyStore)
    {
        var key = NativeMethods.PKEY_AppUserModel_ID;
        var value = Marshal.StringToCoTaskMemUni(Aumid);
        var propVar = new NativeMethods.PropVariant { vt = NativeMethods.VT_LPWSTR, p = value };
        try
        {
            propertyStore.SetValue(ref key, ref propVar);
            propertyStore.Commit();
        }
        finally
        {
            NativeMethods.PropVariantClear(ref propVar);
        }
    }
}
