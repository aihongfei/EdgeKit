using System;
using System.Linq;
using System.IO;
using System.Text.Json;
using EdgeKit.Services.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using WinRT.Interop;

namespace EdgeKit.App.Views;

public sealed partial class TextToolsPage : Page
{
    private static string? _jsonSessionText;

    private enum TextToolTab
    {
        Json,
        Encoding
    }

    private readonly SolidColorBrush _activeTabBackground = new(Color.FromArgb(255, 49, 58, 70));
    private readonly SolidColorBrush _inactiveTabBackground = new(Color.FromArgb(255, 27, 31, 38));
    private readonly SolidColorBrush _activeTabBorder = new(Color.FromArgb(255, 104, 147, 205));
    private readonly SolidColorBrush _inactiveTabBorder = new(Color.FromArgb(255, 55, 62, 72));

    private TextProcessingService? _textTools;
    private TextToolTab _currentTab = TextToolTab.Json;
    private nint _hwnd;
    private byte[]? _inputImageBytes;
    private string _inputImageMimeType = "image/png";
    private string _inputImageExtension = ".png";
    private byte[]? _outputImageBytes;
    private string _outputImageMimeType = "image/png";
    private string _outputImageExtension = ".png";

    private MonacoEditorBridge? _jsonBridge;
    private MonacoEditorBridge? _encodingInputBridge;
    private MonacoEditorBridge? _encodingOutputBridge;
    private bool _encodingEditorsInitialized;

    public TextToolsPage()
    {
        InitializeComponent();
        ApplyTabState();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is TextToolsPageParameter parameter)
        {
            _textTools = parameter.TextTools;
            _hwnd = parameter.Hwnd;
            _currentTab = parameter.ToolId.Equals("text.encode", StringComparison.OrdinalIgnoreCase)
                ? TextToolTab.Encoding
                : TextToolTab.Json;
        }

        ApplyTabState();
        await InitializeEditorAsync();

