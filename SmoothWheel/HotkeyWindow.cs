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
/// A message-pumping window that owns the registered global hotkey. Global hotkeys
/// (RegisterHotKey) require an HWND, and the message pump on this thread also
/// services the WH_MOUSE_LL hook callbacks — so this window is the heart of the
/// UI thread.
/// </summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int HotkeyId = 1;

    private readonly Action _onHotkey;
    private bool _registered;

    public HotkeyWindow(Action onHotkey)
    {
        _onHotkey = onHotkey;
        var cp = new CreateParams
        {
            Caption = "SmoothWheelHotkey",
            // Message-only window: no taskbar entry, no visible window.
            Parent = new IntPtr(-3) // HWND_MESSAGE
        };
        CreateHandle(cp);
    }

    public void Register(uint modifiers, uint vk)
    {
        if (_registered) Unregister();
        NativeMethods.RegisterHotKey(Handle, HotkeyId, modifiers, vk);
        _registered = true;
    }

    public void Unregister()
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(Handle, HotkeyId);
            _registered = false;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WmHotkey && m.WParam.ToInt32() == HotkeyId)
        {
            _onHotkey();
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }
}
