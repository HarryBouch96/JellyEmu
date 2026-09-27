<#
JellyEmu gaming PC setup.

Turns a Windows PC into a JellyEmu gaming PC: games streamed from it play inside Jellyfin on any
screen. Get the command (with a one-time setup code) from JellyEmu's settings, Gaming PCs tab,
and paste it into PowerShell on the PC. It asks for administrator rights.

Installs into one folder (C:\JellyEmu by default), with its own copy of Sunshine; a Sunshine you
already have is left alone:
  sunshine\       games-only Sunshine (Windows service "SunshineJellyEmu"), sunshine-data\ its settings
  bridge\         moonlight-web-stream, which turns the stream into something a browser can play
  pcsx2\ dolphin\ retroarch\   the emulators (PS2, GameCube, and PS1/Game Boy/GBA)
  launcher\       starts the game Jellyfin asks for, syncs saves with the server
  jellyemu\       this PC's id and key, downloads
Plus: the Virtual Display Driver (a monitor only the stream shows) and ViGEmBus (the controller
driver) if they're missing, firewall rules, and a "JellyEmu" task that starts the bridge at sign-in.
Every download is a fixed version, checked against its SHA-256 before it's used.

Run it again to update. -Uninstall removes everything it installed except the two drivers.
-Stage (testing): downloads and sets up the files only; no drivers, services, firewall or pairing.
#>
[CmdletBinding()]
param(
    [string]$Server,
    [string]$Code,
    [string]$InstallDir = 'C:\JellyEmu',
    [switch]$Uninstall,
    [switch]$Stage
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is very slow with its progress bar
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$SetupVersion = '1'

function Step([string]$m) { Write-Host ''; Write-Host "== $m" -ForegroundColor Cyan }
function Say([string]$m)  { Write-Host "   $m" }
function Warn([string]$m) { Write-Host "   ! $m" -ForegroundColor Yellow; $script:warnings += $m }
$warnings = @()

# ---- Administrator ----------------------------------------------------------------------------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and -not $Stage) {
    if (-not $PSCommandPath) { throw 'Save this script to a file and run it from there (it needs to restart itself as administrator).' }
    Write-Host 'JellyEmu setup needs administrator rights: Windows will ask for them now.'
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-File', "`"$PSCommandPath`"", '-InstallDir', "`"$InstallDir`"")
    if ($Server) { $argList += @('-Server', "`"$Server`"") }
    if ($Code) { $argList += @('-Code', "`"$Code`"") }
    if ($Uninstall) { $argList += '-Uninstall' }
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList
    exit
}

$InstallDir  = [IO.Path]::GetFullPath($InstallDir)
$jeDir       = Join-Path $InstallDir 'jellyemu'
$downloads   = Join-Path $jeDir 'downloads'
$configFile  = Join-Path $jeDir 'device.json'
$installedFile = Join-Path $jeDir 'installed.json'
$serviceName = 'SunshineJellyEmu'
$taskName    = 'JellyEmu'
$fwGroup     = 'JellyEmu'
$bridgeUser  = 'JellyEmuStream'   # the bridge account Jellyfin's stream proxy signs in as
$vddHardwareId = 'Root\MttVDD'
$displayClass = [Guid]'4d36e968-e325-11ce-bfc1-08002be10318'
$me = [Security.Principal.WindowsIdentity]::GetCurrent().Name

# ---- Uninstall --------------------------------------------------------------------------------
if ($Uninstall) {
    Step 'Removing JellyEmu from this PC'
    $svc = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    if ($svc -and $svc.PathName -like "*$InstallDir*") {
        Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
        sc.exe delete $serviceName | Out-Null
        Say 'Removed the games Sunshine service'
    }
    Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Get-NetFirewallRule -Group $fwGroup -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Start-Sleep -Seconds 2
    if (Test-Path -LiteralPath $InstallDir) { Remove-Item -LiteralPath $InstallDir -Recurse -Force }
    Say "Removed $InstallDir"
    Write-Host ''
    Write-Host 'Done. Saves are kept on the Jellyfin server. Remove this PC in JellyEmu''s Gaming PCs settings too.'
    Write-Host 'The Virtual Display Driver and ViGEmBus were left installed (other programs may use them);'
    Write-Host 'remove them in Device Manager and Apps if nothing else needs them.'
    exit
}

# ---- This PC's registration -------------------------------------------------------------------
function Save-Config($c) {
    New-Item -ItemType Directory -Force -Path $jeDir | Out-Null
    $c | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $configFile -Encoding UTF8
}

function Protect-Folder([string]$path, [string]$userRights) {
    # Administrators and SYSTEM in full control; the user who set the PC up gets $userRights.
    # Nothing for anyone else: some of these files run as SYSTEM, and device.json holds the key.
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    if ($Stage) { return }
    $out = icacls $path /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "${me}:(OI)(CI)$userRights" /T /C /Q 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Couldn't set permissions on ${path}: $out" }
}

Step 'JellyEmu gaming PC setup'
if ([Environment]::OSVersion.Version.Major -lt 10 -or -not [Environment]::Is64BitOperatingSystem) { throw 'This needs 64-bit Windows 10 or 11.' }

$config = $null
if (Test-Path -LiteralPath $configFile) {
    $config = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
    if ($Server -and $Server.TrimEnd('/') -ne $config.server) { throw "This PC is already set up for $($config.server). Run with -Uninstall first to set it up for another server." }
    $Server = $config.server
    Say "Updating '$($config.name)' (already set up for $Server)"
} else {
    if (-not $Server) { throw 'Give the server address, e.g. -Server https://jellyfin.example.com (the Gaming PCs settings show the full command).' }
    $Server = $Server.TrimEnd('/')
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    if (-not $Stage) { Protect-Folder $InstallDir 'M' }
    if ($Code) {
        Say "Registering with $Server"
        $reg = Invoke-RestMethod -Uri "$Server/jellyemu/stream/pc/register" -Method Post -ContentType 'application/json' `
            -Body (@{ code = $Code; computerName = $env:COMPUTERNAME } | ConvertTo-Json) -TimeoutSec 30
        $config = [pscustomobject]@{ server = $Server; id = $reg.id; name = $reg.name; key = $reg.key; version = $SetupVersion
                                     bridgePort = 0; sunshinePort = 0; hostId = 0; appId = 0; proxyAddresses = @() }
        Say "Registered as '$($reg.name)'"
    } elseif ($Stage) {
        $config = [pscustomobject]@{ server = $Server; id = 'stage'; name = 'Stage test'; key = ''; version = $SetupVersion
                                     bridgePort = 8080; sunshinePort = 57989; hostId = 0; appId = 0; proxyAddresses = @() }
        Say 'Stage test without registering (no BIOS files, no check-in)'
    } else {
        throw 'Give the setup code from JellyEmu''s Gaming PCs settings: -Code XXXX-XXXX'
    }
    Protect-Folder $jeDir 'RX'
    Save-Config $config
}
$registered = [bool]$config.key
$pcHeaders = @{ 'X-JellyEmu-Device' = $config.id; 'X-JellyEmu-Launcher-Key' = $config.key }

# ---- Downloads --------------------------------------------------------------------------------
Step 'Downloading'
New-Item -ItemType Directory -Force -Path $downloads | Out-Null
$manifest = Invoke-RestMethod -Uri "$Server/jellyemu/stream/pc/file/manifest.json" -TimeoutSec 30
$installed = @{}
if (Test-Path -LiteralPath $installedFile) {
    (Get-Content -LiteralPath $installedFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $installed[$_.Name] = $_.Value }
}

function Get-Component([string]$name) {
    $c = $manifest.components.$name
    $file = Join-Path $downloads $c.file
    if ((Test-Path -LiteralPath $file) -and (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -eq $c.sha256) { return $file }
    Say "$($c.label) $($c.version)"
    $part = "$file.part"
    Invoke-WebRequest -Uri $c.url -OutFile $part -UseBasicParsing
    $hash = (Get-FileHash -LiteralPath $part -Algorithm SHA256).Hash
    if ($hash -ne $c.sha256) {
        Remove-Item -LiteralPath $part -Force
        throw "The download of $($c.label) doesn't match its expected checksum, so it wasn't used. Try again later."
    }
    Move-Item -LiteralPath $part -Destination $file -Force
    return $file
}

$sevenZip = Get-Component '7zr'

# Unpacks a component into $dest (over what's there, so saves and settings stay). Archives with a
# single top folder are unpacked from inside it. $only: just these paths from the archive.
function Expand-Component([string]$name, [string]$dest, [string[]]$only = @()) {
    $file = Get-Component $name
    $tmp = Join-Path $jeDir "unpack-$name"
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    if ($file.EndsWith('.7z')) {
        $out = & $sevenZip x -y "-o$tmp" $file @only 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Couldn't unpack $($manifest.components.$name.label): $out" }
    } else {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($file, $tmp)
    }
    $src = $tmp
    $items = @(Get-ChildItem -LiteralPath $tmp)
    if ($items.Count -eq 1 -and $items[0].PSIsContainer) { $src = $items[0].FullName }
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    robocopy $src $dest /E /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Couldn't copy $($manifest.components.$name.label) into $dest" }
    Remove-Item -LiteralPath $tmp -Recurse -Force
    $script:installed[$name] = $manifest.components.$name.version
    $script:installed | ConvertTo-Json | Set-Content -LiteralPath $installedFile -Encoding UTF8
}

function Test-Current([string]$name, [string]$dest) {
    return (Test-Path -LiteralPath $dest) -and $installed[$name] -eq $manifest.components.$name.version
}

# ---- Emulators --------------------------------------------------------------------------------
Step 'Emulators'
$pcsx2 = Join-Path $InstallDir 'pcsx2'
$dolphin = Join-Path $InstallDir 'dolphin'
$retroarch = Join-Path $InstallDir 'retroarch'
if (-not (Test-Current 'pcsx2' $pcsx2)) { Expand-Component 'pcsx2' $pcsx2 }
if (-not (Test-Current 'dolphin' $dolphin)) { Expand-Component 'dolphin' $dolphin }
if (-not (Test-Current 'retroarch' $retroarch)) { Expand-Component 'retroarch' $retroarch }
if (-not (Test-Current 'retroarch-cores' (Join-Path $retroarch 'cores\mgba_libretro.dll'))) {
    $cores = @('RetroArch-Win64\cores\mgba_libretro.dll', 'RetroArch-Win64\cores\gambatte_libretro.dll', 'RetroArch-Win64\cores\swanstation_libretro.dll')
    Expand-Component 'retroarch-cores' (Join-Path $retroarch 'cores') $cores
    # (the archive's top folder is RetroArch-Win64\cores; unpacking from inside it can leave a nested "cores")
    $nested = Join-Path $retroarch 'cores\cores'
    if (Test-Path -LiteralPath $nested) { Move-Item -Path "$nested\*" -Destination (Join-Path $retroarch 'cores') -Force; Remove-Item -LiteralPath $nested -Recurse -Force }
}
Say 'PCSX2, Dolphin and RetroArch are in place'

# BIOS files come from JellyEmu's BIOS folder on the server (they can't be downloaded from anywhere else).
$ps2Bios = $null
$ps1Bios = $false
if ($registered) {
    $bios = @(Invoke-RestMethod -Uri "$Server/jellyemu/stream/pc/bios" -Headers $pcHeaders -TimeoutSec 30)
    foreach ($b in $bios) {
        $dir = if ($b.system -eq 'PlayStation 2') { Join-Path $pcsx2 'bios' } else { Join-Path $retroarch 'system' }
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $target = Join-Path $dir $b.fileName
        $ok = (Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target -Algorithm SHA1).Hash -eq $b.sha1
        if (-not $ok) {
            Invoke-WebRequest -Uri "$Server/jellyemu/stream/pc/bios/file?path=$([Uri]::EscapeDataString($b.path))" -Headers $pcHeaders -OutFile $target -UseBasicParsing
            if ((Get-FileHash -LiteralPath $target -Algorithm SHA1).Hash -ne $b.sha1) { Remove-Item -LiteralPath $target -Force; throw "The BIOS file $($b.fileName) didn't arrive intact" }
        }
        if ($b.system -eq 'PlayStation 2' -and -not $ps2Bios -and $b.fileName -like '*.bin') { $ps2Bios = $b.fileName }
        if ($b.system -eq 'PlayStation') { $ps1Bios = $true }
    }
    Say "BIOS files: $(if ($bios.Count) { ($bios | ForEach-Object { $_.fileName }) -join ', ' } else { 'none' })"
}
if (-not $ps2Bios) { Warn 'No PlayStation 2 BIOS in JellyEmu''s BIOS folder, so this PC won''t offer PS2 games. Add one to the server''s BIOS folder, then run this setup again.' }
if (-not $ps1Bios) { Warn 'No PlayStation BIOS in JellyEmu''s BIOS folder, so this PC won''t offer PS1 games. Add one (e.g. scph5501.bin), then run this setup again.' }

# Settings that make each emulator start straight into the game in a plain window (the launcher
# puts the window on the virtual monitor), with controls matching JellyEmu's controller.
Set-Content -LiteralPath (Join-Path $pcsx2 'portable.txt') -Value '' -Encoding ASCII
New-Item -ItemType Directory -Force -Path (Join-Path $pcsx2 'inis') | Out-Null
@"
[UI]
SettingsVersion = 1
SetupWizardIncomplete = false
StartFullscreen = false
HideMouseCursor = true
ConfirmShutdown = false

[Folders]
Bios = bios

[Filenames]
BIOS = $ps2Bios

[InputSources]
SDL = true
XInput = false

[Pad1]
Type = DualShock2
Up = SDL-0/DPadUp
Right = SDL-0/DPadRight
Down = SDL-0/DPadDown
Left = SDL-0/DPadLeft
Triangle = SDL-0/FaceNorth
Circle = SDL-0/FaceEast
Cross = SDL-0/FaceSouth
Square = SDL-0/FaceWest
Select = SDL-0/Back
Start = SDL-0/Start
L1 = SDL-0/LeftShoulder
R1 = SDL-0/RightShoulder
L2 = SDL-0/+LeftTrigger
R2 = SDL-0/+RightTrigger
L3 = SDL-0/LeftStick
R3 = SDL-0/RightStick
LUp = SDL-0/-LeftY
LRight = SDL-0/+LeftX
LDown = SDL-0/+LeftY
LLeft = SDL-0/-LeftX
RUp = SDL-0/-RightY
RRight = SDL-0/+RightX
RDown = SDL-0/+RightY
RLeft = SDL-0/-RightX
LargeMotor = SDL-0/LargeMotor
SmallMotor = SDL-0/SmallMotor

[Hotkeys]
OpenPauseMenu = SDL-0/Back & SDL-0/Start

[EmuCore/GS]
upscale_multiplier = 2
"@ | Set-Content -LiteralPath (Join-Path $pcsx2 'inis\PCSX2.ini') -Encoding UTF8

Set-Content -LiteralPath (Join-Path $dolphin 'portable.txt') -Value '' -Encoding ASCII
$dolphinConfig = Join-Path $dolphin 'User\Config'
New-Item -ItemType Directory -Force -Path $dolphinConfig | Out-Null
@"
[Interface]
ConfirmStop = False
[Display]
Fullscreen = False
RenderWindowAutoSize = False
[Analytics]
PermissionAsked = True
Enabled = False
[AutoUpdate]
UpdateTrack =
[Core]
SIDevice0 = 6
[DSP]
DSPThread = True
"@ | Set-Content -LiteralPath (Join-Path $dolphinConfig 'Dolphin.ini') -Encoding UTF8
@"
[GCPad1]
Device = XInput/0/Gamepad
Buttons/A = ``Button A``
Buttons/B = ``Button B``
Buttons/X = ``Button X``
Buttons/Y = ``Button Y``
Buttons/Z = ``Shoulder R``
Buttons/Start = Start
Main Stick/Up = ``Left Y+``
Main Stick/Down = ``Left Y-``
Main Stick/Left = ``Left X-``
Main Stick/Right = ``Left X+``
C-Stick/Up = ``Right Y+``
C-Stick/Down = ``Right Y-``
C-Stick/Left = ``Right X-``
C-Stick/Right = ``Right X+``
Triggers/L = ``Trigger L``
Triggers/R = ``Trigger R``
Triggers/L-Analog = ``Trigger L``
Triggers/R-Analog = ``Trigger R``
D-Pad/Up = ``Pad N``
D-Pad/Down = ``Pad S``
D-Pad/Left = ``Pad W``
D-Pad/Right = ``Pad E``
Rumble/Motor = ``Motor L``
"@ | Set-Content -LiteralPath (Join-Path $dolphinConfig 'GCPadNew.ini') -Encoding UTF8
@"
[Settings]
InternalResolution = 2
"@ | Set-Content -LiteralPath (Join-Path $dolphinConfig 'GFX.ini') -Encoding UTF8

@'
# Written by JellyEmu's setup; RetroArch is started by the JellyEmu launcher.
# Portable: all paths are relative to this folder (":" = RetroArch's own folder).
libretro_directory = ":\cores"
libretro_info_path = ":\info"
system_directory = ":\system"
savefile_directory = ":\saves"
savestate_directory = ":\states"
joypad_autoconfig_dir = ":\autoconfig"
input_joypad_driver = "xinput"
input_autodetect_enable = "true"
# The launcher places the window on the streaming monitor; don't go full screen or move it.
video_fullscreen = "false"
video_window_save_positions = "false"
pause_nonactive = "false"
# No on-screen pop-ups (e.g. "controller connected") in the stream.
video_font_enable = "false"
# Some TV apps send a controller button as Escape (= quit in RetroArch): keyboard hotkeys only
# work while F15 is held, which is never sent. No controller menu/quit combos either.
input_enable_hotkey = "f15"
input_menu_toggle_gamepad_combo = "0"
input_quit_gamepad_combo = "0"
config_save_on_exit = "false"
# No "File / Command / Window / Help" menu bar on the window.
ui_menubar_enable = "false"
# Write in-game saves to disk every 5 s, so they survive the stream being quit (force close).
autosave_interval = "5"
# Sunshine switches the default audio device when a stream starts. DirectSound streams follow the
# default device; WASAPI would stay on the old one and go silent (or stall the game).
audio_driver = "dsound"
audio_sync = "true"
video_vsync = "true"
'@ | Set-Content -LiteralPath (Join-Path $retroarch 'retroarch.cfg') -Encoding ASCII

# ---- Launcher ---------------------------------------------------------------------------------
Step 'Launcher'
$launcherDir = Join-Path $InstallDir 'launcher'
New-Item -ItemType Directory -Force -Path $launcherDir | Out-Null
foreach ($f in 'jellyemu-launch.ps1', 'VirtualDisplay.cs') {
    Invoke-WebRequest -Uri "$Server/jellyemu/stream/pc/file/$f" -OutFile (Join-Path $launcherDir $f) -UseBasicParsing
}
$launcher = Join-Path $launcherDir 'jellyemu-launch.ps1'
Say "Installed in $launcherDir"

$platforms = @('GameCube', 'Game Boy', 'Game Boy Color', 'Game Boy Advance')
if ($ps2Bios) { $platforms += 'PlayStation 2' }
if ($ps1Bios) { $platforms += 'PlayStation' }

if ($Stage) {
    Step 'Stage test finished'
    Say "Files are in $InstallDir. Nothing was installed on Windows itself (drivers, services, firewall, tasks)."
    Say "Would offer: $($platforms -join ', ')"
    exit
}

# ---- Drivers ----------------------------------------------------------------------------------
Step 'Drivers'
if (-not (Get-Service ViGEmBus -ErrorAction SilentlyContinue)) {
    Say 'Installing ViGEmBus (lets Sunshine create a controller for the game)'
    $p = Start-Process -FilePath (Get-Component 'vigembus') -ArgumentList '/exenoui', '/qn', '/norestart' -Wait -PassThru
    if ($p.ExitCode -notin 0, 3010) { throw "ViGEmBus setup failed (exit code $($p.ExitCode))" }
} else { Say 'ViGEmBus is already installed' }

function Get-VirtualDisplay { Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | Where-Object { $_.HardwareID -contains $vddHardwareId } | Select-Object -First 1 }
$vdd = Get-VirtualDisplay
if (-not $vdd) {
    Say 'Installing the Virtual Display Driver (a monitor only the stream shows). Windows may ask you to confirm.'
    $vddDir = Join-Path $jeDir 'vdd'
    Remove-Item -LiteralPath $vddDir -Recurse -Force -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory((Get-Component 'vdd'), $vddDir)
    $inf = Get-ChildItem -LiteralPath $vddDir -Recurse -Filter 'MttVDD.inf' | Select-Object -First 1
    # The driver reads its monitor list from C:\VirtualDisplayDriver (one 1080p monitor by default).
    if (-not (Test-Path 'C:\VirtualDisplayDriver\vdd_settings.xml')) {
        New-Item -ItemType Directory -Force -Path 'C:\VirtualDisplayDriver' | Out-Null
        Copy-Item -LiteralPath (Join-Path $inf.DirectoryName 'vdd_settings.xml') -Destination 'C:\VirtualDisplayDriver\vdd_settings.xml'
    }
    Invoke-WebRequest -Uri "$Server/jellyemu/stream/pc/file/DeviceSetup.cs" -OutFile (Join-Path $jeDir 'DeviceSetup.cs') -UseBasicParsing
    Add-Type -Path (Join-Path $jeDir 'DeviceSetup.cs')
    if ([JeDriver]::InstallRootDevice($inf.FullName, $vddHardwareId, 'Display', $displayClass)) { Warn 'Windows wants a restart to finish installing the display driver. Restart, then run this setup again.' }
    for ($i = 0; $i -lt 30 -and -not ($vdd = Get-VirtualDisplay); $i++) { Start-Sleep -Seconds 1 }
    if (-not $vdd) { throw 'The Virtual Display Driver didn''t appear after installing it' }
} else { Say 'The Virtual Display Driver is already installed' }
if ($vdd.Status -ne 'OK') { Enable-PnpDevice -InstanceId $vdd.InstanceId -Confirm:$false }
if (-not ('JeHttp' -as [type])) {
    Invoke-WebRequest -Uri "$Server/jellyemu/stream/pc/file/DeviceSetup.cs" -OutFile (Join-Path $jeDir 'DeviceSetup.cs') -UseBasicParsing
    Add-Type -Path (Join-Path $jeDir 'DeviceSetup.cs')
}

# ---- Games Sunshine ---------------------------------------------------------------------------
Step 'Sunshine (games only)'
function Test-PortFree([int]$port) { -not (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) }
$sunshineDir = Join-Path $InstallDir 'sunshine'
$sunshineData = Join-Path $InstallDir 'sunshine-data'
$service = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($service) { Stop-Service $serviceName -Force; Start-Sleep -Seconds 2 }

if (-not $config.sunshinePort) {
    # Its own ports, clear of a normal Sunshine (47989) and each other (Sunshine uses port-5 .. port+21).
    $config.sunshinePort = @(57989, 58989, 59989) | Where-Object { (Test-PortFree $_) -and (Test-PortFree ($_ + 1)) } | Select-Object -First 1
    if (-not $config.sunshinePort) { throw 'No free ports for the games Sunshine' }
}
if (-not (Test-Current 'sunshine' $sunshineDir)) { Expand-Component 'sunshine' $sunshineDir }
# Runs as SYSTEM: only administrators may change its program and settings.
Protect-Folder $sunshineDir 'RX'
Protect-Folder $sunshineData 'RX'

$sunshineConf = Join-Path $sunshineDir 'config\sunshine.conf'
New-Item -ItemType Directory -Force -Path (Split-Path $sunshineConf) | Out-Null
$outputName = $null
if (Test-Path -LiteralPath $sunshineConf) {
    $line = Select-String -LiteralPath $sunshineConf -Pattern '^output_name\s*=\s*(.+)$' | Select-Object -First 1
    if ($line) { $outputName = $line.Matches[0].Groups[1].Value.Trim() }
}
function Write-SunshineConf {
    $lines = @(
        "sunshine_name = $($env:COMPUTERNAME)-JellyEmu",
        "port = $($config.sunshinePort)",
        "file_apps = $(Join-Path $sunshineData 'apps.json')",
        "file_state = $(Join-Path $sunshineData 'sunshine_state.json')",
        "log_path = $(Join-Path $sunshineData 'sunshine.log')",
        # Its web page only from this PC (the bridge pairs through it during setup).
        'origin_web_ui_allowed = pc'
    )
    if ($script:outputName) { $lines += "output_name = $($script:outputName)" }
    $lines | Set-Content -LiteralPath $sunshineConf -Encoding ASCII
}
Write-SunshineConf

# One app: the launcher, which starts whichever game Jellyfin asked for. The virtual monitor is
# switched on first (in case something turned it off); saves are synced once more afterwards.
$enableDisplay = "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -Command `"Enable-PnpDevice -InstanceId '$($vdd.InstanceId)' -Confirm:`$false -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 1500; exit 0`""
$apps = [ordered]@{
    env  = @{}
    apps = @([ordered]@{
        name = 'JellyEmu'
        cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$launcher`""
        'working-dir' = $launcherDir
        'prep-cmd' = @(
            [ordered]@{ do = $enableDisplay; undo = ''; elevated = $true },
            [ordered]@{ do = ''; undo = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$launcher`" -SyncOnly"; elevated = $false }
        )
    })
}
$apps | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $sunshineData 'apps.json') -Encoding UTF8

# Web API login, used once below to pair the bridge. New random password on every setup run.
$sunshineUser = 'jellyemu'
$sunshinePassword = [Convert]::ToBase64String((1..24 | ForEach-Object { [byte](Get-Random -Maximum 256) }))
Push-Location $sunshineDir
try { & (Join-Path $sunshineDir 'sunshine.exe') --creds $sunshineUser $sunshinePassword | Out-Null } finally { Pop-Location }

if (-not $service) {
    sc.exe create $serviceName binPath= "`"$(Join-Path $sunshineDir 'tools\sunshinesvc.exe')`"" start= auto DisplayName= 'Sunshine (JellyEmu games)' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Couldn't create the $serviceName service" }
    Say "Created the $serviceName service"
}
$sunshineLog = Join-Path $sunshineData 'sunshine.log'
Remove-Item -LiteralPath $sunshineLog -ErrorAction SilentlyContinue
Start-Service $serviceName

# Stream only the virtual monitor: find its id in Sunshine's list of displays.
if (-not $outputName) {
    $found = $null
    for ($i = 0; $i -lt 40 -and -not $found; $i++) {
        Start-Sleep -Seconds 1
        if (-not (Test-Path -LiteralPath $sunshineLog)) { continue }
        $text = [IO.File]::ReadAllText($sunshineLog)
        $at = $text.IndexOf('Currently available display devices:')
        if ($at -lt 0) { continue }
        $json = [regex]::Match($text.Substring($at), '(?s)\n(\[.*?\n\])').Groups[1].Value
        try { $found = ($json | ConvertFrom-Json) | Where-Object { $_.edid.manufacturer_id -eq 'MTT' -or $_.friendly_name -like '*VDD*' } | Select-Object -First 1 } catch { }
    }
    if (-not $found) { throw 'Sunshine didn''t list the virtual monitor. Check that it shows in Settings > System > Display, then run this setup again.' }
    $outputName = $found.device_id
    Write-SunshineConf
    Restart-Service $serviceName
    Say "Streams the virtual monitor ($($found.display_name))"
}
for ($i = 0; $i -lt 30 -and (Test-PortFree ($config.sunshinePort + 1)); $i++) { Start-Sleep -Seconds 1 }

# ---- Bridge -----------------------------------------------------------------------------------
Step 'Bridge (browser streaming)'
$bridgeDir = Join-Path $InstallDir 'bridge'
Get-CimInstance Win32_Process -Filter "Name='web-server.exe' OR Name='streamer.exe'" |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($bridgeDir, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
if (-not (Test-Current 'bridge' $bridgeDir)) { Start-Sleep -Seconds 1; Expand-Component 'bridge' $bridgeDir }
if (-not $config.bridgePort) {
    $config.bridgePort = @(8080, 8090, 8180, 18080) | Where-Object { Test-PortFree $_ } | Select-Object -First 1
    if (-not $config.bridgePort) { throw 'No free port for the bridge' }
}

$bridgeConfigFile = Join-Path $bridgeDir 'server\config.json'
New-Item -ItemType Directory -Force -Path (Split-Path $bridgeConfigFile) | Out-Null
$bridgeConfig = [ordered]@{
    data_storage = [ordered]@{ type = 'json'; path = 'server/data.json'; session_expiration_check_interval = @{ secs = 300; nanos = 0 } }
    web_server = [ordered]@{
        bind_address = "0.0.0.0:$($config.bridgePort)"
        certificate = $null
        url_path_prefix = ''
        session_cookie_secure = $false
        session_cookie_expiration = @{ secs = 86400; nanos = 0 }
        first_login_create_admin = $true
        first_login_assign_global_hosts = $true
        default_user_id = $null
        default_role_id = $null
        # Jellyfin's stream proxy (Caddy) signs every request in as this account, after JellyEmu
        # has checked the viewer's Jellyfin login. The firewall lets only the proxy reach this port.
        forwarded_header = [ordered]@{ username_header = 'X-JellyEmu-Stream-User'; auto_create_missing_user = $false }
    }
    moonlight = [ordered]@{ default_http_port = 47989; pair_device_name = 'JellyEmu' }
    streamer_path = './streamer'
    log = [ordered]@{ level_filter = 'INFO'; file_path = '../bridge.log'; dev_venator = $false }
    default_settings = $null
}
$bridgeConfig | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $bridgeConfigFile -Encoding UTF8

# The stream page loads JellyEmu's stream layer (controls, menu, start screen) when it's embedded
# in Jellyfin; it does nothing when the page is opened on its own.
$streamPage = Join-Path $bridgeDir 'static\stream.html'
$html = [IO.File]::ReadAllText($streamPage)
if ($html -notmatch 'jellyemu/assets/streamlayer\.js') {
    $tag = "    <script src=`"$Server/jellyemu/assets/streamlayer.js`"></script>`r`n"
    $at = $html.IndexOf('<link rel="stylesheet"')
    if ($at -lt 0) { throw 'The bridge''s stream page has changed; the JellyEmu stream layer can''t be added' }
    [IO.File]::WriteAllText($streamPage, $html.Insert($at, $tag.TrimStart()), (New-Object Text.UTF8Encoding $false))
}

# Start-up task: at every sign-in, tell JellyEmu this PC's current address and start the bridge.
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$launcher`" -Start" -WorkingDirectory $launcherDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $me
$principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Save-Config $config
Start-ScheduledTask -TaskName $taskName   # starts the bridge as you (not as administrator)
$bridge = "http://127.0.0.1:$($config.bridgePort)"
for ($i = 0; $i -lt 30 -and (Test-PortFree $config.bridgePort); $i++) { Start-Sleep -Seconds 1 }
if (Test-PortFree $config.bridgePort) { throw "The bridge didn't start (see $bridgeDir\bridge.log)" }

$cookies = [JeHttp]::NewCookies()
function Bridge([string]$method, [string]$path, $body = $null) {
    $json = if ($body -ne $null) { $body | ConvertTo-Json -Depth 6 -Compress } else { $null }
    return [JeHttp]::Request($method, "$bridge$path", $json, $cookies, $null, $null)
}

# The account the proxy signs in as: the bridge makes the first account to sign in its admin.
# (Its password is never needed again: from then on requests sign in through the forwarded header,
# which only this PC and Jellyfin's stream proxy can reach.)
[JeHttp]::BridgeUser = $null
$bridgeData = Join-Path $bridgeDir 'server\data.json'
if (-not (Test-Path -LiteralPath $bridgeData) -or -not ((Get-Content -LiteralPath $bridgeData -Raw) -match [regex]::Escape("`"$bridgeUser`""))) {
    $password = [Convert]::ToBase64String((1..24 | ForEach-Object { [byte](Get-Random -Maximum 256) }))
    Bridge 'POST' '/api/login' @{ name = $bridgeUser; password = $password } | Out-Null
    Say "Created the bridge's $bridgeUser account"
}
[JeHttp]::BridgeUser = $bridgeUser
# The bridge's standard controller mapping swaps A/B and X/Y compared to Sunshine's; undo that.
$roles = (Bridge 'GET' '/api/roles' | ConvertFrom-Json).roles
$admin = $roles | Where-Object { $_.name -eq 'Admin' } | Select-Object -First 1
if ($admin) {
    Bridge 'PATCH' '/api/role' @{ id = $admin.id; name = $null; ty = 'Admin'; permissions = $null
                                  default_settings = @{ controllerConfig = @{ invertAB = $true; invertXY = $true } } } | Out-Null
}

