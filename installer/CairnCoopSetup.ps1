#Requires -Version 5.1
<#
.SYNOPSIS
    CairnCoop Multiplayer Mod Installer
.DESCRIPTION
    Installiert BepInEx 6 (IL2CPP) und den CairnCoop Multiplayer Mod vollautomatisch.
    Benoetigt Steam und Cairn (Windows 10/11). Keine Admin-Rechte noetig.
#>

[CmdletBinding()]
param(
    [switch]$Uninstall,
    [switch]$Silent,
    [string]$CairnPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ============================================================
#  KONSTANTEN
# ============================================================
$MOD_VERSION    = "0.1.0"
$MOD_NAME       = "CairnCoop"
$PLUGIN_FOLDER  = "BepInEx\plugins\CairnCoop"
$GITHUB_RELEASE = "https://github.com/BlackTrophy/CairnCoop/releases/latest/download/CairnCoop.zip"

# BepInEx 6 IL2CPP -- Bleeding Edge builds are on builds.bepinex.dev, NOT on GitHub Releases.
# We resolve the latest build dynamically and fall back to a known-good URL.
# BepInEx Bleeding Edge build server -- supports IL2CPP metadata v23-106 (Unity 6+)
# GitHub releases only have pre.2 (be.697) which does NOT support Unity 6 / metadata v31.
# We must use the build server for builds >= be.753.
$BEPINEX_BUILDSERVER = "https://builds.bepinex.dev/projects/bepinex_be"
$BEPINEX_FALLBACK    = "https://builds.bepinex.dev/projects/bepinex_be/755/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755%2B3fab71a.zip"
$BEPINEX_FALLBACK_VER = "6.0.0-be.755"

function Get-BepInExUrl {
    param([string]$UserAgent)

    # Strategy: scrape build server index for the highest build number,
    # then construct the download URL.
    try {
        $headers  = @{ 'User-Agent' = $UserAgent }
        $html     = (New-Object System.Net.WebClient).DownloadString($BEPINEX_BUILDSERVER)

        # Find all build numbers in href="/projects/bepinex_be/NNN/"
        $buildNums = [regex]::Matches($html, '/projects/bepinex_be/(\d+)/') |
                     ForEach-Object { [int]$_.Groups[1].Value } |
                     Sort-Object -Descending

        foreach ($num in $buildNums | Select-Object -First 5) {
            try {
                $indexUrl  = "$BEPINEX_BUILDSERVER/$num/"
                $indexHtml = (New-Object System.Net.WebClient).DownloadString($indexUrl)
                # Match asset: BepInEx-Unity.IL2CPP-win-x64-*.zip
                $m = [regex]::Match($indexHtml, 'BepInEx-Unity\.IL2CPP-win-x64-[^"]+\.zip')
                if ($m.Success) {
                    $fileName = $m.Value
                    $ver      = [regex]::Match($fileName, '6\.0\.0-be\.\d+').Value
                    return @{
                        Url = "$BEPINEX_BUILDSERVER/$num/$fileName"
                        Ver = $ver
                    }
                }
            } catch {}
        }
    } catch {}

    # Fall back to known-good build
    return @{ Url = $BEPINEX_FALLBACK; Ver = "$BEPINEX_FALLBACK_VER (fallback)" }
}

# ============================================================
#  FARB-HELPERS
# ============================================================
function Write-Header {
    param([string]$Text)
    Write-Host ""
    Write-Host ("=" * 60) -ForegroundColor Cyan
    Write-Host "  $Text" -ForegroundColor Cyan
    Write-Host ("=" * 60) -ForegroundColor Cyan
    Write-Host ""
}
function Write-Step { param([string]$Text) Write-Host "  >> $Text" -ForegroundColor Yellow }
function Write-OK   { param([string]$Text) Write-Host "  [OK] $Text" -ForegroundColor Green }
function Write-Warn { param([string]$Text) Write-Host "  [!!] $Text" -ForegroundColor Magenta }
function Write-Fail { param([string]$Text) Write-Host "  [ERR] $Text" -ForegroundColor Red }
function Write-Info { param([string]$Text) Write-Host "       $Text" -ForegroundColor Gray }

function Pause-ForUser {
    param([string]$Msg = "Weiter mit einer beliebigen Taste...")
    if (-not $Silent) {
        Write-Host ""
        Write-Host "  $Msg" -ForegroundColor White
        $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    }
}

# ============================================================
#  BANNER
# ============================================================
Clear-Host
Write-Host @"

   ██████╗ █████╗ ██╗██████╗ ███╗   ██╗ ██████╗ ██████╗  ██████╗ ██████╗
  ██╔════╝██╔══██╗██║██╔══██╗████╗  ██║██╔════╝██╔═══██╗██╔═══██╗██╔══██╗
  ██║     ███████║██║██████╔╝██╔██╗ ██║██║     ██║   ██║██║   ██║██████╔╝
  ██║     ██╔══██║██║██╔══██╗██║╚██╗██║██║     ██║   ██║██║   ██║██╔═══╝
  ╚██████╗██║  ██║██║██║  ██║██║ ╚████║╚██████╗╚██████╔╝╚██████╔╝██║
   ╚═════╝╚═╝  ╚═╝╚═╝╚═╝  ╚═╝╚═╝  ╚═══╝ ╚═════╝ ╚═════╝  ╚═════╝ ╚═╝

"@ -ForegroundColor Cyan

Write-Host "  Multiplayer Co-op Mod v$MOD_VERSION -- Installer" -ForegroundColor White
Write-Host "  Bis zu 8 Spieler | Steam P2P | Kein Dedicated Server" -ForegroundColor Gray
Write-Host ""

# ============================================================
#  SCHRITT 0: CAIRN PFAD FINDEN
# ============================================================
Write-Header "Cairn Installation erkennen"

function Find-CairnPath {
    # 1. Registry
    $regPaths = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 1347330",
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 1347330"
    )
    foreach ($reg in $regPaths) {
        try {
            $val = (Get-ItemProperty $reg -ErrorAction Stop).InstallLocation
            if ($val -and (Test-Path "$val\Cairn.exe")) { return $val }
        } catch {}
    }

    # 2. Steam libraryfolders.vdf parsen
    $steamReg = @(
        "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam",
        "HKCU:\SOFTWARE\Valve\Steam"
    )
    foreach ($reg in $steamReg) {
        try {
            $steamPath = (Get-ItemProperty $reg -ErrorAction Stop).InstallPath
            $vdf = "$steamPath\steamapps\libraryfolders.vdf"
            if (Test-Path $vdf) {
                $content = Get-Content $vdf -Raw
                # Regex: "path"  "C:\\some\\path"
                $matches = [regex]::Matches($content, '"path"\s+"([^"]+)"')
                foreach ($m in $matches) {
                    $p = $m.Groups[1].Value -replace '\\\\', '\'
                    $candidate = "$p\steamapps\common\Cairn"
                    if (Test-Path "$candidate\Cairn.exe") { return $candidate }
                }
            }
        } catch {}
    }

    # 3. Haeufige Pfade
    $common = @(
        "C:\Program Files (x86)\Steam\steamapps\common\Cairn",
        "C:\Program Files\Steam\steamapps\common\Cairn",
        "D:\Steam\steamapps\common\Cairn",
        "E:\Steam\steamapps\common\Cairn",
        "I:\SteamLibrary\steamapps\common\Cairn"
    )
    foreach ($p in $common) {
        if (Test-Path "$p\Cairn.exe") { return $p }
    }
    return $null
}

