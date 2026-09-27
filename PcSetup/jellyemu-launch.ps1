# JellyEmu game launcher, installed on gaming PCs by JellyEmu's setup script (setup.ps1).
# Run by the games-only Sunshine's single "JellyEmu" app. Asks JellyEmu which game the current
# stream is for, then starts the right emulator and waits until it exits. Sunshine ends the
# stream when this script exits, and ends this script (and the emulator) when the stream is quit.
#
# -Start (run at every sign-in by the "JellyEmu" scheduled task): tells JellyEmu where this PC's
# bridge is now (its IP address may have changed) and starts the bridge.
#
# Saves live on the JellyEmu server, never only on this PC, so any gaming PC (or the browser)
# continues from the same progress:
#  - RetroArch games (GB, GBC, GBA, PS1): the game's in-game save, in JellyEmu's automatic save
#    slot that the browser emulator uses too.
#  - PS2 and GameCube: the player's memory cards, as one "save set" per emulator (the cards hold
#    every game's saves, like on the real consoles).
# Each is synced before the game, every 20 s during it, and once more after it: -SyncOnly is run
# by Sunshine after the game ends (quitting the stream force-closes this script).
param([switch]$SyncOnly, [switch]$Start)

$ErrorActionPreference = 'Stop'
$root     = Split-Path -Parent $PSScriptRoot
$log      = Join-Path $PSScriptRoot 'launcher.log'
$syncDir  = Join-Path $PSScriptRoot 'sync'
$lastGame = Join-Path $syncDir 'last-game.json'

# Written by the setup script: server address, this PC's id and key, its bridge's port and
# Sunshine host/app ids. Readable only by administrators and the user who set the PC up.
$config   = Get-Content -LiteralPath (Join-Path $root 'jellyemu\device.json') -Raw | ConvertFrom-Json
$server   = $config.server.TrimEnd('/')
$deviceId = $config.id

# Preferred game files per platform, in order.
$romTypes = @{
    'PlayStation 2'    = @('.cue', '.chd', '.iso', '.cso', '.bin')
    'GameCube'         = @('.rvz', '.ciso', '.gcz', '.iso', '.gcm')
    'PlayStation'      = @('.m3u', '.cue', '.chd', '.pbp', '.iso')
    'Game Boy'         = @('.gb', '.gbc')
    'Game Boy Color'   = @('.gbc', '.gb')
    'Game Boy Advance' = @('.gba')
}

# RetroArch core per platform, and the folder it keeps saves in (the rest have their own emulator).
$retroArchCores = @{
    'PlayStation'      = @{ Dll = 'swanstation_libretro.dll'; SaveFolder = 'SwanStation' }
    'Game Boy'         = @{ Dll = 'gambatte_libretro.dll';    SaveFolder = 'Gambatte' }
    'Game Boy Color'   = @{ Dll = 'gambatte_libretro.dll';    SaveFolder = 'Gambatte' }
    'Game Boy Advance' = @{ Dll = 'mgba_libretro.dll';        SaveFolder = 'mGBA' }
}

New-Item -ItemType Directory -Force -Path $syncDir | Out-Null

