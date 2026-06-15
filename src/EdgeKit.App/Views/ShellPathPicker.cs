using System;
using System.Runtime.InteropServices;

namespace EdgeKit.App.Views;

/// <summary>Thin Win32 shell picker that returns paths without opening the selected item.</summary>
internal static class ShellPathPicker
{
    private static readonly Guid FileOpenDialogClsid = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");

    private const uint FosPickFolders = 0x00000020;
    private const uint FosForceFileSystem = 0x00000040;
    private const uint FosPathMustExist = 0x00000800;
    private const uint FosFileMustExist = 0x00001000;
    private const uint FosNoValidate = 0x00000100;
    private const uint SigDnFileSysPath = 0x80058000;
    private const int HResultCancelled = unchecked((int)0x800704C7);

    public static string? PickFilePath(nint ownerHwnd)
        => PickPath(ownerHwnd, pickFolder: false);

    public static string? PickFolderPath(nint ownerHwnd)
        => PickPath(ownerHwnd, pickFolder: true);

    private static string? PickPath(nint ownerHwnd, bool pickFolder)
    {
        var dialogType = Type.GetTypeFromCLSID(FileOpenDialogClsid, throwOnError: true)!;
        var dialog = (IFileOpenDialog)Activator.CreateInstance(dialogType)!;
        var options = FosForceFileSystem | FosPathMustExist | FosNoValidate;
        if (pickFolder)
        {
            options |= FosPickFolders;
        }
        else
        {
            options |= FosFileMustExist;
        }

        dialog.SetOptions(options);
        var hr = dialog.Show(ownerHwnd);
        if (hr == HResultCancelled)
        {
            return null;
        }

        Marshal.ThrowExceptionForHR(hr);
        dialog.GetResult(out var item);
        item.GetDisplayName(SigDnFileSysPath, out var pathPtr);
        try
        {
            return Marshal.PtrToStringUni(pathPtr);
        }
        finally
        {
            if (pathPtr != nint.Zero)
            {
                Marshal.FreeCoTaskMem(pathPtr);
            }
        }
    }

    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig]
        int Show(nint parent);

        void SetFileTypes(uint cFileTypes, nint rgFilterSpec);

        void SetFileTypeIndex(uint iFileType);

        void GetFileTypeIndex(out uint piFileType);

        void Advise(nint pfde, out uint pdwCookie);

        void Unadvise(uint dwCookie);

        void SetOptions(uint fos);

        void GetOptions(out uint pfos);

        void SetDefaultFolder(IShellItem psi);

        void SetFolder(IShellItem psi);

        void GetFolder(out IShellItem ppsi);

        void GetCurrentSelection(out IShellItem ppsi);

        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);

        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);

        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);

        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);

        void GetResult(out IShellItem ppsi);

        void AddPlace(IShellItem psi, int fdap);

        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);

        void Close(int hr);

        void SetClientGuid(ref Guid guid);

        void ClearClientData();

        void SetFilter(nint pFilter);

        void GetResults(out nint ppenum);

        void GetSelectedItems(out nint ppsai);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(
            nint pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid bhid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);

        void GetParent(out IShellItem ppsi);

        void GetDisplayName(uint sigdnName, out nint ppszName);

        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        void Compare(IShellItem psi, uint hint, out int piOrder);
    }
}
