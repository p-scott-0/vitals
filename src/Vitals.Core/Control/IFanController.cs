using LibreHardwareMonitor.Hardware;

namespace Vitals.Core.Control;

/// <summary>
/// The (dormant) fan-control takeover seam. LibreHardwareMonitor exposes writable duty-cycle
/// controls on the same chip objects Vitals already reads — this interface wraps them so a
/// future curve engine can drive fans without touching the sensor layer or the UI shell.
///
/// NOT used while FanControl owns the fans: two writers on one header fight each other.
/// Taking over later = implement a curve engine on this interface, add a UI module, and
/// disable the corresponding controls in FanControl (or retire FanControl entirely).
/// </summary>
public interface IFanController
{
    IReadOnlyList<FanChannel> Channels { get; }

    /// <summary>Set a channel to a fixed duty cycle (0–100%).</summary>
    void SetDuty(string controlId, float percent);

    /// <summary>Hand the channel back to BIOS/firmware automatic control.</summary>
    void ReleaseToAuto(string controlId);
}

public sealed record FanChannel(string ControlId, string Name, string HardwareName);

public sealed class LhmFanController : IFanController
{
    private readonly SensorHub _hub;

    public LhmFanController(SensorHub hub) => _hub = hub;

    public IReadOnlyList<FanChannel> Channels =>
        _hub.Sensors
            .Where(t => t.Info.SensorType == nameof(SensorType.Control) && t.Sensor.Control != null)
            .Select(t => new FanChannel(t.Info.Id, t.Info.Name, t.Info.HardwareName))
            .ToList();

    public void SetDuty(string controlId, float percent)
    {
        var control = RequireControl(controlId);
        control.SetSoftware(Math.Clamp(percent, 0f, 100f));
    }

    public void ReleaseToAuto(string controlId)
    {
        var control = RequireControl(controlId);
        control.SetDefault();
    }

    private IControl RequireControl(string controlId)
    {
        var tracked = _hub.Get(controlId)
            ?? throw new ArgumentException($"Unknown control sensor '{controlId}'.");
        return tracked.Sensor.Control
            ?? throw new InvalidOperationException($"Sensor '{controlId}' is not writable.");
    }
}
