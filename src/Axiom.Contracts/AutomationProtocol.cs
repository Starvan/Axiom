using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Axiom.Domain;

namespace Axiom.Contracts;

public sealed record AutomationMessage(int Version, string Type, string RequestId,
    DateTimeOffset? DeadlineUtc = null, AutomationTask? Task = null, string? Id = null,
    long? ExpectedVersion = null, AutomationSnapshot? Snapshot = null, string? Error = null,
    bool? StartupEnabled = null, LoginStartupStatus? Startup = null,
    ShortcutBinding? Shortcut = null, ShortcutSnapshot? Shortcuts = null, ShortcutBinding[]? Bindings = null,
    long? ActivityBeforeId = null, int? ActivityPageSize = null, ActivityPage? Activity = null, ActivityCleanupResult? ActivityCleanup = null,
    ProcessGroup? Group = null, ProcessGroup[]? Groups = null, bool? RefreshSteam = null,
    int? SteamOffset = null, string? SteamScanId = null, SteamCatalogPage? Steam = null,
    SteamToolSettings? SteamSettings = null, SteamToolSnapshot? SteamTool = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TrayCatalog? Tray = null, UsageRequest? Usage = null, UsageSnapshot? UsageSnapshot = null, string? ExportText = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TrayToolSettings? TraySettings = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TrayToolSnapshot? TrayTool = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BrightnessWheelSettings? WheelSettings = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BrightnessWheelSnapshot? Wheel = null);

public sealed record LoginStartupStatus(bool Registered, bool CurrentLocation, bool LauncherAvailable, string? Error = null);

