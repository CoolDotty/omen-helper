param(
  [string]$PackagePrefix = "AD2F1837.OMENCommandCenter",
  [switch]$ShowAllLines,
  [switch]$FromStart,
  [bool]$ShowThread = $true,
  [switch]$ShowTimestamp,
  [switch]$Follow = $true
)

$ErrorActionPreference = "Stop"

function Find-OmenBgLog {
  $packagesRoot = Join-Path $env:LOCALAPPDATA "Packages"
  if (-not (Test-Path $packagesRoot)) { return $null }

  $pkgDirs = Get-ChildItem -Path $packagesRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like "$PackagePrefix*" }

  foreach ($pkgDir in $pkgDirs) {
    $candidate = Join-Path $pkgDir.FullName "LocalCache\\Local\\HPOMEN"
    if (-not (Test-Path $candidate)) { continue }

    $log = Get-ChildItem -Path $candidate -File -Filter "HPOMENBG_*.log*" -ErrorAction SilentlyContinue |
      Sort-Object LastWriteTime -Descending |
      Select-Object -First 1

    if ($log) { return $log.FullName }
  }

  return $null
}

$logPath = Find-OmenBgLog
if (-not $logPath) {
  Write-Error "Could not find OMEN background log under %LOCALAPPDATA%\\Packages\\$PackagePrefix*\\LocalCache\\Local\\HPOMEN."
}

Write-Host "Tailing: $logPath"
Write-Host "Filter: ExecuteBiosWmiCommandThruDriver (command/commandType/inputData/returnCode)"
if ($Follow -and -not $FromStart) {
  Write-Host "Start: end of file (new entries only)"
} else {
  Write-Host "Start: beginning of file"
}
Write-Host "Stop: Ctrl+C"
Write-Host ""

$reCmd = [regex]"\[ExecuteBiosWmiCommandThruDriver\]\s+command=(?<cmd>\d+),\s+commandType=(?<type>\d+),\s+inputDataSize=(?<in>\d+),\s+returnDataSize=(?<out>\d+)"
$reIn = [regex]"\[ExecuteBiosWmiCommandThruDriver\]\s+inputData=(?<data>.*)$"
$reRet = [regex]"\[ExecuteBiosWmiCommandThruDriver\]\s+ret\.returnCode\s+=\s+(?<rc>\d+)"
$rePidTid = [regex]"\[PID:\s*(?<pid>\d+)\]\s+\[TID:\s*(?<tid>\d+)\]"
$reTs = [regex]"^(?<ts>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d+)"

$statesByThread = @{}

function Get-ThreadKeyOrNull([string]$line) {
  $m = $rePidTid.Match($line)
  if (-not $m.Success) { return $null }
  return "$($m.Groups["pid"].Value):$($m.Groups["tid"].Value)"
}

function Get-OrCreateState([string]$key) {
  if ($statesByThread.ContainsKey($key)) { return $statesByThread[$key] }

  $state = [ordered]@{
    ts = $null
    cmd = $null
    type = $null
    inSz = $null
    outSz = $null
    input = $null
    rc = $null
  }

  $statesByThread[$key] = $state
  return $state
}

function Flush-IfComplete([string]$key) {
  if (-not $statesByThread.ContainsKey($key)) { return }
  $state = $statesByThread[$key]

  if ($null -ne $state.cmd -and $null -ne $state.type -and $null -ne $state.rc) {
    $inputText = $state.input

    $line = "BIOS"
    if ($ShowThread) { $line += " [$key]" }
    if ($ShowTimestamp -and $null -ne $state.ts) { $line += " $($state.ts)" }
    $line += " cmd=$($state.cmd) type=$($state.type) in=$($state.inSz) out=$($state.outSz) rc=$($state.rc)"
    if ($null -ne $inputText) { $line += " input(len=" + $inputText.Length + ")=[" + $inputText + "]" }
    Write-Host $line

    $statesByThread.Remove($key) | Out-Null
  }
}

$gcArgs = @{
  Path = $logPath
}
if ($Follow) { $gcArgs["Wait"] = $true }
if ($Follow -and -not $FromStart) { $gcArgs["Tail"] = 0 }

Get-Content @gcArgs | ForEach-Object {
  $line = $_
  if ($ShowAllLines) { Write-Host $line }

  $key = Get-ThreadKeyOrNull $line
  if ($null -eq $key) { return }
  $state = Get-OrCreateState $key

  if ($ShowTimestamp) {
    $mTs = $reTs.Match($line)
    if ($mTs.Success) { $state.ts = $mTs.Groups["ts"].Value }
  }

  $m = $reCmd.Match($line)
  if ($m.Success) {
    $state.cmd = [int]$m.Groups["cmd"].Value
    $state.type = [int]$m.Groups["type"].Value
    $state.inSz = [int]$m.Groups["in"].Value
    $state.outSz = [int]$m.Groups["out"].Value
    Flush-IfComplete $key
    return
  }

  $m = $reIn.Match($line)
  if ($m.Success) {
    $state.input = $m.Groups["data"].Value.Trim()
    Flush-IfComplete $key
    return
  }

  $m = $reRet.Match($line)
  if ($m.Success) {
    $state.rc = [int]$m.Groups["rc"].Value
    Flush-IfComplete $key
    return
  }
}
