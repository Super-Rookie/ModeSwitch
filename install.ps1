# ModeSwitch installer - run this in an ADMINISTRATOR PowerShell.
#   .\install.ps1              install (logon task + Start Menu shortcut, then start it)
#   .\install.ps1 -Uninstall   remove the task and shortcut
# Uninstall restores the Movie preset first (GPU scheduling off, stock clocks, G-Sync off, HDR off)
# unless -NoRestore is given.
param([switch]$Uninstall, [switch]$NoRestore)

$ErrorActionPreference = 'Stop'
$taskName = 'ModeSwitch'
$exe = Join-Path $PSScriptRoot 'bin\ModeSwitch.exe'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\ModeSwitch.lnk'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  Write-Host "Run this script from an administrator PowerShell window." -ForegroundColor Red
  exit 1
}

if ($Uninstall) {
  Get-Process ModeSwitch -ErrorAction SilentlyContinue | Stop-Process -Force

  $hagsBefore = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' -ErrorAction SilentlyContinue).HwSchMode
  if (-not $NoRestore -and (Test-Path $exe)) {
    Write-Host "Restoring the Movie preset before removing..."
    & $exe --apply movie | Out-Null
    Start-Sleep -Seconds 8          # the app relaunches Afterburner to apply the stock curve
    $log = Join-Path $PSScriptRoot 'bin\ModeSwitch.log'
    if (Test-Path $log) { Write-Host "--- last entries from ModeSwitch.log ---"; Get-Content $log -Tail 12 | ForEach-Object { "   $_" } }
  }

  if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    Write-Host "Removed scheduled task '$taskName'."
  }
  if (Test-Path $startMenu) { Remove-Item $startMenu -Force; Write-Host "Removed Start Menu shortcut." }
  Get-Process ModeSwitch -ErrorAction SilentlyContinue | Stop-Process -Force

  $hagsAfter = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' -ErrorAction SilentlyContinue).HwSchMode
  Write-Host ""
  Write-Host "Removed. Nothing was deleted from $PSScriptRoot - delete the folder yourself if you want it gone."
  if (-not $NoRestore) {
    Write-Host "GPU scheduling = $hagsAfter (1 = off, 2 = on); clocks, G-Sync and HDR set to the Movie preset."
    if ($hagsBefore -ne $hagsAfter) { Write-Host "Reboot to complete the GPU scheduling change." -ForegroundColor Yellow }
  } else {
    Write-Host "Settings were left exactly as they were (-NoRestore)."
  }
  exit 0
}

if (-not (Test-Path $exe)) { Write-Host "Not found: $exe" -ForegroundColor Red; exit 1 }

# Runs elevated at logon so it can change GPU scheduling, NVIDIA profile settings and clocks
# without a UAC prompt each time.
$action    = New-ScheduledTaskAction -Execute $exe
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
             -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal `
  -Settings $settings -Description 'Switches the PC between Movie and Game presets from the tray.' -Force | Out-Null
Write-Host "Registered scheduled task '$taskName' (starts elevated at logon)."

$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($startMenu)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = Split-Path $exe
$lnk.Description = 'Switch between Movie and Game presets'
$lnk.Save()
Write-Host "Created Start Menu shortcut."

Get-Process ModeSwitch -ErrorAction SilentlyContinue | Stop-Process -Force
Start-ScheduledTask -TaskName $taskName
Start-Sleep -Seconds 2
if (Get-Process ModeSwitch -ErrorAction SilentlyContinue) {
  Write-Host "ModeSwitch is running - look for the icon in the tray overflow." -ForegroundColor Green
} else {
  Write-Host "Task registered but the app did not start; run $exe manually to see the error." -ForegroundColor Yellow
}
