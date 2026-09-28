# Builds ModeSwitch and its helper tools into bin\ using the .NET Framework 4.8 C# compiler.
# If ModeSwitch is running, exit it first (right-click the tray icon -> Exit): it runs elevated
# and holds the exe open.
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { Write-Host "C# compiler not found: $csc" -ForegroundColor Red; exit 1 }

$src = Join-Path $PSScriptRoot 'src'
$bin = Join-Path $PSScriptRoot 'bin'
if (-not (Test-Path $bin)) { New-Item -ItemType Directory -Path $bin | Out-Null }

# The sound presets use the WinRT spatial-audio API. Its metadata (.winmd) ships with every
# Windows 10/11 install, and the WinRT interop assemblies ship with .NET Framework 4.8.
$fw = Split-Path $csc
$wm = Join-Path $env:WINDIR 'System32\WinMetadata'
$winrt = @("$wm\Windows.Media.winmd", "$wm\Windows.Foundation.winmd", "$fw\System.Runtime.WindowsRuntime.dll", "$fw\System.Runtime.dll")

$targets = @(
  @{ Name = 'ModeSwitch'; Kind = 'winexe'; Refs = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll') + $winrt },
  @{ Name = 'NvProbe';    Kind = 'exe';    Refs = @() },
  @{ Name = 'NvClocks';   Kind = 'exe';    Refs = @() }
)

$failed = $false
foreach ($t in $targets) {
  $cscArgs = @('/nologo', "/target:$($t.Kind)", '/platform:x64', "/out:$bin\$($t.Name).exe")
  $cscArgs += $t.Refs | ForEach-Object { "/reference:$_" }
  $cscArgs += "$src\$($t.Name).cs"
  & $csc @cscArgs
  if ($LASTEXITCODE -eq 0) { Write-Host "built bin\$($t.Name).exe" -ForegroundColor Green }
  else { Write-Host "FAILED: $($t.Name)" -ForegroundColor Red; $failed = $true }
}
if ($failed) { exit 1 }
