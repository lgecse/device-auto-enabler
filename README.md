# Device Auto Enabler

A small, self-contained **.NET 8 Windows service** that keeps configured devices **enabled**. It reacts to hardware changes via WMI and automatically re-enables any configured device it finds in a *disabled* state.

It was built for the **ASUS ROG Xbox Ally X** handheld + **GIGABYTE AORUS RTX 5060 Ti AI Box** eGPU case, where an external GPU can end up disabled after a dock/undock or resume, but it works for any device you can name in Device Manager.

- Runs as the `LocalSystem` Windows service `DeviceAutoEnabler` (auto-start at boot), so it has the rights to change device state.
- **Event-driven** detection via WMI `Win32_DeviceChangeEvent` (near-instant reaction on hotplug/dock) plus one initial scan at service start.
- Near-zero idle cost: no periodic polling, scoped enumeration, per-device cooldown, and transition-only logging.

---

## Install

1. Download `DeviceAutoEnabler-Setup-<version>.exe` (and the matching `.sha256`) from the [Releases](../../releases) page.
2. (Recommended) Verify the download in PowerShell:

   ```powershell
   Get-FileHash .\DeviceAutoEnabler-Setup-<version>.exe -Algorithm SHA256
   # Compare the printed hash with the contents of the .sha256 file.
   ```

3. Run the installer and accept the UAC prompt (installation and device management require Administrator).

The installer will:

- install the executable to `C:\Program Files\DeviceAutoEnabler\`,
- create `C:\ProgramData\DeviceAutoEnabler\config.json` from the shipped example **only if it does not already exist** (your edits survive upgrades),
- lock down `C:\ProgramData\DeviceAutoEnabler\` so only **Administrators** and **SYSTEM** can write it and **Users** can only read it,
- register and start the `DeviceAutoEnabler` service as `LocalSystem` with automatic startup.

### Uninstall

Uninstall from **Settings → Apps** (or *Add/Remove Programs*). This stops and removes the service and deletes the logs. Your `config.json` is intentionally left in place; delete `C:\ProgramData\DeviceAutoEnabler\` manually if you want it gone.

---

## Configure

Edit `C:\ProgramData\DeviceAutoEnabler\config.json`. The service watches this file and **hot-reloads** on save. If you save an invalid file, it is rejected and the last-good configuration is kept (the rejection is logged), so a typo never crash-loops the service.

> **You must edit this file with an elevated editor.** The installer intentionally locks the folder down so only **Administrators** and **SYSTEM** can write it and **Users** are read-only. This is deliberate: the service runs as `LocalSystem`, and this file controls which disabled devices it force-enables, so a standard user must not be able to change it. Opening it in a normal (non-elevated) editor lets you read but **not save**.
>
> The right way to edit it is to launch your editor **as administrator** — for example, from an elevated PowerShell/Terminal:
>
> ```powershell
> # Run this from an elevated (Administrator) prompt
> notepad "C:\ProgramData\DeviceAutoEnabler\config.json"
> ```
>
> You should **not** need to take ownership of the file; if you did, it was only because the editor wasn't elevated. Taking ownership also weakens the folder's protection, so prefer editing elevated instead.

### Finding a device's name / hardware ID

1. Open **Device Manager** (`Win+X` → Device Manager).
2. Find your device, right-click → **Properties** → **Details** tab.
3. From the **Property** dropdown, read:
   - **Friendly name** — use with `"matchOn": "friendlyName"` (falls back to *Device description* if a device has no friendly name).
   - **Device description** — use with `"matchOn": "deviceDesc"`.
   - **Hardware Ids** — use with `"matchOn": "hardwareId"` (e.g. `PCI\VEN_10DE&DEV_2D04`). Hardware IDs are the most reliable, since they don't change with driver naming.

To scope scans to a single device class (cheaper), read the **Class Guid** property on the same Details tab and put it in `classGuid`. For GPUs this is the *Display adapters* class `4d36e968-e325-11ce-bfc1-08002be10318`.

### Config reference

```json
{
  "eventDebounceMs": 2000,
  "perDeviceCooldownSeconds": 60,
  "regexMatchTimeoutMs": 250,
  "logLevel": "Information",
  "logMaxFileBytes": 5242880,
  "logRetainedFileCount": 5,
  "devices": [
    { "match": "PCI\\VEN_10DE&DEV_2D04", "matchOn": "hardwareId", "mode": "contains", "classGuid": "4d36e968-e325-11ce-bfc1-08002be10318" }
  ]
}
```

The service always subscribes to WMI `Win32_DeviceChangeEvent` for near-instant reaction on hotplug/dock (no toggle). If WMI subscription fails, scans still run at startup and on config reload.

| Field | Type | Default | Description |
| --- | --- | --- | --- |
| `eventDebounceMs` | int (0–60000) | `2000` | Coalesce a burst of device-change events into a single scan. |
| `perDeviceCooldownSeconds` | int (0–86400) | `60` | Minimum gap between enable attempts on the *same* device (anti-flap / anti-spam). |
| `regexMatchTimeoutMs` | int (10–10000) | `250` | Match timeout for `regex` mode (ReDoS guard). |
| `logLevel` | string | `Information` | `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, or `None`. |
| `logMaxFileBytes` | long (4096–1073741824) | `5242880` | Rolling log file size cap before it rolls over. |
| `logRetainedFileCount` | int (0–1000) | `5` | Number of rolled log files to keep. |
| `devices` | array | `[]` | The device rules to match & enable (see below). |

**Device rule fields:**

| Field | Values | Default | Description |
| --- | --- | --- | --- |
| `match` | string (required) | — | The value to match against the chosen property. |
| `matchOn` | `friendlyName` \| `deviceDesc` \| `hardwareId` | `friendlyName` | Which device property to compare. |
| `mode` | `contains` \| `exact` \| `regex` | `contains` | `contains`/`exact` are case-insensitive; `regex` runs with the match timeout. |
| `classGuid` | GUID string (optional) | — | Scope enumeration to one setup class for cheaper scans. Omit to search all classes. |

---

## Build from source

Requires **.NET 8 SDK**.

```powershell
dotnet build src/DeviceAutoEnabler/DeviceAutoEnabler.csproj -c Release

dotnet publish src/DeviceAutoEnabler/DeviceAutoEnabler.csproj `
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

> The service targets `net8.0-windows` and uses Windows-only APIs (WMI + SetupAPI), so it **only runs on Windows**.
