using System.IO.Pipes;
using Axiom.Contracts;
using Axiom.Domain;

namespace Axiom.ServiceHost;

internal sealed partial class PluginTaskRuntime
{
    public bool IsReleasedObservation(ResourceKey key, StateValue value) => key.CapabilityId == KeepAwakeCapabilities.Hold
        ? value.Boolean == false : RestorableSettingInvocations.IsRestorable(key.CapabilityId) && value.Schema == RestorableSettingInvocations.Schema && value.Text == "";
    private readonly object settingGate = new();
    private readonly Dictionary<ResourceKey, SettingAuthorization> settingAuthorizations = [];
    private readonly HashSet<SettingLease> settingLeases = [];
    private sealed class SettingAuthorization(long generation, bool authorized)
    {
        public long Generation = generation;
        public bool Authorized = authorized;
        public long? FailedGeneration;
        public SettingLease? Lease;
    }
    private sealed class SettingLease(NamedPipeClientStream pipe)
    {
        public readonly NamedPipeClientStream Pipe = pipe;
        public readonly SemaphoreSlim Serial = new(1, 1);
        public readonly TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly CancellationTokenSource Lifetime = new();
        public TaskCompletionSource<ProviderMessage>? Reply;
        public Task? Monitor;
        public Task? Release;
        public long Sequence;
        public string? Target;
        public bool Connected;
        public string? CapturedSelection;
        public bool ReleaseConfirmed;
        public TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);
    }
    private void SetRestorableAuthorization(ResourceKey key, long generation, bool authorized)
    {
        SettingLease? release = null;
        lock (settingGate)
        {
            if (!settingAuthorizations.TryGetValue(key, out var item)) settingAuthorizations.Add(key, item = new(generation, authorized));
            item.Generation = generation; item.Authorized = authorized;
            if (!authorized && item.Lease is { } lease) { item.Lease = null; release = lease; }
        }
        if (release is not null) StartSettingRelease(key, release);
    }
    private void ReleaseRestorableSettings()
    {
        List<(ResourceKey Key, SettingLease Lease)> release = [];
        lock (settingGate)
            foreach (var pair in settingAuthorizations)
            {
                pair.Value.Authorized = false;
                if (pair.Value.Lease is { } lease) { pair.Value.Lease = null; release.Add((pair.Key, lease)); }
            }
        foreach (var pair in release) StartSettingRelease(pair.Key, pair.Lease);
    }
    private async ValueTask<Outcome> ApplyRestorableAsync(DesiredState desired, CancellationToken token)
    {
        var id = Guid.NewGuid();
        if (wheelTakenOver && IsBrightness(desired.Key.CapabilityId)) return new(OutcomeStatus.Rejected, "亮度已明确由手动接管，未捕获新基线。", id);
        if (stopping || desired.Key.Scope != scope || desired.Target.Schema != RestorableSettingInvocations.Schema || desired.Target.Text is null ||
            !bindings.TryGetValue(desired.Key.CapabilityId, out var binding) || !binding.Descriptor.DesiredState)
            return new(OutcomeStatus.Rejected, "条件恢复目标、权限或会话不匹配。", id);
        var invocation = RestorableSettingInvocations.Decode(desired.Target.Text);
        if (invocation.CapabilityId != desired.Key.CapabilityId) return new(OutcomeStatus.Rejected, "设置租约能力与目标不匹配。", id);
        RestorableSettingInvocations.ValidateLease(invocation);
        var fading = invocation.CapabilityId is BuiltInBrightnessRestoreCapabilities.Set or HardwareBrightnessRestoreCapabilities.Set &&
            BrightnessFadeSettings.Parse(invocation.Parameters).Enabled;
        var operationTimeout = fading ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(15);
        var acquireTimeout = fading ? TimeSpan.FromSeconds(37) : TimeSpan.FromSeconds(12);
        // This currently has one real consumer: hardware brightness primary/all selects fixed handles.
        var selection = invocation.CapabilityId == HardwareBrightnessCapabilities.Set + ".restore" ? invocation.Parameters.GetValueOrDefault("target") : null;
        SettingLease? prior = null;
        lock (settingGate)
        {
            if (settingAuthorizations.TryGetValue(desired.Key, out var held) && held.Authorized && held.Generation == desired.Generation &&
                held.Lease is { } old && old.CapturedSelection != selection)
            { prior = old; held.Lease = null; }
        }
        if (prior is not null)
        {
            StartSettingRelease(desired.Key, prior);
            try { await prior.Released.Task.WaitAsync(token); }
            catch (OperationCanceledException) { return new(OutcomeStatus.Failed, "原目标仍在释放，本次未捕获新范围；不自动重试。", id, FailureKind.Permanent); }
            if (!prior.ReleaseConfirmed)
            {
                lock (settingGate) if (settingAuthorizations.TryGetValue(desired.Key, out var current) && current.Generation == desired.Generation) current.FailedGeneration = desired.Generation;
                Record("新范围未执行", "原目标释放结果未确认；没有捕获或修改新范围，不自动重试。", ActivityLevel.Warning);
                return new(OutcomeStatus.Failed, "原目标释放结果未确认，新范围未执行。", id, FailureKind.Permanent);
            }
        }
        SettingLease lease; bool first;
        lock (settingGate)
        {
            if (!settingAuthorizations.TryGetValue(desired.Key, out var item) || !item.Authorized || item.Generation != desired.Generation)
                return new(OutcomeStatus.Rejected, "设置条件已结束或代际已变化。", id);
            if (item.FailedGeneration == desired.Generation)
                return new(OutcomeStatus.Failed, "本轮设置租约未确认；等待新条件或暂停后启用，不自动重试。", id, FailureKind.Permanent);
            first = item.Lease is null;
            if (first) { item.Lease = new(Client()) { CapturedSelection = selection }; settingLeases.Add(item.Lease); }
            lease = item.Lease!;
            lease.OperationTimeout = operationTimeout;
        }
        var entered = false;
        try
        {
            await lease.Serial.WaitAsync(token); entered = true;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lease.Lifetime.Token);
            deadline.CancelAfter(first ? acquireTimeout : operationTimeout);
            ProviderMessage receipt;
            if (first)
            {
                await lease.Pipe.ConnectForUserAsync(2000, deadline.Token);
                await ProviderProtocol.WriteAsync(lease.Pipe, new(1, "lease-setting", id.ToString("N"), DateTimeOffset.UtcNow.Add(acquireTimeout), Invocation: invocation), deadline.Token);
                receipt = await ProviderProtocol.ReadAsync(lease.Pipe, deadline.Token);
                ValidateSettingReceipt(receipt, id.ToString("N"), 0);
                lease.Connected = true;
                lease.Target = id.ToString("N"); // Stable connection request id, separate from the current Desired target.
                lease.Monitor = MonitorSettingLeaseAsync(desired.Key, lease);
            }
            else
            {
                if (!lease.Connected || lease.Release is not null) throw new IOException("原设置租约正在释放或尚未确认。");
                receipt = await SettingExchangeAsync(lease, "lease-setting-update", invocation, deadline.Token);
            }
            if (receipt.Status != OutcomeStatus.Succeeded)
            {
                BlockSettingLease(desired.Key, lease, desired.Generation);
                StartSettingRelease(desired.Key, lease);
                Record("条件设置未完成", receipt.Reason!, ActivityLevel.Warning);
                return new(receipt.Status!.Value, receipt.Reason!, id, FailureKind.Permanent);
            }
            lock (settingGate)
                if (!settingAuthorizations.TryGetValue(desired.Key, out var current) || !current.Authorized || !ReferenceEquals(current.Lease, lease))
                    throw new OperationCanceledException("设置条件已结束。");
            Record("条件设置已确认", receipt.Reason!, ActivityLevel.Success);
            return new(OutcomeStatus.Succeeded, receipt.Reason!, id);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            BlockSettingLease(desired.Key, lease, desired.Generation);
            StartSettingRelease(desired.Key, lease);
            var message = error is OperationCanceledException ? "条件设置已取消或通信超时；可能已修改，释放结果另行确认，不自动重试。" : error.Message;
            Record("条件设置未确认", message, ActivityLevel.Warning);
            return new(OutcomeStatus.Failed, message, id, FailureKind.Permanent);
        }
        finally { if (entered) lease.Serial.Release(); }
    }
    private static void ValidateSettingReceipt(ProviderMessage receipt, string id, long sequence)
    {
        if (receipt.Version != 1 || receipt.Type != "lease-setting-result" || receipt.RequestId != id || receipt.Sequence != sequence ||
            receipt.CommandId is not null || receipt.Status is not (OutcomeStatus.Succeeded or OutcomeStatus.Failed or OutcomeStatus.Rejected) || string.IsNullOrWhiteSpace(receipt.Reason))
            throw new InvalidDataException("设置租约回执缺失或代际无效。");
    }
    private async Task<ProviderMessage> SettingExchangeAsync(SettingLease lease, string type, TaskInvocation? invocation, CancellationToken token)
    {
        TaskCompletionSource<ProviderMessage> reply; long sequence;
        lock (settingGate)
        {
            if (lease.Reply is not null) throw new IOException("前一次设置请求尚未返回，本次不排队。");
            reply = lease.Reply = new(TaskCreationOptions.RunContinuationsAsynchronously); sequence = ++lease.Sequence;
        }
        await ProviderProtocol.WriteAsync(lease.Pipe, new(1, type, lease.Target!, DateTimeOffset.UtcNow.Add(lease.OperationTimeout), Invocation: invocation, Sequence: sequence), token);
        return await reply.Task.WaitAsync(token);
    }
    private async Task MonitorSettingLeaseAsync(ResourceKey key, SettingLease lease)
    {
        try
        {
            while (!lease.Lifetime.IsCancellationRequested)
            {
                var receipt = await ProviderProtocol.ReadAsync(lease.Pipe, lease.Lifetime.Token);
                TaskCompletionSource<ProviderMessage>? reply;
                lock (settingGate)
                {
                    ValidateSettingReceipt(receipt, lease.Target!, lease.Sequence);
                    reply = lease.Reply ?? throw new InvalidDataException("设置租约返回未请求的回执。"); lease.Reply = null;
                }
                reply.TrySetResult(receipt);
            }
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            lock (settingGate) { lease.Reply?.TrySetException(new IOException("设置租约连接中断，释放结果尚未确认。")); lease.Reply = null; }
            if (lease.Release is null)
            {
                BlockSettingLease(key, lease, null);
                Record("条件设置连接中断", "会话端会尝试按本轮最后确认值释放；未收到恢复回执，不假称已恢复。", ActivityLevel.Warning);
                StartSettingRelease(key, lease);
            }
        }
    }
    private void BlockSettingLease(ResourceKey key, SettingLease lease, long? generation)
    {
        lock (settingGate)
            if (settingAuthorizations.TryGetValue(key, out var item) && ReferenceEquals(item.Lease, lease))
            { item.FailedGeneration = generation ?? item.Generation; item.Lease = null; }
    }
    private void StartSettingRelease(ResourceKey key, SettingLease lease)
    {
        lock (settingGate) lease.Release ??= Task.Run(() => ReleaseSettingLeaseAsync(key, lease));
    }
    private async Task ReleaseSettingLeaseAsync(ResourceKey key, SettingLease lease)
    {
        using var cleanup = new CancellationTokenSource(lease.OperationTimeout);
        var entered = false;
        try
        {
            await lease.Serial.WaitAsync(cleanup.Token); entered = true;
            Task<ProviderMessage>? pending; lock (settingGate) pending = lease.Reply?.Task;
            if (pending is not null) await pending.WaitAsync(cleanup.Token);
            if (!lease.Connected) throw new IOException("尚未收到初始租约回执；关闭连接后的释放结果未确认。");
            var receipt = await SettingExchangeAsync(lease, "lease-setting-release", null, cleanup.Token);
            lease.ReleaseConfirmed = receipt.Status == OutcomeStatus.Succeeded;
            Record(receipt.Status == OutcomeStatus.Succeeded ? "条件设置已释放" : "条件设置释放未确认", receipt.Reason!,
                receipt.Status == OutcomeStatus.Succeeded ? ActivityLevel.Success : ActivityLevel.Warning);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { Record("条件设置释放未确认", "没有收到有效释放回执；会话端仍保留原生准入直到真实工作及清理结束。可能未恢复，不重试。", ActivityLevel.Warning); }
        finally
        {
            lease.Lifetime.Cancel(); lease.Pipe.Dispose();
            if (entered) lease.Serial.Release();
            if (lease.Monitor is { } monitor) await monitor;
            lock (settingGate)
            {
                if (settingAuthorizations.TryGetValue(key, out var current) && ReferenceEquals(current.Lease, lease)) current.Lease = null;
                settingLeases.Remove(lease);
            }
            lease.Lifetime.Dispose(); lease.Released.TrySetResult();
        }
    }
    private Task DrainRestorableSettingsAsync(CancellationToken token)
    {
        ReleaseRestorableSettings();
        lock (settingGate) return Task.WhenAll(settingLeases.Select(l => l.Released.Task)).WaitAsync(token);
    }
}