function Log([string]$message) {
    Add-Content -LiteralPath $log -Encoding UTF8 -Value ('{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $message)
}

$headers = @{ 'X-JellyEmu-Launcher-Key' = $config.key; 'X-JellyEmu-Device' = $deviceId }

# Save requests name whose save they are (from this launcher's own launch info), so a late upload
# can never land in the next player's saves.
function Set-SaveOwner([string]$user, [string]$item) {
    $script:headers['X-JellyEmu-Player'] = $user
    $script:headers['X-JellyEmu-Item'] = $item
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# ---- Save sync ----------------------------------------------------------------------------------
# A save "unit" is either one file (Kind = file) or a set of folders zipped together (Kind = set).
# Its marker (in .\sync) records "<user>|<content hash>" of the version last in sync with the
# server, so changes on either side are detected, a newer save is never overwritten by an older
# one, and one player's saves are never uploaded as another's.

# Emulators keep save files open while a game runs (PCSX2 its memory cards), so they are read
# with sharing allowed, rather than failing with "being used by another process".
function Open-Shared([string]$file) {
    [IO.File]::Open($file, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
}

function Get-FileSha([string]$file) {
    $stream = Open-Shared $file
    try { [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose() }
}

# Files of a unit, relative to its base folder.
function Get-UnitFiles($unit, [string]$base) {
    if ($unit.Kind -eq 'file') {
        $name = Split-Path -Leaf $unit.Path
        if (Test-Path -LiteralPath (Join-Path $base $name)) { return @($name) }
        return @()
    }
    $files = @()
    foreach ($r in $unit.Roots) {
        $dir = Join-Path $base $r
        if (Test-Path -LiteralPath $dir) {
            $files += Get-ChildItem -LiteralPath $dir -File -Recurse | ForEach-Object { $_.FullName.Substring($base.Length).TrimStart('\') }
        }
    }
    return @($files | Sort-Object)
}

function Get-UnitBase($unit) { if ($unit.Kind -eq 'file') { Split-Path -Parent $unit.Path } else { $unit.Base } }

# True when a save file holds nothing: every byte the same (0x00 or 0xFF). Emulators write these
# for games that haven't saved yet; they must never count as a save or replace a real one.
function Test-BlankSave([string]$file) {
    $stream = Open-Shared $file
    try { $mem = New-Object IO.MemoryStream; $stream.CopyTo($mem); $bytes = $mem.ToArray() } finally { $stream.Dispose() }
    if ($bytes.Length -eq 0) { return $true }
    $first = $bytes[0]
    if ($first -ne 0 -and $first -ne 255) { return $false }
    foreach ($b in $bytes) { if ($b -ne $first) { return $false } }
    return $true
}

# Content hash of a unit's files under $base ('' when there are none, or only a blank save).
# @() everywhere: PowerShell turns a one-item list into the item itself.
function Get-ContentHash($unit, [string]$base) {
    $files = @(Get-UnitFiles $unit $base)
    if (-not $files.Count) { return '' }
    if ($unit.Kind -eq 'file') {
        $file = Join-Path $base $files[0]
        if (Test-BlankSave $file) { return '' }
        return Get-FileSha $file
    }
    $lines = $files | ForEach-Object { $_ + '|' + (Get-FileSha (Join-Path $base $_)) }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
    return [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)).Replace('-', '')
}

function Read-Marker($unit) {
    if (Test-Path -LiteralPath $unit.Marker) {
        $parts = (Get-Content -LiteralPath $unit.Marker -Raw).Trim().Split('|')
        if ($parts.Count -eq 2) { return @{ User = $parts[0]; Hash = $parts[1] } }
    }
    return @{ User = ''; Hash = '' }
}

function Write-Marker($unit, [string]$user, [string]$hash) {
    Set-Content -LiteralPath $unit.Marker -Value "$user|$hash" -NoNewline
}

function Clear-Unit($unit) {
    $base = Get-UnitBase $unit
    if ($unit.Kind -eq 'file') { Remove-Item -LiteralPath $unit.Path -ErrorAction SilentlyContinue; return }
    foreach ($r in $unit.Roots) { Remove-Item -LiteralPath (Join-Path $base $r) -Recurse -Force -ErrorAction SilentlyContinue }
}

# Copy a unit's files from one base folder to another.
function Copy-UnitFiles($unit, [string]$fromBase, [string]$toBase) {
    foreach ($f in (Get-UnitFiles $unit $fromBase)) {
        $dest = Join-Path $toBase $f
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
        Copy-Item -LiteralPath (Join-Path $fromBase $f) -Destination $dest -Force
    }
}

function Send-Unit($unit, [string]$user) {
    $base = Get-UnitBase $unit
    if ($unit.Kind -eq 'file') {
        $body = $unit.Path
    } else {
        $body = Join-Path $syncDir "$($unit.Name).upload.zip"
        Remove-Item -LiteralPath $body -ErrorAction SilentlyContinue
        $zip = [IO.Compression.ZipFile]::Open($body, 'Create')
        try {
            foreach ($f in (Get-UnitFiles $unit $base)) {
                $entry = $zip.CreateEntry($f.Replace('\', '/'), [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = (Get-Item -LiteralPath (Join-Path $base $f)).LastWriteTime
                $in = Open-Shared (Join-Path $base $f)
                $out = $entry.Open()
                try { $in.CopyTo($out) } finally { $out.Dispose(); $in.Dispose() }
            }
        } finally { $zip.Dispose() }
    }
    Invoke-WebRequest -Uri "$server$($unit.Remote)" -Method Post -Headers $headers -InFile $body `
        -ContentType 'application/octet-stream' -UseBasicParsing -TimeoutSec 60 | Out-Null
    if ($unit.Kind -ne 'file') { Remove-Item -LiteralPath $body -ErrorAction SilentlyContinue }
    Write-Marker $unit $user (Get-ContentHash $unit $base)
}

# Fetch the server's copy into a scratch folder laid out like the unit's base folder.
# Returns @{ Dir; Hash; Date } (Hash '' when the server has none).
function Get-ServerCopy($unit) {
    $scratch = Join-Path $syncDir "$($unit.Name).download"
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $scratch | Out-Null
    $file = Join-Path $scratch '.download'
    $date = $null
    try {
        $response = Invoke-WebRequest -Uri "$server$($unit.Remote)" -Headers $headers -OutFile $file -PassThru -UseBasicParsing -TimeoutSec 60
        if ($response.Headers['Last-Modified']) { $date = [DateTime]::Parse($response.Headers['Last-Modified']).ToUniversalTime() }
    } catch {
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) { return @{ Dir = $scratch; Hash = ''; Date = $null } }
        throw
    }
    if ($unit.Kind -eq 'file') {
        Move-Item -LiteralPath $file -Destination (Join-Path $scratch (Split-Path -Leaf $unit.Path))
    } else {
        [IO.Compression.ZipFile]::ExtractToDirectory($file, $scratch)
        Remove-Item -LiteralPath $file
    }
    return @{ Dir = $scratch; Hash = (Get-ContentHash $unit $scratch); Date = $date }
}

# Before the game: bring this PC's copy and the server's together for this player.
function Sync-UnitBefore($unit, [string]$user) {
    $base = Get-UnitBase $unit
    New-Item -ItemType Directory -Force -Path $base | Out-Null
    $marker = Read-Marker $unit
    $local = Get-ContentHash $unit $base

    if ($marker.User -and $marker.User -ne $user) {
        # Another player's saves are on this PC. Unsynced changes of theirs are kept aside (never
        # uploaded as this player's); then this player starts from their own saves.
        if ($local -and $local -ne $marker.Hash) {
            $stash = Join-Path $syncDir ("unsynced\$($unit.Name)-$($marker.User)-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
            Copy-UnitFiles $unit $base $stash
            Log "Another player's unsynced changes to $($unit.Label) were kept aside in $stash"
        }
        Clear-Unit $unit
        $local = ''
        $marker = @{ User = $user; Hash = '' }
    }

    $remote = Get-ServerCopy $unit
    try {
        $localChanged = $local -and $local -ne $marker.Hash
        $serverChanged = $remote.Hash -and $remote.Hash -ne $marker.Hash

        if ($localChanged -and $serverChanged -and $local -ne $remote.Hash) {
            # Both changed since they were last in sync: keep the newer, back up the other here.
            $stash = Join-Path $syncDir ("conflicts\$($unit.Name)-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
            $localDate = (Get-UnitFiles $unit $base | ForEach-Object { (Get-Item -LiteralPath (Join-Path $base $_)).LastWriteTimeUtc } | Measure-Object -Maximum).Maximum
            if ($remote.Date -and $remote.Date -gt $localDate) {
                Copy-UnitFiles $unit $base (Join-Path $stash 'this-pc')
                Clear-Unit $unit
                Copy-UnitFiles $unit $remote.Dir $base
                Write-Marker $unit $user $remote.Hash
                Log "Save conflict ($($unit.Label)): used the server's newer copy; this PC's is in $stash"
            } else {
                Copy-UnitFiles $unit $remote.Dir (Join-Path $stash 'server')
                Send-Unit $unit $user
                Log "Save conflict ($($unit.Label)): kept this PC's newer copy; the server's is in $stash"
            }
        } elseif ($localChanged) {
            Send-Unit $unit $user
            Log "Uploaded $($unit.Label) (this PC had changes the server didn't have yet)"
        } elseif ($remote.Hash -and $remote.Hash -ne $local) {
            # The server's copy is different and this PC has no unsynced changes: the server
            # changed, or this PC doesn't have the saves yet (e.g. a new gaming PC).
            Clear-Unit $unit
            Copy-UnitFiles $unit $remote.Dir $base
            Write-Marker $unit $user $remote.Hash
            Log "Downloaded $($unit.Label) from the server"
        } else {
            Write-Marker $unit $user $local
            Log "$($unit.Label) already in sync"
        }
    } finally {
        Remove-Item -LiteralPath $remote.Dir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# During and after the game: upload if it changed.
function Sync-UnitUp($unit, [string]$user) {
    $hash = Get-ContentHash $unit (Get-UnitBase $unit)
    if ($hash -and $hash -ne (Read-Marker $unit).Hash) {
        Send-Unit $unit $user
        Log "Uploaded $($unit.Label) to the server"
    }
}

# ---- Start-up -----------------------------------------------------------------------------------
# This PC's address as the server's side sees it: the local address Windows uses to reach the
# server (or the addresses the server said its stream proxy uses).
function Get-BridgeUrl {
    $targets = @($config.proxyAddresses) + @(([Uri]$server).Host) | Where-Object { $_ }
    foreach ($t in $targets) {
        try {
            $ip = if ($t -as [ipaddress]) { $t } else { ([Net.Dns]::GetHostAddresses($t) | Where-Object { $_.AddressFamily -eq 'InterNetwork' } | Select-Object -First 1).IPAddressToString }
            if (-not $ip) { continue }
            $route = Find-NetRoute -RemoteIPAddress $ip -ErrorAction Stop | Where-Object { $_.IPAddress } | Select-Object -First 1
            if ($route) { return "http://$($route.IPAddress):$($config.bridgePort)" }
        } catch { }
    }
    return $null
}

if ($Start) {
    try {
        $bridgeExe = Join-Path $root 'bridge\web-server.exe'
        $running = Get-CimInstance Win32_Process -Filter "Name='web-server.exe'" | Where-Object { $_.ExecutablePath -eq $bridgeExe }
        if (-not $running) {
            Start-Process -FilePath $bridgeExe -WorkingDirectory (Join-Path $root 'bridge') -WindowStyle Hidden
            Log 'Started the bridge'
        }
        $bridgeUrl = Get-BridgeUrl
        $body = @{ bridgeUrl = $bridgeUrl; version = $config.version } | ConvertTo-Json
        Invoke-RestMethod -Uri "$server/jellyemu/stream/pc/checkin" -Method Post -Headers $headers -Body $body -ContentType 'application/json' -TimeoutSec 20 | Out-Null
        Log "Checked in with JellyEmu (bridge at $bridgeUrl)"
    } catch {
        Log "ERROR (start-up): $($_.Exception.Message)"
    }
    exit 0
}

if ($SyncOnly) {
    try {
        if (Test-Path -LiteralPath $lastGame) {
            $last = Get-Content -LiteralPath $lastGame -Raw | ConvertFrom-Json
            Set-SaveOwner $last.user $last.item
            foreach ($unit in $last.units) { Sync-UnitUp $unit $last.user }
        }
    } catch {
        Log "ERROR (final save sync): $($_.Exception.Message)"
    }
    exit 0
}

# ---- Controller ---------------------------------------------------------------------------------
# The stream's controller is a virtual Xbox controller Sunshine creates when the stream connects, a
# moment after this launcher starts. Another may already be connected (another Sunshine
# session, or a controller used on this PC), so the emulator is told which slot is the
# stream's: the one that appears after the launcher started.
Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class JeXInput {
    [StructLayout(LayoutKind.Sequential)] struct STATE { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
    [DllImport("xinput1_4.dll")] static extern int XInputGetState(int user, out STATE state);
    public static bool Connected(int user) { STATE s; return XInputGetState(user, out s) == 0; }
}
'@
function Get-XInputSlots { @(0..3 | Where-Object { [JeXInput]::Connected($_) }) }
$slotsAtStart = Get-XInputSlots

function Get-StreamControllerSlot {
    for ($i = 0; $i -lt 50; $i++) {
        $new = @(Get-XInputSlots | Where-Object { $slotsAtStart -notcontains $_ })
        if ($new.Count) { Log "The stream's controller is slot $($new[0])"; return $new[0] }
        Start-Sleep -Milliseconds 100
    }
    $now = Get-XInputSlots
    $slot = if ($now.Count) { $now[-1] } else { 0 }
    Log "No new controller appeared (connected: $($now -join ', ')); using slot $slot"
    return $slot
}

# Point each emulator's player 1 at that slot. (PCSX2 names XInput controllers SDL-<slot>; Dolphin
# XInput/<slot>; RetroArch's xinput driver numbers them by slot too.)
function Set-EmulatorController([string]$platform, [int]$slot) {
    switch ($platform) {
        'PlayStation 2' {
            $ini = Join-Path $root 'pcsx2\inis\PCSX2.ini'
            $text = [IO.File]::ReadAllText($ini)
            [IO.File]::WriteAllText($ini, ($text -replace 'SDL-\d+/', "SDL-$slot/"), (New-Object Text.UTF8Encoding $false))
        }
        'GameCube' {
            $ini = Join-Path $root 'dolphin\User\Config\GCPadNew.ini'
            $text = [IO.File]::ReadAllText($ini)
            [IO.File]::WriteAllText($ini, ($text -replace '(?m)^Device = XInput/\d+/Gamepad', "Device = XInput/$slot/Gamepad"), (New-Object Text.UTF8Encoding $false))
        }
        default {
            $cfg = Join-Path $syncDir 'retroarch-controller.cfg'
            Set-Content -LiteralPath $cfg -Value "input_player1_joypad_index = `"$slot`"" -Encoding ASCII
            $script:emuArgs = @('--appendconfig', "`"$cfg`"") + $script:emuArgs
        }
    }
}

# ---- Screen -------------------------------------------------------------------------------------
# Games run on the Virtual Display Driver monitor, which is all the games Sunshine streams, so the
# desktop and notifications on the real screen never appear in the stream. Physical pixels.
Add-Type -Path (Join-Path $PSScriptRoot 'VirtualDisplay.cs')
[JeDisplay]::MakeDpiAware()
Add-Type -AssemblyName System.Windows.Forms

# The virtual monitor is switched on just before this runs; give it a moment to appear.
$target = $null
for ($i = 0; $i -lt 50 -and -not $target; $i++) {
    $found = [JeDisplay]::FindVirtualDisplay()
    if ($found) { $target = $found.Split(',') } else { Start-Sleep -Milliseconds 100 }
}
if ($target) {
    $screen = New-Object System.Drawing.Rectangle ([int]$target[0]), ([int]$target[1]), ([int]$target[2]), ([int]$target[3])
    $screenName = "virtual monitor $($target[4])"
} else {
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $screenName = 'main screen (virtual monitor not found)'
}

# Black cover on that screen first, so the stream never shows the desktop while the emulator
# starts. Not topmost: the emulator's window goes in front of it; it stays until the game ends.
$cover = New-Object System.Windows.Forms.Form
$cover.FormBorderStyle = 'None'
$cover.BackColor = [System.Drawing.Color]::Black
$cover.StartPosition = 'Manual'
$cover.Bounds = $screen
$cover.ShowInTaskbar = $false
$cover.Cursor = [System.Windows.Forms.Cursors]::Default
[System.Windows.Forms.Cursor]::Hide()
$cover.Show()
$cover.Activate()
[System.Windows.Forms.Application]::DoEvents()

# Sunshine switches the default audio device (and sets its format) when the stream's audio starts,
# a few seconds after this launcher runs. If the user's own Sunshine is streaming too, the two
# bounce the device back and forth several times. Each change cuts off programs playing sound on
# it, and emulators don't reopen their audio: they'd play silently, too fast or at a crawl. So the
# emulator starts once the stream's audio has been quiet for 2 s (or after 20 s regardless).
function Wait-StreamAudio {
    $logFile = Join-Path $root 'sunshine-data\sunshine.log'
    if (-not (Test-Path -LiteralPath $logFile)) { return }
    $since = (Get-Date).AddSeconds(-1)
    $deadline = (Get-Date).AddSeconds(20)
    $pattern = '^\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3})\]: \w+: (Reinitializing audio capture|Resetting sink|Changed virtual audio sink format|Audio capture format|Capturing audio from sink)'
    while ((Get-Date) -lt $deadline) {
        $last = $null
        try {
            $stream = Open-Shared $logFile
            try {
                $stream.Seek([Math]::Max(0, $stream.Length - 65536), 'Begin') | Out-Null
                $text = (New-Object IO.StreamReader($stream)).ReadToEnd()
            } finally { $stream.Dispose() }
            foreach ($line in $text -split "`r?`n") {
                if ($line -match $pattern) {
                    $at = [DateTime]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss.fff', $null)
                    if ($at -ge $since) { $last = $at }
                }
            }
        } catch { Log "ERROR (reading the Sunshine log): $($_.Exception.Message)"; return }
        if ($last -and ((Get-Date) - $last).TotalSeconds -ge 2) {
            Log "Stream audio ready after $([Math]::Round(((Get-Date) - $since).TotalSeconds - 1, 1)) s"
            return
        }
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 250
    }
    Log 'Stream audio not settled after 20 s; starting the game anyway'
}

# ---- Game ---------------------------------------------------------------------------------------
try {
    $info = Invoke-RestMethod -Uri "$server/jellyemu/stream/launch" -Headers $headers -TimeoutSec 15
    Log "Starting '$($info.name)' ($($info.platform)) from $($info.path)"

    # The game's folder as this PC reaches it, from the library paths in JellyEmu's Gaming PCs settings.
    if (-not $info.pcPath) { throw "This PC can't reach $($info.path): add its library folder under Library paths in JellyEmu's Gaming PCs settings" }
    $path = $info.pcPath

    $types = $romTypes[$info.platform]
    if (-not $types) { throw "No emulator set up for platform '$($info.platform)'" }

    $rom = $null
    if (Test-Path -LiteralPath $path -PathType Container) {
        $files = Get-ChildItem -LiteralPath $path -File -Recurse | Where-Object { -not $_.Name.StartsWith('.') }
        foreach ($type in $types) {
            $candidates = $files | Where-Object { $_.Extension -ieq $type }
            # Disc sheets are tiny, so take the first by name (Disc 1); otherwise the largest file.
            $rom = if ($type -in '.m3u', '.cue') { $candidates | Sort-Object Name | Select-Object -First 1 }
                   else { $candidates | Sort-Object Length -Descending | Select-Object -First 1 }
            if ($rom) { break }
        }
        if (-not $rom) { throw "No game file found in $path" }
        $rom = $rom.FullName
    } elseif (Test-Path -LiteralPath $path -PathType Leaf) {
        $rom = $path
    } else {
        throw "Game not found: $path"
    }
    Log "Game file: $rom"
    Log "Showing it on the $screenName"

    switch ($info.platform) {
        'PlayStation 2' {
            $exe  = Join-Path $root 'pcsx2\pcsx2-qt.exe'
            $emuArgs = @('-batch', '-nogui', '--', "`"$rom`"")
            $units = @(@{ Kind = 'set'; Name = 'ps2-memcards'; Label = 'the PS2 memory cards'
                          Base = (Join-Path $root 'pcsx2'); Roots = @('memcards')
                          Remote = '/jellyemu/stream/saveset/ps2-memcards'; Marker = (Join-Path $syncDir 'ps2-memcards.synced') })
        }
        'GameCube' {
            $exe  = Join-Path $root 'dolphin\Dolphin.exe'
            $emuArgs = @('-b', '-e', "`"$rom`"")
            $units = @(@{ Kind = 'set'; Name = 'gc-cards'; Label = 'the GameCube memory cards'
                          Base = (Join-Path $root 'dolphin\User'); Roots = @('GC')
                          Remote = '/jellyemu/stream/saveset/gc-cards'; Marker = (Join-Path $syncDir 'gc-cards.synced') })
        }
        default {
            $retro = $retroArchCores[$info.platform]
            $exe  = Join-Path $root 'retroarch\retroarch.exe'
            $core = Join-Path (Join-Path $root 'retroarch\cores') $retro.Dll
            $emuArgs = @('-L', "`"$core`"", "`"$rom`"")
            # RetroArch keeps each game's in-game save at saves\<core>\<game file name>.srm.
            $srm = Join-Path (Join-Path (Join-Path $root 'retroarch\saves') $retro.SaveFolder) ([IO.Path]::GetFileNameWithoutExtension($rom) + '.srm')
            $units = @(@{ Kind = 'file'; Name = 'game-save'; Label = 'the game''s save'; Path = $srm
                          Remote = '/jellyemu/stream/save'; Marker = "$srm.synced" })
        }
    }

    Set-SaveOwner $info.userId $info.itemId
    @{ user = $info.userId; item = $info.itemId; units = $units } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $lastGame
    foreach ($unit in $units) {
        try { Sync-UnitBefore $unit $info.userId } catch { Log "ERROR (syncing $($unit.Label) before the game): $($_.Exception.Message)" }
    }

    Wait-StreamAudio
    Set-EmulatorController $info.platform (Get-StreamControllerSlot)

    # Emulators start in a normal window; it is then made borderless and placed over the target
    # screen (their own full-screen modes always use the main screen). Checked again every second
    # in case the emulator re-creates or moves its window.
    $process = Start-Process -FilePath $exe -ArgumentList $emuArgs -WorkingDirectory (Split-Path -Parent $exe) -PassThru
    $placed = [IntPtr]::Zero
    $want = "$($screen.X),$($screen.Y),$($screen.Width),$($screen.Height)"
    $nextCheck = [DateTime]::MinValue
    $nextSaveSync = [DateTime]::Now.AddSeconds(20)
    while (-not $process.HasExited) {
        [System.Windows.Forms.Application]::DoEvents()
        if ([DateTime]::Now -ge $nextCheck) {
            $window = [JeDisplay]::FindWindow($process.Id)
            if ($window -ne [IntPtr]::Zero -and [JeDisplay]::Rect($window) -ne $want) {
                [JeDisplay]::CoverMonitor($window, $screen.X, $screen.Y, $screen.Width, $screen.Height)
                if ($placed -eq [IntPtr]::Zero) { Log "Placed the game window on the $screenName" }
                $placed = $window
            }
            # Check quickly until the window first appears, then once a second.
            $nextCheck = [DateTime]::Now.AddMilliseconds($(if ($placed -eq [IntPtr]::Zero) { 50 } else { 1000 }))
        }
        if ([DateTime]::Now -ge $nextSaveSync) {
            foreach ($unit in $units) {
                try { Sync-UnitUp $unit $info.userId } catch { Log "ERROR (uploading $($unit.Label)): $($_.Exception.Message)" }
            }
            $nextSaveSync = [DateTime]::Now.AddSeconds(20)
        }
        Start-Sleep -Milliseconds 50
    }
    Log "Emulator exited with code $($process.ExitCode)"
    foreach ($unit in $units) {
        try { Sync-UnitUp $unit $info.userId } catch { Log "ERROR (uploading $($unit.Label)): $($_.Exception.Message)" }
    }
}
catch {
    Log "ERROR: $($_.Exception.Message)"
    exit 1
}
finally {
    [System.Windows.Forms.Cursor]::Show()
    $cover.Close()
}
