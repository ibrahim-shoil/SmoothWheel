// SmoothWheel — smooth, trackpad-style scrolling for Windows mouse wheels.
// Copyright (C) 2026 ibrahim-shoil
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System.Runtime.InteropServices;

namespace SmoothWheel;

/// <summary>
/// All Win32 P/Invoke declarations, struct layouts, and constants used by the
/// scrolling engine and the tray host. Kept in one place so the rest of the
/// code can stay in pure managed style.
/// </summary>
internal static class NativeMethods
{
    // ---- Constants ----------------------------------------------------------

    /// <summary>Wheel delta reported per detent by most mice.</summary>
    public const int WheelDelta = 120;

    /// <summary>Low-level mouse hook id for <see cref="SetWindowsHookEx"/>.</summary>
    public const int WhMouseLl = 14;

    public const int WmMouseWheel = 0x020A;
    public const int WmMouseHWheel = 0x020E;

    /// <summary>Mouse wheel horizontal flag for <c>MOUSEINPUT.dwFlags</c>.</summary>
    public const uint MouseEventWheel = 0x0800;
    public const uint MouseEventHWheel = 0x01000;

    public const uint InputMouse = 0;

    /// <summary>Magic value stamped into injected events so our own hook can recognise them.</summary>
    public const nuint InjectedMarker = 0xAD0BEF;

    public const int ModAlt = 0x0001;
    public const int ModControl = 0x0002;
    public const int ModShift = 0x0004;
    public const int ModWin = 0x0008;
    public const int WmHotkey = 0x0312;

    // ---- Hooking -----------------------------------------------------------

    public delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string lpModuleName);

    // ---- Input synthesis ---------------------------------------------------

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, nuint dwExtraInfo);

    // ---- Foreground window detection (kept lightweight; used for future
    // per-app exclusion) -----------------------------------------------------

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    // ---- Global hotkey -----------------------------------------------------

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---- High-resolution timer period --------------------------------------
    // By default Windows' scheduler ticks every ~15.6ms, which caps any sleep-based
    // loop at ~64 Hz — too coarse for smooth scrolling. timeBeginPeriod(1) lowers
    // the system tick to 1ms for the lifetime of our hook, letting the animation
    // thread run at a real ~200 Hz. Paired with timeEndPeriod(1) on shutdown.

    [DllImport("winmm.dll", SetLastError = true)]
    public static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", SetLastError = true)]
    public static extern uint timeEndPeriod(uint uPeriod);

    // ---- Structs -----------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>Parameter passed to a WH_MOUSE_LL hook procedure.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        // Other input unions (keyboard, hardware) omitted — only mouse is needed.
    }
}
