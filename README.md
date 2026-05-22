# CairnCoop 🧗

**Inoffizieller Multiplayer Co-op Mod für Cairn**

[![Build](https://github.com/BlackTrophy/CairnCoop/actions/workflows/build.yml/badge.svg)](https://github.com/BlackTrophy/CairnCoop/actions)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Version](https://img.shields.io/badge/Version-0.1.0--alpha-orange)](CHANGELOG.md)

> **Bis zu 8 Spieler klettern dieselbe Route in Echtzeit.**  
> Keine Dedicated Server. Kein Account. Nur Steam — und Freunde.

---

## ✨ Features

| Feature | Status |
|---------|--------|
| Bis zu 8 Spieler | ✅ |
| Steam P2P (kein Server) | ✅ |
| Sichtbare Mitspieler | ✅ |
| Vollständige Kletteranimationen | ✅ |
| IK-basierte Hand-/Fußplatzierung | ✅ |
| Seil-System (Rope Sync) | ✅ |
| Anker (Anchors) | ✅ |
| Stamina-Sync | ✅ |
| Checkpoints | ✅ |
| Respawn | ✅ |
| Spectator-Modus | ✅ |
| Late Join (mid-session beitreten) | ✅ |
| Reconnect nach Disconnect | ✅ |

---

## 🚀 Installation (1 Klick)

### Windows (empfohlen)

1. **[Neueste Version herunterladen](https://github.com/BlackTrophy/CairnCoop/releases/latest)**
2. ZIP entpacken
3. `Setup.bat` doppelklicken
4. Fertig ✅

Der Installer:
- Erkennt Cairn automatisch über die Steam Registry
- Installiert BepInEx 6 (IL2CPP) automatisch
- Führt durch die Ersteinrichtung
- Keine Admin-Rechte nötig

---

## 🎮 Spielen

### Session starten (Host)

1. Cairn über Steam starten
2. Im Spiel **F5** drücken
3. Deine **SteamID64** erscheint im BepInEx-Konsolenfenster
4. Diese Zahl an deine Mitspieler schicken

### Beitreten (Client)

1. Diese Datei öffnen: `BepInEx/plugins/CairnCoop/join_code.txt`
2. SteamID64 des Hosts eintragen und speichern
3. Cairn starten → **F6** drücken

### Weitere Tasten

| Taste | Funktion |
|-------|---------|
| F5 | Session hosten |
| F6 | Session beitreten (mit join_code.txt) |
| F7 | Spectator-Modus (nach dem Tod) |

---

## 🔧 Technischer Überblick

| Komponente | Technologie |
|-----------|------------|
| Engine | Unity IL2CPP |
| Mod-Framework | BepInEx 6 (IL2CPP) |
| Transport | Steamworks.NET ISteamNetworking P2P |
| Character IK | RootMotion Final IK |
| Rope Physics | Obi Rope (lokal) + Catenary (remote) |
| Netzwerk-Protokoll | Eigenes Binary-Protokoll, 7 Kanäle, 20/30/10 Hz |
| Spieler-Sync | Snapshot-Interpolation, 100ms Buffer |
| Prediction | Client-side Prediction + Server Rollback |

---

## 🛡️ Transparenz & Sicherheit

Dieses Projekt ist **vollständig Open Source** (MIT-Lizenz).

- ✅ Kein Telemetrie, kein Tracking
- ✅ Keine Serverinfrastruktur (reines Steam P2P)
- ✅ Kein Zugriff auf persönliche Daten außer SteamID (für P2P-Verbindung)
- ✅ Host-seitige Validierung (Anti-Cheat für Coop)
- ✅ Modifiziert keine Spieldateien — nur BepInEx-Plugin

**Was der Mod tut:**
- Liest Spieler-Position/Stamina/IK-Targets aus Spiel-Memory
- Sendet diese Daten über Steam P2P direkt an Mitspieler (verschlüsselt von Steam)
- Spawnt "Ghost"-Repräsentationen der Mitspieler im lokalen Spiel

**Was der Mod NICHT tut:**
- Keine Änderungen an Spiellogik oder Spielstand
- Keine Verbindung zu externen Servern
- Kein Schreiben in Spieldateien

---

## 🤝 Contributing

Feedback, Bug-Reports und Pull Requests sind willkommen!

→ [CONTRIBUTING.md](CONTRIBUTING.md) für Setup-Anleitung

**Bekannte offene Punkte:**
- Harmony-Patch-Methoden-Namen müssen nach Il2CppDumper-Analyse bestätigt werden
- Aava-Prefab-Addressable-Key muss via AssetStudio extrahiert werden
- EOS P2P Transport (Alternative zu Steam) noch nicht implementiert

---

## 📋 Anforderungen

- **Windows 10/11** (64-bit)
- **Steam** (Cairn muss über Steam gestartet werden)
- **Cairn** (Steam App 1347330)
- **Stabile Internetverbindung** (~160 kbps Upload für 8 Spieler)

---

## ⚠️ Haftungsausschluss

Dies ist eine inoffizielle Fanmod. Nicht von The Game Bakers erstellt oder unterstützt.  
Alle Spielinhalte gehören The Game Bakers.  
Verwendung auf eigene Verantwortung.

---

*Sourcecode: [github.com/BlackTrophy/CairnCoop](https://github.com/BlackTrophy/CairnCoop)*
