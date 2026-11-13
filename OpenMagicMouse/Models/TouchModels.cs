using System.Text.Json;

namespace OpenMagicMouse.Models;

/// <summary>
/// Single decoded capacitive touch point from Apple Magic Mouse 2 (PID_0269).
/// Coordinate ranges on surface: X in [-1100 .. +1258] (left -> right),
/// Y in [-2047 .. +1589] (-Y = rear/palm, +Y = front/Lightning port).
/// </summary>
public readonly record struct TouchFinger(
    int Id,
    int X,
    int Y,
    byte TouchMajor,
    byte TouchMinor,
    byte Size,
    byte State)
{
    /// <summary>
    /// 0x00 = released, 0x30 = touch start, 0x40 = active contact/drag.
    /// </summary>
    public bool IsDown => State == 0x30 || State == 0x40;
}

public sealed class TouchFrame
{
    public long TimestampMs { get; init; }
    public byte Clicks { get; init; }
    public short Dx { get; init; }
    public short Dy { get; init; }
    public int BatteryPercent { get; init; } = -1;
    public bool IsConnected { get; init; }
    public TouchFinger[] Fingers { get; init; } = [];
}

public enum GestureAction
{
    None,
    VirtualDesktopLeft,
    VirtualDesktopRight,
    TaskView,
    ShowDesktop,
    BrowserBack,
    BrowserForward,
    MiddleClick,
    AltTabNext,
    AltTabPrev,
    MaximizeWindow,
    MinimizeWindow
}

public sealed class GestureConfig
{
    public int ConfigVersion { get; set; } = 3;
    public bool Enabled { get; set; } = true;
    public bool SmoothScrollEnabled { get; set; } = true;
    public bool NaturalScroll { get; set; } = true;
    public double ScrollSpeed { get; set; } = 1.0;
    public bool NaturalTwoFingerSwipe { get; set; } = true;
    public string TwoFingerHorizontalAction { get; set; } = "VirtualDesktop";
    public string TwoFingerDoubleTapAction { get; set; } = "TaskView";
    public string TwoFingerSwipeUpAction { get; set; } = "TaskView";
    public string TwoFingerSwipeDownAction { get; set; } = "ShowDesktop";
    public string OneFingerFastFlickAction { get; set; } = "BrowserBackForward";
    public string ThreeFingerTapAction { get; set; } = "MiddleClick";
    public string ThreeFingerHorizontalAction { get; set; } = "AltTab";
    public double Sensitivity { get; set; } = 1.0;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    public static string ConfigFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OpenMagicMouse",
        "config.json");

    public static GestureConfig LoadOrDefault()
    {
        try
        {
            var path = ConfigFilePath;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                bool hasVersionField = json.Contains("\"ConfigVersion\"", StringComparison.Ordinal);
                var cfg = JsonSerializer.Deserialize<GestureConfig>(json, JsonOpts);
                if (cfg != null)
                {
                    cfg.Sensitivity = Math.Clamp(cfg.Sensitivity, 0.5, 2.0);
                    cfg.ScrollSpeed = Math.Clamp(cfg.ScrollSpeed, 0.4, 2.2);
                    if (!hasVersionField || cfg.ConfigVersion < 2)
                    {
                        cfg.ConfigVersion = 3;
                        cfg.SmoothScrollEnabled = true;
                        cfg.NaturalScroll = true;
                        cfg.ScrollSpeed = 1.0;
                        cfg.NaturalTwoFingerSwipe = true;
                        cfg.Sensitivity = 1.0;
                        if (string.Equals(cfg.TwoFingerHorizontalAction, "None", StringComparison.OrdinalIgnoreCase))
                        {
                            cfg.TwoFingerHorizontalAction = "VirtualDesktop";
                        }
                        cfg.Save();
                    }
                    else if (cfg.ConfigVersion < 3)
                    {
                        cfg.ConfigVersion = 3;
                        cfg.SmoothScrollEnabled = true;
                        cfg.ScrollSpeed = 1.0;
                        cfg.Save();
                    }
                    return cfg;
                }
            }
        }
        catch
        {
            // Resilient fallback to macOS defaults
        }

        return new GestureConfig();
    }

    public void Save()
    {
        try
        {
            var path = ConfigFilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // Ignore non-fatal config persistence errors
        }
    }

    public GestureConfig Clone() => new()
    {
        ConfigVersion = ConfigVersion,
        Enabled = Enabled,
        SmoothScrollEnabled = SmoothScrollEnabled,
        NaturalScroll = NaturalScroll,
        ScrollSpeed = ScrollSpeed,
        NaturalTwoFingerSwipe = NaturalTwoFingerSwipe,
        TwoFingerHorizontalAction = TwoFingerHorizontalAction,
        TwoFingerDoubleTapAction = TwoFingerDoubleTapAction,
        TwoFingerSwipeUpAction = TwoFingerSwipeUpAction,
        TwoFingerSwipeDownAction = TwoFingerSwipeDownAction,
        OneFingerFastFlickAction = OneFingerFastFlickAction,
        ThreeFingerTapAction = ThreeFingerTapAction,
        ThreeFingerHorizontalAction = ThreeFingerHorizontalAction,
        Sensitivity = Sensitivity
    };
}
