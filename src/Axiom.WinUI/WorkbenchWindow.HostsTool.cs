using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    private bool HostsEditor => editor.Id == HostsCapabilities.TaskId && editor.Action?.CapabilityId == HostsCapabilities.Update;
    private void BuildHostsTool()
    {
        var body = Column(12); var heading = ToolHeading();
        heading.Children.Add(new Border { Width = 40, Height = 40, Background = BlueSoft, CornerRadius = new CornerRadius(10),
            Child = new FontIcon { Glyph = "\uE787", FontSize = 20, Foreground = Blue } });
        var title = Text("定时Hosts", 18, Ink, true); title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 1); heading.Children.Add(title); body.Children.Add(heading);
        body.Children.Add(Text("按选定检测条件，通过自定义DNS或DoH解析多个域名，更新系统hosts管理记录。", 14, Muted));
        var task = status?.Tasks.FirstOrDefault(t => t.Id == HostsCapabilities.TaskId);
        var valid = task is null || task.Action.CapabilityId == HostsCapabilities.Update;
        var toggle = new ToggleSwitch { Header = "启用定时Hosts", OnContent = "已启用", OffContent = "已暂停", IsOn = task is { Paused: false },
            IsEnabled = reachable && task is not null && valid };
        toggle.Toggled += async (_, _) =>
        {
            if (task is not null) await MutateAsync(new(1, "save", Guid.NewGuid().ToString("N"), Task: task with { Paused = !toggle.IsOn }),
                "saved", toggle.IsOn ? "定时Hosts已启用，等待检测条件。" : "定时Hosts已暂停，已写入记录保留。", false);
        };
        PlaceToolToggle(heading, toggle);
        body.Children.Add(Text(task is null ? "尚未配置；先填写域名、DNS和何时执行。" : !valid ? "工具标识被其他任务占用，原配置保留，请检查任务。" :
            (task.Paused ? "已暂停 · " : "已启用，等待检测 · ") + DetectionSummary(task), 13, valid ? Muted : Amber));
        if (task is not null && RunState(task) is { AutomaticallyPaused: true } paused) body.Children.Add(Text(paused.Reason ?? "等待运行条件满足。", 12, Amber));
        body.Children.Add(Text("启用和保存不表示写入成功；每次更新结果见活动记录。暂停不撤销已写入的记录。", 12, Muted));
        var actions = Row(8); var settings = Action("工具设置");
        settings.IsEnabled = reachable && valid && status?.Capabilities.Any(c => c.Id == HostsCapabilities.Update) == true;
        settings.Click += (_, _) =>
        {
            if (busy || status is null) return;
            page = Page.Rules;
            if (task is not null) SelectRule(task);
            else
            {
                var descriptor = status.Capabilities.Single(c => c.Id == HostsCapabilities.Update);
                var action = new TaskInvocation(descriptor.Id, descriptor.SchemaVersion,
                    descriptor.Fields.Where(f => f.DefaultValue is not null).ToImmutableDictionary(f => f.Id, f => f.DefaultValue!));
                editor.LoadDraft(new(HostsCapabilities.TaskId, 0, "定时Hosts", true,
                    new(IntervalCapabilities.Elapsed, 1, ImmutableDictionary<string, string>.Empty.Add("minutes", "60")), action));
                editing = true; notice.IsOpen = false; Render();
            }
        };
        actions.Children.Add(settings);
        var results = Action("查看执行结果"); results.Click += async (_, _) => { page = Page.Activity; Render(); await RefreshAsync(); }; actions.Children.Add(results);
        actions.Children.Add(RefreshButton()); body.Children.Add(actions); content.Children.Add(Card(body));
    }
}
