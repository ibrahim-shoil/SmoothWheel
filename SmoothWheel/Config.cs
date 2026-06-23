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

using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmoothWheel;

/// <summary>
/// Persisted user settings. Stored as JSON in
/// <c>%AppData%\SmoothWheel\config.json</c> so they survive restarts.
/// </summary>
public sealed class Config
{
    public bool Enabled { get; set; } = true;

    /// <summary>Time constant of the inertial glide, in milliseconds. Smaller = snappier stop; larger = lazier coast.</summary>
    public int SmoothnessDurationMs { get; set; } = 100;

    /// <summary>
    /// Independent speed multiplier. Scales how far/fast one notch travels without
    /// changing the glide duration (feel of deceleration). 1.0 = one notch-distance
    /// per notch; higher = faster, more distance per flick.
    /// </summary>
    public double SpeedFactor { get; set; } = 1.5;

    /// <summary>
    /// Maximum scroll velocity, in notches-per-time. Caps how much momentum fast
    /// scrolling can build so it never runs away and stays controllable — a real
    /// trackpad has a max speed (your finger can only move so fast). Without this,
    /// rapid same-direction flicks sum without limit and the page flies off,
    /// making new input imperceptible while it coasts. Default 3 = up to ~3 notches'
    /// worth of velocity at once.
    /// </summary>
    public double MaxVelocity { get; set; } = 3.0;

    /// <summary>Number of micro-steps a single notch is split into. Lower = fewer, larger events (quieter); higher = finer.</summary>
    public int Precision { get; set; } = 12;

    /// <summary>Flip scroll direction (natural-scroll style). On by default per user preference.</summary>
    public bool InvertDirection { get; set; } = true;

    /// <summary>Virtual-key code for the enable/disable hotkey (default 'S').</summary>
    public int HotkeyVk { get; set; } = 0x53; // 'S'

    /// <summary>Modifier flags (MOD_CONTROL | MOD_ALT etc.) for the toggle hotkey.</summary>
    public uint HotkeyModifiers { get; set; } = NativeMethods.ModControl | NativeMethods.ModAlt;

    public bool StartWithWindows { get; set; } = false;

    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SmoothWheel");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize<Config>(json, JsonOptions);
                if (cfg != null)
                {
                    cfg.Clamp();
                    return cfg;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable config — fall back to defaults silently rather
            // than blocking the app. The next Save() will rewrite a clean file.
        }
        return new Config();
    }

    public void Save()
    {
        try
        {
            Clamp();
            Directory.CreateDirectory(DirectoryPath);
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Settings persistence is best-effort; never crash the utility over it.
        }
    }

    /// <summary>Keep stored values inside sane bounds so a hand-edited file can't break scrolling.</summary>
    private void Clamp()
    {
        SmoothnessDurationMs = Math.Clamp(SmoothnessDurationMs, 40, 1000);
        Precision = Math.Clamp(Precision, 2, 64);
    }
}
