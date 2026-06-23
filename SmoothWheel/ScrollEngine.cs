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
using System.Runtime.InteropServices;

namespace SmoothWheel;

/// <summary>
/// Inertial (momentum) scroll engine — the same family of model macOS and
/// precision trackpads use, adapted to a mouse wheel.
///
/// Each real wheel notch injects a fixed impulse of <b>velocity</b> (not
/// position). Every animation frame the current velocity is applied to the
/// scroll position and then multiplied by a friction factor (&lt;1), so motion
/// glides to a stop instead of cutting off. Rapid notches sum their impulses,
/// so flicking the wheel builds real momentum that carries on after you stop —
/// the "throw" feel of a trackpad.
///
/// Tunables (all live-adjustable via the tray menu):
///   <list type="bullet">
///     <item><c>SmoothnessDurationMs</c> — no longer an easing duration; it now
///     sets the glide <i>time constant</i>. Larger = longer, lazier glide.</item>
///     <item><c>Precision</c> — sub-division of a notch into micro-scrolls
///     (controls how fine/quiet each step is).</item>
///   </list>
/// </summary>
internal sealed class ScrollEngine : IDisposable
{
    private readonly object _settingsLock = new();

    // Live-tunable settings (updated from the UI thread under _settingsLock).
    // Velocity decays as v(t) = v0 * exp(-t/T), where T = _timeConstantMs.
    // Total distance from one impulse v0 is v0*T, so to make one notch (delta)
    // travel exactly one notch-distance we inject v0 = delta/T each tick.
    // _speedFactor scales that independently of T: higher = faster peak velocity
    // and more distance per notch, without changing how long the glide lasts.
    // _maxVel caps velocity so fast scrolling saturates instead of running away —
    // a trackpad's max speed is bounded by finger speed, and without a ceiling
    // rapid flicks sum indefinitely, making the page fly off and new input feel
    // ignored while it coasts.
    private double _timeConstantMs = 100.0;
    private double _frictionPerMs = Math.Exp(-1.0 / 100.0); // = exp(-dt/T) for dt=1ms
    private double _speedFactor = 1.5;
    private double _maxVel = 5.4;        // delta/ms ceiling (≈ 3 notches at default T/speed)
    private int _microSize = 7;       // = max(1, WheelDelta / Precision)
    private bool _invert = false;

    // Timestamp (absolute ms) for the next periodic SUMMARY log line.
    private double _nextRefreshQueryMs;

    // Hook state.
    private IntPtr _hookHandle = IntPtr.Zero;
    private NativeMethods.LowLevelMouseProc? _hookProc; // kept as a field so the GC won't collect it
    private bool _disposed;

    // Animation state.
    private Thread? _animThread;
    private volatile bool _animRunning;
    private bool _timerPeriodBoosted;

    // Signal used to wake the animation thread instantly from idle when a wheel
    // notch arrives. Avoids the wakeup latency of sleeping blind in the loop.
    private readonly AutoResetEvent _wakeSignal = new(initialState: false);

    // Input queue: hook proc enqueues raw ticks; animation thread drains it.
    private readonly ConcurrentQueue<WheelInput> _queue = new();

    public bool IsRunning { get; private set; }

    /// <summary>Begin intercepting and smoothing wheel events.</summary>
    public void Start()
    {
        if (IsRunning) return;

        // Lower the system timer resolution from the default ~15.6ms to 1ms for the
        // lifetime of the hook. Without this, Sleep() can't go below ~15.6ms and the
        // animation loop effectively runs at ~64 Hz instead of the ~200 Hz we want —
        // which is the single biggest source of visible choppiness vs. a touchpad.
        if (!_timerPeriodBoosted)
        {
            NativeMethods.timeBeginPeriod(1);
            _timerPeriodBoosted = true;
        }

        // The hook proc delegate MUST be kept alive for the lifetime of the hook,
        // otherwise the GC can collect it and the native callback will crash.
        _hookProc = HookProc;

        IntPtr hMod = NativeMethods.GetModuleHandle(string.Empty);
        _hookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhMouseLl, _hookProc, hMod, 0);

        _animRunning = true;
        _animThread = new Thread(AnimationLoop)
        {
            Name = "SmoothWheel.Animator",
            IsBackground = true
        };
        _animThread.Start();

