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

using System.Threading;

namespace SmoothWheel;

internal static class Program
{
    private const string MutexName = "Global\\SmoothWheel_SingleInstance";

    [STAThread]
    private static void Main()
    {
        // Single-instance guard: if another copy is already running, exit silently.
        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew) return;

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, _) => { /* swallow UI-thread exceptions silently */ };

        var config = Config.Load();
        using var app = new TrayApp(config);
        app.ExitRequested += (_, _) => Application.Exit();

        Application.Run();
    }
}