# Add this PC's games Sunshine to the bridge and pair them.
$hostId = [long]$config.hostId
$paired = $false
if ($hostId) {
    try { $paired = ((Bridge 'GET' "/api/host?host_id=$hostId" | ConvertFrom-Json).host.paired -eq 'Paired') } catch { $hostId = 0 }
}
if (-not $hostId) {
    $hostId = [long]((Bridge 'POST' '/api/host' @{ address = 'localhost'; http_port = [int]$config.sunshinePort } | ConvertFrom-Json).host.host_id)
}
if (-not $paired) {
    Say 'Pairing the bridge with Sunshine'
    $result = [JeHttp]::Pair($bridge, $cookies, $hostId, "https://localhost:$($config.sunshinePort + 1)", $sunshineUser, $sunshinePassword, 'JellyEmu bridge')
    if ($result -notmatch 'Paired') { throw "Pairing failed: $result" }
}
$appId = ((Bridge 'GET' "/api/apps?host_id=$hostId" | ConvertFrom-Json).apps | Where-Object { $_.title -eq 'JellyEmu' } | Select-Object -First 1).app_id
if (-not $appId) { throw 'The games Sunshine has no JellyEmu app' }
$config.hostId = $hostId
$config.appId = [long]$appId
Say 'Paired'

