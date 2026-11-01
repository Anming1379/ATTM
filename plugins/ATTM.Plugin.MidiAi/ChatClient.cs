using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ATTM.Plugin.MidiAi;

/// <summary>一条用户自定义的 API 配置。</summary>
internal sealed record ApiProfile(string Name, string Endpoint, string Model, string ApiKey)
{
    public bool IsOpenAiCompatible => Endpoint.Contains("/v1/", StringComparison.OrdinalIgnoreCase);

    public string Display => string.IsNullOrWhiteSpace(Name) ? Endpoint : Name + " · " + Model;

    public ApiProfile Normalize() => new(
        string.IsNullOrWhiteSpace(Name) ? "未命名" : Name.Trim(),
        Endpoint?.Trim() ?? string.Empty,
        string.IsNullOrWhiteSpace(Model) ? "qwen2.5:3b" : Model.Trim(),
        ApiKey?.Trim() ?? string.Empty);
}

/// <summary>插件设置：本地模型 + 若干条 API + 下载参数。</summary>
internal sealed record ChatSettings(
    string ModelPath,
    int TimeoutSeconds,
    int MaxTokens,
    int DownloadConcurrency,
    List<ApiProfile> Profiles,
    string ActiveProfile)
{
    public static ChatSettings Default => new(
        string.Empty, 180, 512, 8,
        new List<ApiProfile> { new("本机 Ollama", "http://127.0.0.1:11434/api/chat", "qwen2.5:3b", string.Empty) },
        "本机 Ollama");

    public ApiProfile? Active =>
        Profiles.FirstOrDefault(p => string.Equals(p.Name, ActiveProfile, StringComparison.OrdinalIgnoreCase))
        ?? Profiles.FirstOrDefault();

    /// <summary>反序列化后补齐缺省值（兼容旧配置与空字段）。</summary>
    public ChatSettings Normalize()
    {
        var profiles = (Profiles ?? new List<ApiProfile>())
            .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Endpoint))
            .Select(p => p.Normalize())
            .ToList();

        if (profiles.Count == 0) profiles = Default.Profiles;

        var active = profiles.Any(p => string.Equals(p.Name, ActiveProfile, StringComparison.OrdinalIgnoreCase))
            ? ActiveProfile
            : profiles[0].Name;

        return new ChatSettings(
            ModelPath ?? string.Empty,
            TimeoutSeconds <= 0 ? Default.TimeoutSeconds : TimeoutSeconds,
            MaxTokens <= 0 ? Default.MaxTokens : Math.Clamp(MaxTokens, 128, 4096),
            DownloadConcurrency <= 0 ? Default.DownloadConcurrency : Math.Clamp(DownloadConcurrency, 1, 16),
            profiles,
            active);
    }
}

/// <summary>HTTP 备用通道：Ollama 原生 /api/chat 或 OpenAI 兼容 /v1/chat/completions。</summary>
internal static class ChatClient
{
    private static readonly HttpClient Http = new();

    public static string Complete(ChatSettings settings, ApiProfile profile, string system, string user, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(profile.Endpoint))
            throw new InvalidOperationException("这条 API 没有填地址");

        using var request = new HttpRequestMessage(HttpMethod.Post, profile.Endpoint)
        {
            Content = new StringContent(BuildBody(settings, profile, system, user), Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(profile.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 5, 600)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);

        HttpResponseMessage response;
        try
        {
            response = Http.Send(request, HttpCompletionOption.ResponseContentRead, linked.Token);
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException("等待模型超时（" + settings.TimeoutSeconds + " 秒）");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("连不上 API：" + profile.Endpoint + "\n" + ex.Message);
        }

        using (response)
        {
            var body = response.Content.ReadAsStringAsync(linked.Token).GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("API 返回 " + (int)response.StatusCode + "：" + Trim(body));

            return ExtractContent(body, profile.IsOpenAiCompatible);
        }
    }

    private static string BuildBody(ChatSettings settings, ApiProfile profile, string system, string user)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", profile.Model);
            writer.WriteBoolean("stream", false);

            if (profile.IsOpenAiCompatible)
            {
                writer.WriteNumber("temperature", 0.2);
                writer.WriteNumber("max_tokens", settings.MaxTokens);
            }
            else
            {
                writer.WriteStartObject("options");
                writer.WriteNumber("temperature", 0.2);
                writer.WriteNumber("num_predict", settings.MaxTokens);
                writer.WriteEndObject();
            }

            writer.WriteStartArray("messages");
            WriteMessage(writer, "system", system);
            WriteMessage(writer, "user", user);
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteMessage(Utf8JsonWriter writer, string role, string content)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        writer.WriteString("content", content);
        writer.WriteEndObject();
    }

    private static string ExtractContent(string body, bool openAi)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (openAi)
        {
            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var content))
            {
                return content.GetString() ?? string.Empty;
            }
        }
        else if (root.TryGetProperty("message", out var message)
                 && message.TryGetProperty("content", out var content))
        {
            return content.GetString() ?? string.Empty;
        }

        throw new InvalidOperationException("返回的 JSON 里找不到 content 字段：" + Trim(body));
    }

    private static string Trim(string text) => text.Length <= 300 ? text : text[..300] + "...";
}
