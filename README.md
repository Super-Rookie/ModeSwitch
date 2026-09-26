# ModeSwitch

A Windows tray app that flips a PC between a **Movie** preset and a **Game** preset with one click.

It was built for a home-theatre PC with an NVIDIA GPU, where the settings that suit gaming
(hardware-accelerated GPU scheduling, G-SYNC, HDR, an undervolt) caused stutter in madVR/MPC-HC
video playback. Instead of changing half a dozen settings by hand every time, the tray icon does it.

| Tray icon | Meaning |
|---|---|
| Blue film | Movie mode |
| Green gamepad | Game mode |
| Amber | switching, or a reboot is still needed |

## What each mode changes

| Setting | Movie | Game |
|---|---|---|
| Hardware-accelerated GPU scheduling (HAGS) | off | on |
| NVIDIA G-SYNC | off | full screen + windowed |
| NVIDIA Vertical Sync | On | On |
| Windows HDR (every HDR-capable display) | off | on |
| GPU voltage/frequency curve | Afterburner `Profile2` (stock) | Afterburner `Profile1` (undervolt) |
| Afterburner + RTSS | closed | running |

Every value is set in `bin\config.ini`, so the presets can be changed without rebuilding.

HAGS only changes after a reboot. After a switch, ModeSwitch waits until every other setting is
applied, then shows a dialog summarising what changed and asks whether to reboot now.

## Requirements

- Windows 11 (HDR detection uses the 24H2+ colour API, with a fallback for older builds)
- An NVIDIA GPU and driver (uses `nvapi64.dll`, installed with the driver)
- .NET Framework 4.8 (included with Windows 10/11)
- Optional: MSI Afterburner, for per-mode GPU curves

## Install

Open an **administrator** PowerShell in the folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

This registers a scheduled task that starts ModeSwitch **elevated** at logon, with no UAC prompt,
and adds a Start Menu shortcut. Elevation is needed to change HAGS, write NVIDIA driver profile
settings and edit Afterburner's config.

`-ExecutionPolicy Bypass` applies only to that one run. Windows blocks `.ps1` files by default.

### Afterburner setup

ModeSwitch does not set the GPU curve itself; no public API can write a full voltage/frequency
curve. Instead it copies one of Afterburner's saved profiles into Afterburner's `[Startup]` section
and relaunches it with `/s`, which makes Afterburner apply that profile.

1. In Afterburner, save your stock settings to **profile 2** and your undervolt/overclock to
   **profile 1**. Other slots work too; set `ab.movie.profile` / `ab.game.profile` accordingly.
2. Enable **Settings → General → Start with Windows**. Without it, `/s` makes Afterburner exit
   immediately without applying anything. Movie mode still closes Afterburner after logon, so it
   stays out of the way during playback.
3. Point `ab.cfg` in `config.ini` at your GPU's profile file in
   `C:\Program Files (x86)\MSI Afterburner\Profiles\` (the one named `VEN_10DE&DEV_....cfg`).

Leave `ab.exe` blank to skip Afterburner entirely.

## Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall -NoRestore
```

The first form applies the Movie preset first, then removes the logon task and the shortcut, and
tells you if a reboot is needed. `-NoRestore` removes it and leaves every setting as it is. Neither
deletes the folder.

## Using it

- **Left-click** the icon to switch to the other mode.
- **Right-click** for:
  - Movie mode / Game mode
  - **Refresh rate**: a submenu per display, listing every rate at its current resolution
  - **HDR**: current state, with each display listed and ticked if HDR is on; click one to toggle it
  - Reboot now (only while a reboot is pending)
  - **NVIDIA Control Panel** and **Windows display settings**, opened as your normal user (not elevated)
  - Open config.ini
  - Exit

A switch runs in the background. The icon turns amber immediately and the menu shows
"Switching to …" until it finishes, usually within about 7 seconds.

After logon, ModeSwitch re-applies the stored mode (after `apply.onstart.delayms`), because the
Afterburner curve and Windows HDR do not survive a reboot on their own.

## Configuration (`bin\config.ini`)

