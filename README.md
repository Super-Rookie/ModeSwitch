# ModeSwitch

A Windows tray app that flips a PC between a **Movie** preset and a **Game** preset with one click,
and between a **TV room** and a **Theatre room** (projector, AV receiver, subwoofer).

It was built for a home-theatre PC with an NVIDIA GPU, where the settings that suit gaming
(hardware-accelerated GPU scheduling, G-SYNC, HDR, an undervolt) caused stutter in madVR/MPC-HC
video playback. Instead of changing half a dozen settings by hand every time, the tray icon does it.

| Tray icon | Meaning |
|---|---|
| Blue film | Movie mode |
| Purple "3D" | 3D Movie mode (Movie settings plus 1080p for the projector; see below) |
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
| Sound preset (optional) | `sound.movie` | `sound.game` |

Every value is set in `bin\config.ini`, so the presets can be changed without rebuilding.

## 3D Movie mode

A third mode, chosen from the right-click menu like the others. It shows a purple **3D** icon.

3D Movie inherits **every Movie setting**: GPU scheduling, G-SYNC, HDR, Afterburner, sound. It
only overrides what it sets itself, so switching between Movie and 3D Movie never needs a reboot.
Out of the box it adds:

- **Projector to 1920×1080 @ 23 Hz** (`display.3d`). Projectors generally take frame-compatible 3D
  only at 1080p. Movie mode sets it back to 3840×2160 @ 23 Hz (`display.movie`). Both only act
  when the display named by `display.target` is connected, so nothing changes while you're on a TV.
- **A reminder** (`reminder.3d`) to set the projector's 3D format with its remote, or, with network
  control, a note on how the projector gets switched to 3D (`reminder.3d.networked`).

Any Movie key can be given a `.3d` version to differ, e.g. `sound.3d = atmos-hometheater`.

### Projector network control (Sony PJ Talk / SDCP)

With `projector.ip` set, ModeSwitch talks to the projector over the network (TCP 53484). The 3D
item numbers were found by reading every setting before and after changing them on the remote
of a VPL-VW760ES:

| Item | Setting | Values |
|---|---|---|
| `0x0060` | 2D-3D display select | 0 = Auto, 1 = 3D |
| `0x0061` | 3D format | 1 = Side-by-Side, 2 = Over-Under |

- The projector's 3D settings only exist while it receives a 1080p (or lower) signal. They are
  refused (error `0x0180`) at 4K. So leaving 3D sets the projector to 2D *before* switching back
  to 4K, and entering 3D switches it *after* the change to 1080p. At 4K, 2D is reported as
  "3D isn't available at this resolution", which is correct rather than an error.
- **3D Movie mode leaves the projector in 2D**, because a desktop shown in 3D Over-Under is split
  and hard to use. **Play 3D…** switches it to 3D when the film starts, picking Side-by-Side for
  names containing `SBS`/`HSBS` and Over-Under otherwise (ISOs/MVC, `OU`, `TAB`). It switches back
  to 2D when the film ends.
- The **Projector** submenu shows the current state and switches 2D / 3D Over-Under / 3D
  Side-by-Side by hand, or opens the projector's web page.
- Everything is skipped quietly when the projector is off or unreachable.
- Each change is read back to confirm.

### Play 3D Blu-ray / 3D film…

One menu click (or `ModeSwitch.exe --play3d "<file>"`) that:

1. asks for a 3D Blu-ray **ISO** or a 3D video file (remembering the last folder);
2. switches to 3D Movie mode;
3. mounts the ISO and opens its `BDMV\index.bdmv`, so both eyes are read; opening the `.m2ts`
   directly would give only one eye (2D);
4. starts the player fullscreen (whatever `.mkv` files open with, or `play3d.player`) and shows the
   3D-format reminder;
5. when the player closes, ejects the ISO and switches back to the previous mode.

The ISO is mounted only while ModeSwitch holds it open, so Windows ejects it automatically even
if something goes wrong. If MPC-HC hands the film to an already-open window, ModeSwitch waits for
that window instead. Started from Game mode, it leaves GPU scheduling alone both ways, so it
never needs a reboot.

### 3D playback notes

Current NVIDIA drivers no longer output frame-packed stereo 3D: stereo support was dropped
after driver 425.31, and DirectX 11 stereo is gone on RTX 30-series and newer. On such a card:

- **MVC files** (3D Blu-ray rips): set madVR → *devices → your projector → properties → 3D format*
  to **top-and-bottom** (or side-by-side). madVR then packs both eyes into a normal 2D frame, and
  the projector's 3D processing splits them. With **auto**, madVR tries frame packing, which needs
  driver support. Worth a test on your hardware, but expect flat 2D on RTX 30-series and newer.
