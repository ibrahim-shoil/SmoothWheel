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

using System.Collections.Concurrent;
using System.Diagnostics;

namespace SmoothWheel;

/// <summary>
/// Diagnostic logger for the scrolling engine. The hot path (hook proc, animation
/// thread) only does a lock-free enqueue into a bounded queue — it must NOT touch
/// disk, allocate strings, or otherwise perturb the exact timing we're trying to
/// measure. A dedicated background thread drains the queue and writes to a file.
///
/// Output: %LocalAppData%\SmoothWheel\debug.log
/// Toggle at runtime via the tray menu; off by default.
/// </summary>
internal static class DebugLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SmoothWheel", "debug.log");

    private static readonly ConcurrentQueue<string> _queue = new();
    private static readonly SemaphoreSlim _signal = new(0);
    private static Thread? _drainThread;
    private static bool _running;

    public static bool Enabled { get; private set; }

    public static void Start()
    {
        if (_running) return;
        _running = true;
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        // Truncate on each start so each session is self-contained.
        File.WriteAllText(LogPath, $"=== SmoothWheel debug session {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
        _drainThread = new Thread(DrainLoop)
        {
            Name = "SmoothWheel.DebugLog",
            IsBackground = true
        };
        _drainThread.Start();
        Enabled = true;
    }

    public static void Stop()
    {
        if (!_running) return;
        Enabled = false;
        _running = false;
        _signal.Release(); // wake the drainer so it exits
    }

    /// <summary>Enqueue a message. Cheap: only a queue append on the hot path.</summary>
    public static void Log(string message)
    {
        if (!Enabled) return;
        long ts = Stopwatch.GetTimestamp();
        double ms = ts * 1000.0 / Stopwatch.Frequency;
        _queue.Enqueue($"{ms,12:F3}ms  {message}");
        _signal.Release();
    }

    private static void DrainLoop()
    {
        while (_running)
        {
            _signal.Wait(1000);
            // Drain in batches to reduce the number of file writes.
            var sb = new System.Text.StringBuilder(4096);
            int count = 0;
            while (_queue.TryDequeue(out var msg))
            {
                sb.AppendLine(msg);
                count++;
                if (count > 2000) break; // cap per drain to avoid unbounded growth
            }
            if (count > 0)
            {
                try { File.AppendAllText(LogPath, sb.ToString()); }
                catch { /* best effort */ }
            }
        }

        // Final flush of anything queued during shutdown.
        var tail = new System.Text.StringBuilder();
        while (_queue.TryDequeue(out var msg)) tail.AppendLine(msg);
        if (tail.Length > 0)
        {
            try { File.AppendAllText(LogPath, tail.ToString()); } catch { }
        }
    }

    public static string GetLogPath() => LogPath;
}
