using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace EdgeKit.App.Search;

public enum EverythingSearchState
{
    Disabled,
    Loading,
    Searching,
    Ready,
    MissingBridge
}

public sealed class EverythingSearchService
{
    private const int SearchTimeoutMs = 1000;
    private const int MaxResults = 20;

    private readonly string _bridgePath;
    private EverythingSearchState _state = EverythingSearchState.Disabled;
    private bool _enabled;

    public EverythingSearchService()
    {
        _bridgePath = ResolveBridgePath();
    }

    public event EventHandler? StateChanged;

    public EverythingSearchState State => _state;

    public bool IsBusy => _state is EverythingSearchState.Loading or EverythingSearchState.Searching;

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;

        if (enabled)
        {
            SetState(IsEverythingRunning()
                ? EverythingSearchState.Loading
                : EverythingSearchState.MissingBridge);
            return;
        }

        SetState(EverythingSearchState.Disabled);
    }

    public async Task<IReadOnlyList<SearchItem>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (_state == EverythingSearchState.Disabled)
        {
            return Array.Empty<SearchItem>();
        }

        var normalized = NormalizeQuery(query);
        if (normalized.Length == 0)
        {
            return Array.Empty<SearchItem>();
        }

        var wasReady = _state == EverythingSearchState.Ready;

        if (!IsEverythingRunning())
        {
            if (!wasReady)
            {
                SetState(EverythingSearchState.MissingBridge);
            }
            return Array.Empty<SearchItem>();
        }

        if (!File.Exists(_bridgePath))
        {
            if (!wasReady)
            {
                SetState(EverythingSearchState.MissingBridge);
            }
            return Array.Empty<SearchItem>();
        }

        if (!wasReady)
        {
            SetState(EverythingSearchState.Searching);
        }

        var tempCsv = Path.Combine(Path.GetTempPath(), $"edgekit-everything-{Guid.NewGuid():N}.csv");
        Process? process = null;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _bridgePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-timeout");
            startInfo.ArgumentList.Add(SearchTimeoutMs.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add(MaxResults.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-export-csv");
            startInfo.ArgumentList.Add(tempCsv);
            startInfo.ArgumentList.Add("-utf8-bom");
            startInfo.ArgumentList.Add(normalized);

            process = Process.Start(startInfo);
            if (process is null)
            {
                if (!wasReady)
                {
                    SetState(EverythingSearchState.MissingBridge);
                }
                return Array.Empty<SearchItem>();
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (!wasReady)
            {
                SetState(EverythingSearchState.Ready);
            }

            if (!File.Exists(tempCsv))
            {
                return Array.Empty<SearchItem>();
            }

            var items = ReadResults(tempCsv);
            return items;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch
        {
            if (!wasReady)
            {
                SetState(EverythingSearchState.MissingBridge);
            }
            return Array.Empty<SearchItem>();
        }
        finally
        {
            TryDelete(tempCsv);
        }
    }

    private static string NormalizeQuery(string query)
        => string.IsNullOrWhiteSpace(query) ? string.Empty : query.Trim().Trim('"');

    private static string ResolveBridgePath()
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        var folder = architecture switch
        {
            Architecture.Arm64 => "win-arm64",
            _ => "win-x64"
        };

        return Path.Combine(AppContext.BaseDirectory, "Assets", "Everything", folder, "es.exe");
    }

    private static IReadOnlyList<SearchItem> ReadResults(string csvPath)
    {
        var results = new List<SearchItem>();

        using var parser = new TextFieldParser(csvPath, Encoding.UTF8)
        {
            TextFieldType = FieldType.Delimited,
            Delimiters = new[] { "," },
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };

        if (!parser.EndOfData)
        {
            parser.ReadFields();
        }

        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields();
            if (fields is null || fields.Length == 0)
            {
                continue;
            }

            var fullPath = fields[0];
            var item = CreateSearchItem(fullPath);
            if (item is not null)
            {
                results.Add(item);
            }
        }

        return results;
    }

    private static SearchItem? CreateSearchItem(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return null;
        }

        var path = fullPath.Trim();
        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
        {
            return null;
        }

        var trimmedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var title = Path.GetFileName(trimmedPath);
        if (string.IsNullOrWhiteSpace(title))
        {
            title = path;
        }

        return new SearchItem(
            isDirectory ? SearchItemKind.Folder : SearchItemKind.File,
            title,
            path,
            isDirectory ? "\uE8B7" : "\uE8A5",
            iconImage: null,
            payload: path,
            locationPath: path);
    }

    private void SetState(EverythingSearchState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static bool IsEverythingRunning()
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (process.ProcessName.StartsWith("Everything", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }
}
