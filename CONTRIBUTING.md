# Contributing to SmoothWheel

Thanks for your interest in improving SmoothWheel! This is a small, focused
project — contributions of any size are welcome.

## Reporting issues

The most useful bug reports are **specific and reproducible**. Please include:

1. **The app** where scrolling feels wrong (e.g. "File Explorer", "Firefox", "Windows Terminal").
2. **The symptom** — one of:
   - Choppy / stuttery (visible stepping)
   - Laggy / delayed (response latency)
   - Too fast / too slow / too much momentum
   - Sound (beeping/buzzing) while scrolling
3. **Your settings** (Glide duration, Precision, Speed, Max momentum) — or note "defaults".
4. **A debug log** if possible — enable **Debug logging** in the tray menu, scroll
   a few times in the problem app, then attach `%LocalAppData%\SmoothWheel\debug.log`.
   This captures the exact event stream per app and makes the cause obvious.
5. Windows version + your monitor refresh rate(s) (multi-monitor matters).

## Suggesting app-specific tuning

SmoothWheel maintains a list of apps that render wheel events synchronously and
need a coarser event stream. If an app scrolls poorly and isn't on the list, open
an issue with the app's process name (shown in Task Manager → Details) so it can
be added in `ScrollEngine.cs` → `s_coarseApps`.

## Building from source

```bat
git clone https://github.com/ibrahim-shoil/SmoothWheel.git
cd SmoothWheel\SmoothWheel
dotnet build -c Release
dotnet run -c Release
```

See the [README](README.md) for publish/single-file build instructions.

## Code style

- C# 12, file-scoped namespaces, nullable reference types enabled.
- Match the surrounding style: minimal comments on obvious code, thorough comments
  on anything non-obvious (the physics and hook threading have extensive comments
  for good reason).
- Keep P/Invoke declarations in `NativeMethods.cs`.
- Don't introduce busy-waits/spin-loops in the animation path — they starve the
  hook thread and reintroduce lag.

## Submitting changes

1. Fork & branch from `main`.
2. Make your change, keep it focused.
3. `dotnet build -c Release` must pass with **0 warnings**.
4. Open a pull request describing **what** changed and **why**, including how it
   was tested (which apps, which feel).

## Areas that especially need help

- An **installer** (Inno Setup / WiX) and **winget / scoop** manifests
- **Code signing** guidance (or a sponsor for a cert) to remove the SmartScreen warning
- **Per-app profile** tuning — the more apps we characterize, the better it feels
- **Accessibility** review

By contributing, you agree your contributions are licensed under the project's
GPL-3.0 license.
