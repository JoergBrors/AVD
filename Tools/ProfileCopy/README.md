# QGIS Profile Backup & Restore Tool

Ein .NET Windows Forms Tool zum Sichern und Wiederherstellen von QGIS-Profilen über Netzwerk-Freigaben.

## Features

- **Backup erstellen**: Erstellt ZIP-Archive der lokalen QGIS-Profile mit Versionierung
- **Profile wiederherstellen**: Stellt gesicherte Profile wieder her
- **Automatische QGIS-Beendigung**: Optional vor dem Restore
- **Lokale Sicherung**: Erstellt automatisch Backup vor Restore
- **Konfigurierbar**: Verwendung von `host.local` für umgebungsspezifische Einstellungen

## Konfiguration

Das Tool verwendet eine `host.local` Datei für lokale Konfiguration, die nicht ins Git eingecheckt wird:

```ini
# QGIS Profile Backup & Restore Tool - Lokale Konfiguration
# Diese Datei wird NICHT ins Git Repository eingecheckt

# Fileserver Konfiguration
DEFAULT_SHARE=\\\\SERVER\\Freigabe\\QGISProfiles

# Optional: Weitere lokale Anpassungen
# BACKUP_RETENTION_DAYS=30
# AUTO_BACKUP_ENABLED=true

# Debug-Einstellungen
# DEBUG_MODE=false
# LOG_LEVEL=Info
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
- `HostConfiguration.cs` - Konfigurationsmanagement für host.local
- `host.local` - Lokale Konfigurationsdatei (nicht versioniert)
- `.gitignore` - Git-Ignore-Regeln für .NET und lokale Dateien