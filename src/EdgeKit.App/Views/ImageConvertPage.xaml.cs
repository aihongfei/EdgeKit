using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using EdgeKit.Services.Images;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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

public sealed partial class ImageConvertPage : Page
{
    private ImageProcessingService? _imageTools;
    private nint _hwnd;
    private byte[]? _sourceBytes;
    private byte[]? _outputBytes;
    private ImageDescriptor? _sourceDescriptor;
    private ImageDescriptor? _outputDescriptor;
    private string _sourceName = string.Empty;
    private bool _isBusy;
    private bool _isInitialized;

    private const int PreviewDecodePixelWidth = 1200;

    public ImageConvertPage()
    {
        InitializeComponent();
        _isInitialized = true;
        UpdateQualityUi();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is ImageConvertPageParameter parameter)
        {
            _imageTools = parameter.ImageTools;
            _hwnd = parameter.Hwnd;
        }

        ResetAll();

        RootGrid.Focus(FocusState.Programmatic);
    }

    private async void OnPickFileClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (_hwnd == nint.Zero)
        {
            SetStatus("窗口句柄不可用");
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

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        ResetAll();
        SetStatus("已清空");
    }

    private async void OnConvertClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        await ConvertCurrentAsync();
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        await SaveOutputAsync();
    }

    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        await CopyOutputAsync();
    }

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized)
        {
            return;
        }

        UpdateQualityUi();
        if (_sourceBytes is not null)
        {
            _outputBytes = null;
            _outputDescriptor = null;
            UpdateOutputState();
            SetStatus("已切换输出格式，点击转换刷新结果");
        }
    }

    private void OnQualityChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isInitialized)
        {
            return;
        }

        QualityValueText.Text = ((int)Math.Round(e.NewValue)).ToString();
        if (_sourceBytes is not null)
        {
            _outputBytes = null;
            _outputDescriptor = null;
            UpdateOutputState();
            SetStatus("已调整压缩参数，点击转换刷新结果");
        }
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

            // 优先走 StorageItems：真实文件含正确格式，速度最快最可靠
            try
            {
                if (content.Contains(StandardDataFormats.StorageItems))
                {
                    var items = await content.GetStorageItemsAsync();
                    var file = items.OfType<StorageFile>().FirstOrDefault();
                    if (file is not null)
                    {
                        await LoadSourceAsync(await ReadFileBytesAsync(file), file.Name);
                        if (_sourceBytes is not null) return;
                    }
                }
            }
            catch (Exception)
            {
                // StorageItems 失败，继续尝试下一种格式
            }

            // 其次走文本路径（文件路径）
            try
            {
                if (content.Contains(StandardDataFormats.Text))
                {
                    var text = (await content.GetTextAsync()).Trim();
                    if (!string.IsNullOrWhiteSpace(text) && File.Exists(text))
                    {
                        await LoadSourceAsync(await File.ReadAllBytesAsync(text), Path.GetFileName(text));
                        if (_sourceBytes is not null) return;
                    }
                }
            }
            catch (Exception)
            {
                // 文本路径失败，继续尝试下一种格式
            }

            // 最后兜底 Bitmap：先直接试原始字节，只在 ImageSharp 识别失败时归一化
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

                    // 快路径：直接把原始字节交给 ImageSharp
                    if (_imageTools is not null)
                    {
                        var quickCheck = await _imageTools.InspectAsync(rawBytes);
                        if (quickCheck.IsSuccess && quickCheck.Descriptor is not null)
                        {
                            await LoadSourceAsync(rawBytes, "剪贴板图片");
                            return;
                        }
                    }

                    // 慢路径：原始字节无法识别，用新流归一化为 PNG 再试
                    using var normalizeStream = new InMemoryRandomAccessStream();
                    using (var writer = new DataWriter(normalizeStream))
                    {
                        writer.WriteBytes(rawBytes);
                        await writer.StoreAsync();
                        await writer.FlushAsync();
                    }
                    normalizeStream.Seek(0);
                    var pngBytes = await NormalizeClipboardBitmapAsync(normalizeStream);
                    await LoadSourceAsync(pngBytes, "剪贴板图片");
                    return;
                }
            }
            catch (Exception)
            {
                // Bitmap 路径也失败
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
        if (_imageTools is null)
        {
            return;
        }

        SetBusy(true, "正在读取图片...");
        try
        {
            var inspection = await _imageTools.InspectAsync(bytes);
            if (!inspection.IsSuccess || inspection.Descriptor is null)
            {
                ResetAll();
                SetStatus(inspection.Message);
                return;
            }

            _sourceBytes = bytes;
            _sourceName = sourceName;
            _sourceDescriptor = inspection.Descriptor;
            await SetImageSourceAsync(SourceImage, bytes);
            SourcePlaceholderText.Visibility = Visibility.Collapsed;
            SourceMetaText.Text = sourceName;
            SourceInfoText.Text = BuildDescriptorText(inspection.Descriptor);
            _outputBytes = null;
            _outputDescriptor = null;
            UpdateOutputState();
            SetStatus(inspection.Message + "，点击转换生成输出");
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

    private async Task ConvertCurrentAsync()
    {
        if (_imageTools is null || _sourceBytes is null || _sourceDescriptor is null)
        {
            if (_sourceBytes is null)
            {
                SetStatus("请先导入图片");
            }

            return;
        }

        if (_isBusy)
        {
            return;
        }

        SetBusy(true, "正在转换...");
        try
        {
            var result = await _imageTools.ConvertAsync(_sourceBytes, GetSelectedFormat(), (int)Math.Round(QualitySlider.Value));
            if (!result.IsSuccess || result.Output is null)
            {
                _outputBytes = null;
                _outputDescriptor = null;
                UpdateOutputState();
                SetStatus(result.Message);
                return;
            }

            _outputBytes = result.OutputBytes;
            _outputDescriptor = result.Output;
            await SetImageSourceAsync(OutputImage, result.OutputBytes);
            OutputPlaceholderText.Visibility = Visibility.Collapsed;
            OutputMetaText.Text = result.Output.FormatName;
            OutputInfoText.Text = BuildDescriptorText(result.Output);
            UpdateOutputState();
            SetStatus(result.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            _outputBytes = null;
            _outputDescriptor = null;
            UpdateOutputState();
            SetStatus("图片转换失败: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SaveOutputAsync()
    {
        if (_outputBytes is null)
        {
            SetStatus("没有可保存的输出图片");
            return;
        }

        if (_hwnd == nint.Zero)
        {
            SetStatus("窗口句柄不可用");
            return;
        }

        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, _hwnd);
        var suggestedName = string.IsNullOrWhiteSpace(_sourceName)
            ? "image"
            : Path.GetFileNameWithoutExtension(_sourceName);
        var outputFormat = GetSelectedFormat();
        picker.SuggestedFileName = suggestedName + GetFileExtension(outputFormat);
        switch (outputFormat)
        {
            case ImageOutputFormat.Png:
                picker.FileTypeChoices.Add("PNG", new[] { ".png" });
                break;
            case ImageOutputFormat.Jpeg:
                picker.FileTypeChoices.Add("JPG", new[] { ".jpg", ".jpeg" });
                break;
            case ImageOutputFormat.Webp:
                picker.FileTypeChoices.Add("WebP", new[] { ".webp" });
                break;
            case ImageOutputFormat.Bmp:
                picker.FileTypeChoices.Add("BMP", new[] { ".bmp" });
                break;
        }

        try
        {
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            await FileIO.WriteBytesAsync(file, _outputBytes);
            SetStatus("图片已保存");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            SetStatus("保存失败: " + ex.Message);
        }
    }

    private async Task CopyOutputAsync()
    {
        if (_outputBytes is null)
        {
            SetStatus("没有可复制的输出图片");
            return;
        }

        try
        {
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(_outputBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
            }

            stream.Seek(0);
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            Clipboard.SetContent(package);
            SetStatus("图片已复制到剪贴板");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            SetStatus("复制失败: " + ex.Message);
        }
    }

    private void ResetAll()
    {
        _sourceBytes = null;
        _outputBytes = null;
        _sourceDescriptor = null;
        _outputDescriptor = null;
        _sourceName = string.Empty;

        SourceImage.Source = null;
        OutputImage.Source = null;
        SourcePlaceholderText.Visibility = Visibility.Visible;
        OutputPlaceholderText.Visibility = Visibility.Visible;
        SourceMetaText.Text = string.Empty;
        OutputMetaText.Text = string.Empty;
        SourceInfoText.Text = string.Empty;
        OutputInfoText.Text = string.Empty;
        UpdateOutputState();
        UpdateQualityUi();
    }

    private void UpdateOutputState()
    {
        ConvertButton.IsEnabled = !_isBusy && _sourceBytes is not null;
        SaveButton.IsEnabled = !_isBusy && _outputBytes is not null;
        CopyButton.IsEnabled = !_isBusy && _outputBytes is not null;
    }

    private void UpdateQualityUi()
    {
        var format = GetSelectedFormat();
        QualitySlider.IsEnabled = format != ImageOutputFormat.Bmp;
        QualityLabelText.Text = format switch
        {
            ImageOutputFormat.Png => "压缩强度",
            ImageOutputFormat.Jpeg => "质量",
            ImageOutputFormat.Webp => "质量",
            ImageOutputFormat.Bmp => "压缩强度",
            _ => "质量"
        };

        QualityHintText.Text = format switch
        {
            ImageOutputFormat.Png => "PNG 会使用无损压缩，滑杆只影响压缩级别。",
            ImageOutputFormat.Jpeg => "JPG 使用质量值控制压缩与体积。",
            ImageOutputFormat.Webp => "WebP 使用质量值控制压缩与体积。",
            ImageOutputFormat.Bmp => "BMP 不做压缩，滑杆不生效。",
            _ => string.Empty
        };

        QualityValueText.Text = ((int)Math.Round(QualitySlider.Value)).ToString();
    }

    private ImageOutputFormat GetSelectedFormat()
        => (FormatComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()?.ToLowerInvariant() switch
        {
            "jpg" => ImageOutputFormat.Jpeg,
            "webp" => ImageOutputFormat.Webp,
            "bmp" => ImageOutputFormat.Bmp,
            _ => ImageOutputFormat.Png
        };

    private void SetBusy(bool busy, string? status = null)
    {
        _isBusy = busy;
        UpdateOutputState();
        if (!string.IsNullOrWhiteSpace(status))
        {
            SetStatus(status);
        }
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
    }

    private static string BuildDescriptorText(ImageDescriptor descriptor)
        => $"{descriptor.FormatName} · {descriptor.DimensionsText} · {descriptor.FileSizeText}";

    private static string GetFileExtension(ImageOutputFormat format)
        => format switch
        {
            ImageOutputFormat.Png => ".png",
            ImageOutputFormat.Jpeg => ".jpg",
            ImageOutputFormat.Webp => ".webp",
            ImageOutputFormat.Bmp => ".bmp",
            _ => ".img"
        };

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

    private static async Task<byte[]> NormalizeClipboardBitmapAsync(IRandomAccessStream source)
    {
        var decoder = await BitmapDecoder.CreateAsync(source);
        var pixels = await decoder.GetPixelDataAsync();
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(
            decoder.BitmapPixelFormat, BitmapAlphaMode.Premultiplied,
            decoder.PixelWidth, decoder.PixelHeight,
            decoder.DpiX, decoder.DpiY, pixels.DetachPixelData());
        await encoder.FlushAsync();
        output.Seek(0);
        return await ReadStreamBytesAsync(output);
    }

    private static async Task SetImageSourceAsync(Microsoft.UI.Xaml.Controls.Image imageControl, byte[] bytes)
    {
        var bitmap = new BitmapImage
        {
            DecodePixelWidth = PreviewDecodePixelWidth
        };
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