if ($CairnPath -and (Test-Path "$CairnPath\Cairn.exe")) {
    Write-OK "Cairn-Pfad manuell angegeben: $CairnPath"
} else {
    Write-Step "Suche Cairn via Steam Registry..."
    $CairnPath = Find-CairnPath
    if ($CairnPath) {
        Write-OK "Gefunden: $CairnPath"
    } else {
        Write-Fail "Cairn nicht automatisch gefunden!"
        Write-Host ""
        $CairnPath = Read-Host "  Bitte Cairn-Installationspfad eingeben (Ordner mit Cairn.exe)"
        if (-not (Test-Path "$CairnPath\Cairn.exe")) {
            Write-Fail "Cairn.exe nicht gefunden unter: $CairnPath"
            Write-Host "  Bitte Cairn ueber Steam installieren und erneut versuchen." -ForegroundColor Red
            Pause-ForUser "Druecke eine Taste zum Beenden..."
            exit 1
        }
    }
}

$BepInExPath = "$CairnPath\BepInEx"
$PluginPath  = "$CairnPath\$PLUGIN_FOLDER"

# ============================================================
#  UNINSTALL
# ============================================================
if ($Uninstall) {
    Write-Header "CairnCoop deinstallieren"
    Write-Step "Entferne Mod-Dateien..."
    if (Test-Path $PluginPath) {
        Remove-Item $PluginPath -Recurse -Force
        Write-OK "Plugin-Ordner entfernt: $PluginPath"
    } else {
        Write-Warn "Plugin-Ordner nicht gefunden (bereits deinstalliert?)"
    }
    Write-OK "CairnCoop deinstalliert. BepInEx wurde nicht entfernt."
    Pause-ForUser
    exit 0
}