        if (_currentTab == TextToolTab.Json)
        {
            await RestoreJsonEditorSessionAsync();
        }
        else
        {
            await InitializeEncodingEditorsAsync();
            StatusText.Text = "可输入文本，或粘贴/拖入图片文件";
        }
    }

    protected override async void OnNavigatedFrom(NavigationEventArgs e)
    {
        await CaptureJsonEditorSessionAsync();
        base.OnNavigatedFrom(e);
    }

    private async System.Threading.Tasks.Task InitializeEditorAsync()
    {
        if (_jsonBridge is not null && _jsonBridge.IsReady)
        {
            return;
        }

        try
        {
            _jsonBridge ??= new MonacoEditorBridge(JsonEditor, "json-editor.html", readOnly: false);
            _jsonBridge.Ready += () => StatusText.Text = "JSON 编辑器就绪";
            _jsonBridge.ContentChanged += value => _jsonSessionText = value;
            await _jsonBridge.InitializeAsync(TimeSpan.FromSeconds(8));
            if (!_jsonBridge.IsReady)
            {
                StatusText.Text = "JSON 编辑器初始化超时";
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or FileNotFoundException or System.Runtime.InteropServices.COMException)
        {
            _jsonBridge = null;
            StatusText.Text = "JSON 编辑器初始化失败: " + ex.Message;
        }
    }

    private async System.Threading.Tasks.Task InitializeEncodingEditorsAsync()
    {
        if (_encodingEditorsInitialized
            && _encodingInputBridge?.IsReady == true
            && _encodingOutputBridge?.IsReady == true)
        {
            return;
        }

        _encodingEditorsInitialized = true;
        _encodingInputBridge = new MonacoEditorBridge(EncodingInputEditor, "plain-editor.html", readOnly: false);
        _encodingInputBridge.ContentChanged += _ => { };
        _encodingInputBridge.ImagePasteRequested += async () =>
            await PasteClipboardIntoEncodingAsync(onlyWhenEmpty: false);
        _encodingOutputBridge = new MonacoEditorBridge(EncodingOutputEditor, "plain-editor.html", readOnly: true);

        try
        {
            await System.Threading.Tasks.Task.WhenAll(
                _encodingInputBridge.InitializeAsync(TimeSpan.FromSeconds(8)),
                _encodingOutputBridge.InitializeAsync(TimeSpan.FromSeconds(8)));
        }
        catch
        {
            _encodingEditorsInitialized = false;
            _encodingInputBridge = null;
            _encodingOutputBridge = null;
            throw;
        }
    }

    private async void OnJsonTabClick(object sender, RoutedEventArgs e)
    {
        _currentTab = TextToolTab.Json;
        ApplyTabState();
        await InitializeEditorAsync();
        await RestoreJsonEditorSessionAsync();
    }

    private async void OnEncodingTabClick(object sender, RoutedEventArgs e)
    {
        await CaptureJsonEditorSessionAsync();
        _currentTab = TextToolTab.Encoding;
        ApplyTabState();

        try
        {
            await InitializeEncodingEditorsAsync();
            StatusText.Text = "可输入文本，或粘贴/拖入图片文件";
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException)
        {
            StatusText.Text = "编码编辑器初始化失败: " + ex.Message;
        }
    }

    private async void OnJsonFormatClick(object sender, RoutedEventArgs e)
    {
        if (_textTools is null)
        {
            return;
        }

        var result = _textTools.FormatJson(await GetJsonEditorTextAsync());
        await ApplyJsonResultAsync(result);
    }

    private async void OnJsonCompactClick(object sender, RoutedEventArgs e)
    {
        if (_textTools is null)
        {
            return;
        }

        var result = _textTools.CompactJson(await GetJsonEditorTextAsync());
        await ApplyJsonResultAsync(result);
    }

    private async void OnJsonValidateClick(object sender, RoutedEventArgs e)
    {
        if (_textTools is null)
        {
            return;
        }

        var result = _textTools.ValidateJson(await GetJsonEditorTextAsync());
        StatusText.Text = result.Message;
    }

    private async void OnJsonEscapeClick(object sender, RoutedEventArgs e)
    {
        if (_textTools is null)
        {
            return;
        }

        await SetJsonEditorTextAsync(_textTools.EscapeJsonString(await GetJsonEditorTextAsync()));
        StatusText.Text = "JSON 字符串转义完成";
    }

    private async void OnJsonUnescapeClick(object sender, RoutedEventArgs e)
    {
        if (_textTools is null)
        {
            return;
        }

        var result = _textTools.UnescapeJsonString(await GetJsonEditorTextAsync());
        await ApplyJsonResultAsync(result);
    }

    private async void OnJsonFindClick(object sender, RoutedEventArgs e)
        => await ExecuteEditorScriptAsync("window.edgekitEditor && window.edgekitEditor.openFind();");

    private async void OnJsonReplaceClick(object sender, RoutedEventArgs e)
        => await ExecuteEditorScriptAsync("window.edgekitEditor && window.edgekitEditor.openReplace();");

    private async void OnJsonPasteClick(object sender, RoutedEventArgs e)
        => await PasteClipboardIntoJsonAsync(onlyWhenEmpty: false);

    private async void OnJsonCopyClick(object sender, RoutedEventArgs e)
    {
        CopyText(await GetJsonEditorTextAsync());
        StatusText.Text = "JSON 已复制";
    }

    private async void OnJsonClearClick(object sender, RoutedEventArgs e)
    {
        await SetJsonEditorTextAsync(string.Empty);
        StatusText.Text = "已清空";
    }

    private async void OnUrlEncodeClick(object sender, RoutedEventArgs e)
    {
        if (_textTools is null)
        {
            return;
        }

        var input = await GetEncodingInputTextAsync();
        await SetEncodingTextOutputAsync(_textTools.UrlEncode(input));
        StatusText.Text = "URL 编码完成";
    }

    private async void OnUrlDecodeClick(object sender, RoutedEventArgs e)
    {
        if (_textTools is null)
        {
            return;
        }

        var input = await GetEncodingInputTextAsync();
        await SetEncodingTextOutputAsync(_textTools.UrlDecode(input));
        StatusText.Text = "URL 解码完成";
    }

    private async void OnBase64EncodeClick(object sender, RoutedEventArgs e)
    {
        if (_textTools is null)
        {
            return;
        }

        if (_inputImageBytes is not null)
        {
            await ApplyImageBytesAsBase64Async(_inputImageBytes, "输入图片");
            return;
        }

        var input = await GetEncodingInputTextAsync();
        await SetEncodingTextOutputAsync(_textTools.Base64Encode(input));
        StatusText.Text = "Base64 文本编码完成";
    }

    private async void OnBase64DecodeClick(object sender, RoutedEventArgs e)
    {
        if (_textTools is null)
        {
            return;
        }

        await DecodeBase64AutomaticallyAsync();
    }

    private async void OnEncodingCopyClick(object sender, RoutedEventArgs e)
    {
        if (_outputImageBytes is not null)
        {
            await CopyOutputImageFileAsync();
            return;
        }

        var output = _encodingOutputBridge is null ? string.Empty : await _encodingOutputBridge.GetTextAsync();
        if (string.IsNullOrEmpty(output))
        {
            StatusText.Text = "没有可复制内容";
            return;
        }

        CopyText(output);
        StatusText.Text = "输出已复制";
    }

    private async void OnEncodingPasteClick(object sender, RoutedEventArgs e)
        => await PasteClipboardIntoEncodingAsync(onlyWhenEmpty: false);

    private async void OnEncodingClearClick(object sender, RoutedEventArgs e)
    {
        if (_encodingInputBridge is not null)
        {
            await _encodingInputBridge.SetTextAsync(string.Empty);
        }

        ClearInputImage();
        await SetEncodingTextOutputAsync(string.Empty);
        StatusText.Text = "已清空";
    }

    private async System.Threading.Tasks.Task CopyOutputImageFileAsync()
    {
        if (_outputImageBytes is null)
        {
            StatusText.Text = "没有可复制的图片";
            return;
        }

        try
        {
            var tempFile = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
                "edgekit-image" + _outputImageExtension,
                CreationCollisionOption.GenerateUniqueName);
            await FileIO.WriteBytesAsync(tempFile, _outputImageBytes);

            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetStorageItems(new[] { tempFile });
            Clipboard.SetContent(package);
            StatusText.Text = "图片文件已复制";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            StatusText.Text = "复制图片失败: " + ex.Message;
        }
    }

    private void OnEncodingInputDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
    }

    private async void OnEncodingInputDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var file = items.OfType<StorageFile>().FirstOrDefault();
            if (file is null)
            {
                StatusText.Text = "未找到可用图片文件";
                return;
            }

            await LoadImageFileIntoInputAsync(file);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Runtime.InteropServices.COMException)
        {
            StatusText.Text = "读取拖入文件失败: " + ex.Message;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async System.Threading.Tasks.Task ApplyJsonResultAsync(TextToolResult result)
    {
        if (result.IsSuccess)
        {
            await SetJsonEditorTextAsync(result.Output);
        }

        StatusText.Text = result.Message;
    }

    private async System.Threading.Tasks.Task<string> GetJsonEditorTextAsync()
    {
        await InitializeEditorAsync();
        return _jsonBridge is null ? string.Empty : await _jsonBridge.GetTextAsync();
    }

    private async System.Threading.Tasks.Task SetJsonEditorTextAsync(string value)
    {
        await InitializeEditorAsync();
        _jsonSessionText = value;
        if (_jsonBridge is not null)
        {
            await _jsonBridge.SetTextAsync(value);
        }
    }

    private async System.Threading.Tasks.Task ExecuteEditorScriptAsync(string script)
    {
        await InitializeEditorAsync();
        if (_jsonBridge is not null)
        {
            await _jsonBridge.ExecuteScriptAsync(script);
        }
    }

    private async System.Threading.Tasks.Task PasteClipboardIntoJsonAsync(bool onlyWhenEmpty)
    {
        try
        {
            if (onlyWhenEmpty && !string.IsNullOrEmpty(await GetJsonEditorTextAsync()))
            {
                return;
            }

            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                await SetJsonEditorTextAsync(text);
                if (_textTools is not null)
                {
                    var result = _textTools.FormatJson(text);
                    if (result.IsSuccess)
                    {
                        await SetJsonEditorTextAsync(result.Output);
                    }
                }

                StatusText.Text = "已粘贴剪贴板文本";
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            StatusText.Text = "读取剪贴板失败: " + ex.Message;
        }
    }

    private async System.Threading.Tasks.Task RestoreJsonEditorSessionAsync()
    {
        if (_jsonSessionText is null)
        {
            StatusText.Text = "JSON 编辑器就绪";
            return;
        }

        var currentText = await GetJsonEditorTextAsync();
        if (!string.Equals(currentText, _jsonSessionText, StringComparison.Ordinal))
        {
            await SetJsonEditorTextAsync(_jsonSessionText);
        }

        StatusText.Text = string.IsNullOrEmpty(_jsonSessionText)
            ? "JSON 编辑器就绪"
            : "已恢复 JSON 内容";
    }

    private async System.Threading.Tasks.Task CaptureJsonEditorSessionAsync()
    {
        if (_jsonBridge is null || !_jsonBridge.IsReady)
        {
            return;
        }

        try
        {
            _jsonSessionText = await GetJsonEditorTextAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            StatusText.Text = "暂未能保存 JSON 内容: " + ex.Message;
        }
    }

    private async System.Threading.Tasks.Task PasteClipboardIntoEncodingAsync(bool onlyWhenEmpty)
    {
        if (onlyWhenEmpty && _encodingInputBridge is not null
            && !string.IsNullOrEmpty(await _encodingInputBridge.GetTextAsync()))
        {
            return;
        }

        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Bitmap))
            {
                var bitmapReference = await content.GetBitmapAsync();
                using var stream = await bitmapReference.OpenReadAsync();
                var bytes = await ReadStreamBytesAsync(stream);
                await SetInputImageAsync(bytes, "剪贴板图片");
                return;
            }

            if (content.Contains(StandardDataFormats.StorageItems))
            {
                var items = await content.GetStorageItemsAsync();
                var file = items.OfType<StorageFile>().FirstOrDefault();
                if (file is not null)
                {
                    await LoadImageFileIntoInputAsync(file);
                    return;
                }
            }

            if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                if (_encodingInputBridge is not null)
                {
                    await _encodingInputBridge.SetTextAsync(text);
                }

                ClearInputImage();
                await SetEncodingTextOutputAsync(string.Empty);
                StatusText.Text = "已读取剪贴板文本";
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException or System.Runtime.InteropServices.COMException)
        {
            StatusText.Text = "读取剪贴板失败: " + ex.Message;
        }
    }

    private async System.Threading.Tasks.Task LoadImageFileIntoInputAsync(StorageFile file)
    {
        using var stream = await file.OpenReadAsync();
        var bytes = await ReadStreamBytesAsync(stream);
        await SetInputImageAsync(bytes, file.Name);
    }

    private async System.Threading.Tasks.Task ApplyImageBytesAsBase64Async(byte[] bytes, string sourceName)
    {
        if (_textTools is null)
        {
            return;
        }

        SetEncodingLoading(true, $"{sourceName} 正在转 Base64...");
        var result = await System.Threading.Tasks.Task.Run(() => _textTools.ImageToBase64(bytes));
        if (!result.IsSuccess)
        {
            SetEncodingLoading(false);
            StatusText.Text = result.Message;
            return;
        }

        var details = await BuildImageDetailsAsync(result.Bytes, result.MimeType);
        SetEncodingLoading(false);
        SetEncodingOutputLoading(true, "正在写入完整 Base64...");
        await SetEncodingTextOutputAsync(result.Output);
        SetEncodingOutputLoading(false);
        StatusText.Text = $"{sourceName} 已转 Base64（{details}）";
    }

    private async System.Threading.Tasks.Task SetInputImageAsync(byte[] bytes, string sourceName)
    {
        if (_textTools is null)
        {
            return;
        }

        SetEncodingLoading(true, "正在读取图片...");
        var result = await System.Threading.Tasks.Task.Run(() => _textTools.ImageToBase64(bytes));
        if (!result.IsSuccess)
        {
            SetEncodingLoading(false);
            StatusText.Text = result.Message;
            return;
        }

        _inputImageBytes = result.Bytes;
        _inputImageMimeType = result.MimeType;
        _inputImageExtension = result.Extension;

        var bitmap = await CreateBitmapImageAsync(result.Bytes);
        EncodingInputImagePreview.Source = bitmap;
        EncodingInputImageInfo.Text = sourceName + "，" + await BuildImageDetailsAsync(result.Bytes, result.MimeType, bitmap);
        if (_encodingInputBridge is not null)
        {
            await _encodingInputBridge.SetTextAsync(string.Empty);
        }

        EncodingInputEditor.Visibility = Visibility.Collapsed;
        EncodingInputImagePanel.Visibility = Visibility.Visible;
        await SetEncodingTextOutputAsync(string.Empty);
        SetEncodingLoading(false);
        StatusText.Text = "图片已放入输入区，点击 Base64 转";
    }

    private async System.Threading.Tasks.Task SetImagePreviewAsync(ImageBase64Result result)
    {
        _outputImageBytes = result.Bytes;
        _outputImageMimeType = result.MimeType;
        _outputImageExtension = result.Extension;

        var bitmap = await CreateBitmapImageAsync(result.Bytes);
        DecodedImagePreview.Source = bitmap;
        DecodedImageInfo.Text = await BuildImageDetailsAsync(result.Bytes, result.MimeType, bitmap);
        if (_encodingOutputBridge is not null)
        {
            await _encodingOutputBridge.SetTextAsync(string.Empty);
        }

        EncodingOutputEditor.Visibility = Visibility.Collapsed;
        ImagePreviewPanel.Visibility = Visibility.Visible;
    }

    private async System.Threading.Tasks.Task SetEncodingTextOutputAsync(string text)
    {
        _outputImageBytes = null;
        DecodedImagePreview.Source = null;
        DecodedImageInfo.Text = string.Empty;
        ImagePreviewPanel.Visibility = Visibility.Collapsed;
        EncodingOutputEditor.Visibility = Visibility.Visible;
        if (_encodingOutputBridge is not null)
        {
            await _encodingOutputBridge.SetTextAsync(text);
        }
    }

    private async System.Threading.Tasks.Task DecodeBase64AutomaticallyAsync()
    {
        if (_textTools is null)
        {
            return;
        }

        var input = await GetEncodingInputTextAsync();
        SetEncodingLoading(true, "正在自动识别 Base64...");
        var imageResult = await System.Threading.Tasks.Task.Run(() => _textTools.Base64ToImage(input));
        if (imageResult.IsSuccess)
        {
            await SetImagePreviewAsync(imageResult);
            SetEncodingLoading(false);
            StatusText.Text = imageResult.Message;
            return;
        }

        var textResult = await System.Threading.Tasks.Task.Run(() => _textTools.Base64Decode(input));
        if (textResult.IsSuccess)
        {
            await SetEncodingTextOutputAsync(textResult.Output);
            StatusText.Text = "Base64 文本解码完成";
        }
        else
        {
            StatusText.Text = textResult.Message;
        }

        SetEncodingLoading(false);
    }

    private async System.Threading.Tasks.Task<string> GetEncodingInputTextAsync()
        => _encodingInputBridge is null ? string.Empty : await _encodingInputBridge.GetTextAsync();

    private async System.Threading.Tasks.Task DecodeImageBase64Async()
    {
        if (_textTools is null)
        {
            return;
        }

        SetEncodingLoading(true, "正在解析图片 Base64...");
        var input = await GetEncodingInputTextAsync();
        var result = await System.Threading.Tasks.Task.Run(() => _textTools.Base64ToImage(input));
        if (result.IsSuccess)
        {
            await SetImagePreviewAsync(result);
        }

        SetEncodingLoading(false);
        StatusText.Text = result.Message;
    }

    private void SetEncodingLoading(bool isLoading, string? message = null)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            EncodingLoadingText.Text = message;
        }

        EncodingProgressRing.IsActive = isLoading;
        EncodingLoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetEncodingOutputLoading(bool isLoading, string? message = null)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            EncodingOutputLoadingText.Text = message;
        }

        EncodingOutputProgressRing.IsActive = isLoading;
        EncodingOutputLoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearInputImage()
    {
        _inputImageBytes = null;
        EncodingInputImagePreview.Source = null;
        EncodingInputImageInfo.Text = string.Empty;
        EncodingInputImagePanel.Visibility = Visibility.Collapsed;
        EncodingInputEditor.Visibility = Visibility.Visible;
    }

    private static async System.Threading.Tasks.Task<byte[]> ReadStreamBytesAsync(IRandomAccessStream stream)
    {
        if (stream.Size > int.MaxValue)
        {
            throw new IOException("文件过大");
        }

        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static async System.Threading.Tasks.Task<BitmapImage> CreateBitmapImageAsync(byte[] bytes)
    {
        var image = new BitmapImage();
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
        }

        stream.Seek(0);
        await image.SetSourceAsync(stream);
        return image;
    }

    private static async System.Threading.Tasks.Task<string> BuildImageDetailsAsync(
        byte[] bytes,
        string mimeType,
        BitmapImage? bitmap = null)
    {
        bitmap ??= await CreateBitmapImageAsync(bytes);
        var dimensions = bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0
            ? $"{bitmap.PixelWidth} x {bitmap.PixelHeight}，"
            : string.Empty;
        return $"{mimeType}，{dimensions}{FormatByteSize(bytes.Length)}";
    }

    private static string FormatByteSize(int byteCount)
    {
        if (byteCount < 1024)
        {
            return byteCount + " B";
        }

        var kib = byteCount / 1024d;
        if (kib < 1024)
        {
            return kib.ToString("0.#") + " KB";
        }

        return (kib / 1024d).ToString("0.##") + " MB";
    }

    private static string GetImageTypeLabel(string mimeType)
        => mimeType switch
        {
            "image/jpeg" => "JPEG 图片",
            "image/webp" => "WEBP 图片",
            "image/gif" => "GIF 图片",
            "image/bmp" => "BMP 图片",
            _ => "PNG 图片"
        };

    private void ApplyTabState()
    {
        var jsonActive = _currentTab == TextToolTab.Json;
        SubtitleText.Text = jsonActive ? "JSON 编辑器" : "URL / Base64 编码转换";

        JsonPanel.Visibility = jsonActive ? Visibility.Visible : Visibility.Collapsed;
        JsonToolbar.Visibility = jsonActive ? Visibility.Visible : Visibility.Collapsed;
        EncodingPanel.Visibility = jsonActive ? Visibility.Collapsed : Visibility.Visible;

        StyleTab(JsonTabButton, jsonActive);
        StyleTab(EncodingTabButton, !jsonActive);
    }

    private void StyleTab(Button button, bool active)
    {
        button.Background = active ? _activeTabBackground : _inactiveTabBackground;
        button.BorderBrush = active ? _activeTabBorder : _inactiveTabBorder;
        button.Foreground = active
            ? new SolidColorBrush(Colors.White)
            : (Brush)Application.Current.Resources["EdgeMutedBrush"];
    }

    private static void CopyText(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
