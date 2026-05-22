# Contributing to CairnCoop

## Voraussetzungen

| Tool | Version | Link |
|------|---------|------|
| .NET SDK | 6.0+ | https://dotnet.microsoft.com/download |
| BepInEx 6 | IL2CPP win-x64 | https://builds.bepinex.dev/projects/bepinex_be |
| Cairn | Steam (aktuelle Version) | Steam App 1347330 |
| Il2CppDumper | Latest | https://github.com/Perfare/Il2CppDumper |

## Entwicklungssetup

```powershell
# 1. Repo klonen
git clone https://github.com/BlackTrophy/CairnCoop.git
cd CairnCoop

# 2. BepInEx installieren und Cairn einmal starten
#    → generiert BepInEx/interop/ DLLs

# 3. Bauen
dotnet build -c Debug -p:CairnPath="I:\SteamLibrary\steamapps\common\Cairn"
```

## Reverse Engineering (notwendig für Patches)

```powershell
# Il2CppDumper ausführen
Il2CppDumper.exe `
    "I:\SteamLibrary\steamapps\common\Cairn\GameAssembly.dll" `
    "I:\SteamLibrary\steamapps\common\Cairn\Cairn_Data\il2cpp_data\Metadata\global-metadata.dat" `
    ".\re_output\"

# dump.cs öffnen und Klassennamen bestätigen:
# - ClimbingV2RAHManager, ClimbingV2HoldsManager
# - ObiSolverManager, PawnManager
# GameBridge.cs und ClimbingPatches.cs entsprechend anpassen
```

## Branch-Strategie

```
main   ← stabiler Release-Branch (nur via PR)
dev    ← aktive Entwicklung
feat/* ← Feature-Branches
fix/*  ← Bugfix-Branches
```

## Pull Requests

1. Aus `dev` branchen: `git checkout -b feat/mein-feature`
2. Commits auf Englisch (Codekommentare Deutsch/Englisch)
3. Vor PR: `dotnet build -p:UseStubs=true` muss erfolgreich sein
4. PR gegen `dev` stellen, nicht `main`

## Wichtige TODO-Stellen

Suche im Code nach `// TODO:` um offene RE-Abhängigkeiten zu finden.
Alle TODOs in `GameBridge.cs` brauchen Il2CppDumper-Bestätigung der exakten
Feldnamen in der IL2CPP-Reflection.