# ============================================================
#  SCHRITT 1: BEPINEX PRUEFEN / INSTALLIEREN
# ============================================================
Write-Header "BepInEx 6 (IL2CPP) installieren"

$bepinexInstalled = (Test-Path "$CairnPath\winhttp.dll") -and
                    (Test-Path "$BepInExPath\core\BepInEx.Unity.IL2CPP.dll")

if ($bepinexInstalled) {
    Write-OK "BepInEx ist bereits installiert."
} else {
    Write-Step "Suche neueste BepInEx 6 IL2CPP Version..."
    $bepInfo = Get-BepInExUrl -UserAgent "CairnCoop-Installer/$MOD_VERSION"
    $BEPINEX_URL = $bepInfo.Url
    $BEPINEX_VER = $bepInfo.Ver
    Write-OK "BepInEx $BEPINEX_VER"
    Write-Info "Von: $BEPINEX_URL"

    $tmpZip = [System.IO.Path]::Combine($env:TEMP, "BepInEx_CairnCoop.zip")
    $tmpDir = [System.IO.Path]::Combine($env:TEMP, "BepInEx_CairnCoop_Extract")

    try {
        $wc = New-Object System.Net.WebClient
        $wc.Headers.Add("User-Agent", "CairnCoop-Installer/$MOD_VERSION")
        $wc.DownloadFile($BEPINEX_URL, $tmpZip)
        Write-OK "Download abgeschlossen ($([math]::Round((Get-Item $tmpZip).Length / 1MB, 1)) MB)"
    } catch {
        Write-Fail "Download fehlgeschlagen: $_"
        Write-Warn "Bitte manuell herunterladen:"
        Write-Info $BEPINEX_URL
        Write-Info "Inhalt in '$CairnPath' entpacken, dann Installer erneut starten."
        Pause-ForUser
        exit 1
    }

    Write-Step "Entpacke BepInEx..."
    if (Test-Path $tmpDir) { Remove-Item $tmpDir -Recurse -Force }
    Expand-Archive -Path $tmpZip -DestinationPath $tmpDir -Force

    Copy-Item "$tmpDir\*" -Destination $CairnPath -Recurse -Force
    Remove-Item $tmpZip -Force
    Remove-Item $tmpDir -Recurse -Force

    Write-OK "BepInEx installiert."
}

