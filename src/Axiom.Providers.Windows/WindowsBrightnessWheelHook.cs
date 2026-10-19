using System.Runtime.InteropServices;

namespace Axiom.Providers.Windows;

// Installed/disposed on the existing shortcut message thread. No I/O, waits or brightness work here.
public sealed class WindowsBrightnessWheelHook : IDisposable
{
    private readonly Hook callback;
    private nint handle;
    public WindowsBrightnessWheelHook(int modifiers, uint key, Action<int> wheel)
    {
        callback = (code, message, data) =>
        {
            if (code >= 0 && message == 0x020A)
            {
                var pressed = (Down(0x11) ? 1 : 0) | (Down(0x12) ? 2 : 0) | (Down(0x10) ? 4 : 0) | (Down(0x5B) || Down(0x5C) ? 8 : 0);
                var input = Marshal.PtrToStructure<MouseInput>(data);
                if (pressed == modifiers && (key == 0 || Down((int)key)))
                { wheel(unchecked((short)(input.Data >> 16))); return 1; }
            }
            return CallNextHookEx(handle, code, message, data);
        };
        handle = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0);
        if (handle == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "滚轮监听安装失败。");
    }
    public void Dispose()
    {
        if (handle == 0) return;
        if (!UnhookWindowsHookEx(handle)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "滚轮监听释放未确认。");
        handle = 0; GC.KeepAlive(callback);
    }
    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    private delegate nint Hook(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { internal int X, Y; internal uint Data, Flags, Time; internal nuint Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int kind, Hook callback, nint module, uint thread);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
}
