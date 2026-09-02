using System.Text;
using LibreHardwareMonitor.Hardware;

namespace Vitals.Core;

public sealed class TrackedSensor
{
    internal ISensor Sensor { get; }
    public SensorInfo Info { get; }
    public RingBuffer History { get; }

    private double _value = double.NaN;

    public TrackedSensor(ISensor sensor, SensorInfo info, int historyCapacity)
    {
        Sensor = sensor;
        Info = info;
        History = new RingBuffer(historyCapacity);
    }

    /// <summary>Last polled value, or null if the sensor has no reading.</summary>
    public double? Value
    {
        get { var v = _value; return double.IsNaN(v) ? null : v; }
        internal set => _value = value ?? double.NaN;
    }
}

/// <summary>
/// Owns the LibreHardwareMonitor Computer object and the polling threads. Read-only by design;
/// the write path for fans lives behind Control/IFanController and stays dormant while
/// FanControl owns the curves.
///
/// Two dedicated threads: the main loop (CPU/GPU/board/memory, every PollInterval) and a
/// storage loop (SMART queries, every StoragePollInterval). SMART reads can block for many
/// seconds when a disk is asleep, so they must never share a thread with the fast sensors.
/// </summary>
public sealed class SensorHub : IDisposable
{
    private const int HistorySeconds = 3600;

    private readonly Computer _computer;
    private readonly Dictionary<string, TrackedSensor> _byId = new();
    private readonly object _enumLock = new();
    private Thread? _mainThread;
    private Thread? _storageThread;
    private volatile bool _running;
    private long _lastPollTicks;
    private long _lastStorageTicks;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan StoragePollInterval { get; set; } = TimeSpan.FromSeconds(30);
    public bool StorageEnabled { get; }
    public event EventHandler? Updated;
    public string? LastError { get; private set; }
    public string? StorageError { get; private set; }

