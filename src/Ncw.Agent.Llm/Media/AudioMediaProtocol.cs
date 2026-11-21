using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Llm.Auth;
using Ncw.Core.Domain;

namespace Ncw.Agent.Llm.Media;

/// <summary>语音合成(TTS)请求。</summary>
public sealed record AudioSpeechRequest(
    string Model,
    string Input,
    string Voice = "alloy",
    string? ResponseFormat = null,
    double? Speed = null);

public sealed record AudioSpeechResult(byte[] AudioBytes, string ContentType);

/// <summary>语音转写(STT)请求。</summary>
public sealed record AudioTranscriptionRequest(
    string Model,
    byte[] AudioBytes,
    string FileName,
    string ContentType = "audio/mpeg",
    string? Language = null,
    string? Prompt = null,
    string? ResponseFormat = null);

public sealed record AudioTranscriptionResult(string Text, string? Language = null, double? DurationSeconds = null);

public sealed class AudioMediaException(int? statusCode, string message) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}

/// <summary>音频模型的线上编解码(OpenAI 兼容:<c>/audio/speech</c> 与 <c>/audio/transcriptions</c>)。</summary>
public static class AudioMediaCodec
{
    public const string SpeechPath = "/audio/speech";
    public const string TranscriptionsPath = "/audio/transcriptions";

    public static EncodedMediaHttp BuildSpeechRequest(
        AudioProtocol protocol,
        string baseUrl,
        AudioSpeechRequest request,
        string? bearerToken,
        string userAgent = "NextCoWork/3.x")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        if (protocol != AudioProtocol.OpenAiAudio)
        {
            throw new ArgumentOutOfRangeException(nameof(protocol));
        }

        var url = ProviderEndpoints.JoinUrl(baseUrl, SpeechPath);
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["input"] = request.Input,
            ["voice"] = string.IsNullOrWhiteSpace(request.Voice) ? "alloy" : request.Voice.Trim(),
        };

        if (!string.IsNullOrWhiteSpace(request.ResponseFormat))
        {
            body["response_format"] = request.ResponseFormat.Trim();
        }

        if (request.Speed is { } speed)
        {
            body["speed"] = speed;
        }

        var json = body.ToJsonString();
        var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation("user-agent", userAgent);
        if (!string.IsNullOrEmpty(bearerToken))
        {
            message.Headers.TryAddWithoutValidation("authorization", "Bearer " + bearerToken);
        }

        return new EncodedMediaHttp(message, url, json);
    }

    public static EncodedMediaHttp BuildTranscriptionRequest(
        AudioProtocol protocol,
        string baseUrl,
        AudioTranscriptionRequest request,
        string? bearerToken,
        string userAgent = "NextCoWork/3.x")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        if (protocol != AudioProtocol.OpenAiAudio)
        {
            throw new ArgumentOutOfRangeException(nameof(protocol));
        }

        var url = ProviderEndpoints.JoinUrl(baseUrl, TranscriptionsPath);
        var form = new MultipartFormDataContent
        {
            { new StringContent(request.Model, Encoding.UTF8), "model" },
        };
        var file = new ByteArrayContent(request.AudioBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType);
        form.Add(file, "file", string.IsNullOrWhiteSpace(request.FileName) ? "audio.mp3" : request.FileName);

        if (!string.IsNullOrWhiteSpace(request.Language))
        {
            form.Add(new StringContent(request.Language.Trim(), Encoding.UTF8), "language");
        }

        if (!string.IsNullOrWhiteSpace(request.Prompt))
        {
            form.Add(new StringContent(request.Prompt, Encoding.UTF8), "prompt");
        }

        if (!string.IsNullOrWhiteSpace(request.ResponseFormat))
        {
            form.Add(new StringContent(request.ResponseFormat.Trim(), Encoding.UTF8), "response_format");
        }

        var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        ImageMediaCodec.ApplyCommonHeaders(message, bearerToken, userAgent);
        var preview = new JsonObject
        {
            ["multipart"] = true,
            ["model"] = request.Model,
            ["file"] = request.FileName,
            ["bytes"] = request.AudioBytes.Length,
        }.ToJsonString();
        return new EncodedMediaHttp(message, url, preview);
    }

    public static AudioTranscriptionResult ParseTranscriptionResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                var text = ImageMediaCodec.StringProp(root, "text") ?? "";
                var language = ImageMediaCodec.StringProp(root, "language");
                double? duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var sec) ? sec : null;
                return new AudioTranscriptionResult(text, language, duration);
            }
        }
        catch (JsonException)
        {
            // 纯文本 response_format="text"
        }

        return new AudioTranscriptionResult(body.Trim());
    }
}

/// <summary>音频协议客户端:语音合成(TTS)与语音转写(STT)。</summary>
public sealed class AudioMediaClient(IUpstreamHttp http, string userAgent = "NextCoWork/3.x")
{
    public async Task<AudioSpeechResult> GenerateSpeechAsync(
        AudioProtocol protocol,
        string baseUrl,
        AudioSpeechRequest request,
        string? bearerToken,
        CancellationToken ct = default)
    {
        var encoded = AudioMediaCodec.BuildSpeechRequest(protocol, baseUrl, request, bearerToken, userAgent);
        using (encoded.Message)
        {
            using var response = await http.SendAsync(encoded.Message, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                throw new AudioMediaException(status, ImageMediaCodec.ExtractErrorMessage(status, text));
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "audio/mpeg";
            return new AudioSpeechResult(bytes, contentType);
        }
    }

    public async Task<AudioTranscriptionResult> TranscribeAsync(
        AudioProtocol protocol,
        string baseUrl,
        AudioTranscriptionRequest request,
        string? bearerToken,
        CancellationToken ct = default)
    {
        var encoded = AudioMediaCodec.BuildTranscriptionRequest(protocol, baseUrl, request, bearerToken, userAgent);
        using (encoded.Message)
        {
            using var response = await http.SendAsync(encoded.Message, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                throw new AudioMediaException(status, ImageMediaCodec.ExtractErrorMessage(status, body));
            }

            return AudioMediaCodec.ParseTranscriptionResponse(body);
        }
    }
}