# ============================================================
#  SCHRITT 1b: DOORSTOP CONFIG PRUEFEN / REPARIEREN
# ============================================================
# BepInEx 6 IL2CPP braucht eine korrekte doorstop_config.ini.
# Falls vorher BepInEx 5 installiert war, zeigt sie auf die falsche DLL.
$doorstopCfg = "$CairnPath\doorstop_config.ini"
$doorstopOk  = $false
if (Test-Path $doorstopCfg) {
    $cfgRaw = Get-Content $doorstopCfg -Raw -ErrorAction SilentlyContinue
    if ($cfgRaw -match 'BepInEx\.Unity\.IL2CPP\.dll') {
        $doorstopOk = $true
    }
}
if (-not $doorstopOk) {
    Write-Warn "doorstop_config.ini fehlt oder zeigt auf falsche DLL -- wird repariert..."
    $doorstopContent = @"
[UnityDoorstop]
enabled = true
targetAssembly = BepInEx\core\BepInEx.Unity.IL2CPP.dll
"@
    Set-Content $doorstopCfg $doorstopContent -Encoding UTF8
    Write-OK "doorstop_config.ini -> BepInEx\core\BepInEx.Unity.IL2CPP.dll"
} else {
    Write-OK "doorstop_config.ini korrekt."
}

# ============================================================
#  SCHRITT 2: INTEROP-DLLS PRUEFEN
# ============================================================
Write-Header "IL2CPP Interop-Assemblies pruefen"

# Helper: count .dll files safely -- avoids .Count on $null in strict mode
function Count-Dlls {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return 0 }
    return (Get-ChildItem $Path -Filter "*.dll" -ErrorAction SilentlyContinue | Measure-Object).Count
}

$interopPath  = "$BepInExPath\interop"
$dllCount     = Count-Dlls $interopPath
$interopReady = ($dllCount -gt 50)

if ($interopReady) {
    Write-OK "Interop-DLLs vorhanden ($dllCount DLLs)."
} else {
    Write-Warn "Interop-DLLs noch nicht generiert."
    Write-Host ""
    Write-Host "  BepInEx generiert diese DLLs beim ERSTEN Spielstart." -ForegroundColor White
    Write-Host "  Wichtig: Cairn MUSS ueber Steam gestartet werden (nicht direkt Cairn.exe)." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  Bitte jetzt:" -ForegroundColor White
    Write-Host ""
    Write-Host "    1. Cairn ueber Steam starten" -ForegroundColor Cyan
    Write-Host "    2. Wenn der Splash-Screen erscheint, ca. 30 Sekunden warten" -ForegroundColor Cyan
    Write-Host "    3. Cairn wieder schliessen" -ForegroundColor Cyan
    Write-Host "    4. Diesen Installer erneut starten" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  Falls Cairn crasht:" -ForegroundColor Yellow
    Write-Host "    BepInEx\LogOutput.log pruefen -- zeigt den genauen Fehler" -ForegroundColor Gray
    Write-Host "    Pfad: $CairnPath\BepInEx\LogOutput.log" -ForegroundColor Gray
    Write-Host ""

    $launchNow = "N"
    if (-not $Silent) {
        $launchNow = Read-Host "  Cairn jetzt automatisch starten? (J/N)"
    }

    if ($launchNow -match "^[Jj]") {
        Write-Step "Starte Cairn ueber Steam..."
        Start-Process "steam://rungameid/1347330"
        Write-Host ""
        Write-Host "  Warte auf Interop-Generierung (max. 120 s)..." -ForegroundColor Yellow

        $waited = 0
        while (-not $interopReady -and $waited -lt 120) {
            Start-Sleep -Seconds 5
            $waited += 5
            $dllCount     = Count-Dlls $interopPath
            $interopReady = ($dllCount -gt 50)
            Write-Host "       Warte... ($waited s, $dllCount DLLs)" -ForegroundColor Gray
        }

        if ($interopReady) {
            Write-OK "Interop-DLLs generiert ($dllCount DLLs)!"
            Write-Step "Bitte Cairn jetzt schliessen und Installer neu starten."
            Pause-ForUser
            exit 0
        } else {
            Write-Fail "Timeout nach $waited s ($dllCount DLLs)."
            Write-Host ""
            Write-Host "  Moegliche Ursachen:" -ForegroundColor Yellow
            Write-Host "  - Cairn crasht beim Start -> BepInEx\LogOutput.log pruefen" -ForegroundColor Gray
            Write-Host "  - Antivirus blockiert winhttp.dll -> Ausnahme hinzufuegen" -ForegroundColor Gray
            Write-Host "  - Cairn nicht ueber Steam gestartet -> Steam verwenden" -ForegroundColor Gray
            Write-Host ""
            Write-Host "  Log: $CairnPath\BepInEx\LogOutput.log" -ForegroundColor Cyan
            Pause-ForUser
            exit 1
        }
    } else {
        Write-Host ""
        Write-Host "  Nach dem naechsten Spielstart Installer bitte erneut ausfuehren." -ForegroundColor Cyan
        Pause-ForUser
        exit 0
    }
}