    /// <summary>Age of the last completed main poll — the UI shows a stall warning when this grows.</summary>
    public TimeSpan SinceLastPoll =>
        DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastPollTicks), DateTimeKind.Utc);

    public TimeSpan SinceLastStoragePoll =>
        DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastStorageTicks), DateTimeKind.Utc);

    public SensorHub(bool storageEnabled = true)
    {
        StorageEnabled = storageEnabled;
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsMemoryEnabled = true,
            IsStorageEnabled = storageEnabled,
        };
    }

    /// <summary>Opens the hardware and takes one synchronous fast-sensor poll (no storage).</summary>
    public void Open()
    {
        _computer.Open();
        PollMain();
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _mainThread = new Thread(MainLoop) { IsBackground = true, Name = "Vitals.SensorPoll" };
        _mainThread.Start();
        if (StorageEnabled)
        {
            _storageThread = new Thread(StorageLoop) { IsBackground = true, Name = "Vitals.StoragePoll" };
            _storageThread.Start();
        }
    }

    private void MainLoop()
    {
        while (_running)
        {
            var started = DateTime.UtcNow;
            try
            {
                PollMain();
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
            try { Updated?.Invoke(this, EventArgs.Empty); } catch { }
            var remaining = PollInterval - (DateTime.UtcNow - started);
            if (remaining > TimeSpan.Zero) Thread.Sleep(remaining);
        }
    }

    private void StorageLoop()
    {
        while (_running)
        {
            try
            {
                PollStorage();
                StorageError = null;
            }
            catch (Exception ex)
            {
                StorageError = ex.Message;
            }
            // sleep in slices so Dispose does not wait a full interval
            var until = DateTime.UtcNow + StoragePollInterval;
            while (_running && DateTime.UtcNow < until) Thread.Sleep(250);
        }
    }

    private static bool IsStorage(IHardware hw) => hw.HardwareType == HardwareType.Storage;

    private void PollMain()
    {
        foreach (var hw in _computer.Hardware)
            if (!IsStorage(hw)) UpdateHardware(hw);

        var now = DateTime.UtcNow;
        lock (_enumLock)
        {
            foreach (var hw in _computer.Hardware)
                if (!IsStorage(hw)) CollectSensors(hw, now);
        }
        Interlocked.Exchange(ref _lastPollTicks, DateTime.UtcNow.Ticks);
    }

    private void PollStorage()
    {
        foreach (var hw in _computer.Hardware)
            if (IsStorage(hw)) UpdateHardware(hw);

        var now = DateTime.UtcNow;
        lock (_enumLock)
        {
            foreach (var hw in _computer.Hardware)
                if (IsStorage(hw)) CollectSensors(hw, now);
        }
        Interlocked.Exchange(ref _lastStorageTicks, DateTime.UtcNow.Ticks);
    }

    private static void UpdateHardware(IHardware hw)
    {
        hw.Update();
        foreach (var sub in hw.SubHardware)
            UpdateHardware(sub);
    }

    private void CollectSensors(IHardware hw, DateTime now)
    {
        foreach (var s in hw.Sensors)
        {
            string id = s.Identifier.ToString();
            if (!_byId.TryGetValue(id, out var tracked))
            {
                tracked = new TrackedSensor(
                    s,
                    new SensorInfo(id, s.Name, hw.Name, hw.HardwareType.ToString(), s.SensorType.ToString()),
                    HistorySeconds);
                _byId[id] = tracked;
            }
            tracked.Value = s.Value;
            if (s.Value.HasValue)
                tracked.History.Add(now, s.Value.Value);
        }
        foreach (var sub in hw.SubHardware)
            CollectSensors(sub, now);
    }

    public IReadOnlyCollection<TrackedSensor> Sensors
    {
        get { lock (_enumLock) return _byId.Values.ToArray(); }
    }

    public TrackedSensor? Get(string? id)
    {
        if (id is null) return null;
        lock (_enumLock) return _byId.TryGetValue(id, out var t) ? t : null;
    }

    // ---- role auto-picks -------------------------------------------------

    /// <summary>Best-guess sensor for a metric role ("cpu.temp", "gpu.hotspot", ...); null if absent.</summary>
    public TrackedSensor? AutoPick(string role)
    {
        var all = Sensors;
        return role switch
        {
            "cpu.temp" => Prefer(all, "Cpu", "Temperature", ["Tctl/Tdie", "Tctl", "Tdie", "Core Max", "CPU Package", "Core Average"]),
            "cpu.load" => Prefer(all, "Cpu", "Load", ["CPU Total"]),
            "cpu.clock" => Prefer(all, "Cpu", "Clock", ["Cores (Average)", "Core #1"]),
            "gpu.temp" => Prefer(all, "GpuNvidia", "Temperature", ["GPU Core"]),
            // RTX 50-series no longer reports Hot Spot; fall back to VRAM junction temp
            "gpu.hotspot" => Prefer(all, "GpuNvidia", "Temperature", ["Hot Spot", "Memory Junction"]),
            "gpu.load" => Prefer(all, "GpuNvidia", "Load", ["GPU Core"]),
            "gpu.power" => Prefer(all, "GpuNvidia", "Power", ["GPU Package", "GPU Power"]),
            "board.vrm" => Prefer(all, "SuperIO", "Temperature", ["VRM", "MOS"]),
            "board.system" => Prefer(all, "SuperIO", "Temperature", ["System"]),
            // physical RAM, not the "Virtual Memory" hardware entry
            "ram.load" => all.FirstOrDefault(t => t.Info.SensorType == "Load" && t.Info.Id.StartsWith("/ram/", StringComparison.Ordinal)),
            // NVMe "Composite" first; skip the constant Warning/Critical threshold pseudo-sensors
            "ssd.temp" => all.Where(t => t.Info.HardwareType == "Storage" && t.Info.SensorType == "Temperature"
                                         && !t.Info.Name.Contains("Warning", StringComparison.OrdinalIgnoreCase)
                                         && !t.Info.Name.Contains("Critical", StringComparison.OrdinalIgnoreCase))
                             .OrderByDescending(t => t.Info.Name.Contains("Composite", StringComparison.OrdinalIgnoreCase))
                             .FirstOrDefault(),
            "fan.cpu" => ById(all, "/fan/0", "SuperIO"),
            "fan.pump" => ById(all, "/fan/1", "SuperIO"),
            "fan.case" => ById(all, "/fan/2", "SuperIO"),
            "fan.gpu" => Prefer(all, "GpuNvidia", "Fan", ["GPU Fan 1", "GPU Fan", "Fan"]),
            _ => null,
        };
    }

    private static TrackedSensor? Prefer(
        IReadOnlyCollection<TrackedSensor> all, string hwType, string sensorType, string[] namePreferences)
    {
        var pool = all.Where(t => t.Info.HardwareType == hwType && t.Info.SensorType == sensorType).ToList();
        foreach (var pref in namePreferences)
        {
            var hit = pool.FirstOrDefault(t => t.Info.Name.Contains(pref, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return pool.FirstOrDefault();
    }

    private static TrackedSensor? ById(IReadOnlyCollection<TrackedSensor> all, string idSuffix, string hwType) =>
        all.FirstOrDefault(t => t.Info.HardwareType == hwType && t.Info.Id.EndsWith(idSuffix, StringComparison.Ordinal));

    /// <summary>RPM sensor paired with its duty-% sensor (same index on the same hardware), for fan tiles.</summary>
    public List<(TrackedSensor Rpm, TrackedSensor? Percent)> FanPairs()
    {
        var all = Sensors;
        var result = new List<(TrackedSensor, TrackedSensor?)>();
        foreach (var fan in all.Where(t => t.Info.SensorType == "Fan")
                              .OrderBy(t => t.Info.HardwareType).ThenBy(t => t.Info.Id))
        {
            string controlId = fan.Info.Id.Replace("/fan/", "/control/");
            var pct = all.FirstOrDefault(t => t.Info.Id == controlId && t.Info.SensorType == "Control");
            result.Add((fan, pct));
        }
        return result;
    }

    public string DumpText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Vitals sensor dump — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('=', 72));
        foreach (var group in Sensors.GroupBy(t => (t.Info.HardwareType, t.Info.HardwareName)))
        {
            sb.AppendLine();
            sb.AppendLine($"[{group.Key.HardwareType}] {group.Key.HardwareName}");
            foreach (var t in group.OrderBy(t => t.Info.SensorType).ThenBy(t => t.Info.Id))
            {
                string val = t.Value.HasValue ? t.Value.Value.ToString("0.##") : "-";
                sb.AppendLine($"  {t.Info.SensorType,-12} {t.Info.Name,-28} = {val,10}   {t.Info.Id}");
            }
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        _running = false;
        _mainThread?.Join(TimeSpan.FromSeconds(3));
        _storageThread?.Join(TimeSpan.FromSeconds(1)); // may be mid-SMART-query; don't wait on it
        try { _computer.Close(); } catch { }
    }
}
