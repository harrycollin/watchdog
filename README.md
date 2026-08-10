# Kiosk Watchdog

One Windows app: **`KioskWatchdog.exe`**

| How you start it | What it does |
|------------------|--------------|
| Start Menu / desktop | Configuration & status UI |
| Windows Service (`--service`) | Unattended monitoring (installed by Setup) |

## Install or update

Run **`KioskWatchdogSetup-<version>.exe`** from a [GitHub Release](../../releases) (admin).

- Installs to `C:\Program Files\KioskWatchdog`
- Registers and starts the **KioskWatchdog** service
- Creates a Start Menu shortcut
- **Does not** write a sample `config.json` — open the UI, pick your app, and Save
- **Running Setup again upgrades that same install** (same AppId / folder). It does not create a second copy.
- Existing `C:\ProgramData\KioskWatchdog\config.json` is kept on upgrade

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

## Develop

```bat
dotnet test
dotnet publish src\KioskWatchdog.UI\KioskWatchdog.UI.csproj -c Release -r win-x64 --self-contained true -o artifacts\publish
```

`KioskWatchdog.TestApp` is for local failure simulation only (not installed).

Electron should expose `GET http://127.0.0.1:<port>/health` — see `docs/electron-health-server.example.js`.
