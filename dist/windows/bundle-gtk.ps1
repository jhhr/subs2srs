<#
.SYNOPSIS
  Copy the GTK4 runtime (DLLs + data) from an MSYS2 UCRT64 installation next to a
  published subs2srs.exe so the result runs on a machine without MSYS2.

.DESCRIPTION
  GirCore 0.7.0 loads the MSYS2-named libraries (libgtk-4-1.dll, libgobject-2.0-0.dll, …)
  with NativeLibrary.Load, which probes the application directory. GLib derives its
  prefix (for share\ and lib\) from the directory the DLL lives in, so a flat layout
  "everything next to subs2srs.exe" works with no registry or PATH changes.

  Steps:
    1. Seed list = DLLs GirCore imports directly + gdk-pixbuf loaders (png, jpeg).
    2. Close the dependency set with ntldd -R, keeping only ucrt64\bin paths.
    3. Copy DLLs flat into the publish dir.
    4. Copy data: compiled GSettings schemas, Adwaita icons (pruned), pixbuf loaders
       (+ regenerated loaders.cache with relative paths), third-party licenses.
    5. Write gtk-bundle-manifest.txt (file list with sizes and package versions).

.PARAMETER Msys2Root
  MSYS2 installation root (default C:\msys64). Needs mingw-w64-ucrt-x86_64-gtk4 and
  mingw-w64-ucrt-x86_64-ntldd installed.

.PARAMETER PublishDir
  Directory containing subs2srs.exe (output of dotnet publish -r win-x64).

.EXAMPLE
  dotnet publish subs2srs/subs2srs.csproj -c Release -r win-x64 --self-contained true -o out\win-x64
  pwsh dist/windows/bundle-gtk.ps1 -Msys2Root C:\msys64 -PublishDir out\win-x64
#>
[CmdletBinding()]
param(
    [string]$Msys2Root = 'C:\msys64',
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [switch]$FullIconTheme
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ucrt = Join-Path $Msys2Root 'ucrt64'
$bin = Join-Path $ucrt 'bin'
$ntldd = Join-Path $bin 'ntldd.exe'
$PublishDir = (Resolve-Path $PublishDir).Path

if (-not (Test-Path (Join-Path $bin 'libgtk-4-1.dll'))) {
    throw "libgtk-4-1.dll not found in $bin. Install with: pacman -S mingw-w64-ucrt-x86_64-gtk4"
}
if (-not (Test-Path $ntldd)) {
    throw "ntldd.exe not found in $bin. Install with: pacman -S mingw-w64-ucrt-x86_64-ntldd"
}
if (-not (Test-Path (Join-Path $PublishDir 'subs2srs.exe'))) {
    throw "subs2srs.exe not found in $PublishDir. Run dotnet publish -r win-x64 first."
}

function Write-Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# Run a native tool and capture stdout; stderr is discarded. Windows PowerShell 5.1 turns
# redirected native stderr into terminating errors under ErrorActionPreference=Stop, hence the wrapper.
function Invoke-Native([string]$exe, [string[]]$arguments) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $out = & $exe @arguments 2>$null }
    finally { $ErrorActionPreference = $prev }
    return $out
}

# ── 1. Seed DLLs ───────────────────────────────────────────────────────────────
$seed = @(
    'libgtk-4-1.dll', 'libgdk_pixbuf-2.0-0.dll', 'libgio-2.0-0.dll', 'libgobject-2.0-0.dll',
    'libglib-2.0-0.dll', 'libgmodule-2.0-0.dll', 'libpango-1.0-0.dll', 'libpangocairo-1.0-0.dll',
    'libcairo-2.dll', 'libcairo-gobject-2.dll', 'libgraphene-1.0-0.dll', 'libharfbuzz-0.dll',
    'libharfbuzz-gobject-0.dll', 'libfreetype-6.dll'
) | ForEach-Object { Join-Path $bin $_ }

$loadersSrc = Join-Path $ucrt 'lib\gdk-pixbuf-2.0\2.10.0\loaders'
$loaderDlls = Get-ChildItem $loadersSrc -Filter '*.dll' |
    Where-Object { $_.Name -match 'png|jpeg|svg' } | ForEach-Object { $_.FullName }

$missing = $seed | Where-Object { -not (Test-Path $_) }
if ($missing) { throw "Missing seed DLLs: $($missing -join ', ')" }

# ── 2. Dependency closure via ntldd ───────────────────────────────────────────
Write-Step 'Resolving DLL closure with ntldd'
$closure = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)
foreach ($s in $seed + $loaderDlls) { [void]$closure.Add($s) }

