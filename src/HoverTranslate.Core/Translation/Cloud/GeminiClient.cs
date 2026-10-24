using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HoverTranslate.Core.Translation.Cloud;

/// <summary>
/// Gemini API: REST <c>generateContent</c>, ключ в заголовке <c>x-goog-api-key</c> (не в адресе — чтобы он не попал в логи и
/// сообщения об ошибках). Один запрос — один перевод; повтор только при 503 (сервис перегружен), один раз.
/// Ключ нигде не логируется.
/// </summary>
public sealed class GeminiClient(HttpClient http, ILogger<GeminiClient> log)
{
    public static readonly Uri BaseAddress = new("https://generativelanguage.googleapis.com/");

    /// <summary>Сколько ждать ответа: p90 по замеру — ~16 с.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(25);

    private const double Temperature = 0.2;

    /// <summary>Что делает модель: реплика из игры или визуальной новеллы — на разговорный русский, только перевод.</summary>
    internal const string SystemInstruction =
        "You translate English text from video games and visual novels into natural, spoken Russian. " +
        "Keep the tone, the pauses (ellipses) and the speaker's attitude; use informal address (ты) unless the text is clearly formal. " +
        "Output only the Russian translation: no notes, no quotes around it, no explanations. " +
        "If lines labelled 'Previous lines' are given, they are context only — do not translate them.";

    /// <param name="context">Предыдущие реплики (только для понимания контекста, не переводятся); можно пустой список.</param>
    /// <exception cref="CloudException">Любая ошибка сети или сервиса: тип — в <see cref="CloudException.Kind"/>.</exception>
    public async Task<string> TranslateAsync(string apiKey, string model, string text, IReadOnlyList<string> context, CancellationToken ct)
    {
        string body = BuildRequest(text, context);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"v1beta/models/{Uri.EscapeDataString(model)}:generateContent")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                request.Headers.Add("x-goog-api-key", apiKey);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(RequestTimeout);
                using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                string json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return ParseTranslation(json);

                var error = MapError((int)response.StatusCode, json, model);
                log.LogDebug("Gemini: {Status}, модель {Model}: {Message}", (int)response.StatusCode, model, error.Message);
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable && attempt == 0)
                {
                    log.LogDebug("Gemini: 503, повтор через секунду");
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                    continue;
                }
                throw error;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new CloudException(CloudErrorKind.Timeout, $"Gemini не ответил за {RequestTimeout.TotalSeconds:0} с");
            }
            catch (HttpRequestException ex)
            {
                // Сообщение исключения содержит адрес без ключа и причину сокета — для лога достаточно.
                log.LogDebug(ex, "Gemini: сетевая ошибка");
                throw new CloudException(CloudErrorKind.Network, "нет связи с Google (проверьте интернет)", ex);
            }
        }
    }

    internal static string BuildRequest(string text, IReadOnlyList<string> context)
    {
        var prompt = new StringBuilder();
        if (context.Count > 0)
        {
            prompt.AppendLine("Previous lines (context only):");
            foreach (string line in context) prompt.AppendLine(line);
            prompt.AppendLine().AppendLine("Translate this:");
        }
        prompt.Append(text);
        return JsonSerializer.Serialize(new
        {
            systemInstruction = new { parts = new[] { new { text = SystemInstruction } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = prompt.ToString() } } } },
            generationConfig = new { temperature = Temperature },
        });
    }

    internal static string ParseTranslation(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0
                && candidates[0].TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
            {
                string text = string.Concat(parts.EnumerateArray()
                    .Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : null)).Trim();
                if (text.Length > 0) return text;
            }
            if (root.TryGetProperty("promptFeedback", out var feedback) && feedback.TryGetProperty("blockReason", out _))
                throw new CloudException(CloudErrorKind.Blocked, "Gemini отказался переводить этот текст");
            throw new CloudException(CloudErrorKind.Blocked, "Gemini вернул пустой ответ");
        }
        catch (JsonException ex)
        {
            throw new CloudException(CloudErrorKind.Other, "непонятный ответ Gemini", ex);
        }
    }

    internal static CloudException MapError(int status, string json, string model)
    {
        string detail = ErrorText(json);
        return status switch
        {
            400 when detail.Contains("API key", StringComparison.OrdinalIgnoreCase) =>
                new CloudException(CloudErrorKind.InvalidKey, "ключ не принят Google (проверьте, что он скопирован целиком)"),
            401 or 403 => new CloudException(CloudErrorKind.InvalidKey, "ключ не принят Google или у него нет доступа к Gemini API"),
            404 => new CloudException(CloudErrorKind.ModelNotFound, $"модель «{model}» недоступна"),
            429 => new CloudException(CloudErrorKind.RateLimited, "достигнут лимит запросов Gemini (бесплатный уровень ограничен)"),
            >= 500 => new CloudException(CloudErrorKind.Server, $"сервис Gemini временно недоступен (код {status})"),
            _ => new CloudException(CloudErrorKind.Other, detail.Length > 0
                ? $"Gemini вернул ошибку {status}: {Shorten(detail)}"
                : $"Gemini вернул ошибку {status}"),
        };
    }

    private static string Shorten(string text)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= 200 ? text : text[..200] + "…";
    }

    /// <summary>Поле error.message из ответа об ошибке; пусто, если тело не в формате Google.</summary>
    private static string ErrorText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message)
                ? message.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
