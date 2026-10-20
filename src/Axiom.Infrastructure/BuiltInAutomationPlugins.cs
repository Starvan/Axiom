using Axiom.Engine;

namespace Axiom.Infrastructure;

// Shared by the Host composition root and complete-catalog export/template checks.
// HDR retains its separate Desired State lifecycle adapter.
public static class BuiltInAutomationPlugins
{
    public static IAutomationPlugin[] Create() => [.. CreateWithoutHosts(), new HostsPlugin()];
    internal static IAutomationPlugin[] CreateWithoutHosts() =>
    [
        new MediaPlaybackPlugin(), new SessionDisplayPlugin(), new SessionPresencePlugin(), new DisplayCountPlugin(), new HdrStatePlugin(), new NetworkCategoryPlugin(), new InternetConnectivityPlugin(), new ProgramPlugin(), new AxiomStartupPlugin(), new WindowPlugin(), new TrayPlugin(), new KeyboardPlugin(),
        new SystemEventPlugin(), new IntervalPlugin(), new DailyTimePlugin(), new MonthlyTimePlugin(), new WindowedIntervalPlugin(), new WindowOncePlugin(), new FileArrivalPlugin(), new AudioPlaybackPlugin(), new AudioPlaybackAdjustPlugin(), new AudioPlaybackTogglePlugin(),
        new PowerStatePlugin(), new PowerSchemeStatePlugin(), new PowerSaverStatePlugin(), new BuiltInBrightnessPlugin(), new HardwareBrightnessPlugin(), new EndpointMutePlugin(), new SessionLockPlugin(), new SessionLockStatePlugin(), new DisplayOffPlugin(), new WindowMonitorPlugin(), new ForegroundWindowLayoutPlugin(), new ForegroundWindowMonitorPlugin(), new UsbDevicePlugin(), new PowerSchemePlugin(), new TaskbarAutoHidePlugin(), new TaskbarAutoHideRestorePlugin(), new WifiProfilePlugin(), new KeepAwakePlugin(), new ConditionRestorePlugin(), new ApplicationAudioPlugin(), new ApplicationAudioActivityPlugin(), new ProgramPriorityPlugin(), new CurrentMediaPlugin(), new MicrophonePlugin(), new MicrophoneTogglePlugin(), new NotificationPlugin(), new SystemSoundPlugin(), new OnceTimePlugin(), new AudioPlugin(), new OpenTargetPlugin(), new OpenTargetBatchPlugin(), new WorkPreparationPlugin(), new ForegroundPlugin(), new ForegroundScreenCoverPlugin(), new ScreenContentPlugin(), new WindowPresencePlugin(), new WindowStateDetectionPlugin()
    ];
}