        IsRunning = true;
    }

    /// <summary>Stop intercepting. Fully unhooks so disabled mode has zero cost.</summary>
    public void Stop()
    {
        if (!IsRunning) return;

        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        _animRunning = false;
        // Wake the animation thread immediately if it's blocked on the idle wait,
        // so it notices _animRunning == false without waiting for the 1s timeout.
        _wakeSignal.Set();
        IsRunning = false;

        // Restore the default timer resolution so we're not keeping the whole system
        // on a 1ms tick (which raises idle power use) when smoothing is disabled.
        if (_timerPeriodBoosted)
        {
            NativeMethods.timeEndPeriod(1);
            _timerPeriodBoosted = false;
        }
    }

    /// <summary>Push live-tunable settings from the config without restarting the hook.</summary>
    public void ApplySettings(Config config)
    {
        lock (_settingsLock)
        {
            // Map the user-facing "duration" onto a friction time constant.
            // Velocity decays as v(t) = v0 * exp(-t/T), so a larger duration means
            // a gentler friction and a longer glide.
            _timeConstantMs = Math.Max(40.0, config.SmoothnessDurationMs);
            _frictionPerMs = Math.Exp(-1.0 / _timeConstantMs);
            _speedFactor = Math.Clamp(config.SpeedFactor, 0.25, 5.0);
            // Velocity ceiling in delta/ms: MaxVelocity is in "notches' worth" units,
            // so multiply by the per-notch impulse magnitude (delta/T * speedFactor).
            // e.g. default MaxVel=3, T=100, speed=1.5 -> ceiling 3 * (120/100 * 1.5) = 5.4.
            // Clamped to a sane upper bound so aggressive menu tuning (T=60, speed=3,
            // maxVel=8) can't push velocity so high that one emit interval accumulates
            // multiple notches. The per-emit delta cap is a second line of defense.
            double perNotch = (NativeMethods.WheelDelta / _timeConstantMs) * _speedFactor;
            double rawCeiling = Math.Max(perNotch, config.MaxVelocity * perNotch);
            _maxVel = Math.Min(rawCeiling, 15.0); // 15 delta/ms = at most ~1 notch per 8ms emit
            _microSize = Math.Max(1, NativeMethods.WheelDelta / config.Precision);
            _invert = config.InvertDirection;
        }
    }

    // -----------------------------------------------------------------------
    // Hook
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolve the foreground window's owning process name for diagnostics. Returns
    /// a short, cheap label (PID + exe name) so the debug log can correlate per-app
    /// behavior. Safe to call from the hook proc; process lookup is bounded.
    /// </summary>
    private static string GetForegroundAppLabel()
    {
        try
        {
            IntPtr hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "<no-fg>";
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return "<no-pid>";
            var proc = Process.GetProcessById((int)pid);
            return $"{proc.ProcessName}({pid})";
        }
        catch
        {
            return "<unknown>";
        }
    }

    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == NativeMethods.WmMouseWheel || msg == NativeMethods.WmMouseHWheel)
            {
                var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);

                // Pass through our own injected micro-scrolls unmodified so we don't
                // re-intercept them (which would cause an infinite loop).
                if (info.dwExtraInfo != NativeMethods.InjectedMarker)
                {
                    // mouseData: high word = wheel delta (signed).
                    int delta = (short)(info.mouseData >> 16);
                    var axis = (msg == NativeMethods.WmMouseWheel) ? Axis.Vertical : Axis.Horizontal;
                    string app = GetForegroundAppLabel();
                    _queue.Enqueue(new WheelInput(axis, delta, app));
                    DebugLog.Log($"NOTCH  axis={axis} delta={delta} app={app}");
                    // Wake the animation thread instantly. When idle it's blocked on
                    // this signal rather than sleeping blind, so the first notch after
                    // a pause (e.g. right after switching apps/tabs) starts moving with
                    // no wakeup latency.
                    _wakeSignal.Set();

                    // Returning a non-zero value (without calling CallNextHookEx) tells
                    // the OS to drop the default behavior — i.e. suppress the notch jump.
                    return new IntPtr(1);
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    // -----------------------------------------------------------------------
    // Animation (inertial)
    // -----------------------------------------------------------------------

    private void AnimationLoop()
    {
        // Per-axis inertial state.
        double velV = 0.0, velH = 0.0;        // velocity in delta-units per ms
        double accV = 0.0, accH = 0.0;        // fractional accumulator carried across frames
        // Per-axis last-emission timestamp (ms). Used to rate-limit SendInput so we
        // don't flood native apps that can't render 600 events/sec — see StepAxis.
        double lastEmitV = 0.0, lastEmitH = 0.0;

        long prevTicks = Stopwatch.GetTimestamp();
        double freqMs = 1000.0 / Stopwatch.Frequency;

        // Current target app (foreground when the most recent notch arrived). Drives
        // the per-app emission cadence — coarse Win32 renderers get a longer interval.
        string curApp = "";

        while (_animRunning)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            double dtMs = (nowTicks - prevTicks) * freqMs;
            prevTicks = nowTicks;

            // Clamp dt so a stalled frame (e.g. system suspend/resume) doesn't catapult
            // the accumulator; anything beyond ~50ms is treated as 50ms.
            if (dtMs > 50.0) dtMs = 50.0;
            if (dtMs <= 0.0) dtMs = 1.0;

            double frictionPerMs;
            double timeConstantMs;
            double speedFactor;
            double maxVel;
            int microSize;
            bool invert;
            lock (_settingsLock)
            {
                frictionPerMs = _frictionPerMs;
                timeConstantMs = _timeConstantMs;
                speedFactor = _speedFactor;
                maxVel = _maxVel;
                microSize = _microSize;
                invert = _invert;
            }

            // Apply freshly intercepted notches as velocity impulses. Distance from
            // an impulse v0 under exponential friction is v0 * T. Setting v0 = delta/T
            // makes a single notch glide exactly one notch-distance before stopping —
            // the physically correct calibration (v1 wrongly used delta/16, which made
            // a single notch overshoot ~19x). speedFactor scales the impulse so one
            // notch travels more/less than one notch-distance at the same glide feel.
            //
            // Reversal handling: a trackpad reverses instantly because the finger sets
            // velocity directly. Summing impulses here (vel += impulse) would instead
            // make an opposite-direction notch fight the existing momentum — e.g.
            // scrolling up at +0.8 then flicking down (-0.4) leaves +0.4, still going
            // up, which reads as stutter. So when a new notch is opposite in sign to
            // the current velocity, we REPLACE the velocity rather than add, giving an
            // immediate clean reversal. Same-direction notches still sum => momentum.
            while (_queue.TryDequeue(out var input))
            {
                curApp = input.App;   // most recent notch wins (matches what user is scrolling)
                int delta = invert ? -input.Delta : input.Delta;
                double impulse = (delta / timeConstantMs) * speedFactor;
                if (input.Axis == Axis.Vertical)
                {
                    if (velV != 0.0 && Math.Sign(velV) != Math.Sign(impulse))
                        velV = impulse;            // reverse instantly, don't fight
                    else
                        velV += impulse;           // same direction: build momentum
                }
                else
                {
                    if (velH != 0.0 && Math.Sign(velH) != Math.Sign(impulse))
                        velH = impulse;
                    else
                        velH += impulse;
                }
            }

            // Cap velocity to the ceiling so fast same-direction scrolling saturates
            // instead of running away. Without this, N rapid flicks sum to N× velocity
            // and the page flies off — new flicks become imperceptible relative to the
            // huge existing motion (the "lagging / can't keep up" feel). A real trackpad
            // can't exceed your finger's physical speed, so neither should we.
            velV = Math.Clamp(velV, -maxVel, maxVel);
            velH = Math.Clamp(velH, -maxVel, maxVel);

            // Integrate: add v*dt to the accumulator, then decay velocity by friction.
            // nowMs (absolute, ms) is passed for emission rate-limiting.
            double nowMs = nowTicks * freqMs;

            // Periodic summary so the log shows the live engine settings + which
            // emission profile is active for the current foreground app. Per-app
            // tuning: coarse Win32 renderers get a 16ms stream, coalescing apps 8ms.
            double emitIntervalMs = EmitIntervalForApp(curApp);
            if (nowMs >= _nextRefreshQueryMs)
            {
                _nextRefreshQueryMs = nowMs + 2000.0;
                DebugLog.Log($"SUMMARY emit={emitIntervalMs:F0}ms({1000.0 / emitIntervalMs:F0}Hz) app={curApp} T={timeConstantMs}ms speed={speedFactor} maxVel={maxVel:F1} microSize={microSize} invert={invert}");
            }

            accV = StepAxis(velV, accV, nowMs, dtMs, frictionPerMs, microSize, emitIntervalMs,
                Axis.Vertical, ref velV, ref lastEmitV);
            accH = StepAxis(velH, accH, nowMs, dtMs, frictionPerMs, microSize, emitIntervalMs,
                Axis.Horizontal, ref velH, ref lastEmitH);

            // ~200 Hz pacing while scrolling; instant wakeup from idle.
            //
            // Active scrolling: sleep the remaining time to the next 5ms boundary
            // (Stopwatch-measured). With timeBeginPeriod(1) active, Sleep(1) is
            // accurate to ~1ms and — critically — YIELDS the CPU core while waiting.
            // We deliberately do NOT spin-wait for the final sub-millisecond: a tight
            // spin hogs the core for ~1ms of every frame, and the WH_MOUSE_LL hook
            // thread (which delivers wheel deltas and runs on the same
            // message-pumping UI context) gets starved. Under cursor movement the hook
            // floods with mouse-move events at 500–1000 Hz; combined with our spin it
            // can't deliver wheel deltas promptly -> the "lag returns when moving the
            // cursor" symptom. Yielding instead of spinning lets the hook thread run,
            // so wheel events arrive on time even under heavy cursor-move load.
            double elapsedMs = (Stopwatch.GetTimestamp() - nowTicks) * freqMs;
            bool idle = velV == 0.0 && velH == 0.0 && _queue.IsEmpty;
            if (idle)
            {
                _wakeSignal.WaitOne(1000);
            }
            else
            {
                int sleepMs = (int)(5.0 - elapsedMs);
                if (sleepMs > 0)
                {
                    try { Thread.Sleep(sleepMs); }
                    catch (ThreadInterruptedException) { return; }
                }
                // We intentionally do NOT spin for the final sub-millisecond: a spin
                // hogs the core and starves the WH_MOUSE_LL hook thread (which delivers
                // wheel deltas and runs on the same message-pumping UI context). Under
                // cursor movement the hook floods with mouse-move events at 500–1000 Hz;
                // combined with a spin it can't deliver wheel deltas promptly -> the
                // "lag returns when moving the cursor" symptom. Sleeping instead of
                // spinning lets the hook thread run, so wheel events arrive on time even
                // under heavy cursor-move load. Slight frame jitter at 200 Hz is invisible.
            }
        }
    }

    /// <summary>
    /// Advance one axis: integrate velocity into the accumulator, decay velocity
    /// by friction, and emit accumulated whole deltas as wheel events.
    ///
    /// Emission is RATE-LIMITED to ~100 Hz (MinEmitIntervalMs), independent of the
    /// 200 Hz physics loop. This is the key to native-app smoothness:
    ///   - Apps like Firefox coalesce wheel events internally and animate their own
    ///     scroll, so they'd be smooth at any rate.
    ///   - Native apps (Explorer, Notepad, list/edit controls) do NOT coalesce — they
    ///     repaint synchronously on every WM_MOUSEWHEEL. At 600 events/sec they fall
    ///     behind the event stream, the visible scroll lags the physics, and you get
    ///     the "lag and delay" feel. At ~100 Hz they keep up.
    /// Physics still runs at 200 Hz (that's what makes velocity smooth); we just batch
    /// the accumulated whole deltas between emission ticks into one larger event.
    /// Velocity below a dead zone is snapped to zero so motion stops cleanly instead
    /// of trickling tiny events that beep near scroll boundaries.
    /// </summary>
    private double StepAxis(double velocity, double accumulator, double nowMs,
        double dtMs, double frictionPerMs, int microSize, double minEmitIntervalMs,
        Axis axis, ref double velOut, ref double lastEmit)
    {
        if (velocity == 0.0)
        {
            velOut = 0.0;
            return accumulator;
        }

        // Integrate position from velocity, then decay velocity by friction.
        accumulator += velocity * dtMs;
        velocity *= Math.Pow(frictionPerMs, dtMs);

        // Dead zone: once slow, stop cleanly instead of emitting sub-step trickles
        // that some apps beep at near scroll boundaries.
        if (Math.Abs(velocity) < 0.3)
        {
            velOut = 0.0;
            return 0.0;
        }
        velOut = velocity;

        // Rate-limit emission. The interval is chosen PER APP by the caller:
        //   - coalescing apps (Firefox/Chrome/Settings): 8ms (~125Hz), fine stream
        //   - synchronous Win32 renderers (Explorer/Terminal/Notepad): 16ms (~62Hz),
        //     coarser stream with bigger deltas — they repaint once per event and
        //     look choppier under a fine/fast stream, so bigger steps integrate better.
        if (nowMs - lastEmit < minEmitIntervalMs)
            return accumulator;

        if (Math.Abs(accumulator) >= microSize)
        {
            // CAP the delta per event to prevent teleports. Scaled by the emit
            // interval: at 8ms (fine stream) cap = 1 notch (120); at 16ms (coarse
            // Win32 stream, where we WANT bigger events) cap = 2 notches (240). The
            // cap is what stops runaway velocity from dumping -511 in one event.
            int maxDeltaPerEmit = (int)(NativeMethods.WheelDelta * Math.Ceiling(minEmitIntervalMs / 8.0));
            int sign = Math.Sign(accumulator);
            int desired = (int)(Math.Abs(accumulator) / microSize) * microSize;
            int totalDelta = sign * Math.Min(desired, maxDeltaPerEmit);
            // Snap to whole microSize multiples so the cap doesn't create sub-step residue.
            totalDelta = sign * (Math.Abs(totalDelta) / microSize) * microSize;
            accumulator -= totalDelta;
            double sinceLast = nowMs - lastEmit;
            Emit(new List<int> { totalDelta }, axis);
            lastEmit = nowMs;
            DebugLog.Log($"EMIT   axis={axis} delta={totalDelta} sinceLast={sinceLast:F2}ms target={minEmitIntervalMs:F2}ms({1000.0 / minEmitIntervalMs:F0}Hz) vel={velocity:F2}");
        }

        return accumulator;
    }

    /// <summary>Inject micro-scroll deltas, tagged so our hook ignores them.</summary>
    private static void Emit(List<int> chunks, Axis axis)
    {
        uint flag = axis == Axis.Vertical
            ? NativeMethods.MouseEventWheel
            : NativeMethods.MouseEventHWheel;

        var inputs = new NativeMethods.INPUT[chunks.Count];
        for (int i = 0; i < chunks.Count; i++)
        {
            inputs[i] = new NativeMethods.INPUT
            {
                type = NativeMethods.InputMouse,
                u = new NativeMethods.InputUnion
                {
                    mi = new NativeMethods.MOUSEINPUT
                    {
                        dx = 0,
                        dy = 0,
                        mouseData = (uint)chunks[i],
                        dwFlags = flag,
                        time = 0,
                        dwExtraInfo = NativeMethods.InjectedMarker
                    }
                }
            };
        }

        NativeMethods.SendInput((uint)inputs.Length, inputs,
            Marshal.SizeOf<NativeMethods.INPUT>());
    }

    // -----------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _wakeSignal.Dispose();
    }

    private readonly record struct WheelInput(Axis Axis, int Delta, string App);

    private enum Axis { Vertical, Horizontal }

    /// <summary>
    /// Classify an app's rendering style to pick the right emission cadence.
    /// Coalescing apps (modern browsers, XAML/Settings) animate their own smooth
    /// scroll between wheel events, so they want a fine, fast stream (8ms).
    /// Synchronous-rendering Win32 apps (Explorer, Terminal, classic controls)
    /// repaint once per event and drop frames under a fast stream, so they look
    /// smoother with a coarser, larger-delta stream (16ms) — the eye integrates
    /// the bigger steps better than rapid small jitter.
    /// </summary>
    private static double EmitIntervalForApp(string app)
    {
        // Coarse-rendering Win32 hosts. Match by prefix so process names like
        // "WindowsTerminal" / "WindowTerminal" / "OpenConsole" / "conhost" all hit.
        if (app.Length == 0) return 8.0;
        foreach (var key in s_coarseApps)
            if (app.StartsWith(key, StringComparison.OrdinalIgnoreCase)) return 16.0;
        return 8.0;
    }

    // Process-name prefixes for apps that render wheel events synchronously and
    // look choppy under a fine/fast event stream. Tunable; add more as discovered.
    private static readonly string[] s_coarseApps =
    {
        "explorer",       // File Explorer + many shell controls
        "WindowsTerminal",// Windows Terminal
        "WindowTerminal",
        "OpenConsole",    // conhost replacement
        "conhost",        // legacy console host
        "cmd",            // cmd.exe console
        "notepad",        // classic Notepad (Win32 edit control)
        "mmc",            // Management Console snap-ins
        "taskmgr",        // Task Manager
        "regedit",        // Registry Editor
    };
}
