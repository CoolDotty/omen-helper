param(
  [string]$PackagePrefix = "AD2F1837.OMENCommandCenter",
  [switch]$ShowAllLines,
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
Write-Host "Stop: Ctrl+C"
Write-Host ""

$reCmd = [regex]"\[ExecuteBiosWmiCommandThruDriver\]\s+command=(?<cmd>\d+),\s+commandType=(?<type>\d+),\s+inputDataSize=(?<in>\d+),\s+returnDataSize=(?<out>\d+)"
$reIn  = [regex]"\[ExecuteBiosWmiCommandThruDriver\]\s+inputData=(?<data>.*)$"
$reRet = [regex]"\[ExecuteBiosWmiCommandThruDriver\]\s+ret\.returnCode\s+=\s+(?<rc>\d+)"

$last = [ordered]@{
  cmd  = $null
  type = $null
  inSz = $null
  outSz = $null
  input = $null
  rc = $null
}

function Flush-IfComplete {
  if ($null -ne $last.cmd -and $null -ne $last.type -and $null -ne $last.rc) {
    $inputShort = $last.input
    if ($null -ne $inputShort -and $inputShort.Length -gt 120) {
      $inputShort = $inputShort.Substring(0, 120) + "…"
    }
    $line = "BIOS cmd=$($last.cmd) type=$($last.type) in=$($last.inSz) out=$($last.outSz) rc=$($last.rc)"
    if ($null -ne $inputShort) { $line += " input=[$inputShort]" }
    Write-Host $line
    $last.cmd = $null
    $last.type = $null
    $last.inSz = $null
    $last.outSz = $null
    $last.input = $null
    $last.rc = $null
  }
}

$gcArgs = @{
  Path = $logPath
}
if ($Follow) { $gcArgs["Wait"] = $true }

Get-Content @gcArgs | ForEach-Object {
  $line = $_
  if ($ShowAllLines) { Write-Host $line }

  $m = $reCmd.Match($line)
  if ($m.Success) {
    $last.cmd = [int]$m.Groups["cmd"].Value
    $last.type = [int]$m.Groups["type"].Value
    $last.inSz = [int]$m.Groups["in"].Value
    $last.outSz = [int]$m.Groups["out"].Value
    Flush-IfComplete
    return
  }

  $m = $reIn.Match($line)
  if ($m.Success) {
    $last.input = $m.Groups["data"].Value.Trim()
    Flush-IfComplete
    return
  }

  $m = $reRet.Match($line)
  if ($m.Success) {
    $last.rc = [int]$m.Groups["rc"].Value
    Flush-IfComplete
    return
  }
}

