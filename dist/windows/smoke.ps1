<#
.SYNOPSIS
  Start a published subs2srs.exe with MSYS2 removed from PATH, check that it stays alive,
  shows a top-level window and reaches the settings-loading stage, then kill it.

.DESCRIPTION
  Checks:
    1. the process is still running after -Seconds seconds,
    2. it owns a top-level window (GTK initialised and MainWindow was shown),
    3. %APPDATA%\subs2srs\preferences.json exists (MainWindow.LoadSettings ran).
  Any log-*.txt written to %LOCALAPPDATA%\subs2srs\Logs during the run is printed and scanned
  for GLib criticals.

  .NET resolves %APPDATA% / %LOCALAPPDATA% through the shell, not the environment, so this
  script cannot redirect them: preferences.json is created in the real profile if it did not
  exist (the app writes defaults only when the file is missing).

.PARAMETER PublishDir
  Directory containing subs2srs.exe with the GTK runtime bundled.

.PARAMETER TimeoutSeconds
  How long to wait for the main window to appear (default 60). A cold start right after
  publishing can take well over 5 s: the freshly copied DLLs are scanned by Defender.

.PARAMETER HoldSeconds
  How long the process must keep running after the window appeared (default 3).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [int]$TimeoutSeconds = 60,
    [int]$HoldSeconds = 3
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path (Resolve-Path $PublishDir) 'subs2srs.exe'
if (-not (Test-Path $exe)) { throw "not found: $exe" }

$prefs = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'subs2srs\preferences.json'
$logDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'subs2srs\Logs'
$prefsExistedBefore = Test-Path $prefs
$start = Get-Date

# Make sure we are NOT picking up a developer's MSYS2 from PATH.
$env:PATH = ($env:PATH -split ';' | Where-Object { $_ -notmatch 'msys64' }) -join ';'
$env:GSK_RENDERER = 'cairo'

Write-Host "Starting $exe"
$p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru

$hasWindow = $false
$windowAt = $null
$deadline = $start.AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 250
    if ($p.HasExited) { break }
    $p.Refresh()
    if ($p.MainWindowHandle -ne 0) {
        $hasWindow = $true
        $windowAt = Get-Date
        break
    }
}
# Keep it running a little longer so a crash right after the first frame is caught.
if ($hasWindow) { Start-Sleep -Seconds $HoldSeconds }
$p.Refresh()
$alive = -not $p.HasExited
$title = if ($alive) { $p.MainWindowTitle } else { '' }
$startupMs = if ($windowAt) { [int]($windowAt - $start).TotalMilliseconds } else { -1 }

$prefsNow = Test-Path $prefs
$logs = @()
if (Test-Path $logDir) {
    $logs = @(Get-ChildItem $logDir -Filter 'log-*.txt' | Where-Object { $_.LastWriteTime -ge $start })
}

if ($alive) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }

Write-Host ("process alive           : {0}" -f $alive)
Write-Host ("top-level window        : {0}  (title '{1}', appeared after {2} ms, timeout {3} s)" -f $hasWindow, $title, $startupMs, $TimeoutSeconds)
Write-Host ("preferences.json        : {0}  ({1})" -f $prefsNow, $prefs)
Write-Host ("log files written       : {0}" -f $logs.Count)
foreach ($l in $logs) {
    Write-Host "--- $($l.Name) ---"
    Get-Content $l.FullName -Tail 40
}

$criticals = @()
foreach ($l in $logs) { $criticals += @(Select-String -Path $l.FullName -Pattern 'CRITICAL|Gtk-WARNING|GLib-GObject-CRITICAL') }
if ($criticals.Count -gt 0) {
    Write-Warning 'GLib criticals/warnings found in the log:'
    $criticals | ForEach-Object { Write-Warning $_.Line }
}

# Leave the profile as we found it.
if (-not $prefsExistedBefore -and $prefsNow) {
    Remove-Item $prefs -Force -ErrorAction SilentlyContinue
    $dir = Split-Path $prefs
    if (-not (Get-ChildItem $dir -Force -ErrorAction SilentlyContinue)) { Remove-Item $dir -Force -ErrorAction SilentlyContinue }
}

if (-not $alive) { throw "subs2srs.exe exited early (code $($p.ExitCode))" }
if (-not $hasWindow) { throw "subs2srs.exe showed no top-level window within $TimeoutSeconds s" }
if (-not $prefsNow) { throw 'preferences.json was not created: MainWindow.LoadSettings did not run' }
Write-Host 'Smoke test passed.' -ForegroundColor Green
