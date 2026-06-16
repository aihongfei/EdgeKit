using System;
using System.ComponentModel;
using System.Diagnostics;
using Serilog;

namespace EdgeKit.App.Interaction;

public static class ElevatedProcessLauncher
{
    public static bool Start(string fileName, string arguments = "", string workingDirectory = "")
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas"
            };

            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                info.WorkingDirectory = workingDirectory;
            }

            Process.Start(info);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Information("用户取消管理员授权 file={FileName}", fileName);
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Warning(ex, "管理员方式启动失败 file={FileName}", fileName);
            return false;
        }
    }
}
