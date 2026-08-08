using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CPURacer.Game;

/// <summary>
/// Focus-independent race controls. GetAsyncKeyState alone is unreliable while Taskmgr
/// owns foreground focus on some Win11 setups, so we also keep a WH_KEYBOARD_LL mirror.
/// </summary>
public static class GameInput
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private const int VkUp = 0x26;
    private const int VkDown = 0x28;
    private const int VkW = 0x57;
    private const int VkS = 0x53;
    private const int VkSpace = 0x20;
    private const int VkTab = 0x09;
    private const int VkLeft = 0x25;
    private const int VkRight = 0x27;
    private const int VkA = 0x41;
    private const int VkD = 0x44;

    private static readonly bool[] HookDown = new bool[256];
    private static LowLevelKeyboardProc? _proc;
    private static IntPtr _hook;

    public static bool ThrottleDown =>
        IsDown(VkUp) || IsDown(VkW) || IsDown(VkRight) || IsDown(VkD);

    public static bool BrakeDown =>
        IsDown(VkDown) || IsDown(VkS) || IsDown(VkLeft) || IsDown(VkA);

    public static bool RestartPressed => IsDown(VkSpace);

    /// <summary>Tab — toggle Play/Debug chrome (edge-detect in App).</summary>
    public static bool DebugToggleDown => IsDown(VkTab);

    public static void Install()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _proc = HookCallback;
        // LL hooks run on the installing thread's message loop. GetModuleHandle(null) is the
        // process image — required when hosted by dotnet.exe or when the callback lives in a DLL.
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
    }

    public static void Uninstall()
    {
        if (_hook == IntPtr.Zero)
        {
            return;
        }

        _ = UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
        Array.Clear(HookDown);
    }

    private static bool IsDown(int vk)
    {
        if ((uint)vk < (uint)HookDown.Length && HookDown[vk])
        {
            return true;
        }

        // Fallback / also covers keys held before Install().
        return (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var vk = Marshal.ReadInt32(lParam);
            if ((uint)vk < (uint)HookDown.Length)
            {
                var msg = (int)wParam;
                if (msg is WmKeyDown or WmSysKeyDown)
                {
                    HookDown[vk] = true;
                }
                else if (msg is WmKeyUp or WmSysKeyUp)
                {
                    HookDown[vk] = false;
                }
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
