using System.Collections.Immutable;
using Axiom.Domain;
using Axiom.WinUI;

namespace Axiom.Engine.Tests;

public sealed class TaskRunConditionsEditorTests
{
    private static AutomationTask Original() => new(Guid.NewGuid().ToString("D"), 7, "保留设置", true,
        new("synthetic.state", 1, ImmutableDictionary<string, string>.Empty),
        new("synthetic.action", 1, ImmutableDictionary<string, string>.Empty),
        SchemaVersion: 2, TimeWindow: new(540, 1080, 62));

    [Fact]
    public void OuterConditionsRoundTripWithoutChangingManualPauseOrOldActionWindow()
    {
        var task = Original(); var editor = new TaskEditorState(); editor.Load(task);
        Assert.False(editor.Dirty);
        editor.RunConditions = new("off", new(1200, 480, 127));
        Assert.True(editor.Dirty);
        var built = editor.Build();
        Assert.Equal(4, built.SchemaVersion); Assert.Equal(task.TimeWindow, built.TimeWindow);
        Assert.True(built.Paused); Assert.Equal(task.Version, built.Version);
        Assert.Equal(editor.RunConditions, built.RunConditions);
        editor.Load(built); Assert.False(editor.Dirty);
        editor.RunConditions = null;
        Assert.True(editor.Dirty); Assert.Equal(2, editor.Build().SchemaVersion);
        editor.Load(built); Assert.False(editor.Dirty); Assert.Equal("off", editor.RunConditions!.Hdr);
    }

    [Fact]
    public void GateSaveRequiresNegotiationAndRejectsInvalidDraftWithoutDroppingInput()
    {
        var editor = new TaskEditorState(); editor.Load(Original()); editor.RunConditions = new("off");
        var action = new TaskCapability("synthetic.action", 1, TaskCapabilityKind.Action, "", "", "", [], "", "");
        var snapshot = new AutomationSnapshot([], [action], [], true, "ready", "", DateTimeOffset.UtcNow, []);
        Assert.False(editor.CanSave(snapshot));
        Assert.True(editor.CanSave(snapshot with { RunConditionsVersion = 1 }));
        Assert.False(editor.CanSave(snapshot with { RunConditionsVersion = 1, Capabilities = [action with { SupportsRunConditions = false }] }));
        editor.RunConditions = new("off", new(480, 480, 127));
        Assert.Throws<ArgumentException>(() => editor.Build());
        Assert.Equal(480, editor.RunConditions.TimeWindow!.EndMinute); Assert.True(editor.Dirty);
        editor.Load(Original()); Assert.False(editor.Dirty); Assert.Null(editor.RunConditions);
    }
}