| Key | Meaning |
|---|---|
| `apply.onstart`, `apply.onstart.delayms` | re-apply the stored mode after logon, and how long to wait first |
| `hags.movie`, `hags.game` | `1` = HAGS off, `2` = on |
| `vsync.both` | NVIDIA profile settings applied in both modes, as `id:value` pairs |
| `gsync.movie`, `gsync.game` | NVIDIA profile settings for each mode, as `id:value` pairs |
| `hdr.movie`, `hdr.game` | `on` / `off` |
| `ab.exe`, `ab.cfg`, `ab.args` | Afterburner executable, the GPU profile file, and launch arguments (`/s`) |
| `ab.applydelayms` | how long Afterburner gets to apply a profile before the switch counts as done |
| `ab.<mode>.profile` | which Afterburner section to apply (`Profile1` … `Profile5`) |
| `ab.<mode>.memclk` | memory offset override in kHz (`0` = none; `1500000` = +1500 MHz) |
| `ab.<mode>.power` | power-limit override in % (`0` = keep the profile's own value) |
| `ab.<mode>.stopafter` | close Afterburner once the profile is applied |
| `oc.<mode>.clearoffsets` | reset flat pstate clock offsets to 0 (separate from the curve) |
| `apps.<mode>.stop`, `apps.<mode>.start` | extra processes to close / a program to start (`path|args`) |
| `open.nvcp`, `open.display` | what the menu shortcuts open: NVIDIA Control Panel's Store app ID, and a `ms-settings:` page |

### Finding NVIDIA setting IDs for your driver

The G-SYNC and V-Sync IDs and values differ between drivers, so read them from yours:

1. Set G-SYNC / V-Sync the way you want for one mode in NVIDIA Control Panel.
2. Run `bin\NvProbe.exe`. It dumps the sync-related settings in the global driver profile,
   read-only.
3. Put the IDs and values into `gsync.<mode>` / `vsync.both`, then repeat for the other mode.

For reference, driver 616.64 used:

| Setting | ID | Off | Full screen + windowed |
|---|---|---|---|
| Enable G-SYNC globally | `0x1194F158` | `0` | `2` |
| VRR global feature | `0x1094F157` | `0` | `1` |
| VRR requested state | `0x1094F1F7` | `0` | `2` |
| G-SYNC | `0x10A879CF` | `4` | `0` |
| Vertical Sync | `0x00A879CF` | On = `0x47814940` | |

Enabling G-SYNC in NVIDIA Control Panel can reset Vertical Sync to "Use the 3D application
setting", which is why V-Sync is re-applied on every switch.

## Driver updates

You normally don't need to change anything when NVIDIA releases a new driver.

- **Every switch verifies the NVIDIA settings.** After writing G-SYNC and V-Sync, ModeSwitch opens
  a fresh session with the driver and reads each value back. The notification and log say
  `NVIDIA settings applied and verified (driver 616.64)`, or `NVIDIA check FAILED` with the exact
  setting that didn't stick, including when the driver no longer recognises a setting ID.
- **Driver changes are detected at logon.** The driver version is remembered. When it changes,
  a notification says `NVIDIA driver changed: 616.64 -> 620.10`. The automatic re-apply after logon
  then restores any settings the installer reset, and verifies them.

If a check fails after an update, NVIDIA has changed a setting ID or value. That's a config edit,
not a new version: set G-SYNC / V-Sync in NVIDIA Control Panel, run `bin\NvProbe.exe`, and update
`config.ini` (see [Finding NVIDIA setting IDs](#finding-nvidia-setting-ids-for-your-driver)).

## Log

Every switch is appended to `bin\ModeSwitch.log`, one block per switch, recording what each
display and Afterburner actually reported rather than what was requested:

```
=== 2026-09-26 15:23:35  switch to game ===
NVIDIA driver changed: 999.99 -> 616.64
NVIDIA settings applied and verified (driver 616.64)
Afterburner: Profile1 applied
HDR on LG TV: on
GPU scheduling: on after reboot
```

The "driver changed" line only appears on the first switch after a driver update.

Overrides only appear when they change something, e.g. `Afterburner: Profile1 applied (memory +1500 MHz)`.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `install.ps1 cannot be loaded because running scripts is disabled` | Use the `-ExecutionPolicy Bypass` form shown above |
| Log says `Afterburner exited` | Enable "Start with Windows" in Afterburner (see Afterburner setup) |
| `NVIDIA check FAILED` after a driver update | Re-read the setting IDs/values with `NvProbe.exe` and update `config.ini` (see Driver updates) |
| HDR state looks wrong | HDR is read with the 24H2+ query, which separates HDR from Auto Color Management. On older Windows builds the fallback cannot make that distinction |
| A setting failed | Check `bin\ModeSwitch.log`; errors include the API return code |
| Can't rebuild: file in use | The app is running elevated; right-click → Exit first |

## Build

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

This compiles `src\*.cs` into `bin\` with the C# compiler that ships with .NET Framework 4.8, so
no SDK or Visual Studio is needed. The source avoids C# 6+ syntax for that reason.

| File | Purpose |
|---|---|
| `src\ModeSwitch.cs` | the tray app |
| `src\NvProbe.cs` | read-only dump of sync-related NVIDIA driver profile settings |
| `src\NvClocks.cs` | read, or set, GPU pstate clock offsets |
| `bin\config.ini` | all settings |
| `install.ps1` | installer / uninstaller |
| `build.ps1` | builds everything into `bin\` |

`ModeSwitch.exe --apply movie|game` applies a mode without the tray icon and exits. The uninstaller
uses it.

## Caveats

- NVIDIA profile settings and clock offsets use **undocumented NVAPI entry points**, the same ones
  used by tools like NVIDIA Profile Inspector and Afterburner. A driver update could change them.
  Re-run `NvProbe.exe` if G-SYNC stops switching.
- Applying a GPU overclock or undervolt is at your own risk. On the machine this was built for,
  a +1500 MHz memory offset coincided with daily GPU hangs, which is why both presets ship with
  the memory override at 0.
- Not affiliated with NVIDIA, MSI or Microsoft.