$binNorm = $bin.TrimEnd('\').ToLowerInvariant()
foreach ($dll in @($seed + $loaderDlls)) {
    $out = Invoke-Native $ntldd @('-R', $dll)
    foreach ($line in $out) {
        # "  libfoo-1.dll => C:\msys64\ucrt64\bin\libfoo-1.dll (0x...)"
        if ($line -match '=>\s+(.+?\.dll)\s*(\(|$)') {
            $p = $Matches[1].Trim()
            if ($p.ToLowerInvariant().StartsWith($binNorm) -and (Test-Path $p)) { [void]$closure.Add($p) }
        }
    }
}

# ── 3. Copy DLLs flat ─────────────────────────────────────────────────────────
Write-Step "Copying $($closure.Count) DLLs into $PublishDir"
foreach ($p in $closure | Sort-Object) {
    Copy-Item $p -Destination $PublishDir -Force
}

# ── 4. Data files ─────────────────────────────────────────────────────────────
Write-Step 'GSettings schemas'
$schemaDst = Join-Path $PublishDir 'share\glib-2.0\schemas'
New-Item -ItemType Directory -Force $schemaDst | Out-Null
$schemaSrc = Join-Path $ucrt 'share\glib-2.0\schemas'
Get-ChildItem $schemaSrc -Filter '*.xml' | Where-Object { $_.Name -match '^org\.gtk\.|^org\.gnome\.desktop\.(interface|a11y|peripherals|sound|wm)' } |
    Copy-Item -Destination $schemaDst -Force
& (Join-Path $bin 'glib-compile-schemas.exe') $schemaDst
if ($LASTEXITCODE -ne 0) { throw 'glib-compile-schemas failed' }
Get-ChildItem $schemaDst -Filter '*.xml' | Remove-Item -Force   # only the compiled file is needed

Write-Step 'Icons'
$iconDst = Join-Path $PublishDir 'share\icons'
New-Item -ItemType Directory -Force $iconDst | Out-Null
$hicolor = Join-Path $ucrt 'share\icons\hicolor\index.theme'
New-Item -ItemType Directory -Force (Join-Path $iconDst 'hicolor') | Out-Null
Copy-Item $hicolor -Destination (Join-Path $iconDst 'hicolor') -Force
$adwaitaSrc = Join-Path $ucrt 'share\icons\Adwaita'
$adwaitaDst = Join-Path $iconDst 'Adwaita'
if ($FullIconTheme) {
    Copy-Item $adwaitaSrc -Destination $adwaitaDst -Recurse -Force
} else {
    # Icons GTK4 itself and subs2srs use (menus, file chooser, spin buttons, drop-downs, …).
    $iconNames = @(
        'pan-down-symbolic', 'pan-up-symbolic', 'pan-start-symbolic', 'pan-end-symbolic',
        'list-add-symbolic', 'list-remove-symbolic', 'edit-clear-symbolic', 'edit-find-symbolic',
        'edit-delete-symbolic', 'document-open-symbolic', 'document-save-symbolic', 'folder-symbolic',
        'folder-new-symbolic', 'folder-open-symbolic', 'go-up-symbolic', 'go-down-symbolic',
        'go-previous-symbolic', 'go-next-symbolic', 'go-home-symbolic', 'window-close-symbolic',
        'window-minimize-symbolic', 'window-maximize-symbolic', 'window-restore-symbolic',
        'object-select-symbolic', 'emblem-ok-symbolic', 'dialog-information-symbolic',
        'dialog-warning-symbolic', 'dialog-error-symbolic', 'dialog-question-symbolic',
        'view-more-symbolic', 'view-refresh-symbolic', 'view-list-symbolic', 'view-grid-symbolic',
        'open-menu-symbolic', 'media-playback-start-symbolic', 'media-playback-stop-symbolic',
        'media-playback-pause-symbolic', 'audio-volume-high-symbolic', 'image-missing',
        'text-x-generic-symbolic', 'video-x-generic-symbolic', 'audio-x-generic-symbolic',
        'drive-harddisk-symbolic', 'drive-removable-media-symbolic', 'user-home-symbolic',
        'user-desktop-symbolic', 'user-trash-symbolic', 'starred-symbolic', 'non-starred-symbolic',
        'computer-symbolic', 'network-server-symbolic', 'folder-documents-symbolic',
        'folder-download-symbolic', 'folder-music-symbolic', 'folder-pictures-symbolic',
        'folder-videos-symbolic', 'application-x-executable-symbolic', 'preferences-system-symbolic',
        'process-working-symbolic', 'content-loading-symbolic', 'system-search-symbolic',
        'input-keyboard-symbolic', 'accessories-text-editor-symbolic', 'help-about-symbolic',
        'changes-prevent-symbolic', 'changes-allow-symbolic', 'insert-image-symbolic', 'zoom-in-symbolic',
        'zoom-out-symbolic', 'zoom-original-symbolic', 'zoom-fit-best-symbolic', 'edit-copy-symbolic',
        'edit-cut-symbolic', 'edit-paste-symbolic', 'edit-select-all-symbolic', 'edit-undo-symbolic',
        'edit-redo-symbolic', 'format-text-bold-symbolic', 'color-select-symbolic', 'find-location-symbolic',
        'orientation-portrait-symbolic', 'orientation-landscape-symbolic', 'printer-symbolic',
        'document-print-symbolic', 'emblem-system-symbolic', 'emblem-default-symbolic',
        'checkbox-symbolic', 'checkbox-checked-symbolic', 'checkbox-mixed-symbolic',
        'radio-symbolic', 'radio-checked-symbolic', 'radio-mixed-symbolic', 'switch-on-symbolic', 'switch-off-symbolic'
    )
    New-Item -ItemType Directory -Force $adwaitaDst | Out-Null
    Copy-Item (Join-Path $adwaitaSrc 'index.theme') -Destination $adwaitaDst -Force
    $copied = 0
    Get-ChildItem $adwaitaSrc -Recurse -File | Where-Object {
        $iconNames -contains [IO.Path]::GetFileNameWithoutExtension($_.Name)
    } | ForEach-Object {
        $rel = $_.FullName.Substring($adwaitaSrc.Length).TrimStart('\')
        $dst = Join-Path $adwaitaDst $rel
        New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
        Copy-Item $_.FullName -Destination $dst -Force
        $copied++
    }
    Write-Host "    $copied icon files"
}
$null = Invoke-Native (Join-Path $bin 'gtk4-update-icon-cache.exe') @('--force', '--quiet', $adwaitaDst)
if ($LASTEXITCODE -ne 0) { Write-Warning 'gtk4-update-icon-cache failed (icons still work, just slower)' }
$null = Invoke-Native (Join-Path $bin 'gtk4-update-icon-cache.exe') @('--force', '--quiet', '--ignore-theme-index', (Join-Path $iconDst 'hicolor'))

Write-Step 'GdkPixbuf loaders'
$loadersDst = Join-Path $PublishDir 'lib\gdk-pixbuf-2.0\2.10.0\loaders'
New-Item -ItemType Directory -Force $loadersDst | Out-Null
foreach ($l in $loaderDlls) { Copy-Item $l -Destination $loadersDst -Force }
# gdk-pixbuf-query-loaders writes absolute paths; rewrite them relative to the module dir
# (GDK_PIXBUF_MODULEDIR is set by WindowsRuntimeSetup at startup).
$loaderPaths = @(Get-ChildItem $loadersDst -Filter '*.dll' | ForEach-Object { $_.FullName })
$cacheText = Invoke-Native (Join-Path $bin 'gdk-pixbuf-query-loaders.exe') $loaderPaths
if (-not $cacheText) { throw 'gdk-pixbuf-query-loaders produced no output' }
$cacheText = $cacheText -replace [regex]::Escape($loadersDst.Replace('\', '/') + '/'), '' `
                        -replace [regex]::Escape($loadersDst.Replace('\', '\\') + '\\'), '' `
                        -replace [regex]::Escape($loadersDst + '\'), ''
$cacheText | Set-Content -Path (Join-Path $PublishDir 'lib\gdk-pixbuf-2.0\2.10.0\loaders.cache') -Encoding ascii

Write-Step 'Third-party licenses'
$licDst = Join-Path $PublishDir 'THIRD-PARTY-LICENSES'
New-Item -ItemType Directory -Force $licDst | Out-Null
$licSrc = Join-Path $ucrt 'share\licenses'
$pkgs = @()
$pacman = Join-Path $Msys2Root 'usr\bin\pacman.exe'
foreach ($p in $closure) {
    $owner = Invoke-Native $pacman @('-Qqo', ('/ucrt64/bin/' + [IO.Path]::GetFileName($p)))
    if ($owner) { $pkgs += (("$owner" -split '\s+')[0]) }
}
$pkgs = $pkgs | Sort-Object -Unique
foreach ($pkg in $pkgs) {
    $short = $pkg -replace '^mingw-w64-ucrt-x86_64-', ''
    $src = Join-Path $licSrc $short
    if (Test-Path $src) { Copy-Item $src -Destination (Join-Path $licDst $short) -Recurse -Force }
}
Copy-Item (Join-Path $PSScriptRoot 'THIRD-PARTY-README.txt') -Destination $licDst -Force -ErrorAction SilentlyContinue

# ── 5. Manifest ───────────────────────────────────────────────────────────────
Write-Step 'Writing gtk-bundle-manifest.txt'
$manifest = @("# GTK runtime bundled from MSYS2 UCRT64 on $(Get-Date -Format s)", '')
$manifest += '# packages'
foreach ($pkg in $pkgs) {
    $ver = Invoke-Native $pacman @('-Q', $pkg)
    $manifest += "  $ver"
}
$manifest += '', '# files'
foreach ($p in $closure | Sort-Object) {
    $f = Get-Item (Join-Path $PublishDir ([IO.Path]::GetFileName($p)))
    $manifest += ('  {0,-40} {1,10:N0} bytes' -f $f.Name, $f.Length)
}
$manifest | Set-Content -Path (Join-Path $PublishDir 'gtk-bundle-manifest.txt') -Encoding utf8

$total = (Get-ChildItem $PublishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Done. Publish dir is {0:N1} MB." -f $total) -ForegroundColor Green
