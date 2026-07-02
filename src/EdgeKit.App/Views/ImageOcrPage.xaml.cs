using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using EdgeKit.Core.Clipboard;
using EdgeKit.Services.Images;
using EdgeKit.Services.Ocr;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace EdgeKit.App.Views;

/// <summary>
/// 图片识别文字工具页。支持选择文件、粘贴图片、识别文字、复制结果、保存到剪贴板历史。
/// </summary>
public sealed partial class ImageOcrPage : Page
{
    private IOcrService? _ocrService;
    private IClipboardRepository? _clipboardRepository;
    private nint _hwnd;
    private byte[]? _sourceBytes;
    private string _sourceName = string.Empty;
    private string _recognizedText = string.Empty;
    private bool _isBusy;

    private const int PreviewDecodePixelWidth = 1200;

    public ImageOcrPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is ImageOcrPageParameter parameter)
        {
            _ocrService = parameter.OcrService;
            _clipboardRepository = parameter.ClipboardRepository;
            _hwnd = parameter.Hwnd;
        }

        ResetAll();
        RootGrid.Focus(FocusState.Programmatic);
    }

    private async void OnPickFileClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _hwnd == nint.Zero)
        {
            if (_hwnd == nint.Zero)
            {
                SetStatus("窗口句柄不可用");
            }

            return;
        }

        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, _hwnd);
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".webp");
        picker.FileTypeFilter.Add(".bmp");
        picker.FileTypeFilter.Add(".gif");

        try
        {
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            await LoadSourceAsync(await ReadFileBytesAsync(file), file.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            SetStatus("读取图片失败: " + ex.Message);
        }
    }

    private async void OnPasteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_isBusy)
        {
            return;
        }

        await PasteFromClipboardAsync();
    }

    private async void OnPasteClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        await PasteFromClipboardAsync();
    }

    private async void OnRecognizeClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        await RecognizeCurrentAsync();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_recognizedText))
        {
            SetStatus("没有可复制的识别结果");
            return;
        }

        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(_recognizedText);
            Clipboard.SetContent(package);
            SetStatus("识别结果已复制到剪贴板");
        }
        catch (Exception ex)
        {
            SetStatus("复制失败: " + ex.Message);
        }
    }

    private void OnSaveHistoryClick(object sender, RoutedEventArgs e)
    {
        if (_clipboardRepository is null)
        {
            SetStatus("剪贴板仓储未初始化");
            return;
        }

        if (string.IsNullOrWhiteSpace(_recognizedText))
        {
            SetStatus("没有可保存的识别结果");
            return;
        }

        try
        {
            var item = new ClipboardItem(
                0,
                ClipboardItemKind.Text,
                BuildPreview(_recognizedText),
                _recognizedText,
                ImagePath: null,
                Files: null,
                GroupId: null,
                Pinned: false,
                SourceAppName: "OCR 识别",
                SourceProcessPath: string.Empty,
                Hash: ComputeHash("text:" + _recognizedText),
                CreatedUtc: DateTime.UtcNow);

            _clipboardRepository.Add(item);
            SetStatus("已保存到剪贴板历史");
        }
        catch (Exception ex)
        {
            SetStatus("保存失败: " + ex.Message);
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        ResetAll();
        SetStatus("已清空");
    }

    private async Task PasteFromClipboardAsync()
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            var content = Clipboard.GetContent();

            try
            {
                if (content.Contains(StandardDataFormats.StorageItems))
                {
                    var items = await content.GetStorageItemsAsync();
                    var file = items.OfType<StorageFile>().FirstOrDefault();
                    if (file is not null)
                    {
                        await LoadSourceAsync(await ReadFileBytesAsync(file), file.Name);
                        if (_sourceBytes is not null)
                        {
                            return;
                        }
                    }
                }
            }
            catch
            {
                // 继续尝试其他格式
            }

            try
            {
                if (content.Contains(StandardDataFormats.Text))
                {
                    var text = (await content.GetTextAsync()).Trim();
                    if (!string.IsNullOrWhiteSpace(text) && File.Exists(text))
                    {
                        await LoadSourceAsync(await File.ReadAllBytesAsync(text), Path.GetFileName(text));
                        if (_sourceBytes is not null)
                        {
                            return;
                        }
                    }
                }
            }
            catch
            {
                // 继续尝试其他格式
            }

            try
            {
                if (content.Contains(StandardDataFormats.Bitmap))
                {
                    var reference = await content.GetBitmapAsync();
                    byte[] rawBytes;
                    using (var bitmapStream = await reference.OpenReadAsync())
                    {
                        rawBytes = await ReadStreamBytesAsync(bitmapStream);
                    }

                    if (LooksLikeImage(rawBytes))
                    {
                        await LoadSourceAsync(rawBytes, "剪贴板图片");
                        return;
                    }

                    var pngBytes = await NormalizeClipboardBitmapAsync(rawBytes);
                    await LoadSourceAsync(pngBytes, "剪贴板图片");
                    return;
                }
            }
            catch
            {
                // Bitmap 路径失败
            }

            SetStatus("剪贴板里没有可用图片");
        }
        catch (Exception ex)
        {
            SetStatus("读取剪贴板失败: " + ex.Message);
        }
    }

    private async Task LoadSourceAsync(byte[] bytes, string sourceName)
    {
        SetBusy(true, "正在读取图片...");
        try
        {
            var inspection = await new ImageProcessingService().InspectAsync(bytes);
            if (!inspection.IsSuccess || inspection.Descriptor is null)
            {
                ResetAll();
                SetStatus(inspection.Message);
                return;
            }

            _sourceBytes = bytes;
            _sourceName = sourceName;
            await SetImageSourceAsync(SourceImage, bytes);
            SourcePlaceholderText.Visibility = Visibility.Collapsed;
            SourceInfoText.Text = $"{inspection.Descriptor.FormatName} · {inspection.Descriptor.DimensionsText} · {inspection.Descriptor.FileSizeText}";
            _recognizedText = string.Empty;
            ResultTextBox.Text = string.Empty;
            ResultPlaceholderText.Visibility = Visibility.Visible;
            ResultInfoText.Text = string.Empty;
            UpdateActionState();
            SetStatus(inspection.Message + "，点击识别提取文字");
        }
        catch (Exception ex)
        {
            ResetAll();
            SetStatus("读取图片失败: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RecognizeCurrentAsync()
    {
        if (_ocrService is null || _sourceBytes is null)
        {
            SetStatus(_sourceBytes is null ? "请先导入图片" : "OCR 服务未初始化");
            return;
        }

        SetBusy(true, "正在识别文字...");
        try
        {
            var result = await _ocrService.RecognizeAsync(_sourceBytes);
            _recognizedText = result.Text;
            ResultTextBox.Text = result.Text;
            ResultPlaceholderText.Visibility = string.IsNullOrWhiteSpace(result.Text) ? Visibility.Visible : Visibility.Collapsed;
            ResultInfoText.Text = result.IsSuccess ? result.Message : string.Empty;
            UpdateActionState();
            SetStatus(result.IsSuccess ? result.Message : result.Message);
        }
        catch (Exception ex)
        {
            SetStatus("识别失败: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ResetAll()
    {
        _sourceBytes = null;
        _sourceName = string.Empty;
        _recognizedText = string.Empty;

        SourceImage.Source = null;
        SourcePlaceholderText.Visibility = Visibility.Visible;
        SourceInfoText.Text = string.Empty;

        ResultTextBox.Text = string.Empty;
        ResultPlaceholderText.Visibility = Visibility.Visible;
        ResultInfoText.Text = string.Empty;

        UpdateActionState();
    }

    private void UpdateActionState()
    {
        RecognizeButton.IsEnabled = !_isBusy && _sourceBytes is not null;
        CopyButton.IsEnabled = !_isBusy && !string.IsNullOrWhiteSpace(_recognizedText);
        SaveHistoryButton.IsEnabled = !_isBusy && !string.IsNullOrWhiteSpace(_recognizedText);
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _isBusy = busy;
        UpdateActionState();
        if (!string.IsNullOrWhiteSpace(status))
        {
            SetStatus(status);
        }
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
    }

    private static bool LooksLikeImage(byte[] bytes)
    {
        if (bytes.Length < 8)
        {
            return false;
        }

        // PNG
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return true;
        }

        // JPEG
        if (bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            return true;
        }

        // BMP
        if (bytes[0] == 0x42 && bytes[1] == 0x4D)
        {
            return true;
        }

        // GIF
        if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
        {
            return true;
        }

        // WebP
        if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return true;
        }

        return false;
    }

    private static string BuildPreview(string text)
    {
        var single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        const int maxLength = 200;
        return single.Length > maxLength ? single[..maxLength] + "…" : single;
    }

    private static string ComputeHash(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<byte[]> ReadFileBytesAsync(StorageFile file)
    {
        using var stream = await file.OpenReadAsync();
        return await ReadStreamBytesAsync(stream);
    }

    private static async Task<byte[]> ReadStreamBytesAsync(IRandomAccessStream stream)
    {
        if (stream.Size > int.MaxValue)
        {
            throw new IOException("文件过大");
        }

        var bytes = new byte[(int)stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static async Task<byte[]> NormalizeClipboardBitmapAsync(byte[] rawBytes)
    {
        using var sourceStream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(sourceStream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(rawBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
        }

        sourceStream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(sourceStream);
        var pixels = await decoder.GetPixelDataAsync();

        using var outputStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outputStream);
        encoder.SetPixelData(
            decoder.BitmapPixelFormat,
            BitmapAlphaMode.Premultiplied,
            decoder.PixelWidth,
            decoder.PixelHeight,
            decoder.DpiX,
            decoder.DpiY,
            pixels.DetachPixelData());
        await encoder.FlushAsync();

        outputStream.Seek(0);
        return await ReadStreamBytesAsync(outputStream);
    }

    private static async Task SetImageSourceAsync(Microsoft.UI.Xaml.Controls.Image imageControl, byte[] bytes)
    {
        var bitmap = new BitmapImage { DecodePixelWidth = PreviewDecodePixelWidth };
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
        }

        stream.Seek(0);
        await bitmap.SetSourceAsync(stream);
        imageControl.Source = bitmap;
    }
}