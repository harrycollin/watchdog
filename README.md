# Kiosk Watchdog

One Windows app: **`KioskWatchdog.exe`**

| How you start it | What it does |
|------------------|--------------|
| Start Menu / desktop | Configuration & status UI |
| Windows Service (`--service`) | Unattended monitoring (installed by Setup) |

## Install or update

Run **`KioskWatchdogSetup-<version>.exe`** from a [GitHub Release](../../releases) (admin).

- Installs to `C:\Program Files\KioskWatchdog`
- Registers and starts the **KioskWatchdog** service (automatic start at boot by default)
- Creates a Start Menu shortcut (does **not** open the UI at login)
- **Does not** write a sample `config.json` — open the UI, pick your app, and Save
- **Running Setup again upgrades that same install** (same AppId / folder). It does not create a second copy.
- Existing `C:\ProgramData\KioskWatchdog\config.json` is kept on upgrade

In the UI **Global settings**, you can turn off “Start watchdog service automatically when Windows boots” (`service.startOnBoot` in config). That only changes the Windows Service start type — the configuration window never auto-opens.

### Create a release

```bash
git tag v1.0.1
git push origin v1.0.1
```

Or run **Actions → Build and Release → Run workflow** and set a version.

That publishes `KioskWatchdogSetup-<version>.exe` (and a portable zip) on the repo Releases page. Artifacts are also attached to the workflow run.

## Config & logs

- Config: `C:\ProgramData\KioskWatchdog\config.json`
- Logs: `C:\ProgramData\KioskWatchdog\logs\`
- Status / commands: `status.json` and `command.json` in the same ProgramData folder

### Webhook notifications

Optional machine-wide webhook under `notifications.webhook` (also editable in the UI). The service POSTs JSON in the background so a slow or failing endpoint cannot block monitoring.

- **Events** — selectable transitions (`restartLimitReached`, `error`, `restart`, `unhealthy`, `recovered`)
- **Status report** — optional periodic POST listing every configured watch and its live status (heartbeat-style)

The kiosk must be able to reach the webhook URL over the network. See `config/config.example.json` for the full shape.

### Multi-app configuration

One service can watch several apps. Each entry has a `kind`:

- **`process`** — monitor a `.exe` (Electron / Win32 kiosk). Optional HTTP health.
- **`http`** — start/stop a local website via shell commands (`npm start`, `dotnet run`, …). Health URL is required and used as liveness.

```json
{
  "applications": [
    {
      "id": "kiosk-main",
      "enabled": true,
      "kind": "process",
      "application": {
        "executablePath": "C:\\Kiosk\\App\\App.exe",
        "arguments": "",
        "workingDirectory": "C:\\Kiosk\\App",
        "displayName": "My Kiosk"
      },
      "monitoring": { "processCheckIntervalSeconds": 5, "healthCheckIntervalSeconds": 10, "healthTimeoutSeconds": 45, "gracefulTerminationTimeoutSeconds": 10 },
      "restart": { "restartOnExit": true, "restartOnUnhealthy": true, "restartDelaySeconds": 5, "maxRestarts": 5, "restartWindowMinutes": 10 },
      "health": { "enabled": false, "type": "http", "url": "" },
      "launch": { "mode": "interactive" }
    },
    {
      "id": "local-site",
      "enabled": true,
      "kind": "http",
      "application": { "displayName": "Local website" },
      "http": {
        "startCommand": "npm start",
        "stopCommand": "",
        "workingDirectory": "C:\\Sites\\MyApp"
      },
      "health": { "enabled": true, "type": "http", "url": "http://127.0.0.1:3000/health" },
      "launch": { "mode": "interactive" }
    }
  ]
}
```

HTTP apps run `startCommand` through `cmd.exe /c` (interactive session when the service is in Session 0). Leave `startCommand` empty for probe-only monitoring. If `stopCommand` is empty, the watchdog kills the started command’s process tree.

Also supported: **`tcp`** (localhost port check + optional start/stop commands) and **`windowsService`** (monitor/restart by service name). HTTP health accepts `expectedStatusCode` (default 200).

### Schedules

Optional per-app `schedule` (also in the UI under **Schedule**). Uses the PC’s **local** time and is re-checked every monitor tick:

```json
"schedule": {
  "enabled": true,
  "startTime": "09:00",
  "endTime": "18:00",
  "daysOfWeek": ["monday", "tuesday", "wednesday", "thursday", "friday"]
}
```

Outside the window the app is stopped (`OutsideSchedule`) without treating it as a manual stop, so it can start again when the window opens. If `endTime` is earlier than `startTime`, the window wraps past midnight (day = the day the window starts).

### Resource limits

Optional per-app `resources` (UI: **Resources**). Restart when working-set RAM and/or CPU stay over a threshold for `breachDurationSeconds` (default 300). Set a limit to `0` to ignore that metric. `includeChildProcesses` (default true) sums Electron/Chromium helpers that share the same `.exe` path.

```json
"resources": {
  "enabled": true,
  "maxMemoryMegabytes": 1536,
  "maxCpuPercent": 90,
  "breachDurationSeconds": 300,
  "includeChildProcesses": true
}
```

The UI lists targets with type labels; Start / Stop / Restart target the selected id. Live status shows **uptime**, **memory/CPU**, and the next **schedule** transition. **Logs** opens an in-app viewer over the rolling ProgramData log files (also “Open folder”). Closing the UI minimizes to the tray.

## Develop

```bat
dotnet test tests\KioskWatchdog.Core.Tests
dotnet publish src\KioskWatchdog.UI\KioskWatchdog.UI.csproj -c Release -r win-x64 --self-contained true -o artifacts\publish
```

CI also runs Windows integration tests against `KioskWatchdog.TestApp` (crash / exit / health-fail) and a minimal Electron fixture in `fixtures/electron-health-app`.

`KioskWatchdog.TestApp` is for local failure simulation only (not installed).

Electron should expose `GET http://127.0.0.1:<port>/health` — see `docs/electron-health-server.example.js`.
