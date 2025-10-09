# DEBUGGING GUIDE - Restore Crash Problem

## Problem
- Anwendung schließt sich ohne erkennbare Fehler beim Restore von inkompatiblen Backups
- Tritt auf bei Auswahl von Dateien anderer Szenarien

## Implementierte Fixes (Version mit verbessertem Exception Handling):

### 1. Umfassendes Exception Handling in BtnRestore_Click
- Try-Catch um gesamte Methode
- Detailliertes Logging jedes Schritts
- Sichere Grid-Zugriffe mit separatem Exception Handling
- Fehlermeldungen werden ins Log und MessageBox ausgegeben

### 2. Erweiterte Diagnostik
- Log-Ausgaben für jeden wichtigen Schritt:
  - "Restore gestartet - Verarbeite ausgewählte Zeile..."
  - "Ausgewählte Datei: 'XXX', Pfad: 'YYY'"  
  - "Kompatibilitätsprüfung: KOMPATIBEL/INKOMPATIBEL"
  - "Starte Restore-Prozess für 'XXX'..."

### 3. Robuste Grid-Datenextraktion
```csharp
try
{
    zipPath = grid.SelectedRows[0].Cells["FullNameHidden"]?.Value?.ToString();
    fileName = grid.SelectedRows[0].Cells["Datei"]?.Value?.ToString() ?? "";
    txtLog.AppendText($"Ausgewählte Datei: '{fileName}', Pfad: '{zipPath}'\r\n");
}
catch (Exception ex)
{
    txtLog.AppendText($"FEHLER beim Grid-Zugriff: {ex.Message}\r\n");
    // Zeige Fehlermeldung und kehre zurück statt Crash
}
```

## Test-Schritte:

1. **Starte die neue Version der Anwendung**
2. **Aktiviere "🔍 Alle Backups anzeigen"**
3. **Wähle ein inkompatibles Backup (rot markiert)**
4. **Klicke auf "Ausgewählte Sicherung wiederherstellen"**
5. **Beobachte das Log für detaillierte Ausgaben**

## Erwartetes Verhalten:

### Bei korrektem Funktionieren:
- Log zeigt: "Restore gestartet - Verarbeite ausgewählte Zeile..."
- Log zeigt: "Ausgewählte Datei: '...', Pfad: '...'"
- Log zeigt: "Kompatibilitätsprüfung: INKOMPATIBEL"
- Warnung-Dialog erscheint für Bestätigung
- Nach Bestätigung: "Starte Restore-Prozess für '...'"

### Bei Problemen:
- Log zeigt spezifische Fehlermeldungen
- MessageBox mit detailliertem Fehler
- Anwendung bleibt geöffnet

## Mögliche Root-Causes:

1. **Grid-Spalten Problem**: FullNameHidden Spalte existiert nicht oder falsch benannt
2. **BackupItem Daten Problem**: IsCompatibleWithCurrentScenario falsch gesetzt
3. **ProgressForm Problem**: Exception in der Progress-Dialog Anzeige
4. **BackupService Problem**: Exception in RestoreBackupAsync
5. **Threading Problem**: UI-Thread Zugriff Violation

## Next Steps wenn Problem weiterhin auftritt:

1. **Prüfe Log-Ausgaben** - Wo stoppt die Ausgabe?
2. **Teste mit kompatiblen Backups** - Funktioniert Restore grundsätzlich?
3. **Prüfe Grid-Struktur** - Sind alle Spalten korrekt definiert?
4. **Windows Event Log** - Eventuell Application Crash Einträge?