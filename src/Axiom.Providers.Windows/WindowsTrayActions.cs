using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Axiom.Providers.Windows;

public sealed record WindowsTrayEntry(string ExecutablePath, string DisplayName, int IconCount);
public sealed record WindowsTrayCatalog(WindowsTrayEntry[] Entries, string? Notice);

[SupportedOSPlatform("windows")]
public static class WindowsTrayActions
{
    private static event Action<string, (nint Window, uint Id, Guid Guid)>? IconRemoved;

    // A subscription belongs only to an enabled continuous tool. Identity reads are confined to its configured paths.
    public static async Task WatchTargetsAsync(string[] paths, Action<bool, long, bool> publish, CancellationToken token)
    {
        if (paths.Length is < 1 or > 32) throw new ArgumentException("请选择 1–32 个目标程序。");
        using var notifications = new WindowsTrayEvents();
        var gate = new object();
        var previous = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p,
            _ => new HashSet<(nint Window, uint Id, Guid Guid)>(), StringComparer.OrdinalIgnoreCase);
        long counter = 0;
        void Removed(string path, (nint Window, uint Id, Guid Guid) identity)
        {
            lock (gate) if (previous.TryGetValue(path, out var identities)) identities.Remove(identity);
        }
        IconRemoved += Removed;
        try
        {
            bool Read()
            {
                var appeared = false;
                lock (gate)
                {
                    var registrations = ReadRegistrations();
                    foreach (var path in previous.Keys.ToArray())
                    {
                        token.ThrowIfCancellationRequested();
                        var matches = WindowsProgramControl.Matching(path);
                        try
                        {
                            var active = matches.Count == 0 ? [] : FindActive(path, registrations, matches, token).ToHashSet();
                            appeared |= active.Except(previous[path]).Any();
                            previous[path] = active;
                        }
                        finally { foreach (var process in matches) process.Dispose(); }
                    }
                }
                return appeared;
            }
            bool HasActive() { lock (gate) return previous.Values.Any(v => v.Count > 0); }
            Read(); var present = HasActive(); publish(true, counter, present);
            while (true)
            {
                await notifications.Events.Reader.ReadAsync(token);
                // Bounded channel merges native bursts. Removal alone never emits an execution opportunity.
                var appeared = Read(); var active = HasActive();
                if (appeared) counter = checked(counter + 1);
                if (appeared || present != active) publish(false, counter, active);
                present = active;
            }
        }
        finally { IconRemoved -= Removed; }
    }
    // Read on demand only. Historical settings are candidates, never proof that an icon is active.
    public static WindowsTrayCatalog ReadCatalog(CancellationToken token)
    {
        var registrations = ReadRegistrations();
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = registrations.Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcesses())
            using (process)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    running.Add(process.ProcessName);
                    if (process.SessionId == current.SessionId && process.MainModule?.FileName is { } path && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        paths.Add(Path.GetFullPath(path));
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or UnauthorizedAccessException) { }
            }
        var entries = new List<WindowsTrayEntry>(); var skipped = 0;
        var activePaths = paths.Where(path => running.Contains(Path.GetFileNameWithoutExtension(path))).ToArray();
        if (activePaths.Length > 512) throw new IOException("运行中的程序路径超过当前识别范围，未返回截断名单。");
        foreach (var path in activePaths)
        {
            token.ThrowIfCancellationRequested();
            if (!running.Contains(Path.GetFileNameWithoutExtension(path))) continue;
            List<Process>? matches = null;
            try
            {
                matches = WindowsProgramControl.Matching(path);
                if (matches.Count == 0) continue;
                var active = FindActive(path, registrations, matches, token);
                if (active.Length == 0) continue;
                // InitialTooltip is historical (RTSS even retains an old version), not a live label.
                var title = Path.GetFileNameWithoutExtension(path);
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(path);
                    if (!string.IsNullOrWhiteSpace(info.FileDescription)) title = info.FileDescription.Trim();
                    else if (!string.IsNullOrWhiteSpace(info.ProductName)) title = info.ProductName.Trim();
                }
                catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException) { }
                entries.Add(new(path, title[..Math.Min(title.Length, 160)], active.Length));
                if (entries.Count > 128) throw new IOException("当前托盘程序超过 128 个，未返回截断名单。");
            }
            catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or InvalidOperationException)
            { skipped++; }
            finally { if (matches is not null) foreach (var process in matches) process.Dispose(); }
        }
        return new([.. entries.OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)],
            skipped > 0 ? $"{skipped} 个程序无法核对权限或图标归属，未列入可选名单。" : null);
    }
    public static async Task<ProgramControlResult> HideAsync(string path, TimeSpan wait, CancellationToken token)
    {
        if (wait < TimeSpan.FromSeconds(1) || wait > TimeSpan.FromSeconds(120)) throw new ArgumentOutOfRangeException(nameof(wait));
        if (!File.Exists(path)) return new(false, "目标程序不存在，请重新选择路径。");
        token.ThrowIfCancellationRequested();
        using var notifications = new WindowsTrayEvents();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(wait);
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var result = HideCurrent(path, deadline.Token);
                if (result is not null) return result;
                await notifications.Events.Reader.ReadAsync(deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new(false, $"等待托盘图标超时：{Path.GetFileName(path)} 未发现可识别图标。程序可能未运行、没有图标或系统记录不支持；未结束程序。"); }
    }
    private sealed record Registration(string Path, uint? Id, Guid Guid);
    private static Registration[] ReadRegistrations()
    {
        using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
        if (root is null) return [];
        var names = root.GetSubKeyNames();
        if (names.Length > 4096) throw new IOException("系统托盘记录超过当前支持范围，未修改图标。");
        var records = new List<Registration>();
        var folders = new Dictionary<Guid,string>();
        foreach (var name in names)
        {
            using var key = root.OpenSubKey(name); if (key is null) continue;
            if (key.GetValue("ExecutablePath") is not string raw || NormalizeRecordedPath(raw, folders) is not { } path) continue;
            var guidValue = key.GetValue("IconGuid");
            var guid = guidValue is byte[] { Length:16 } bytes ? new Guid(bytes) : guidValue is string text && Guid.TryParse(text, out var parsed) ? parsed : Guid.Empty;
            uint? id = key.GetValue("UID") is int number ? unchecked((uint)number) : null;
            if (guid != Guid.Empty || id.HasValue) records.Add(new(path, id, guid));
        }
        return [.. records.Distinct()];
    }
    private static string? NormalizeRecordedPath(string raw, Dictionary<Guid,string> folders)
    {
        if (raw.Length > 32768) return null;
        raw = Environment.ExpandEnvironmentVariables(raw);
        if (raw.StartsWith('{') && raw.IndexOf('}') is var end && end > 0 && Guid.TryParse(raw[..(end+1)],out var folder))
        {
            if (!folders.TryGetValue(folder, out var directory))
            {
                if (SHGetKnownFolderPath(ref folder, 0, 0, out var pointer) < 0) return null;
                try { directory = Marshal.PtrToStringUni(pointer) ?? ""; } finally { Marshal.FreeCoTaskMem(pointer); }
                folders[folder] = directory;
            }
            raw = directory + raw[(end+1)..];
        }
        try { return Path.IsPathFullyQualified(raw) && !raw.StartsWith(@"\\",StringComparison.Ordinal) ? Path.GetFullPath(raw) : null; }
        catch (ArgumentException) { return null; }
    }
    private static ProgramControlResult? HideCurrent(string path, CancellationToken token)
    {
        var matches = WindowsProgramControl.Matching(path);
        try
        {
            if (matches.Count == 0) return null;
            var registrations = ReadRegistrations();
            var candidates = FindActive(path, registrations, matches, token);
            var hidden = 0; var failed = 0;
            foreach (var candidate in candidates)
            {
                token.ThrowIfCancellationRequested();
                if (matches.All(p => p.HasExited)) break;
                if (candidate.Guid == Guid.Empty && (GetWindowThreadProcessId(candidate.Window,out var pid) == 0 || !matches.Any(p => p.Id == pid && !p.HasExited))) continue;
                var identity = new IconIdentifier { Size=(uint)Marshal.SizeOf<IconIdentifier>(),Window=candidate.Window,Id=candidate.Id,Guid=candidate.Guid };
                if (Shell_NotifyIconGetRect(ref identity,out _) < 0) continue;
                token.ThrowIfCancellationRequested();
                var data = new IconData { Size=(uint)Marshal.SizeOf<IconData>(),Window=candidate.Window,Id=candidate.Id,Guid=candidate.Guid,
                    Flags=candidate.Guid == Guid.Empty ? 0u : 32u,Tip="",Info="",Title="" };
                if (Shell_NotifyIcon(2,ref data) && Shell_NotifyIconGetRect(ref identity,out _) < 0)
                {
                    hidden++;
                    IconRemoved?.Invoke(path, candidate);
                    WindowsTrayEvents.Invalidate();
                }
                else failed++;
            }
            if (hidden == 0 && failed == 0) return null;
            return new(failed == 0, $"{Path.GetFileName(path)}：已隐藏 {hidden} 个托盘图标" + (failed > 0 ? $"，{failed} 个未确认移除。" : "；保留后台进程，未关闭程序。") + " 软件重新创建图标后可能再出现。");
        }
        finally { foreach (var process in matches) process.Dispose(); }
    }
    private static (nint Window,uint Id,Guid Guid)[] FindActive(string path, Registration[] registrations, List<Process> matches, CancellationToken token)
    {
        var selected = registrations.Where(r => r.Path.Equals(path,StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selected.Length > 64) throw new InvalidOperationException("目标托盘记录过多，未修改图标。");
        var ids = matches.Select(p => p.Id).ToHashSet(); var windows = new HashSet<nint>();
        bool Collect(nint window,nint state)
        {
            GetWindowThreadProcessId(window,out var pid);
            if (ids.Contains((int)pid)) windows.Add(window);
            return true;
        }
        EnumWindows((window,state) => { Collect(window,state); EnumChildWindows(window,Collect,0); return true; },0);
        nint message = 0; var count = 0;
        while ((message = FindWindowEx(-3,message,null,null)) != 0)
        { token.ThrowIfCancellationRequested(); if (++count > 4096) throw new InvalidOperationException("消息窗口数量超过识别范围，未修改图标。"); Collect(message,0); EnumChildWindows(message,Collect,0); }
        if (windows.Count > 256) throw new InvalidOperationException("目标窗口数量超过识别范围，未修改图标。");
        var candidates = new HashSet<(nint Window,uint Id,Guid Guid)>();
        foreach (var record in selected)
        {
            if (record.Guid != Guid.Empty)
            {
                if (registrations.Any(r => r.Guid == record.Guid && !r.Path.Equals(path,StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("托盘图标的程序归属存在冲突，未修改图标。");
                candidates.Add((0,0,record.Guid));
            }
            else foreach (var window in windows) candidates.Add((window,record.Id!.Value,Guid.Empty));
        }
        // RTSS/MFC may use its current HWND value as uID. Probe this single bounded convention
        // for each verified owned window; GetRect must confirm the exact live identity before any delete.
        foreach (var window in windows) candidates.Add((window,unchecked((uint)window.ToInt64()),Guid.Empty));
        var active = new List<(nint Window,uint Id,Guid Guid)>();
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (matches.All(p => p.HasExited)) break;
            if (candidate.Guid == Guid.Empty && (GetWindowThreadProcessId(candidate.Window,out var pid) == 0 || !matches.Any(p => p.Id == pid && !p.HasExited))) continue;
            var identity = new IconIdentifier { Size=(uint)Marshal.SizeOf<IconIdentifier>(),Window=candidate.Window,Id=candidate.Id,Guid=candidate.Guid };
            if (Shell_NotifyIconGetRect(ref identity,out _) >= 0) active.Add(candidate);
            if (active.Count > 64) throw new InvalidOperationException("目标活动图标过多，未修改图标。");
        }
        return [.. active];
    }
    private delegate bool EnumCallback(nint window,nint state);
    [StructLayout(LayoutKind.Sequential)] private struct IconIdentifier { public uint Size; public nint Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct IconData
    {
        public uint Size; public nint Window; public uint Id,Flags,Message; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Tip;
        public uint State,Mask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] public string Title;
        public uint InfoFlags; public Guid Guid; public nint Balloon;
    }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback,nint state);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint window,EnumCallback callback,nint state);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern nint FindWindowEx(nint parent,nint after,string? cls,string? title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window,out uint pid);
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid id,uint flags,nint token,out nint path);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint operation,ref IconData data);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref IconIdentifier id,out Rect rect);
}
