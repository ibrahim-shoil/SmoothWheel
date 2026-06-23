# Changelog

All notable changes to SmoothWheel are documented here.
This project follows [Keep a Changelog](https://keepachangelog.com/) and uses
[Semantic Versioning](https://semver.org/).

## [1.0.0] — 2026-06-23

First public release.

### Added
- **Inertial momentum scrolling** — velocity + exponential-friction physics,
  replacing the original fixed-duration ease-out model. Each wheel notch injects
  a velocity impulse; rapid notches sum into real momentum that carries on after
  you stop, the same model precision trackpads and macOS use.
- **Independent Speed control** (1.0–3.0×) decoupled from glide duration, so peak
  velocity and deceleration feel can be tuned separately.
- **Max momentum cap** — fast scrolling saturates instead of running away, keeping
  it controllable (a real trackpad can't exceed finger speed).
- **Instant reversal** — opposite-direction input replaces velocity instead of
  summing, so up→down flips immediately with no "fighting" stutter.
- **Instant wake from idle** via an `AutoResetEvent`, eliminating the response
  delay on the first flick after switching apps/tabs.
- **Per-app emission tuning** — coalescing apps (Firefox, Chrome, Settings) get a
  fine 8 ms (~125 Hz) stream; synchronous Win32 renderers (Explorer, Terminal,
  Notepad, cmd, conhost, mmc, taskmgr, regedit) get a coarser 16 ms (~62 Hz) stream
  with larger deltas so they don't drop frames.
- **200 Hz physics loop** via `timeBeginPeriod(1)`, overcoming Windows' default
  ~15.6 ms timer granularity (the single biggest source of choppiness vs. a trackpad).
- **Quiet operation** — per-frame event coalescing + a velocity dead-zone prevent
  the per-event beeps that plague naive smooth-scroll tools.
- **System tray UI** with live-tunable menus for every setting, persisted to
  `%AppData%\SmoothWheel\config.json`.
- **Global toggle hotkey** (`Ctrl+Alt+S`).
- **Start with Windows** (registry Run key).
- **Diagnostic logging** (`debug.log`) for correlating per-app behavior.
- Single-file, self-contained publish target (runs on any Win10/11 x64 without
  .NET installed).

### Performance
- ~0% CPU while idle (verified) — the hook fully unhooks when disabled, and the
  animation thread blocks on a wake signal rather than polling.

### Documentation
- Full README, CONTRIBUTING guide, and this changelog.

### License
- GNU GPL v3, copyright © 2026 ibrahim-shoil.

---

### Design notes (the journey to v1.0)

SmoothWheel's feel was tuned iteratively against real feedback. Key course
corrections:

1. **ease-out pulses → inertial model**: the original fixed-duration ease-out
   released most motion up front then crawled, and stacked new fast-start pulses
   instead of building speed. Switching to velocity+friction produced the
   recognizable trackpad "flick and coast."
2. **Impulse calibration**: `impulse = delta / time_constant` (one notch travels
   exactly one notch-distance) — an earlier `delta / 16` constant overshot ~19×.
3. **Timer resolution**: `Sleep(8)` doesn't sleep 8 ms on default Windows — it
   sleeps ~15.6 ms, halving the real update rate. `timeBeginPeriod(1)` fixed it.
4. **No spin-waits**: a tight spin to hit frame boundaries starved the
   `WH_MOUSE_LL` hook thread (lag returned under cursor movement). Sleep-based
   pacing fixed it without raising idle CPU.
5. **Rate-limited emission, not raw physics**: physics runs at 200 Hz, but
   injected events are rate-limited per app — flooding synchronous Win32 renderers
   at 600 events/sec caused the "lag and delay" they're known for.