public static class AutomationProtocol
{
    public const int MaximumFrame = 262144;
    public static string PipeName(string sid) => "axiom-tasks-v1-" + sid.Replace('-', '_');
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static void ValidateRequest(AutomationMessage request, DateTimeOffset now, bool shortcutAgent = false)
    {
        if (request.Wheel is not null || request.WheelSettings is not null && request.Type != "save-brightness-wheel") throw new InvalidDataException("亮度工具字段不属于此操作。");
        if (request.Type is "read-brightness-wheel" or "save-brightness-wheel" or "resume-brightness-wheel")
        {
            if (shortcutAgent || request.Type == "save-brightness-wheel" && request.WheelSettings is null || request.Type == "resume-brightness-wheel" && request.ExpectedVersion is not > 0)
                throw new InvalidDataException("亮度工具操作无效。");
            // Reuse the closed empty read-request envelope to reject every unrelated existing field.
            ValidateRequest(request with { Type = "read-tray-tool", WheelSettings = null, ExpectedVersion = null }, now);
            if (request.Type != "resume-brightness-wheel" && request.ExpectedVersion is not null) throw new InvalidDataException("亮度工具版本字段无效。");
            if (request.WheelSettings is not null) BrightnessWheelTool.Validate(request.WheelSettings);
            return;
        }
        if (request.Version != 1 || string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 64 ||
            request.DeadlineUtc is not { } deadline || deadline <= now || deadline > now.AddSeconds(request.Type == "shutdown" ? 45 : 15) ||
            request.TrayTool is not null || request.Snapshot is not null || request.Error is not null || request.Startup is not null || request.Shortcuts is not null ||
            request.Activity is not null || request.ActivityCleanup is not null || request.Groups is not null || request.Steam is not null || request.SteamTool is not null || request.Tray is not null || request.UsageSnapshot is not null || request.ExportText is not null ||
            request.ActivityBeforeId is <= 0 || request.ActivityPageSize is < 1 or > 64) throw new InvalidDataException("请求格式无效。");
        if (request.TraySettings is not null && request.Type != "save-tray-tool") throw new InvalidDataException("托盘工具字段不属于此操作。");
        if (request.Type == "usage" && !shortcutAgent)
        {
            if (request.TraySettings is not null || request.Usage is null || request.Task is not null || request.Id is not null || request.ExpectedVersion is not null || request.StartupEnabled is not null || request.Shortcut is not null || request.Bindings is not null || request.Group is not null || request.SteamSettings is not null || request.RefreshSteam is not null || request.SteamOffset is not null || request.SteamScanId is not null || request.ActivityBeforeId is not null || request.ActivityPageSize is not null)
                throw new InvalidDataException("观察请求字段无效。");
            UsageValidation.Request(request.Usage); return;
        }
        if (request.Usage is not null) throw new InvalidDataException("观察字段不属于此操作。");
        if (request.Group is not null && request.Type != "save-group" ||
            request.SteamSettings is not null && request.Type != "save-steam-settings" ||
            request.RefreshSteam is not null && request.Type != "scan-steam" ||
            (request.SteamOffset is not null || request.SteamScanId is not null) && request.Type is not ("scan-steam" or "read-steam-tool") ||
            request.SteamOffset is < 0 or > 4096 || request.SteamScanId is not null && !Guid.TryParseExact(request.SteamScanId, "N", out _) ||
            request.SteamOffset is > 0 && (request.SteamScanId is null || request.RefreshSteam == true))
            throw new InvalidDataException("组或工具箱请求字段无效。");
        if (shortcutAgent)
        {
            if (request.TraySettings is not null || request.SteamSettings is not null || request.Group is not null || request.RefreshSteam is not null || request.SteamOffset is not null || request.SteamScanId is not null ||
                request.ActivityBeforeId is not null || request.ActivityPageSize is not null || request.Task is not null || request.Id is not null || request.ExpectedVersion is not null || request.StartupEnabled is not null || request.Shortcut is not null ||
                !(request.Type == "read-shortcuts" && request.Bindings is null || request.Type is "apply-shortcuts" or "restore-shortcuts" && request.Bindings is not null))
                throw new InvalidDataException("快捷键请求操作或字段无效。");
            if (request.Bindings is not null) KeyboardShortcuts.ValidateSet(request.Bindings);
            return;
        }
        if (request.Bindings is not null || request.Shortcut is not null && request.Type != "save-shortcut" ||
            request.ActivityBeforeId is not null && request.Type != "read-activity" || request.ActivityPageSize is not null && request.Type != "read-activity")
            throw new InvalidDataException("请求字段无效。");
        var valid = request.Type switch
        {
            "read-tray-tool" => request.Task is null && request.Id is null && request.ExpectedVersion is null && request.StartupEnabled is null && request.Shortcut is null && request.SteamSettings is null && request.Group is null && request.RefreshSteam is null && request.SteamOffset is null && request.SteamScanId is null && request.ActivityBeforeId is null && request.ActivityPageSize is null,
            "save-tray-tool" => request.TraySettings is not null && request.Task is null && request.Id is null && request.ExpectedVersion is null && request.StartupEnabled is null && request.Shortcut is null && request.SteamSettings is null && request.Group is null && request.RefreshSteam is null && request.SteamOffset is null && request.SteamScanId is null && request.ActivityBeforeId is null && request.ActivityPageSize is null,
            "run-tray-tool" => request.ExpectedVersion > 0 && request.Task is null && request.Id is null && request.StartupEnabled is null && request.Shortcut is null && request.SteamSettings is null && request.Group is null && request.RefreshSteam is null && request.SteamOffset is null && request.SteamScanId is null && request.ActivityBeforeId is null && request.ActivityPageSize is null,
            "read-tray" or "read-steam-tool" or "refresh-steam-tool" => request.Task is null && request.Id is null && request.ExpectedVersion is null && request.StartupEnabled is null && request.Shortcut is null,
            "save-steam-settings" => request.SteamSettings is not null && request.Task is null && request.Id is null && request.ExpectedVersion is null && request.StartupEnabled is null && request.Shortcut is null,
            "read-groups" or "scan-steam" => request.Task is null && request.Id is null && request.ExpectedVersion is null && request.StartupEnabled is null,
            "save-group" => request.Group is not null && request.Task is null && request.Id is null && request.ExpectedVersion is null && request.StartupEnabled is null,
            "delete-group" => request.Task is null && Guid.TryParseExact(request.Id, "D", out _) && request.ExpectedVersion > 0 && request.StartupEnabled is null,
            "read-activity" => request.Task is null && request.Id is null && request.ExpectedVersion is null && request.StartupEnabled is null && request.Shortcut is null,
            "clear-old-activity" => request.Task is null && request.Id is null && request.ExpectedVersion is null && request.StartupEnabled is null && request.Shortcut is null && request.ActivityBeforeId is null && request.ActivityPageSize is null,
            "read-shortcuts" or "restore-shortcuts" => request.Task is null && request.Id is null && request.ExpectedVersion is null && request.StartupEnabled is null,
            "save-shortcut" => request.Shortcut is not null && request.Task is null && request.Id is null && request.ExpectedVersion is null && request.StartupEnabled is null,
            "delete-shortcut" => request.Task is null && Guid.TryParseExact(request.Id, "D", out _) && request.ExpectedVersion > 0 && request.StartupEnabled is null,
            "read" or "shutdown" or "read-startup" => request.Task is null && request.Id is null && request.ExpectedVersion is null && request.StartupEnabled is null,
            "set-startup" => request.Task is null && request.Id is null && request.ExpectedVersion is null && request.StartupEnabled is not null,
            "save" => request.Task is not null && request.Id is null && request.ExpectedVersion is null && request.StartupEnabled is null,
            "delete" => request.Task is null && Guid.TryParseExact(request.Id, "D", out _) && request.ExpectedVersion > 0 && request.StartupEnabled is null,
            _ => false
        };
        if (!valid) throw new InvalidDataException("请求操作或字段无效。");
        if (request.Type == "save" && (request.Task!.Id == Axiom.Contracts.TrayTool.TaskId || request.Task.Detection.CapabilityId == Axiom.Contracts.TrayTool.Trigger) ||
            request.Type == "delete" && request.Id == Axiom.Contracts.TrayTool.TaskId)
            throw new InvalidDataException("请在工具箱中管理托盘工具。");
        if (request.Type == "save-tray-tool") Axiom.Contracts.TrayTool.Validate(request.TraySettings!);
        if (request.Type == "save" && (request.Task!.Id == BrightnessWheelTool.TaskId || request.Task.Detection.CapabilityId == BrightnessWheelTool.Trigger) || request.Type == "delete" && request.Id == BrightnessWheelTool.TaskId)
            throw new InvalidDataException("请在工具箱中管理亮度滚轮工具。");
        if (request.Type == "save") TaskDetectionExpressions.ValidateTask(request.Task!);
    }
    public static async Task WriteAsync(Stream stream, AutomationMessage message, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        if (bytes.Length > MaximumFrame) throw new InvalidDataException("请求过大。");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
    public static async Task<AutomationMessage> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaximumFrame) throw new InvalidDataException("请求过大。");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<AutomationMessage>(bytes, Options) ?? throw new InvalidDataException("空请求。");
    }
}
