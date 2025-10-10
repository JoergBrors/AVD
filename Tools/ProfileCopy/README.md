# QGIS Profile Backup & Restore Tool v2.0

Ein .NET Windows Forms Tool zum Sichern und Wiederherstellen von QGIS-Profilen über Netzwerk-Freigaben.

**🎯 Letztes Update:** 10. Oktober 2025  
**📦 Aktuelle Version:** v2.0 mit vollständigem Logging und Szenario-Management

## Features

- **🗂️ Szenario-Management**: Multi-Szenario-Support mit ZIP_POSTFIX-Integration
- **📊 Intelligente Grid-Anzeige**: Szenario-Spalte mit automatischer Filterung
- **💾 Backup erstellen**: ZIP-Archive mit automatischer Szenario-Kennzeichnung
- **🔄 Profile wiederherstellen**: Mit Kompatibilitätsprüfung und Warnungen
- **🔧 Vollständiges Logging**: Thread-safe Logs im Temp-Verzeichnis
- **� Fortschrittsdialog**: Animierte Progress-Anzeige für alle Operationen
- **⚡ Asynchrone Verarbeitung**: Threading für responsive UI auch bei großen Operationen
- **❌ Abbrechen**: Möglichkeit, laufende Operationen zu stoppen
- **⚠️ Prozess-Management**: Optional QGIS-Prozesse vor Backup/Restore beenden
- **🎯 Automatische Aktualisierung**: Grid und Filter reagieren auf Szenario-Wechsel

## Konfiguration

Das Tool verwendet eine `host.local` Datei für lokale Konfiguration, die nicht ins Git eingecheckt wird:

```ini
# QGIS Profile Backup & Restore Tool - Lokale Konfiguration
# Diese Datei wird NICHT ins Git Repository eingecheckt

# Fileserver Konfiguration
DEFAULT_SHARE=\\\\SERVER\\Freigabe\\QGISProfiles

# Szenario-System mit Backup-Prozess-Kontrolle
ACTIVE_SCENARIO=QGIS_Default
APPLICATION_TITLE=QGIS Profile Backup & Restore Tool
PROCESS_KILL_DELAY_MS=2000
SHOW_KILL_WARNING=true

[QGIS_Default]
SOURCE_PATH=%APPDATA%\QGIS\QGIS3\profiles
TARGET_SHARE=\\\\SERVER\\Freigabe\\QGISProfiles
PROCESS_NAMES=qgis-bin,qgis,qgis-ltr-bin,qgis-ltr
SCENARIO_TITLE=QGIS Standard Profile
```

## Verwendung

1. Starten Sie die Anwendung
2. Passen Sie bei Bedarf die Pfade an:
   - **Fileshare (Root)**: Netzwerk-Pfad zur Backup-Freigabe
   - **Lokales QGIS-Profil**: Pfad zum lokalen QGIS-Profilordner
3. Für Backup:
   - Geben Sie eine Version ein
   - Klicken Sie auf "Backup erstellen"
4. Für Restore:
   - Wählen Sie eine Sicherung aus der Liste
   - Klicken Sie auf "Ausgewählte Sicherung wiederherstellen"

## Systemanforderungen

- Windows
- .NET 9.0 oder höher
- QGIS (für Profile-Backup/Restore)
- Zugriff auf Netzwerk-Freigabe

## Entwicklung

Das Projekt basiert auf Windows Forms und nutzt:
- `System.IO.Compression` für ZIP-Operationen
- `System.Diagnostics` für Prozess-Management
- Konfiguration über `HostConfiguration` Singleton

### Build

```bash
dotnet build
```

### Run

```bash
dotnet run
```

### Single Executable erstellen

```bash
dotnet publish QGISProfileTool.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

Die erstellte `QGISProfileTool.exe` (ca. 120 MB) ist eigenständig und benötigt keine .NET Installation.

## Struktur

- `Form1.cs` - Hauptformular mit UI und Geschäftslogik
- `BackupService.cs` - Asynchrone Backup/Restore-Operationen
- `FileLogger.cs` - Thread-safe Logging-System
- `ProgressForm.cs` - Fortschrittsdialog mit Animationen
- `HostConfiguration.cs` - Multi-Szenario-Konfigurationsmanagement
- `host.local` - Lokale Konfigurationsdatei (nicht versioniert)

## 📚 Dokumentation

Die folgenden Dokumentationsdateien enthalten detaillierte Informationen zu spezifischen Features:

### 🎯 Feature-Dokumentation
- **[FEATURES-v2.0.md](FEATURES-v2.0.md)** - Vollständige Übersicht aller v2.0 Features
- **[SZENARIO-GRID-UPDATE.md](SZENARIO-GRID-UPDATE.md)** - Szenario-Spalte und intelligente Filterung
- **[SZENARIO-WECHSEL-FIX.md](SZENARIO-WECHSEL-FIX.md)** - ZIP_POSTFIX und Grid-Filter Korrekturen

### 🔧 Technische Dokumentation
- **[LOGGING-IMPLEMENTATION.md](LOGGING-IMPLEMENTATION.md)** - Umfassendes Logging-System
- **[TEMP-LOGGING-UPDATE.md](TEMP-LOGGING-UPDATE.md)** - Log-Dateien im Temp-Verzeichnis
- **[DEBUG-RESTORE-CRASH.md](DEBUG-RESTORE-CRASH.md)** - Restore-Crash Debugging und Fixes

### 🚀 Deployment
- **[README-Deployment.md](README-Deployment.md)** - Installations- und Deployment-Anleitung

Alle Dokumentationen sind auf dem neuesten Stand (Oktober 2025) und enthalten vollständige technische Details sowie Benutzeranleitungen.