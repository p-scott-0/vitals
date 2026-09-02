using Vitals.Core;

// Dumps every sensor LibreHardwareMonitor can see to a text file (and stdout).
// Usage: Vitals.Probe.exe [output-path] — must run elevated (the manifest enforces it).

string outPath = args.Length > 0 ? args[0] : "vitals-sensors.txt";

using var hub = new SensorHub();
try
{
    hub.Open();
}
catch (Exception ex)
{
    string err = "FATAL: sensor init failed: " + ex;
    Console.Error.WriteLine(err);
    File.WriteAllText(outPath, err);
    return 1;
}

// a few extra polls so slow sensors (storage, EC) populate
hub.Start();
Thread.Sleep(6000);

string dump = hub.DumpText();
File.WriteAllText(outPath, dump);
Console.WriteLine(dump);
Console.WriteLine($"Written to {Path.GetFullPath(outPath)}");
return 0;
