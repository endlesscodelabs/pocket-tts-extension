using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace VoicesDesktop.Services;

public class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_C = 0x43;
    private const uint VK_MENU = 0x12; // Alt key
    private const int DEFAULT_HOTKEY_ID = 9001;

    // Win32 RegisterHotKey
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // Win32 Window Subclassing for WM_HOTKEY
    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    // Win32 Low-Level Keyboard Hook
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private readonly IntPtr _hWnd;
    private readonly SubclassProc _subclassProc;
    private readonly LowLevelKeyboardProc _hookProc;
    private IntPtr _hookId = IntPtr.Zero;
    private bool _isHotKeyRegistered = false;
    private long _lastTriggerTick = 0;
    private bool _disposed = false;

    public event EventHandler? HotkeyPressed;

    public HotkeyManager(IntPtr hWnd)
    {
        _hWnd = hWnd;
        _subclassProc = WndProc;
        _hookProc = HookCallback;

        // Channel 1: Subclass window for WM_HOTKEY
        if (_hWnd != IntPtr.Zero)
        {
            SetWindowSubclass(_hWnd, _subclassProc, (UIntPtr)DEFAULT_HOTKEY_ID, UIntPtr.Zero);
        }
    }

    public bool Register()
    {
        // 1. Register official RegisterHotKey
        if (_hWnd != IntPtr.Zero && !_isHotKeyRegistered)
        {
            _isHotKeyRegistered = RegisterHotKey(_hWnd, DEFAULT_HOTKEY_ID, MOD_ALT | MOD_NOREPEAT, VK_C);
        }

        // 2. Install low-level keyboard hook as redundant channel
        if (_hookId == IntPtr.Zero)
        {
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            IntPtr moduleHandle = GetModuleHandle(curModule?.ModuleName);
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, moduleHandle, 0);
        }

        return _isHotKeyRegistered || _hookId != IntPtr.Zero;
    }

    public void Unregister()
    {
        if (_isHotKeyRegistered && _hWnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hWnd, DEFAULT_HOTKEY_ID);
            _isHotKeyRegistered = false;
        }

        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private void FireTrigger()
    {
        long now = Environment.TickCount64;
        if (now - _lastTriggerTick < 500) return; // Debounce 500ms
        _lastTriggerTick = now;

        _ = Task.Run(() => HotkeyPressed?.Invoke(this, EventArgs.Empty));
    }

    private IntPtr WndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (uMsg == WM_HOTKEY && wParam.ToInt32() == DEFAULT_HOTKEY_ID)
        {
            FireTrigger();
            return IntPtr.Zero;
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (kbd.vkCode == VK_C)
                {
                    // Check if Alt key is pressed (either via flag 0x20 or GetAsyncKeyState)
                    bool altDown = ((kbd.flags & 0x20) != 0) || ((GetAsyncKeyState((int)VK_MENU) & 0x8000) != 0);
                    if (altDown)
                    {
                        FireTrigger();
                        return (IntPtr)1; // Consume C key so it doesn't type into the active window
                    }
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Unregister();
            if (_hWnd != IntPtr.Zero)
            {
                RemoveWindowSubclass(_hWnd, _subclassProc, (UIntPtr)DEFAULT_HOTKEY_ID);
            }
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