# ============================================================
#  SCHRITT 3: MOD INSTALLIEREN
# ============================================================
Write-Header "CairnCoop Mod installieren"

New-Item -ItemType Directory -Force -Path $PluginPath | Out-Null

# Lokale Build-Artefakte pruefen (Entwickler-Modus)
$localDll = Join-Path $PSScriptRoot "..\bin\Release\CairnCoop.dll"
$altDll   = Join-Path $PSScriptRoot "..\CairnCoop.dll"

if (Test-Path $localDll) {
    Write-Step "Lokale Build-Version gefunden -- verwende Build-Artefakte..."
    Copy-Item $localDll $PluginPath -Force
    Write-OK "CairnCoop.dll installiert (lokaler Build)."
} elseif (Test-Path $altDll) {
    Write-Step "CairnCoop.dll neben Installer gefunden..."
    Copy-Item $altDll $PluginPath -Force
    Write-OK "CairnCoop.dll installiert."
} else {
    # Kein lokaler Build -- versuche aus Source zu bauen

    # ------------------------------------------------------------------
    # Dotnet-SDK suchen: Get-Command dotnet liefert auf manchen Systemen
    # nur das 32-bit Runtime ohne SDK (C:\Program Files (x86)\dotnet).
    # Wir pruefen daher 64-bit zuerst und verifizieren per --list-sdks.
    # ------------------------------------------------------------------
    function Find-DotnetSdk {
        $candidates = @(
            "$env:ProgramW6432\dotnet\dotnet.exe",                   # explizit 64-bit
            "$env:ProgramFiles\dotnet\dotnet.exe",                   # 64-bit
            (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
            "${env:ProgramFiles(x86)}\dotnet\dotnet.exe"             # 32-bit letzter Versuch
        ) | Where-Object { $_ } | Select-Object -Unique

        foreach ($exe in $candidates) {
            if (-not (Test-Path $exe -ErrorAction SilentlyContinue)) { continue }
            try {
                $sdks = & $exe --list-sdks 2>&1
                if ($LASTEXITCODE -eq 0 -and ($sdks | Where-Object { $_ -match '^\d' })) {
                    return $exe
                }
            } catch {}
        }
        return $null
    }

    $dotnet = Find-DotnetSdk

    if ($null -ne $dotnet) {
        Write-OK ".NET SDK gefunden: $dotnet"

        # ------------------------------------------------------------------
        # Neueste Source holen: git clone von main ist bevorzugt, weil die
        # gebundelte Source evtl. veraltet ist. Fallback: gebundelte Source.
        # ------------------------------------------------------------------
        $gitExe    = Get-Command git -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue
        $srcDir    = Join-Path $env:TEMP "CairnCoop_src_build"
        $usedClone = $false

        if ($gitExe) {
            Write-Step "Lade neueste Source von GitHub (main)..."
            if (Test-Path $srcDir) { Remove-Item $srcDir -Recurse -Force }
            # KEIN 2>&1: git schreibt Fortschritt auf stderr; mit 2>&1 und
            # $ErrorActionPreference=Stop wird stderr als Fehler behandelt.
            & $gitExe clone --depth 1 "https://github.com/BlackTrophy/CairnCoop.git" $srcDir
            if ($LASTEXITCODE -eq 0 -and (Test-Path "$srcDir\CairnCoop.csproj")) {
                $usedClone = $true
                Write-OK "Repository geklont -- neueste Version."
            } else {
                Write-Warn "git clone fehlgeschlagen -- verwende gebundelte Source."
            }
        } else {
            Write-Warn "git nicht gefunden -- verwende gebundelte Source."
        }

        $csproj = if ($usedClone) { "$srcDir\CairnCoop.csproj" }
                  else             { Join-Path $PSScriptRoot "..\CairnCoop.csproj" }

        if (-not (Test-Path $csproj)) {
            Write-Fail "CairnCoop.csproj nicht gefunden: $csproj"
            Pause-ForUser; exit 1
        }

        Write-Step "Baue Mod aus Source..."
        $buildArgs = @(
            "build", $csproj,
            "-c", "Release",
            "-p:CairnPath=$CairnPath",
            "--nologo",
            "-v", "minimal"
        )
        Write-Info "$dotnet $($buildArgs -join ' ')"
        & $dotnet @buildArgs

        if ($usedClone) { Remove-Item $srcDir -Recurse -Force -ErrorAction SilentlyContinue }

        if ($LASTEXITCODE -eq 0) {
            Write-OK "Build erfolgreich!"
        } else {
            Write-Fail "Build fehlgeschlagen. Pruefen Sie die Ausgabe oben."
            Pause-ForUser
            exit 1
        }
    } else {
        # Kein .NET SDK -- Download von GitHub Releases
        Write-Step "Kein .NET SDK -- lade vorkompilierte Version von GitHub..."

        try {
            $modZip = [System.IO.Path]::Combine($env:TEMP, "CairnCoop_mod.zip")
            $wc2    = New-Object System.Net.WebClient
            $wc2.Headers.Add("User-Agent", "CairnCoop-Installer/$MOD_VERSION")
            $wc2.DownloadFile($GITHUB_RELEASE, $modZip)

            $modTmp = [System.IO.Path]::Combine($env:TEMP, "CairnCoop_mod")
            if (Test-Path $modTmp) { Remove-Item $modTmp -Recurse -Force }
            Expand-Archive $modZip $modTmp -Force
            Copy-Item "$modTmp\*" $PluginPath -Recurse -Force
            Remove-Item $modZip -Force
            Remove-Item $modTmp -Recurse -Force
            Write-OK "Vorkompilierter Mod installiert."
        } catch {
            Write-Fail "Download fehlgeschlagen: $_"
            Write-Host ""
            Write-Host "  Optionen:" -ForegroundColor White
            Write-Host "  A) .NET 6 SDK installieren: https://dotnet.microsoft.com/download" -ForegroundColor Cyan
            Write-Host "  B) CairnCoop.dll manuell herunterladen:" -ForegroundColor Cyan
            Write-Host "     https://github.com/BlackTrophy/CairnCoop/releases/latest" -ForegroundColor Cyan
            Write-Host "     -> in '$PluginPath' entpacken" -ForegroundColor Cyan
            Pause-ForUser
            exit 1
        }
    }
}

# ============================================================
#  SCHRITT 4: KONFIGURATION
# ============================================================
Write-Header "Konfiguration"

# join_code.txt erstellen (leer -- Nutzer traegt SteamID64 des Hosts ein)
$joinCodePath = "$PluginPath\join_code.txt"
if (-not (Test-Path $joinCodePath)) {
    Set-Content $joinCodePath -Value "" -Encoding UTF8
    Write-OK "join_code.txt erstellt"
}

# BepInEx.cfg -- Konsole aktivieren fuer Debugging
$bepCfgDir  = "$BepInExPath\config"
$bepCfgPath = "$bepCfgDir\BepInEx.cfg"
New-Item -ItemType Directory -Force -Path $bepCfgDir | Out-Null

if (Test-Path $bepCfgPath) {
    $cfg = Get-Content $bepCfgPath -Raw
    if ($cfg -match "Enabled = false") {
        $cfg = $cfg -replace "Enabled = false", "Enabled = true"
        Set-Content $bepCfgPath $cfg -Encoding UTF8
        Write-OK "BepInEx Konsole aktiviert (Debugging)."
    } else {
        Write-OK "BepInEx.cfg bereits konfiguriert."
    }
} else {
    $cfgContent = @"
[Logging.Console]
Enabled = true
LogLevels = Fatal, Error, Warning, Message, Info
"@
    Set-Content $bepCfgPath $cfgContent -Encoding UTF8
    Write-OK "BepInEx.cfg erstellt."
}

# ============================================================
#  FERTIG
# ============================================================
Write-Header "Installation abgeschlossen!"

Write-Host ""
Write-Host "  CairnCoop $MOD_VERSION wurde erfolgreich installiert!" -ForegroundColor Green
Write-Host ""
Write-Host ("=" * 60) -ForegroundColor DarkCyan
Write-Host "  WIE MAN SPIELT" -ForegroundColor White
Write-Host ("=" * 60) -ForegroundColor DarkCyan
Write-Host ""
Write-Host "  SESSION STARTEN (HOST):" -ForegroundColor Yellow
Write-Host "  1. Cairn ueber Steam starten" -ForegroundColor Gray
Write-Host "  2. Im Spiel  F5  druecken" -ForegroundColor Cyan
Write-Host "  3. Deine SteamID64 erscheint im BepInEx-Log" -ForegroundColor Gray
Write-Host "  4. SteamID64 an Mitspieler weitergeben" -ForegroundColor Gray
Write-Host ""
Write-Host "  BEITRETEN (CLIENT):" -ForegroundColor Yellow
Write-Host "  1. SteamID64 des Hosts in diese Datei eintragen:" -ForegroundColor Gray
Write-Host "     $joinCodePath" -ForegroundColor Cyan
Write-Host "  2. Cairn starten" -ForegroundColor Gray
Write-Host "  3. Im Spiel  F6  druecken" -ForegroundColor Cyan
Write-Host ""
Write-Host "  WEITERE TASTENKUERZEL:" -ForegroundColor Yellow
Write-Host "  F7 = Spectator-Modus (nach dem Tod)" -ForegroundColor Gray
Write-Host ""
Write-Host ("=" * 60) -ForegroundColor DarkCyan
Write-Host "  SUPPORT & SOURCECODE" -ForegroundColor White
Write-Host ("=" * 60) -ForegroundColor DarkCyan
Write-Host ""
Write-Host "  GitHub: https://github.com/BlackTrophy/CairnCoop" -ForegroundColor Cyan
Write-Host "  Issues: https://github.com/BlackTrophy/CairnCoop/issues" -ForegroundColor Cyan
Write-Host ""

# Installation verifizieren
$installedDll = "$PluginPath\CairnCoop.dll"
if (Test-Path $installedDll) {
    $size = [math]::Round((Get-Item $installedDll).Length / 1KB, 0)
    Write-OK "Verifiziert: CairnCoop.dll ($size KB) in $PluginPath"
} else {
    Write-Warn "CairnCoop.dll nicht gefunden -- Installation moeglicherweise unvollstaendig."
}

Write-Host ""
Pause-ForUser "Druecke eine Taste um den Installer zu schliessen..."
