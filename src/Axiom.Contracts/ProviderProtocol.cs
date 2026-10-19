using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Axiom.Domain;

namespace Axiom.Contracts;

public sealed record ProviderResource(string CapabilityId, string ResourceId);
public sealed record ProviderSample(ProviderResource Resource, StateQuality Quality, string SchemaId, int SchemaVersion,
    bool? Boolean = null, decimal? Number = null, string? Text = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);
public sealed record ProviderMessage(int Version, string Type, string RequestId, DateTimeOffset? DeadlineUtc = null,
    ProviderResource[]? Resources = null, TaskInvocation? Invocation = null, Guid? CommandId = null,
    long Sequence = 0, ProviderSample[]? Samples = null, OutcomeStatus? Status = null, string? Reason = null,
    string[]? RequiredCapabilities = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TrayCatalog? Tray = null, UsageOptions? UsageOptions = null, UsageSample? UsageSample = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BrightnessWheelSettings? WheelSettings = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? WheelSteps = null);

public static class ProviderProtocol
{
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static string PipeName(string sid) => "axiom-providers-v1-" + sid.Replace('-', '_');
    public static void Validate(ProviderMessage message, DateTimeOffset now)
    {
        if (message.Version != 1 || string.IsNullOrWhiteSpace(message.RequestId) || message.RequestId.Length > 64 ||
            message.DeadlineUtc is not { } deadline || deadline <= now || deadline > now.AddSeconds(45) ||
            message.Samples is not null || message.Status is not null || message.Reason is not null || message.Sequence < 0 ||
            message.Sequence != 0 && message.Type is not ("lease-setting-update" or "lease-setting-release") || message.Tray is not null || message.UsageSample is not null)
            throw new InvalidDataException("Provider 请求无效。");
        if (message.WheelSteps is not null || message.WheelSettings is not null && message.Type != "observe-brightness-wheel") throw new InvalidDataException("滚轮字段不属于此操作。");
        if (message.Type == "observe-brightness-wheel" && message.WheelSettings is { Enabled: true } wheel && message.Invocation is null &&
            message.Resources is null && message.RequiredCapabilities is null && message.CommandId is null && message.UsageOptions is null)
        { BrightnessWheelTool.Validate(wheel); return; }
        if (message.Type is "abandon-brightness" or "resume-brightness" && message.Invocation is null && message.Resources is null &&
            message.RequiredCapabilities is null && message.CommandId is null && message.UsageOptions is null && message.WheelSettings is null) return;
        if (message.Type == "observe-usage" && message.UsageOptions is not null && message.Invocation is null && message.CommandId is null && message.Resources is null && message.RequiredCapabilities is null) { UsageValidation.Options(message.UsageOptions); return; }
        if (message.UsageOptions is not null) throw new InvalidDataException("Unexpected observation options.");
        if (message.Type == "observe-tray" && message.Invocation is not null && message.CommandId is null && message.Resources is null && message.RequiredCapabilities is null) { TrayCapabilities.ValidateTargets(message.Invocation); return; }
        if (message.Type == "read-tray" && message.Invocation is null && message.CommandId is null && message.Resources is null && message.RequiredCapabilities is null) return;
        if (message.Type == "observe" && message.Invocation is null && message.CommandId is null && message.Resources is { Length: <= 128 } resources &&
            message.RequiredCapabilities is { Length: > 0 and <= 128 } capabilities && capabilities.All(c => !string.IsNullOrWhiteSpace(c) && c.Length <= 128) &&
            resources.All(r => !string.IsNullOrWhiteSpace(r.CapabilityId) && r.CapabilityId.Length <= 128 && !string.IsNullOrWhiteSpace(r.ResourceId) && r.ResourceId.Length <= 1024) &&
            resources.Distinct().Count() == resources.Length) return;
        if (message.Type == "invoke" && message.Resources is null && message.RequiredCapabilities is null && message.Invocation is not null && message.CommandId is { } id && id != Guid.Empty) return;
        if (message.Type == "lease" && message.Resources is null && message.RequiredCapabilities is null && message.CommandId is null && message.Invocation is not null)
        { KeepAwakeCapabilities.Validate(message.Invocation); return; }
        if (message.Type is "lease-setting" or "lease-setting-update" && message.Resources is null && message.RequiredCapabilities is null && message.CommandId is null && message.Invocation is not null)
        { RestorableSettingInvocations.ValidateLease(message.Invocation); return; }
        if (message.Type == "lease-setting-release" && message.Resources is null && message.RequiredCapabilities is null && message.CommandId is null && message.Invocation is null) return;
        throw new InvalidDataException("Provider 请求字段无效。");
    }
    public static async Task WriteAsync(Stream stream, ProviderMessage message, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        if (bytes.Length > 262144) throw new InvalidDataException("Provider 消息过大。");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
    public static async Task<ProviderMessage> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < 1 or > 262144) throw new InvalidDataException("Provider 消息过大。");
        var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<ProviderMessage>(bytes, Options) ?? throw new InvalidDataException("Provider 消息为空。");
    }
}