# ---- Firewall ---------------------------------------------------------------------------------
Step 'Firewall'
$checkIn = Invoke-RestMethod -Uri "$Server/jellyemu/stream/pc/checkin" -Method Post -Headers $pcHeaders -ContentType 'application/json' `
    -Body (@{ version = $SetupVersion } | ConvertTo-Json) -TimeoutSec 30
$config.proxyAddresses = @($checkIn.proxyAddresses)
Get-NetFirewallRule -Group $fwGroup -ErrorAction SilentlyContinue | Remove-NetFirewallRule
# The bridge: only Jellyfin's stream proxy may connect (it checks everyone's Jellyfin login first).
New-NetFirewallRule -Group $fwGroup -DisplayName 'JellyEmu bridge (from the Jellyfin server only)' -Direction Inbound -Action Allow `
    -Protocol TCP -LocalPort $config.bridgePort -RemoteAddress $config.proxyAddresses -Program (Join-Path $bridgeDir 'web-server.exe') | Out-Null
# The stream itself (WebRTC) goes straight to the device playing, on the home network or Tailscale.
New-NetFirewallRule -Group $fwGroup -DisplayName 'JellyEmu stream (WebRTC)' -Direction Inbound -Action Allow `
    -Protocol UDP -Program (Join-Path $bridgeDir 'streamer.exe') -Profile Private, Domain | Out-Null
Say "The bridge accepts connections from $($config.proxyAddresses -join ', ') only"
$publicNets = @(Get-NetConnectionProfile | Where-Object { $_.NetworkCategory -eq 'Public' -and $_.IPv4Connectivity -ne 'Disconnected' })
if ($publicNets) { Warn "Your network '$($publicNets[0].Name)' is set to Public, so Windows blocks the stream on it. Set it to Private in Settings > Network & internet." }

# ---- Check in ---------------------------------------------------------------------------------
Step 'Telling JellyEmu about this PC'
$config.version = $SetupVersion
Save-Config $config
Start-ScheduledTask -TaskName $taskName   # sends this PC's bridge address
Start-Sleep -Seconds 3
$checkIn = Invoke-RestMethod -Uri "$Server/jellyemu/stream/pc/checkin" -Method Post -Headers $pcHeaders -ContentType 'application/json' `
    -Body (@{ hostId = $config.hostId; appId = $config.appId; platforms = $platforms; version = $SetupVersion } | ConvertTo-Json) -TimeoutSec 30