- **Files already side-by-side / top-and-bottom** play as normal video; just set the projector to
  the matching format.
- For full-resolution frame-packed 3D, a standalone 3D-capable player is the dependable route.

Tested on an RTX 5080 with driver 616.64 and the NVIDIA stereo driver reinstalled (via 3D Fix
Manager): Direct3D 11 still offers **no stereo display modes**, so madVR can't frame-pack there
either. madVR has no Direct3D 9 stereo output, so there is no workaround inside madVR. Keep
`play3d.framepacking = false` and madVR on top-and-bottom. Set it to `true` only on hardware whose
driver does offer stereo modes. ISO/MVC films then leave the projector on Auto, since it detects
frame packing by itself.

The madVR 3D format only applies to MVC content, so it can stay set permanently without affecting
2D playback.

## Rooms: TV or Theatre

A second choice in the menu, independent of Movie / 3D Movie / Game, for a setup with a TV in one
place and a projector, AV receiver and subwoofer in another:

| | TV room | Theatre room |
|---|---|---|
| LG TV (webOS) | on, switched to the PC's input | off |
| AV receiver (Sony) | off | on, switched to the PC's input |
| Projector (Sony) | off | on |
| Subwoofer (on a TP-Link Tapo smart plug) | off | on |
| Windows display | the TV only | the projector only |
| Windows default sound device | the TV | the receiver |

After the devices, the current mode's resolution, HDR and sound preset are applied for the display
now in use, with `sound.<room>.<mode>` (e.g. `sound.tv.movie`) overriding `sound.<mode>`. **Play
3D…** switches to the Theatre room first. Also `ModeSwitch.exe --room tv|theatre`.

Order matters for comfort and speed:
- The projector is switched on first, because it takes longest to warm up.
- The subwoofer comes on only once the picture is there (so the receiver is on), and goes off
  before the receiver, so it never thumps.
- The TV is switched off only after Windows has moved to the projector.

A switch typically takes about 20 seconds either way, mostly the projector warming up or the TV
waking. Each switch logs per-step timings, e.g.
`took 23s: projector 0.0s, receiver 5.1s, display 13.8s, ...`.

### How each device is controlled

| Device | Protocol | Notes |
|---|---|---|
| LG TV | webOS SSAP: JSON over a websocket, port 3000 (3001 TLS) | Pair once from **Room setup → Pair TV** and accept the prompt on the TV. It's switched on with Wake-on-LAN (`tv.mac`), which needs **General → Mobile TV On → Turn on via Wi-Fi** on the TV. After a long spell off it can take over 30 s to answer. The switch waits 20 s, then carries on and selects the input in the background. |
| Sony receiver (STR-DN1080) | Sony Audio Control API: JSON-RPC over HTTP, port 10000 | Needs **Network Settings → External Control: On**. It accepts only `setPowerStatus` `off` (`standby` is refused). To be switched back on over the network it needs **Network Standby** and **Remote Start**. UK/EU models hide both from their setup menu, but the API still has them (`system/getPowerSettings`: `quickStartMode` and `wolMode`). ModeSwitch turns them on whenever it reaches the receiver. With them on, it stays reachable in standby and comes on in about 2 s. Without them, it drops off the network when off, and Wake-on-LAN doesn't wake it. The Theatre switch then asks for the remote, carries on, and selects the PC input in the background once the receiver's network is back (over a minute). |
| Sony projector | PJ Talk / SDCP, TCP 53484 | Power item `0x0130` (1 on, 0 off), status `0x0102`. It stays reachable in standby. |
| TP-Link Tapo plug | KLAP (handshake, then AES-encrypted requests) | Enter the TP-Link account login once from **Room setup → Subwoofer plug login…**. It's stored DPAPI-encrypted in `HKCU\Software\ModeSwitch`, readable only by that Windows user, never in a file. |

Windows remembers a display layout for each combination of connected displays, so switching a
device on by hand can bring back an old layout, e.g. "TV only" in the Theatre room. With
`room.followdisplay = true`, ModeSwitch puts the room's display, sound device and resolution back
whenever a display connects or disconnects. It only acts on such a change, so a layout chosen
afterwards with Win+P is left alone.

**Room setup → Check devices** reads every device's state without changing anything.

## Settings window (picture)

**Settings…** in the tray menu opens a dark, always-on-top window with two tabs. OK / Apply save
to `bin\settings.ini`, which ModeSwitch writes itself. It overrides `config.ini` and is kept out
of git.

