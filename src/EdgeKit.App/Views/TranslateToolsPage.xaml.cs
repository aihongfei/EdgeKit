using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EdgeKit.Core.Agent;
using EdgeKit.Services.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace EdgeKit.App.Views;

/// <summary>翻译工具页：上方输入原文与语言选择，下方左右并排展示有道翻译与大模型翻译结果。</summary>
public sealed partial class TranslateToolsPage : Page
{
    private readonly Dictionary<string, string> _sourceLanguages = new()
    {
        ["自动检测"] = "auto",
        ["中文"] = "zh-CHS",
        ["英语"] = "en",
        ["日语"] = "ja",
        ["韩语"] = "ko",
        ["法语"] = "fr",
        ["西班牙语"] = "es",
        ["俄语"] = "ru",
        ["德语"] = "de"
    };

    private readonly Dictionary<string, string> _targetLanguages = new()
    {
        ["中文"] = "zh-CHS",
        ["英语"] = "en",
        ["日语"] = "ja",
        ["韩语"] = "ko",
        ["法语"] = "fr",
        ["西班牙语"] = "es",
        ["俄语"] = "ru",
        ["德语"] = "de"
    };

    private IAgentService? _agentService;
    private YoudaoTranslationService? _youdaoService;
    private CancellationTokenSource? _translationCts;
    private DispatcherQueueTimer? _copyToastTimer;

    public TranslateToolsPage()
    {
        InitializeComponent();

        _copyToastTimer = DispatcherQueue.CreateTimer();
        _copyToastTimer.Interval = TimeSpan.FromSeconds(1.4);
        _copyToastTimer.IsRepeating = false;
        _copyToastTimer.Tick += OnCopyToastTimerTick;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is TranslateToolsPageParameter parameter)
        {
            _agentService = parameter.AgentService;
            _youdaoService = parameter.Youdao;
        }

        InitializeLanguageBoxes();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _translationCts?.Cancel();
        _translationCts?.Dispose();
        _translationCts = null;
    }

    private void InitializeLanguageBoxes()
    {
        if (SourceLanguageBox.Items.Count > 0)
        {
            return;
        }

        foreach (var name in _sourceLanguages.Keys)
        {
            SourceLanguageBox.Items.Add(name);
        }

        foreach (var name in _targetLanguages.Keys)
        {
            TargetLanguageBox.Items.Add(name);
        }

        SourceLanguageBox.SelectedIndex = 0;
        TargetLanguageBox.SelectedIndex = 1;
    }

    private async void OnTranslateClick(object sender, RoutedEventArgs e)
    {
        var text = SourceTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            YoudaoResultText.Text = "请输入待翻译文本";
            LlmResultText.Text = "请输入待翻译文本";
            return;
        }

        if (TargetLanguageBox.SelectedItem is not string targetName || !_targetLanguages.TryGetValue(targetName, out var targetCode))
        {
            YoudaoResultText.Text = "请选择目标语言";
            LlmResultText.Text = "请选择目标语言";
            return;
        }

        var sourceCode = SourceLanguageBox.SelectedItem is string sourceName
            && _sourceLanguages.TryGetValue(sourceName, out var code)
            ? code
            : "auto";

        _translationCts?.Cancel();
        _translationCts?.Dispose();
        _translationCts = new CancellationTokenSource();
        var token = _translationCts.Token;

        YoudaoResultText.Text = string.Empty;
        LlmResultText.Text = string.Empty;
        YoudaoProgress.IsActive = true;
        YoudaoProgress.Visibility = Visibility.Visible;
        LlmProgress.IsActive = true;
        LlmProgress.Visibility = Visibility.Visible;
        TranslateButton.IsEnabled = false;

        try
        {
            var youdaoTask = TranslateYoudaoAsync(text, sourceCode, targetCode, token);
            var llmTask = TranslateLlmAsync(
                SourceLanguageBox.SelectedItem as string ?? "自动检测",
                targetName,
                text,
                token);

            await Task.WhenAll(youdaoTask, llmTask).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            YoudaoProgress.IsActive = false;
            YoudaoProgress.Visibility = Visibility.Collapsed;
            LlmProgress.IsActive = false;
            LlmProgress.Visibility = Visibility.Collapsed;
            TranslateButton.IsEnabled = true;
        }
    }

    private async Task TranslateYoudaoAsync(string text, string sourceCode, string targetCode, CancellationToken token)
    {
        if (_youdaoService is null)
        {
            YoudaoResultText.Text = "服务未初始化";
            return;
        }

        var result = await _youdaoService.TranslateAsync(text, sourceCode, targetCode, token).ConfigureAwait(true);
        YoudaoResultText.Text = result.Success
            ? result.Translation
            : $"{result.Error}";
    }

    private async Task TranslateLlmAsync(string sourceLanguageName, string targetName, string text, CancellationToken token)
    {
        if (_agentService is null)
        {
            LlmResultText.Text = "服务未初始化";
            return;
        }

        AgentConversation? conversation = null;

        try
        {
            conversation = _agentService.CreateConversation(AgentConversationMode.Translate);
            var prompt = $"请将以下{sourceLanguageName}文本翻译成{targetName}。只输出译文，不要添加解释、不要输出原文：\n\n{text}";
            var builder = new System.Text.StringBuilder();

            await foreach (var ev in _agentService.SendStreamingAsync(conversation.Id, prompt, token).ConfigureAwait(true))
            {
                if (ev.Kind == AgentStreamEventKind.Delta)
                {
                    builder.Append(ev.Delta);
                    LlmResultText.Text = builder.ToString();
                }
                else if (ev.Kind == AgentStreamEventKind.Failed)
                {
                    LlmResultText.Text = $"大模型翻译失败：{ev.ErrorMessage}";
                    return;
                }
            }

            if (builder.Length == 0)
            {
                LlmResultText.Text = "大模型未返回译文";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LlmResultText.Text = $"大模型翻译异常：{ex.Message}";
        }
        finally
        {
            if (conversation is not null)
            {
                try
                {
                    _agentService.DeleteConversation(conversation.Id);
                }
                catch
                {
                    // 清理临时会话失败不影响翻译结果展示。
                }
            }
        }
    }

    private async void OnCopyYoudaoClick(object sender, RoutedEventArgs e)
        => await CopyToClipboardAsync(YoudaoResultText.Text, "有道翻译结果已复制").ConfigureAwait(true);

    private async void OnCopyLlmClick(object sender, RoutedEventArgs e)
        => await CopyToClipboardAsync(LlmResultText.Text, "大模型翻译结果已复制").ConfigureAwait(true);

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        SourceTextBox.Text = string.Empty;
        YoudaoResultText.Text = string.Empty;
        LlmResultText.Text = string.Empty;
    }

    private async Task CopyToClipboardAsync(string text, string successMessage)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            ShowCopyToast(successMessage, success: true);
        }
        catch (Exception ex)
        {
            // 复制失败静默处理，避免打断用户。
            System.Diagnostics.Debug.WriteLine($"复制失败：{ex.Message}");
        }
    }

    private void ShowCopyToast(string text, bool success)
    {
        CopyToastText.Text = text;
        CopyToastIcon.Glyph = success ? "\uE73E" : "\uE711";
        CopyToastIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            success ? "EdgeAccentBrush" : "EdgeMutedBrush"];
        CopyToast.Visibility = Visibility.Visible;

        _copyToastTimer?.Stop();
        _copyToastTimer?.Start();
    }

    private void OnCopyToastTimerTick(DispatcherQueueTimer sender, object args)
    {
        CopyToast.Visibility = Visibility.Collapsed;
    }
}