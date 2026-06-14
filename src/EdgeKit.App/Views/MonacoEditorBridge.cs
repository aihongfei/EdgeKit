using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace EdgeKit.App.Views;

/// <summary>
/// Wraps a WebView2-hosted Monaco editor and exchanges text through the
/// PostWebMessage channel instead of ExecuteScript string marshaling, so that
/// very large documents can be set/retrieved without freezing the UI thread.
/// </summary>
internal sealed class MonacoEditorBridge
{
    private const string VirtualHost = "edgekit.assets";

    private readonly WebView2 _view;
    private readonly string _htmlFileName;
    private readonly bool _readOnly;

    private bool _initialized;
    private bool _ready;
    private string? _initializationError;
    private TaskCompletionSource<bool> _readySource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<string>? _pendingTextRequest;

    public MonacoEditorBridge(WebView2 view, string htmlFileName, bool readOnly)
    {
        _view = view;
        _htmlFileName = htmlFileName;
        _readOnly = readOnly;
    }

    /// <summary>Raised when the embedded editor reports it is ready.</summary>
    public event Action? Ready;

    /// <summary>Raised (debounced by the editor) when the editable content changes.</summary>
    public event Action<string>? ContentChanged;

    /// <summary>Raised when the user pastes an image into the editor (Ctrl+V).</summary>
    public event Action? ImagePasteRequested;

    public bool IsReady => _ready;

    public async Task InitializeAsync(TimeSpan timeout)
    {
        if (_ready)
        {
            return;
        }

        if (_initialized)
        {
            await WaitUntilReadyAsync(timeout);
            return;
        }

        _initialized = true;
        _initializationError = null;

        // Make the WebView2 surface transparent so the editor's transparent
        // background lets the WinUI acrylic panel show through (no white flash).
        _view.DefaultBackgroundColor = global::Windows.UI.Color.FromArgb(0, 0, 0, 0);

        try
        {
            await _view.EnsureCoreWebView2Async();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            MarkInitializationFailed("WebView2 初始化失败: " + ex.Message);
            throw new InvalidOperationException(_initializationError, ex);
        }

        _view.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        _view.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

        var assetsRoot = Path.Combine(AppContext.BaseDirectory, "Assets");
        var editorPath = Path.Combine(assetsRoot, "Editor", _htmlFileName);
        if (!File.Exists(editorPath))
        {
            MarkInitializationFailed("编辑器资源不存在: " + editorPath);
            throw new FileNotFoundException(_initializationError, editorPath);
        }

        _view.CoreWebView2.SetVirtualHostNameToFolderMapping(
            VirtualHost,
            assetsRoot,
            CoreWebView2HostResourceAccessKind.Allow);

        var query = _readOnly ? "?readonly=1" : string.Empty;
        _view.Source = new Uri($"https://{VirtualHost}/Editor/{_htmlFileName}{query}");

        await WaitUntilReadyAsync(timeout);
    }

    public async Task SetTextAsync(string value)
    {
        await EnsureReadyAsync();
        var payload = JsonSerializer.Serialize(new EditorTextMessage("setText", value ?? string.Empty));
        _view.CoreWebView2.PostWebMessageAsJson(payload);
    }

    public async Task<string> GetTextAsync()
    {
        await EnsureReadyAsync();

        var request = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingTextRequest = request;
        _view.CoreWebView2.PostWebMessageAsJson("{\"type\":\"requestText\"}");

        var finished = await Task.WhenAny(request.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        return finished == request.Task ? request.Task.Result : string.Empty;
    }

    public async Task FocusAsync()
    {
        await EnsureReadyAsync();
        _view.CoreWebView2.PostWebMessageAsJson("{\"type\":\"focus\"}");
    }

    public Task ExecuteScriptAsync(string script) => _view.ExecuteScriptAsync(script).AsTask();

    private async Task EnsureReadyAsync()
    {
        await InitializeAsync(TimeSpan.FromSeconds(8));
        if (!_ready)
        {
            await WaitUntilReadyAsync(TimeSpan.FromSeconds(8));
        }
    }

    private async Task WaitUntilReadyAsync(TimeSpan timeout)
    {
        if (_ready)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_initializationError))
        {
            throw new InvalidOperationException(_initializationError);
        }

        try
        {
            await _readySource.Task.WaitAsync(timeout);
        }
        catch (TimeoutException ex)
        {
            MarkInitializationFailed($"编辑器加载超时（{_htmlFileName}）");
            throw new TimeoutException(_initializationError, ex);
        }
    }

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (args.IsSuccess)
        {
            return;
        }

        MarkInitializationFailed($"编辑器页面加载失败（{_htmlFileName}）: {args.WebErrorStatus}");
    }

    private void MarkInitializationFailed(string message)
    {
        _initializationError = message;
        _ready = false;
        _initialized = false;
        _readySource.TrySetException(new InvalidOperationException(message));
        _readySource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var type = document.RootElement.TryGetProperty("type", out var typeProperty)
                ? typeProperty.GetString()
                : string.Empty;

            switch (type)
            {
                case "ready":
                    _ready = true;
                    _readySource.TrySetResult(true);
                    Ready?.Invoke();
                    break;
                case "text":
                    var value = document.RootElement.TryGetProperty("value", out var valueProperty)
                        ? valueProperty.GetString() ?? string.Empty
                        : string.Empty;
                    _pendingTextRequest?.TrySetResult(value);
                    _pendingTextRequest = null;
                    break;
                case "contentChanged":
                    var changed = document.RootElement.TryGetProperty("value", out var changedProperty)
                        ? changedProperty.GetString() ?? string.Empty
                        : string.Empty;
                    ContentChanged?.Invoke(changed);
                    break;
                case "imagePaste":
                    ImagePasteRequested?.Invoke();
                    break;
                default:
                    break;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed messages from the embedded editor.
        }
    }

    private sealed class EditorTextMessage
    {
        public EditorTextMessage(string type, string value)
        {
            Type = type;
            Value = value;
        }

        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string Type { get; }

        [System.Text.Json.Serialization.JsonPropertyName("value")]
        public string Value { get; }
    }
}