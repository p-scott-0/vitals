using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vitals.Core.Config;

public sealed class VitalsSettings
{
    // general
    public int PollSeconds { get; set; } = 1;
    public bool CloseToTray { get; set; } = true;
    public bool FpsEnabled { get; set; } = true;
    public bool FpsLogging { get; set; } = true;
    /// <summary>Poll SSD/HDD SMART sensors on their own thread. Turn off if a sleeping disk causes stalls. Applies at next launch.</summary>
    public bool StorageSensors { get; set; } = true;

    // dashboard
    public int ChartWindowMinutes { get; set; } = 5;
    /// <summary>Fan sensor id → explicit show/hide. Absent = automatic (GPU fans always, board fans once seen spinning).</summary>
    public Dictionary<string, bool> FanVisibility { get; set; } = new();
    /// <summary>Fan sensor id → user nickname.</summary>
    public Dictionary<string, string> FanNames { get; set; } = new();

    // overlay
    public bool OverlayEnabled { get; set; }
    public bool OverlayLocked { get; set; } = true;
    public bool OverlayHorizontal { get; set; } = true;
    public double OverlayOpacity { get; set; } = 1.0;
    public double OverlayBackgroundOpacity { get; set; } = 0.75;
    public double OverlayScale { get; set; } = 1.0;
    public double OverlayX { get; set; } = 40;
    public double OverlayY { get; set; } = 40;
    public string OverlayHeaderColor { get; set; } = "#22D3EE";
    public string OverlayValueColor { get; set; } = "#E8ECF4";
    public string OverlayBackgroundColor { get; set; } = "#0F1216";
    public string TimeFormat { get; set; } = "HH:mm";
    /// <summary>How FANS group entries read on the overlay: "rpm", "pct" (duty %), or "both".</summary>
    public string OverlayFanMode { get; set; } = "rpm";

    // games
    /// <summary>Process names treated as games (auto-learned from sustained FPS, or added by hand).</summary>
    public List<string> KnownGames { get; set; } = new();

    /// <summary>Metric roles shown on the overlay (grouped automatically by prefix).</summary>
    public List<string> OverlayMetrics { get; set; } =
        ["fps", "cpu.temp", "cpu.load", "gpu.temp", "gpu.load", "ram.load", "net.down", "time"];

    /// <summary>Role → LibreHardwareMonitor sensor identifier overrides (else auto-picked).</summary>
    public Dictionary<string, string> SensorMap { get; set; } = new();

    /// <summary>Reserved for the future fan-control module. Unused while FanControl owns the fans.</summary>
    public JsonElement? Control { get; set; }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vitals");

    public static string FilePath => Path.Combine(Dir, "settings.json");

    public static VitalsSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<VitalsSettings>(File.ReadAllText(FilePath), Options) ?? new VitalsSettings();
        }
        catch
        {
            // corrupt settings fall back to defaults; the bad file is replaced on next save
        }
        return new VitalsSettings();
    }

    public static void Save(VitalsSettings settings)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
    }
}
