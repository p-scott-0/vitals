using System.Net.NetworkInformation;

namespace Vitals.Core.Net;

/// <summary>
/// Aggregate download/upload throughput across all up, non-loopback adapters.
/// Runs on its own thread (never the thread pool) so a slow adapter enumeration can only ever
/// delay the next network sample, not pile up callbacks. Adapter enumeration — the expensive
/// call — happens every 15 s; the per-second work is just reading counters.
/// </summary>
public sealed class NetMonitor : IDisposable
{
    private readonly Thread _thread;
    private volatile bool _running = true;

    public double DownBytesPerSec { get; private set; }
    public double UpBytesPerSec { get; private set; }
    public string? LastError { get; private set; }

    public NetMonitor()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Vitals.Net" };
        _thread.Start();
    }

    private void Loop()
    {
        NetworkInterface[] nics = [];
        DateTime nicsAt = DateTime.MinValue;
        long lastRx = 0, lastTx = 0;
        DateTime lastAt = DateTime.MinValue;

        while (_running)
        {
            try
            {
                var now = DateTime.UtcNow;
                if ((now - nicsAt).TotalSeconds >= 15)
                {
                    nics = NetworkInterface.GetAllNetworkInterfaces()
                        .Where(n => n.OperationalStatus == OperationalStatus.Up
                                    && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                        .ToArray();
                    nicsAt = now;
                }

                long rx = 0, tx = 0;
                foreach (var nic in nics)
                {
                    try
                    {
                        var st = nic.GetIPStatistics();
                        rx += st.BytesReceived;
                        tx += st.BytesSent;
                    }
                    catch
                    {
                        // adapter vanished between enumerations; picked up next refresh
                    }
                }

                if (lastAt != DateTime.MinValue)
                {
                    double sec = Math.Max(0.2, (now - lastAt).TotalSeconds);
                    DownBytesPerSec = Math.Max(0, rx - lastRx) / sec;
                    UpBytesPerSec = Math.Max(0, tx - lastTx) / sec;
                }
                lastRx = rx;
                lastTx = tx;
                lastAt = now;
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
            Thread.Sleep(1000);
        }
    }

    public static string Format(double bytesPerSec) =>
        bytesPerSec >= 1_000_000 ? $"{bytesPerSec / 1_000_000:0.0} MB/s"
        : bytesPerSec >= 1_000 ? $"{bytesPerSec / 1_000:0} KB/s"
        : $"{bytesPerSec:0} B/s";

    public void Dispose() => _running = false;
}
