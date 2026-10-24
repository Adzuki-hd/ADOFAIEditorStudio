using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrameCacheSpike;

internal static class ProtocolJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    internal static T Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new InvalidOperationException("协议消息为空。");
    }
}

internal sealed class WorkerRequest
{
    public string Type { get; set; } = string.Empty;
    public string? SessionId { get; set; }
    public string? RenderIdentity { get; set; }
    public double? StartSeconds { get; set; }
    public double? EndSeconds { get; set; }
    public long? Generation { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? FrameRate { get; set; }
    public int? DelayMilliseconds { get; set; }
}

internal sealed class WorkerEvent
{
    public string Type { get; set; } = string.Empty;
    public string? SessionId { get; set; }
    public string? RenderIdentity { get; set; }
    public double? TimeSeconds { get; set; }
    public int? FrameIndex { get; set; }
    public int? FrameCount { get; set; }
    public string? PayloadToken { get; set; }
    public string? PixelFormat { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public long? Generation { get; set; }
    public string? Error { get; set; }
}

internal sealed record NativeFrame(
    string SessionId,
    string RenderIdentity,
    double TimeSeconds,
    int FrameIndex,
    string PayloadToken,
    string PixelFormat,
    int Width,
    int Height,
    long RequestGeneration,
    DateTimeOffset CapturedAtUtc);

internal static class WorkerEventExtensions
{
    internal static NativeFrame ToNativeFrame(this WorkerEvent message)
    {
        if (!string.Equals(message.Type, "frame", StringComparison.Ordinal))
        {
            throw new ArgumentException("消息不是 frame。", nameof(message));
        }

        if (message.SessionId is null
            || message.RenderIdentity is null
            || message.TimeSeconds is null
            || message.FrameIndex is null
            || message.PayloadToken is null
            || message.PixelFormat is null
            || message.Width is null
            || message.Height is null)
        {
            throw new InvalidOperationException("frame 消息缺少必需字段。");
        }

        return new NativeFrame(
            message.SessionId,
            message.RenderIdentity,
            message.TimeSeconds.Value,
            message.FrameIndex.Value,
            message.PayloadToken,
            message.PixelFormat,
            message.Width.Value,
            message.Height.Value,
            message.Generation ?? 0L,
            DateTimeOffset.UtcNow);
    }
}
