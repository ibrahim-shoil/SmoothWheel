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
/// Toggles "Start with Windows" via the per-user HKCU Run key — the simplest,
/// elevation-free way to auto-start a background utility.
/// </summary>
internal static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SmoothWheel";

    // Translated Microsoft.Win32.Registry equivalents kept local so we don't pull
    // in the Microsoft.Win32.Registry NuGet package for two calls.
    private const int HkeyCurrentUser = unchecked((int)0x80000001);
    private const int KeySetValue = 0x20006;
    private const int KeyRead = 0x20019;
    private const int KeyAllAccess = 0xF003F;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int RegCreateKeyEx(int hKey, string lpSubKey, int reserved,
        string lpClass, int dwOptions, int samDesired, IntPtr lpSecurityAttributes,
        out IntPtr phkResult, IntPtr lpdwDisposition);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int RegSetValueEx(IntPtr hKey, string lpValueName, int reserved,
        int dwType, string lpData, int cbData);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegDeleteValue(IntPtr hKey, string lpValueName);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegCloseKey(IntPtr hKey);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int RegOpenKeyEx(int hKey, string lpSubKey, int ulOptions,
        int samDesired, out IntPtr phkResult);

    private const int RegSz = 1;

    public static void Enable()
    {
        var exePath = Application.ExecutablePath;
        // Quote the path so spaces in the install location don't break the launch.
        var command = "\"" + exePath + "\"";

        int rc = RegCreateKeyEx(HkeyCurrentUser, RunKeyPath, 0, null!, 0,
            KeySetValue, IntPtr.Zero, out IntPtr hKey, IntPtr.Zero);
        if (rc != 0) return;
        try
        {
            RegSetValueEx(hKey, ValueName, 0, RegSz, command, (command.Length + 1) * 2);
        }
        finally
        {
            RegCloseKey(hKey);
        }
    }

    public static void Disable()
    {
        int rc = RegOpenKeyEx(HkeyCurrentUser, RunKeyPath, 0, KeySetValue, out IntPtr hKey);
        if (rc != 0) return;
        try
        {
            RegDeleteValue(hKey, ValueName);
        }
        finally
        {
            RegCloseKey(hKey);
        }
    }

    public static bool IsEnabled()
    {
        int rc = RegOpenKeyEx(HkeyCurrentUser, RunKeyPath, 0, KeyRead, out IntPtr hKey);
        if (rc != 0) return false;
        try
        {
            // Querying the value: simplest reliable check is to attempt reading via the
            // public Microsoft.Win32 API surface we already trust — but to stay
            // self-contained we treat "key open + value present" by reading through a
            // tiny buffer query.
            return TryReadValue(hKey);
        }
        finally
        {
            RegCloseKey(hKey);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueEx(IntPtr hKey, string lpValueName,
        int lpReserved, IntPtr lpType, IntPtr lpData, ref int lpcbData);

    private static bool TryReadValue(IntPtr hKey)
    {
        int size = 0;
        int rc = RegQueryValueEx(hKey, ValueName, 0, IntPtr.Zero, IntPtr.Zero, ref size);
        return rc == 0 || rc == 234; // 0 = OK, 234 = MORE_DATA (value exists)
    }
}
