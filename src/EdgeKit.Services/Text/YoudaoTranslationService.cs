using System;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EdgeKit.Core.Services;
using EdgeKit.Services.Settings;

namespace EdgeKit.Services.Text;

/// <summary>
/// 有道翻译 API（openapi.youdao.com/api，signType=v3）封装。
/// 从设置中读取并解密 AppKey / AppSecret；调用失败时返回可读错误信息。
/// </summary>
public sealed class YoudaoTranslationService
{
    private readonly HttpClient _http;
    private readonly ISettingsService _settings;

    public YoudaoTranslationService(HttpClient http, ISettingsService settings)
    {
        _http = http;
        _settings = settings;
    }

    /// <summary>
    /// 翻译文本。
    /// </summary>
    /// <param name="text">待翻译文本。</param>
    /// <param name="from">源语言代码，如 "auto"、"zh-CHS"、"en"；null 或空表示 auto。</param>
    /// <param name="to">目标语言代码，如 "zh-CHS"、"en"、"ja"。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>翻译结果；失败时返回包含错误文案的结果。</returns>
    public async Task<YoudaoResult> TranslateAsync(
        string text,
        string? from,
        string to,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new YoudaoResult(false, string.Empty, "请输入待翻译文本");
        }

        var appKey = GetAppKey();
        var appSecret = GetAppSecret();

        if (string.IsNullOrWhiteSpace(appKey) || string.IsNullOrWhiteSpace(appSecret))
        {
            return new YoudaoResult(false, string.Empty, "请先在设置中配置有道 App Key 和 App Secret");
        }

        var salt = Guid.NewGuid().ToString("N")[..16];
        var curtime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signInput = BuildSignInput(text, appKey, salt, curtime, appSecret);

        var query = new StringBuilder();
        query.Append("q=").Append(Uri.EscapeDataString(text));
        query.Append("\u0026from=").Append(Uri.EscapeDataString(string.IsNullOrWhiteSpace(from) ? "auto" : from));
        query.Append("\u0026to=").Append(Uri.EscapeDataString(to));
        query.Append("\u0026appKey=").Append(Uri.EscapeDataString(appKey));
        query.Append("\u0026salt=").Append(Uri.EscapeDataString(salt));
        query.Append("\u0026sign=").Append(Uri.EscapeDataString(signInput));
        query.Append("\u0026signType=v3");
        query.Append("\u0026curtime=").Append(Uri.EscapeDataString(curtime));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://openapi.youdao.com/api")
            {
                Content = new StringContent(query.ToString(), Encoding.UTF8, "application/x-www-form-urlencoded")
            };

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new YoudaoResult(false, string.Empty, $"有道翻译请求失败：HTTP {(int)response.StatusCode}");
            }

            return ParseResponse(json);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new YoudaoResult(false, string.Empty, $"有道翻译调用异常：{ex.Message}");
        }
    }

    private static string BuildSignInput(string text, string appKey, string salt, string curtime, string appSecret)
    {
        var input = TruncateForSign(text);
        var raw = $"{appKey}{input}{salt}{curtime}{appSecret}";
        return ComputeSha256Hex(raw);
    }

    private static string TruncateForSign(string text)
    {
        var len = text.Length;
        if (len <= 20)
        {
            return text;
        }

        return text[..10] + len.ToString(CultureInfo.InvariantCulture) + text[^10..];
    }

    private static string ComputeSha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static YoudaoResult ParseResponse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("errorCode", out var errorCode))
            {
                var code = errorCode.GetString();
                if (!string.IsNullOrEmpty(code) && code != "0")
                {
                    var message = GetErrorMessage(code);
                    return new YoudaoResult(false, string.Empty, $"有道翻译错误 [{code}]：{message}");
                }
            }

            if (root.TryGetProperty("translation", out var translation) && translation.GetArrayLength() > 0)
            {
                var result = string.Join("\n", translation.EnumerateArray().Select(t => t.GetString()).Where(s => !string.IsNullOrEmpty(s)));
                return new YoudaoResult(true, result, string.Empty);
            }

            return new YoudaoResult(false, string.Empty, "有道翻译返回结果为空");
        }
        catch (Exception ex)
        {
            return new YoudaoResult(false, string.Empty, $"解析有道翻译结果失败：{ex.Message}");
        }
    }

    private static string GetErrorMessage(string code)
    {
        return code switch
        {
            "101" => "缺少必填参数",
            "102" => "不支持的语言类型",
            "103" => "翻译文本过长",
            "104" => "不支持的 API 类型",
            "105" => "不支持的签名类型",
            "106" => "不支持的响应类型",
            "107" => "不支持的传输加密类型",
            "108" => "AppKey 无效",
            "109" => "BatchLog 格式不正确",
            "110" => "无相关服务的有效实例",
            "111" => "开发者账号无效",
            "201" => "解密失败，可能为假冒请求",
            "202" => "签名检验失败",
            "203" => "访问 IP 地址不在可访问 IP 列表",
            "205" => "请求的接口与应用的平台类型不一致",
            "301" => "辞典查询失败",
            "302" => "翻译查询失败",
            "303" => "服务器其他错误",
            "304" => "会话闲置太久超时",
            "401" => "账户已经欠费停",
            "411" => "访问频率受限",
            _ => "未知错误"
        };
    }

    private string GetAppKey()
    {
        var encrypted = _settings.YoudaoAppKeyEncrypted;
        return string.IsNullOrWhiteSpace(encrypted) ? string.Empty : SecretProtector.Unprotect(encrypted);
    }

    private string GetAppSecret()
    {
        var encrypted = _settings.YoudaoAppSecretEncrypted;
        return string.IsNullOrWhiteSpace(encrypted) ? string.Empty : SecretProtector.Unprotect(encrypted);
    }
}

/// <summary>有道翻译调用结果。</summary>
public sealed record YoudaoResult(bool Success, string Translation, string Error);