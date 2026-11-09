using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace VoicesDesktop.Services;

public static class ClipboardHelper
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_MENU = 0x12; // Alt
    private const byte VK_C = 0x43;

    public static string? GetClipboardText()
    {
        for (int i = 0; i < 8; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    IntPtr handle = GetClipboardData(CF_UNICODETEXT);
                    if (handle != IntPtr.Zero)
                    {
                        IntPtr pointer = GlobalLock(handle);
                        if (pointer != IntPtr.Zero)
                        {
                            try
                            {
                                return Marshal.PtrToStringUni(pointer);
                            }
                            finally
                            {
                                GlobalUnlock(handle);
                            }
                        }
                    }
                    return null;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(15);
        }
        return null;
    }

    public static bool SetClipboardText(string text)
    {
        if (text == null) return false;

        for (int i = 0; i < 8; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    int bytesNeeded = (text.Length + 1) * 2;
                    IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytesNeeded);
                    if (hMem == IntPtr.Zero) return false;

                    IntPtr pMem = GlobalLock(hMem);
                    if (pMem == IntPtr.Zero)
                    {
                        GlobalFree(hMem);
                        return false;
                    }

                    try
                    {
                        Marshal.Copy(text.ToCharArray(), 0, pMem, text.Length);
                        Marshal.WriteInt16(pMem, text.Length * 2, 0); // null terminator
                    }
                    finally
                    {
                        GlobalUnlock(hMem);
                    }

                    if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero)
                    {
                        GlobalFree(hMem);
                        return false;
                    }
                    return true;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(15);
        }
        return false;
    }

    public static void EmptyClipboardSafe()
    {
        for (int i = 0; i < 5; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    return;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(15);
        }
    }

    /// <summary>
    /// Simulates Ctrl + C using keybd_event with explicit release of Alt and C keys.
    /// This guarantees that target applications see pure Ctrl+C rather than Alt+Ctrl+C.
    /// </summary>
    public static void SimulateCtrlC()
    {
        // 1. Release physical Alt and C keys
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_C, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(25);

        // 2. Synthesize pure Ctrl + C
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero); // Ctrl down
        keybd_event(VK_C, 0, 0, UIntPtr.Zero);       // C down
        Thread.Sleep(20);
        keybd_event(VK_C, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // C up
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // Ctrl up
    }

    /// <summary>
    /// Non-destructively captures currently selected text across any active Windows application.
    /// Preserves original clipboard content.
    /// </summary>
    public static async Task<string?> CaptureSelectedTextAsync(int maxTimeoutMs = 350)
    {
        // 1. Save original clipboard text
        string? originalText = GetClipboardText();

        // 2. Clear clipboard so we can detect when target application pastes fresh text
        EmptyClipboardSafe();

        // 3. Simulate pure Ctrl + C
        SimulateCtrlC();

        // 4. Poll clipboard until target app puts text on it
        string? capturedText = null;
        int elapsed = 0;
        int step = 25;

        while (elapsed < maxTimeoutMs)
        {
            await Task.Delay(step);
            elapsed += step;

            string? current = GetClipboardText();
            if (!string.IsNullOrWhiteSpace(current))
            {
                capturedText = current;
                break;
            }
        }

        // 5. Restore original clipboard text
        if (!string.IsNullOrEmpty(originalText))
        {
            SetClipboardText(originalText);
        }

        // Fallback: If target app didn't respond to Ctrl+C, but original clipboard had text, return original
        if (string.IsNullOrWhiteSpace(capturedText) && !string.IsNullOrWhiteSpace(originalText))
        {
            capturedText = originalText;
        }

        return capturedText?.Trim();
    }
}
