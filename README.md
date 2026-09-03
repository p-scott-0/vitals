<p align="center">
  <img src="src/Vitals.App/Assets/vitals-256.png" width="96" alt="Vitals icon">
</p>

# Vitals

A system vitals dashboard for Windows gaming PCs: temperature gauges, fan speeds, history graphs, a lightweight click-through game overlay, and FPS capture with session logging. Built as the *display* half of a Dragon Center replacement — fan **control** stays in [FanControl](https://github.com/Rem0o/FanControl.Releases), which Vitals coexists with.

## Download

Grab **Vitals.exe** from the [latest release](../../releases/latest) and run it. It's a standalone single file — no installer, no .NET runtime to install. Windows will ask for admin rights on launch: that's required to read CPU and motherboard sensors (FanControl and HWiNFO need the same).

To install (or update) it properly, run it once with `--install`: it copies itself to `%LOCALAPPDATA%\Programs\Vitals`, adds a Start Menu shortcut, and hands over from any running copy — one UAC prompt for the whole thing.

```
Vitals.exe --install
```

Tick **Start with Windows** in Settings to have it launch at logon without a UAC prompt (it registers an elevated scheduled task).

## What it shows

- **Gauges** — CPU (Tctl/Tdie), GPU core, GPU VRAM/Hot Spot, VRM — with warn/hot colour zones and load/power subtexts.
- **Fans** — RPM and duty % for every board header and GPU fan; hide unused headers and rename fans in Settings.
- **System tiles** — CPU/GPU load, GPU power, RAM, system temp, SSD temp.
- **History charts** — temperatures, fan speeds and FPS over a configurable 1–60 minute window.
- **Overlay** — Razer Cortex–style strip (or vertical stack) grouping metrics under FPS / CPU / GPU / RAM / BOARD / FANS / NET / PLAY / TIME. Configurable metrics, fan readout (RPM, duty %, or both), clock format, header/value/background colours, background opacity, scale (drag the corner), and position (drag it). Click-through when locked. Renders over borderless/windowed fullscreen games.
- **FPS** — via Intel [PresentMon](https://github.com/GameTechDev/PresentMon) (passive ETW frame telemetry, no game injection). Auto-attaches to the foreground game, shows live FPS, and logs each session (per-second FPS, average, 1 % lows) to `%APPDATA%\Vitals\logs\`.

Settings live in `%APPDATA%\Vitals\settings.json`.

## Notes on coexistence

- LibreHardwareMonitor-based apps share a global mutex for Super-I/O bus access, so running Vitals alongside FanControl is safe.
- Storage (SMART) sensors are polled on their own thread — a sleeping hard drive can block a SMART read for seconds — and can be turned off in Settings.
- RTX 50-series GPUs no longer report a Hot Spot sensor; Vitals shows the VRAM junction temperature instead.

## Build from source

Requires the .NET 10 SDK.

```
dotnet build -c Release
```

Standalone single-file exe (what the release workflow produces):

```
dotnet publish src/Vitals.App/Vitals.App.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

Releases: every push to `main` refreshes the rolling **latest** release; pushing a `v*` tag (e.g. `git tag v1.1.0 && git push --tags`) creates a versioned release. The version shown in the app comes from the tag, or from `<Version>` in `Vitals.App.csproj` for main builds.

## Architecture

- **Vitals.Core** — sensor engine on `LibreHardwareMonitorLib` (CPU/GPU/board/memory on a 1 s thread, storage on its own), PresentMon-based FPS service, network throughput monitor, settings store. No UI dependencies.
- **Vitals.App** — WPF dashboard (custom-drawn arc gauges, history charts, fan tiles), tray icon, overlay window.
- **Vitals.Probe** — console tool that dumps every detected sensor to a text file (run elevated). Useful for checking what LibreHardwareMonitor sees on a machine.

### Fan-control takeover seam (dormant)

`Vitals.Core/Control/IFanController.cs` wraps LibreHardwareMonitor's fan `IControl` write path (`SetSoftware` / `SetDefault`) — the same mechanism FanControl uses. Nothing in the app calls it yet; it exists so Vitals can absorb fan-curve duty later without re-architecting. Never run two programs controlling the same fan header at once.

## Credits

- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) — sensor access (MPL 2.0).
- [Intel PresentMon](https://github.com/GameTechDev/PresentMon) — frame telemetry, bundled under the MIT license (see `tools/presentmon/LICENSE.txt`).