Each profile has **3 save slots** (Saved settings → Save / Restore), so you can experiment and
go back:

- **Projector**: 3 slots per projector preset. A slot holds that preset's colour temperature,
  gamma, contrast, brightness, colour, hue, sharpness and R/G/B gain and bias. Restore sends them
  back to the projector.
- **GPU colour**: 3 slots per mode and display. Restore loads the slot into the controls (and
  previews it); OK makes it the saved setting.

Slots are saved straight away, with the time they were saved, whether or not you then press OK.

A 4th slot, **Default (locked)**, holds the settings as they were when first captured. That's
every projector preset's picture values, and neutral GPU colour for every mode and display. It
can be restored at any time but never saved over, so there's always a way back. Its keys are
`pjslot.<preset>.default` and `gpuslot.<mode>.<display>.default` in `settings.ini`.

### Projector picture

- **Picture preset for each mode**: Movie / 3D Movie / Game each pick one of the projector's
  presets (Cinema Film 1/2, Reference, TV, Photo, Game, Bright Cinema, Bright TV, User), or leave
  it as it is. It's set on every switch. After a Theatre room switch it's set once the projector
  has warmed up.
- **Adjust the projector's picture**: live controls for the preset showing now: contrast,
  brightness, colour, hue, sharpness, colour temperature, gamma, and R/G/B gain and bias. Changes
  go straight to the projector, which keeps them inside that preset, so each mode's preset
  carries its own adjustments. Picking another preset under "Showing now" switches the projector
  to it and reads its values.

### Projector 3D

The projector keeps **separate picture memory for 3D**: its own current preset, and its own
adjustments for every preset. Measured on the VW760ES: switching 3D on moved it from User to
Cinema Film 1, and Reference has contrast 99 in 2D but 92 in 3D. So 3D has its own tab:

- **3D films**: the preset set when a 3D film starts with Play 3D… (`pj.3dplay.preset`), after the
  projector has switched to 3D.
- The same live adjustments, plus **3D depth** (−2 to +2, item `0x0062`), and 3 slots plus a locked
  Default per 3D preset (`pj3dslot.*`).

Each tab only edits while the projector shows its kind of picture; otherwise its controls are
locked with a note. 3D is detected by the 3D Depth item, which only answers in 3D. So a 2D
adjustment can never land in 3D memory, or the other way round. To adjust 3D, start a 3D film.

**3D view.** In frame-compatible 3D the projector splits every frame between the eyes and
stretches each half back to full size. For Over-Under (TAB) that's top and bottom; for
Side-by-Side (SBS) it's left and right. A normal window would come out cut in half and
stretched. So while the projector shows 3D (checked every 2 s), the Settings window is drawn as
two copies. Each is squashed to half height (TAB) or half width (SBS), one in each half, so each
eye sees one copy at its normal shape. The real window waits off screen and comes back when 3D
ends. Clicks on either copy are mapped back to the real controls:

- sliders drag and wheel as normal
- buttons and checkboxes click as normal
- number boxes step with their arrows or the wheel (click the box to type)
- lists step to the next item on a click, or with the wheel, because their drop-downs would open
  off screen

The **3D view** checkbox turns it off.

Item numbers are from Sony's VPL-VW protocol manual (SDCP):

| Item | Setting | Values |
|---|---|---|
| `0x0002` | Calib. preset | 0 Cinema Film 1, 1 Cinema Film 2, 2 Reference, 3 TV, 4 Photo, 5 Game, 6 Bright Cinema, 7 Bright TV, 8 User |
| `0x0010`–`0x0014` | Contrast, Brightness, Colour, Hue, Sharpness | 0–100 |
| `0x0017` | Colour temp | 0 D93, 1 D75, 2 D65, 9 D55, 3–6 and 8 Custom 1–5 |
| `0x0022` | Gamma correction | 0 Off, 1 1.8, 2 2.0, 3 2.1, 4 2.2, 5 2.4, 6 2.6, 7–10 Gamma 7–10 |
| `0x0050`–`0x0055` | Gain R/G/B, Bias R/G/B | −30 to +30 (16-bit signed) |

### GPU colour

Per mode and per display (the TV, the projector):

- **Brightness, contrast and gamma**, for all channels or red, green and blue separately. These are
  applied through the display's gamma ramp, like NVIDIA Control Panel's desktop colour settings.
- **Digital vibrance** (0–100 %, 50 = unchanged) and **hue** (0–359°), through NVAPI.

ModeSwitch applies them on every mode or room switch, after the resolution and HDR changes (which
reset the gamma ramp). With **Preview on screen**, changes for the current mode show as you
make them, and Cancel puts back what was saved.

