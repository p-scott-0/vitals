namespace Vitals.Core;

public sealed record SensorInfo(
    string Id,
    string Name,
    string HardwareName,
    string HardwareType,
    string SensorType);

/// <summary>Fixed-capacity time/value history, safe for one writer + snapshot readers.</summary>
public sealed class RingBuffer
{
    private readonly double[] _values;
    private readonly double[] _unixSeconds;
    private readonly int _capacity;
    private int _next;
    private int _count;
    private readonly object _lock = new();

    public RingBuffer(int capacity)
    {
        _capacity = capacity;
        _values = new double[capacity];
        _unixSeconds = new double[capacity];
    }

    public void Add(DateTime utcNow, double value)
    {
        lock (_lock)
        {
            _values[_next] = value;
            _unixSeconds[_next] = (utcNow - DateTime.UnixEpoch).TotalSeconds;
            _next = (_next + 1) % _capacity;
            if (_count < _capacity) _count++;
        }
    }

    /// <summary>Oldest-to-newest points no older than <paramref name="window"/>.</summary>
    public (double[] UnixSeconds, double[] Values) Snapshot(TimeSpan window)
    {
        lock (_lock)
        {
            double cutoff = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds - window.TotalSeconds;
            var times = new List<double>(_count);
            var vals = new List<double>(_count);
            int start = (_next - _count + _capacity) % _capacity;
            for (int i = 0; i < _count; i++)
            {
                int idx = (start + i) % _capacity;
                if (_unixSeconds[idx] >= cutoff)
                {
                    times.Add(_unixSeconds[idx]);
                    vals.Add(_values[idx]);
                }
            }
            return (times.ToArray(), vals.ToArray());
        }
    }
}