# The game library as this PC sees it (library paths from the Gaming PCs settings).
if (-not @($checkIn.libraryPaths).Count) {
    Warn 'No library paths are set in JellyEmu''s Gaming PCs settings yet, so this PC can''t find the games. Add them there (no need to run this again).'
}
foreach ($lp in @($checkIn.libraryPaths)) {
    if (Test-Path -LiteralPath $lp.pc) { Say "Game library reachable: $($lp.pc)"; continue }
    if ($lp.pc.StartsWith('\\')) {
        $shareHost = $lp.pc.TrimStart('\').Split('\')[0]
        Write-Host "   $($lp.pc) needs a sign-in. Enter an account on $shareHost that can read the games:"
        $cred = Get-Credential -Message "Account for $shareHost (to read the game library)"
        if ($cred) {
            cmdkey /add:$shareHost /user:$($cred.UserName) /pass:$($cred.GetNetworkCredential().Password) | Out-Null
            if (Test-Path -LiteralPath $lp.pc) { Say "Game library reachable: $($lp.pc)"; continue }
        }
    }
    Warn "This PC can't open $($lp.pc). Check the path in the Gaming PCs settings, or that this PC can reach it."
}

Step 'Done'
Say "'$($config.name)' can now stream: $($platforms -join ', ')"
Say 'It shows up in "Play on" in Jellyfin. The bridge starts by itself whenever you sign in to this PC.'
if ($warnings.Count) { Write-Host ''; Write-Host 'Things to sort out:' -ForegroundColor Yellow; $warnings | ForEach-Object { Write-Host " - $_" -ForegroundColor Yellow } }