- The gamma ramp has no effect while Windows HDR is on; vibrance and hue still apply.
- A display that has no custom colour in any mode is left alone, so NVIDIA Control Panel settings
  stay. Once one mode has custom colour, the other modes use neutral on that display.
- Windows refuses gamma ramps that stray too far from neutral. The error says so. Setting
  `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM\GdiIcmGammaRange = 256` lifts the limit.

## Sound presets

Four presets, applied to the **current default playback device** (e.g. the TV or AV receiver
over HDMI). Pick one from the tray menu at any time, or set `sound.movie` / `sound.game` to apply
one automatically when switching modes.

| Preset key | What it sets |
|---|---|
| `atmos-hometheater` | 7.1 speakers (front, back and side pairs full-range), then Dolby Atmos for Home Theater. Windows bitstreams Dolby MAT 2.0 over HDMI and chooses the output format itself |
| `atmos-headphones` | 2 speakers (both full-range), 24-bit 96 kHz, then Dolby Atmos for Headphones, which renders binaural stereo |
| `stereo-24-96` | Spatial sound off, 2 speakers (both full-range), 24-bit 96 kHz |
| `7.1-24-96` | Spatial sound off, 7.1 speakers (front, back and side pairs full-range), 24-bit 96 kHz |

- The Atmos presets use Windows' documented spatial-audio API and need the **Dolby Access** app
  (free from the Microsoft Store) to have been set up once.
- Every preset runs in the same order. It turns spatial sound off, since spatial sound controls the
  output format while it's active. It sets the speaker layout and full-range speakers, and the
  default format where the preset has one: the same settings as *Sound Control Panel → Configure*
  and *Properties → Advanced*. Then it switches the preset's spatial format on, if any. Each step is
  read back to confirm. The layout always matches the preset, so an Atmos preset never inherits a
  layout left behind by another one. Windows' speaker setup doesn't offer centre or subwoofer as
  full-range, so those are left as they are.
- Atmos for Home Theater can't run at 96 kHz: it carries Atmos inside a fixed HDMI bitstream
  format. That's why it and the 24/96 PCM presets are separate choices.
- If the device can't do a format (for example 7.1 on a stereo-only output), the notification says
  so and nothing is changed.

`ModeSwitch.exe --sound <preset>` applies a preset from the command line (e.g. a shortcut) and
exits. The result is written to the log.

HAGS only changes after a reboot. After a switch, ModeSwitch waits until every other setting is
applied, then shows a dialog summarising what changed and asks whether to reboot now. It compares
against the value HAGS had when Windows started, so switching Game → Movie without rebooting in
between correctly needs no reboot.

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

**Left- or right-click** opens the menu, with the current mode ticked. A click never switches
anything by itself, so a stray click can't change modes (and GPU scheduling) by accident.

- The menu has:
  - Movie mode / 3D Movie mode / Game mode
  - **Play 3D Blu-ray / 3D film…**
  - **TV room** / **Theatre room**
  - **Volume**: sliders for the TV and receiver volume, right in the menu, with a clickable scale
    in steps of 5 under each. Also mute for both, and the TV's sound output (TV speakers / wired
    headphones). Levels are read in the background when the submenu opens.
  - **Room setup**: a **Subwoofer** toggle (ticked while it's on), check devices, pair the TV, the
    Tapo login
  - **Refresh rate**: a submenu per display, listing every rate at its current resolution
  - **HDR**: current state, with each display listed and ticked if HDR is on; click one to toggle it
  - **Sound**: the current output and format, with the four presets underneath; the active one is ticked
  - Reboot now (only while a reboot is pending)
  - **NVIDIA Control Panel** and **Windows display settings**, opened as your normal user (not elevated)
  - **Settings…**: the picture settings window (see [Settings window](#settings-window-picture))
  - Open config.ini (and config.local.ini, if there is one)
  - Exit

A switch runs in the background. The icon turns amber immediately and the menu shows
"Switching to …" until it finishes, usually within about 7 seconds.

After logon, ModeSwitch re-applies the stored mode (after `apply.onstart.delayms`), because the
Afterburner curve and Windows HDR do not survive a reboot on their own.

## Configuration (`bin\config.ini`)

Keys in `bin\config.local.ini`, if present, override `config.ini`. Put your own network details
there: `projector.ip`, `tv.ip`, `tv.mac`, `avr.ip`, `sub.ip`. It's in `.gitignore`, so they stay
out of source control, and `config.ini` ships with them blank:

```ini
projector.ip = 192.168.1.50
tv.ip        = 192.168.1.51
tv.mac       = AA-BB-CC-DD-EE-FF
avr.ip       = 192.168.1.52
sub.ip       = 192.168.1.53
```

Pairing keys and the Tapo login are kept in the registry (`HKCU\Software\ModeSwitch`), never in
these files.

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
| `sound.movie`, `sound.game` | sound preset applied when switching to that mode (blank = leave audio alone) |
| `projector.ip`, `projector.community` | projector address and PJ Talk community (default `SONY`) |
| `projector.<mode>` | projector items to set per mode, as `item:value` pairs (e.g. `0x0060:0`) |
| `play3d.player`, `play3d.args` | player for Play 3D… (blank = whatever `.mkv` opens with) and its arguments |
| `play3d.framepacking` | `true` only if the driver offers stereo modes (see 3D playback notes) |
| `room.<room>.display` | display(s) for the room, by name, as a preference list (`SONY PJ, SONY AVSYSTEM`) |
| `room.<room>.audio` | the room's default sound device, by name |
| `room.followdisplay` | put the room's display back when a display connects or disconnects |
| `room.tv.receiveroff` | `false` = leave the receiver on in the TV room |
| `tv.ip`, `tv.mac`, `tv.input` | LG TV address, MAC for Wake-on-LAN, and the PC's input (`HDMI_1`) |
| `avr.ip`, `avr.input`, `avr.inputname` | receiver address, the PC's input (`extInput:bd-dvd`), and its name for messages |
| `avr.volume.max` | highest receiver volume the menu slider allows (blank = the receiver's own maximum) |
| `sub.ip` | the subwoofer's Tapo plug |
| `sound.<room>.<mode>` | sound preset for that mode in that room; `none` = leave sound alone |
| `display.target` | part of the display's name the resolution keys apply to (blank = primary display) |
| `display.<mode>` | resolution and refresh for that mode, e.g. `1920x1080@23`; skipped if the display isn't connected |
| `reminder.<mode>` | a line added to that mode's notification, for things the app can't do itself |
| `<key>.3d` | any Movie key with `.3d` instead of `.movie`, to make 3D Movie differ from Movie |
| `log.maxkb` | log size limit before it rotates to `ModeSwitch.old.log` |
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

The log is size-limited. Once it passes `log.maxkb` (default 256 KB, roughly a thousand switches),
it's renamed to `ModeSwitch.old.log`, replacing the previous one, and a new log starts. At most
about twice the limit is ever on disk.

Overrides only appear when they change something, e.g. `Afterburner: Profile1 applied (memory +1500 MHz)`.

The notification after a switch is a condensed version, one short line per step, because Windows
cuts notification text at about 255 characters. Failures are listed first so they're never the part
that gets cut, and routine lines such as processes being stopped stay in the log only:

```
NVIDIA settings verified (616.64)
Afterburner: Profile1
HDR: on (LG TV)
Sound: Atmos Home Theater
```

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
| `src\Room.cs` | TV / receiver / Tapo plug control, and which display Windows uses |
| `src\RoomSound.cs` | the Volume submenu |
| `src\Picture.cs` | projector picture items, and GPU colour (gamma ramp, vibrance, hue) |
| `src\Settings.cs` | the Settings window |
| `src\NvProbe.cs` | read-only dump of sync-related NVIDIA driver profile settings |
| `src\NvClocks.cs` | read, or set, GPU pstate clock offsets |
| `bin\config.ini` | all settings |
| `bin\config.local.ini` | your device addresses (not in git) |
| `bin\settings.ini` | written by the Settings window (not in git) |
| `install.ps1` | installer / uninstaller |
| `build.ps1` | builds everything into `bin\` |

`ModeSwitch.exe --apply movie|3d|game` applies a mode without the tray icon and exits. The uninstaller
uses it. `ModeSwitch.exe --room tv|theatre` does the same for a room.

## Caveats

- NVIDIA profile settings and clock offsets use **undocumented NVAPI entry points**, the same ones
  used by tools like NVIDIA Profile Inspector and Afterburner. A driver update could change them.
  Re-run `NvProbe.exe` if G-SYNC stops switching.
- Applying a GPU overclock or undervolt is at your own risk. On the machine this was built for,
  a +1500 MHz memory offset coincided with daily GPU hangs, which is why both presets ship with
  the memory override at 0.
- The LG, Sony and Tapo protocols are the devices' own network APIs, several of them
  reverse-engineered by the community. A firmware update could change them.
- Not affiliated with NVIDIA, MSI, Microsoft, LG, Sony or TP-Link.
