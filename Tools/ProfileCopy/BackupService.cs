using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QGISProfileTool
{
    public class BackupService
    {
        private readonly string _userName;
        private readonly FileLogger _logger;

        public BackupService()
        {
            _userName = Environment.UserName;
            _logger = FileLogger.Instance;
            _logger.LogInfo("BackupService initialisiert", $"Benutzer: {_userName}");
        }

        public async Task<string> CreateBackupAsync(
            string localPath, 
            string shareRoot, 
            string version,
            bool killProcesses,
            IProgress<ProgressInfo> progress, 
            CancellationToken cancellationToken)
        {
            return await Task.Run(async () =>
            {
                var config = HostConfiguration.Instance;
                _logger.LogInfo("BackupService.CreateBackupAsync gestartet", 
                    $"Benutzer: {_userName} | Quelle: {localPath} | Ziel: {shareRoot} | Version: {version}");
                
                progress.Report(ProgressInfo.Indeterminate("Validiere Eingaben...", ""));
                await Task.Delay(200, cancellationToken);

                // Validierungen
                if (!Directory.Exists(localPath))
                {
                    _logger.LogError("CreateBackupAsync - Validierung fehlgeschlagen", null, $"Quellverzeichnis nicht gefunden: {localPath}");
                    throw new DirectoryNotFoundException($"Quellverzeichnis nicht gefunden: {localPath}");
                }

                if (string.IsNullOrWhiteSpace(version))
                {
                    _logger.LogError("CreateBackupAsync - Validierung fehlgeschlagen", null, "Version ist leer");
                    throw new ArgumentException("Version darf nicht leer sein");
                }

                progress.Report(ProgressInfo.Indeterminate("Eingaben validiert", "Bereite Backup vor..."));

                // Backup-Pfade erstellen
                string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
                string userShare = GetUserSharePath(shareRoot, _userName);
                
                // ZIP-Postfix aus Konfiguration lesen
                string zipPostfix = config.GetScenarioZipPostfix(config.ActiveScenario);
                string zipFileName = string.IsNullOrEmpty(zipPostfix) 
                    ? $"QGISProfiles_{version}_{timestamp}.zip"  // Legacy Format
                    : $"QGISProfiles_{zipPostfix}_{version}_{timestamp}.zip";  // Neues Format mit Szenario
                    
                string zipPath = Path.Combine(userShare, zipFileName);

                _logger.LogInfo("Backup-Pfade berechnet", 
                    $"ZIP-Datei: {zipFileName} | Pfad: {zipPath} | Postfix: {zipPostfix}");

                try
                {
                    if (killProcesses)
                    {
                        progress.Report(ProgressInfo.Indeterminate("Beende Prozesse...", ""));
                        await StopQGISAsync(progress, cancellationToken);
                        
                        // Konfigurierbare Wartezeit nach dem Beenden der Prozesse
                        int delayMs = config.ProcessKillDelayMs;
                        if (delayMs > 0)
                        {
                            progress.Report(ProgressInfo.Indeterminate($"Warte {delayMs}ms...", "Sicherheitsdelay nach Prozess-Beendigung"));
                            await Task.Delay(delayMs, cancellationToken);
                        }
                    }

                    progress.Report(ProgressInfo.Indeterminate("Erstelle ZIP-Archiv...", localPath));
                    await CreateZipWithProgressAsync(localPath, zipPath, progress, cancellationToken);

                    progress.Report(ProgressInfo.Indeterminate("Backup erfolgreich!", zipPath));
                    await Task.Delay(500, cancellationToken);

                    _logger.LogOperation("Backup erfolgreich erstellt", 
                        $"Datei: {zipFileName} | Größe: {new FileInfo(zipPath).Length / (1024 * 1024)} MB");

                    return zipPath;
                }
                catch (Exception ex)
                {
                    _logger.LogError("CreateBackupAsync fehlgeschlagen", ex, $"Fehler: {ex.Message}");
                    throw;
                }

            }, cancellationToken);
        }

        public async Task RestoreBackupAsync(
            string zipPath, 
            string localPath, 
            bool killProcesses, 
            bool createSnapshot,
            IProgress<ProgressInfo> progress, 
            CancellationToken cancellationToken)
        {
            await Task.Run(async () =>
            {
                _logger.LogInfo("RestoreBackupAsync gestartet", 
                    $"ZIP: {zipPath} | Ziel: {localPath} | Kill Prozesse: {killProcesses} | Snapshot: {createSnapshot}");

                progress.Report(ProgressInfo.Indeterminate("Validiere Restore-Parameter...", ""));
                await Task.Delay(200, cancellationToken);

                // Validierungen
                if (!File.Exists(zipPath))
                {
                    _logger.LogError("RestoreBackupAsync - Validierung fehlgeschlagen", null, $"Backup-Datei nicht gefunden: {zipPath}");
                    throw new FileNotFoundException($"Backup-Datei nicht gefunden: {zipPath}");
                }

                if (string.IsNullOrWhiteSpace(localPath))
                {
                    _logger.LogError("RestoreBackupAsync - Validierung fehlgeschlagen", null, "Zielverzeichnis ist leer");
                    throw new ArgumentException("Zielverzeichnis darf nicht leer sein");
                }

                try
                {
                    if (killProcesses)
                    {
                        progress.Report(ProgressInfo.Indeterminate("Beende Prozesse...", ""));
                        await StopQGISAsync(progress, cancellationToken);
                        await Task.Delay(1000, cancellationToken);
                    }

                    // Snapshot erstellen wenn gewünscht
                    if (createSnapshot && Directory.Exists(localPath))
                    {
                        string snapshotName = $"Snapshot_{DateTime.Now:yyyyMMdd_HHmm}";
                        string snapshotPath = Path.Combine(Path.GetDirectoryName(localPath)!, snapshotName);
                        
                        progress.Report(ProgressInfo.Indeterminate("Erstelle Snapshot...", snapshotPath));
                        
                        Directory.CreateDirectory(snapshotPath);
                        foreach (string file in Directory.GetFiles(localPath, "*", SearchOption.AllDirectories))
                        {
                            string relativePath = Path.GetRelativePath(localPath, file);
                            string destFile = Path.Combine(snapshotPath, relativePath);
                            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                            File.Copy(file, destFile, true);
                        }
                        
                        _logger.LogOperation("Snapshot erstellt", $"Pfad: {snapshotPath}");
                    }

                    // Löschen der existierenden Profile
                    if (Directory.Exists(localPath))
                    {
                        progress.Report(ProgressInfo.Indeterminate("Lösche existierende Profile...", localPath));
                        await DeleteDirectoryAsync(localPath, progress, cancellationToken);
                    }

                    // Zielverzeichnis erstellen
                    Directory.CreateDirectory(localPath);

                    progress.Report(ProgressInfo.Indeterminate("Extrahiere Backup...", "Stelle Profile wieder her"));
                    await ExtractZipWithProgressAsync(zipPath, localPath, progress, cancellationToken);

                    progress.Report(ProgressInfo.Indeterminate("Restore erfolgreich!", "Profile wiederhergestellt"));
                    await Task.Delay(500, cancellationToken);

                    _logger.LogOperation("Restore erfolgreich abgeschlossen", 
                        $"ZIP: {Path.GetFileName(zipPath)} | Ziel: {localPath}");
                }
                catch (Exception ex)
                {
                    _logger.LogError("RestoreBackupAsync fehlgeschlagen", ex, $"Fehler: {ex.Message}");
                    throw;
                }

            }, cancellationToken);
        }

        public async Task<List<BackupItem>> GetBackupsAsync(
            string shareRoot, 
            string activeScenario,
            IProgress<ProgressInfo> progress, 
            CancellationToken cancellationToken,
            bool? overrideShowAll = null)
        {
            return await Task.Run(async () =>
            {
                var results = new List<BackupItem>();
                var hostConfig = HostConfiguration.Instance;
                string activeZipPostfix = hostConfig.GetScenarioZipPostfix(activeScenario);
                bool showAllBackups = overrideShowAll ?? hostConfig.ShowAllBackups;

                _logger.LogInfo("Backup-Liste wird geladen", 
                    $"Szenario: {activeScenario} | ShowAll: {showAllBackups} | Pfad: {shareRoot}");

                progress.Report(ProgressInfo.Indeterminate(
                    showAllBackups ? "Suche alle Backups..." : $"Suche {activeScenario} Backups...", 
                    shareRoot));
                await Task.Delay(200, cancellationToken);

                if (!Directory.Exists(shareRoot))
                    return results;

                string userFolder = GetUserSharePath(shareRoot, _userName);
                if (!Directory.Exists(userFolder))
                {
                    EnsureDirectory(userFolder);
                    return results;
                }

                progress.Report(ProgressInfo.Indeterminate("Lade Backup-Liste...", "Analysiere Dateien"));
                
                var files = Directory.GetFiles(userFolder, "*.zip");
                int totalFiles = files.Length;
                int scenarioMatches = 0;
                int totalValidFiles = 0;
                var scenarioStats = new Dictionary<string, int>();

                for (int i = 0; i < files.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string file = files[i];
                    var fileInfo = new FileInfo(file);
                    
                    progress.Report(ProgressInfo.Determinate(
                        "Analysiere Backups...", 
                        i + 1, 
                        totalFiles, 
                        fileInfo.Name));

                    // Neues Format: QGISProfiles_Szenario_Version_Zeitstempel.zip
                    var newMatch = System.Text.RegularExpressions.Regex.Match(
                        fileInfo.Name, @"^QGISProfiles_(.+?)_(.+?)_(\d{8}-\d{4})\.zip$");
                    
                    // Altes Format: QGISProfiles_Version_Zeitstempel.zip (für Rückwärtskompatibilität)
                    var oldMatch = System.Text.RegularExpressions.Regex.Match(
                        fileInfo.Name, @"^QGISProfiles_(.+?)_(\d{8}-\d{4})\.zip$");

                    string scenario = "";
                    string version = "(unbekannt)";
                    DateTime zeitstempel = fileInfo.LastWriteTime;
                    bool isValidFormat = false;

                    if (newMatch.Success)
                    {
                        scenario = newMatch.Groups[1].Value;
                        version = newMatch.Groups[2].Value;
                        isValidFormat = true;
                        if (DateTime.TryParseExact(newMatch.Groups[3].Value, "yyyyMMdd-HHmm", null, 
                            System.Globalization.DateTimeStyles.None, out DateTime dt))
                        {
                            zeitstempel = dt;
                        }
                    }
                    else if (oldMatch.Success)
                    {
                        // Altes Format
                        scenario = "QGIS_Default";
                        version = oldMatch.Groups[1].Value;
                        isValidFormat = true;
                        if (DateTime.TryParseExact(oldMatch.Groups[2].Value, "yyyyMMdd-HHmm", null, 
                            System.Globalization.DateTimeStyles.None, out DateTime dt))
                        {
                            zeitstempel = dt;
                        }
                    }

                    // Statistiken sammeln
                    if (isValidFormat)
                    {
                        totalValidFiles++;
                        if (scenarioStats.ContainsKey(scenario))
                            scenarioStats[scenario]++;
                        else
                            scenarioStats[scenario] = 1;
                    }

                    // Entscheide ob angezeigt werden soll
                    bool shouldDisplay = showAllBackups || scenario == activeZipPostfix || (!isValidFormat && showAllBackups);
                    
                    if (shouldDisplay)
                    {
                        if (scenario == activeZipPostfix) scenarioMatches++;
                        
                        bool isCompatible = scenario == activeZipPostfix || !isValidFormat;
                        string warningMessage = "";
                        
                        if (!isCompatible)
                        {
                            warningMessage = $"⚠️ Gehört zu Szenario '{scenario}' (aktuell: {activeScenario})";
                        }

                        results.Add(new BackupItem
                        {
                            FullName = file,
                            Datei = fileInfo.Name,
                            Version = version,
                            Scenario = scenario,
                            Zeitstempel = zeitstempel,
                            GroesseBytes = fileInfo.Length,
                            IsCompatibleWithCurrentScenario = isCompatible,
                            WarningMessage = warningMessage
                        });
                    }
                }

                // Statistik-String erstellen
                var statsDetails = scenarioStats.Select(kvp => $"{kvp.Key}({kvp.Value})").ToList();
                string statsMessage = $"Gesamt: {totalFiles} | Gültig: {totalValidFiles} | Szenarien: {string.Join(", ", statsDetails)}";
                
                if (showAllBackups)
                {
                    progress.Report(ProgressInfo.Indeterminate($"Alle Backups geladen: {results.Count} angezeigt", 
                        $"{statsMessage} | Kompatibel mit {activeScenario}: {scenarioMatches}"));
                    _logger.LogInfo("Backup-Liste geladen (alle Szenarien)", 
                        $"Gesamt: {results.Count} | Kompatibel: {scenarioMatches} | Stats: {statsMessage}");
                }
                else
                {
                    progress.Report(ProgressInfo.Indeterminate($"Gefunden: {scenarioMatches} {activeScenario} Backups", 
                        $"{statsMessage} | {totalValidFiles - scenarioMatches} andere Szenarien ausgeblendet"));
                    _logger.LogInfo("Backup-Liste geladen (gefiltert)", 
                        $"Szenario: {activeScenario} | Angezeigt: {scenarioMatches} | Ausgeblendet: {totalValidFiles - scenarioMatches}");
                }
                
                await Task.Delay(200, cancellationToken);

                return results;
            }, cancellationToken);
        }

        private async Task CreateZipWithProgressAsync(string sourceDir, string zipPath, IProgress<ProgressInfo> progress, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                try
                {
                    _logger.LogInfo("ZIP-Erstellung gestartet", $"Quelle: {sourceDir} | Ziel: {zipPath}");
                    
                    progress.Report(ProgressInfo.Indeterminate("Analysiere Quell-Verzeichnis...", sourceDir));
                    
                    if (!Directory.Exists(sourceDir))
                    {
                        _logger.LogError("ZIP-Erstellung fehlgeschlagen", null, $"Quell-Verzeichnis nicht gefunden: {sourceDir}");
                        throw new DirectoryNotFoundException($"Quell-Verzeichnis nicht gefunden: {sourceDir}");
                    }

                    var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
                    int totalFiles = files.Length;
                    
                    if (totalFiles == 0)
                    {
                        _logger.LogWarning("ZIP-Erstellung übersprungen", $"Keine Dateien gefunden in: {sourceDir}");
                        throw new InvalidOperationException($"Keine Dateien im Quell-Verzeichnis gefunden: {sourceDir}");
                    }

                    _logger.LogInfo("ZIP-Analyse abgeschlossen", $"Dateien gefunden: {totalFiles}");
                    progress.Report(ProgressInfo.Indeterminate($"Erstelle ZIP-Archiv...", $"{totalFiles} Dateien gefunden"));

                    // Lösche existierende ZIP-Datei
                    if (File.Exists(zipPath))
                    {
                        File.Delete(zipPath);
                        _logger.LogInfo("Bestehende ZIP-Datei gelöscht", zipPath);
                    }

                    // Stelle sicher dass Zielverzeichnis existiert
                    string? zipDirectory = Path.GetDirectoryName(zipPath);
                    if (!string.IsNullOrEmpty(zipDirectory))
                    {
                        Directory.CreateDirectory(zipDirectory);
                    }

                    int processedFiles = 0;
                    int errorCount = 0;
                    var errors = new List<string>();

                    using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                    {
                        foreach (string filePath in files)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            
                            try
                            {
                                string relativePath = Path.GetRelativePath(sourceDir, filePath);
                                
                                progress.Report(ProgressInfo.Determinate(
                                    "Komprimiere Dateien...", 
                                    processedFiles + 1, 
                                    totalFiles, 
                                    relativePath));

                                archive.CreateEntryFromFile(filePath, relativePath);
                                processedFiles++;
                            }
                            catch (Exception ex)
                            {
                                errorCount++;
                                string error = $"Fehler bei {Path.GetFileName(filePath)}: {ex.Message}";
                                errors.Add(error);
                                
                                if (errorCount <= 10) // Nur erste 10 Fehler sammeln
                                {
                                    _logger.LogWarning("ZIP-Dateifehler", error);
                                }
                            }
                        }
                    }

                    // Verifikation des erstellten ZIP
                    var zipInfo = new FileInfo(zipPath);
                    if (!zipInfo.Exists || zipInfo.Length == 0)
                        throw new IOException("ZIP-Datei wurde nicht korrekt erstellt oder ist leer");

                    string resultMessage = $"✅ {processedFiles} Dateien komprimiert";
                    if (errorCount > 0)
                    {
                        resultMessage += $" ({errorCount} Fehler)";
                        progress.Report(ProgressInfo.Indeterminate("⚠️ ZIP mit Warnungen erstellt", 
                            $"{resultMessage} | Größe: {zipInfo.Length / (1024 * 1024)} MB"));
                            
                        _logger.LogWarning("ZIP-Erstellung mit Fehlern abgeschlossen", 
                            $"Verarbeitet: {processedFiles} | Fehler: {errorCount} | Größe: {zipInfo.Length / (1024 * 1024)} MB");
                            
                        // Log erste 3 Fehler
                        foreach (var error in errors.Take(3))
                        {
                            progress.Report(ProgressInfo.Indeterminate("Warnung", error));
                            _logger.LogWarning("ZIP-Dateifehler", error);
                        }
                    }
                    else
                    {
                        progress.Report(ProgressInfo.Indeterminate("ZIP-Archiv erstellt", 
                            $"{resultMessage} | Größe: {zipInfo.Length / (1024 * 1024)} MB"));
                        _logger.LogOperation("ZIP-Erstellung erfolgreich", 
                            $"Dateien: {processedFiles} | Größe: {zipInfo.Length / (1024 * 1024)} MB | Pfad: {zipPath}");
                    }
                }
                catch (Exception ex)
                {
                    progress.Report(ProgressInfo.Indeterminate($"❌ ZIP-Erstellungsfehler: {ex.Message}", sourceDir));
                    _logger.LogError("ZIP-Erstellung fehlgeschlagen", ex, $"Fehler beim Erstellen von {zipPath}: {ex.Message}");
                    throw new IOException($"Fehler beim Erstellen des ZIP-Archivs '{zipPath}': {ex.Message}", ex);
                }
            }, cancellationToken);
        }

        private async Task ExtractZipWithProgressAsync(string zipPath, string extractPath, IProgress<ProgressInfo> progress, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                _logger.LogInfo("ZIP-Extraktion gestartet", $"Quelle: {zipPath} | Ziel: {extractPath}");
                
                using var archive = ZipFile.OpenRead(zipPath);
                int totalEntries = archive.Entries.Count;
                int processedEntries = 0;
                
                _logger.LogInfo("ZIP-Archiv analysiert", $"Einträge gefunden: {totalEntries}");

                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    progress.Report(ProgressInfo.Determinate(
                        "Extrahiere Dateien...", 
                        processedEntries + 1, 
                        totalEntries, 
                        entry.Name));

                    string destinationPath = Path.Combine(extractPath, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    
                    if (!string.IsNullOrEmpty(entry.Name)) // Nicht nur Ordner
                    {
                        entry.ExtractToFile(destinationPath, true);
                    }
                    
                    processedEntries++;
                }
                
                _logger.LogOperation("ZIP-Extraktion erfolgreich", 
                    $"Extrahiert: {processedEntries} Einträge nach {extractPath}");
            }, cancellationToken);
        }

        private async Task DeleteDirectoryAsync(string path, IProgress<ProgressInfo> progress, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                _logger.LogInfo("Verzeichnis-Löschung gestartet", $"Pfad: {path}");
                
                var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
                int totalFiles = files.Length;
                
                _logger.LogInfo("Verzeichnis analysiert", $"Dateien gefunden: {totalFiles}");

                for (int i = 0; i < files.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    progress.Report(ProgressInfo.Determinate(
                        "Lösche Dateien...", 
                        i + 1, 
                        totalFiles, 
                        Path.GetFileName(files[i])));

                    try
                    {
                        File.Delete(files[i]);
                    }
                    catch (Exception ex) 
                    { 
                        _logger.LogWarning("Datei-Löschung fehlgeschlagen", $"Datei: {files[i]} | Fehler: {ex.Message}");
                    }
                }

                Directory.Delete(path, true);
                _logger.LogOperation("Verzeichnis-Löschung erfolgreich", $"Gelöscht: {totalFiles} Dateien aus {path}");
            }, cancellationToken);
        }

        private async Task StopQGISAsync(IProgress<ProgressInfo> progress, CancellationToken cancellationToken)
        {
            await Task.Run(async () =>
            {
                var config = HostConfiguration.Instance;
                string[] processNames = config.ActiveProcessNames;
                var allProcesses = new List<Process>();

                _logger.LogInfo("Prozess-Beendigung gestartet", $"Prozesse: {string.Join(", ", processNames)}");

                progress.Report(ProgressInfo.Indeterminate("Suche nach Prozessen...", 
                    $"Suchkriterien: {string.Join(", ", processNames)}"));

                // Sammle alle relevanten Prozesse - auch mit Datei-Endungen
                foreach (string processName in processNames)
                {
                    try
                    {
                        // Suche ohne .exe Endung
                        var processes = Process.GetProcessesByName(processName);
                        allProcesses.AddRange(processes);
                        
                        // Auch mit .exe Endung probieren falls der Name ohne .exe nicht gefunden wurde
                        if (processes.Length == 0 && !processName.EndsWith(".exe"))
                        {
                            processes = Process.GetProcessesByName(processName + ".exe");
                            allProcesses.AddRange(processes);
                        }
                        
                        // Umgekehrt - wenn der Name mit .exe endet, probiere auch ohne
                        if (processes.Length == 0 && processName.EndsWith(".exe"))
                        {
                            string nameWithoutExt = processName.Substring(0, processName.Length - 4);
                            processes = Process.GetProcessesByName(nameWithoutExt);
                            allProcesses.AddRange(processes);
                        }
                    }
                    catch (Exception ex)
                    {
                        progress.Report(ProgressInfo.Indeterminate($"⚠️ Fehler bei Prozesssuche: {processName}", ex.Message));
                        _logger.LogWarning("Prozesssuche fehlgeschlagen", $"Prozess: {processName} | Fehler: {ex.Message}");
                    }
                }

                if (allProcesses.Count == 0)
                {
                    progress.Report(ProgressInfo.Indeterminate("✅ Keine Prozesse zu beenden", "Alle Prozesse bereits beendet oder nicht gefunden"));
                    _logger.LogInfo("Prozess-Beendigung übersprungen", "Keine relevanten Prozesse gefunden");
                    return;
                }

                progress.Report(ProgressInfo.Indeterminate($"Beende {allProcesses.Count} Prozesse...", 
                    string.Join(", ", allProcesses.Select(p => $"{p.ProcessName}({p.Id})"))));

                int killedCount = 0;
                var failedKills = new List<string>();

                foreach (var process in allProcesses)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        string processInfo = $"{process.ProcessName} (PID: {process.Id})";
                        
                        if (!process.HasExited)
                        {
                            process.Kill();
                            await Task.Delay(100, cancellationToken); // Kurz warten
                            
                            if (process.HasExited)
                            {
                                killedCount++;
                                _logger.LogOperation("Prozess beendet", processInfo);
                                progress.Report(ProgressInfo.Indeterminate($"✅ Beendet: {processInfo}", $"{killedCount}/{allProcesses.Count} Prozesse beendet"));
                            }
                            else
                            {
                                failedKills.Add(processInfo);
                                _logger.LogWarning("Prozess-Beendigung fehlgeschlagen", $"Prozess reagierte nicht: {processInfo}");
                            }
                        }
                        else
                        {
                            _logger.LogInfo("Prozess bereits beendet", processInfo);
                        }
                    }
                    catch (Exception ex)
                    {
                        string processInfo = $"{process.ProcessName} (PID: {process.Id})";
                        failedKills.Add(processInfo);
                        _logger.LogError("Prozess-Beendigung fehlgeschlagen", ex, $"Fehler beim Beenden von {processInfo}: {ex.Message}");
                        progress.Report(ProgressInfo.Indeterminate($"❌ Fehler: {processInfo}", ex.Message));
                    }
                }

                string resultMessage = $"✅ {killedCount} Prozesse erfolgreich beendet";
                if (failedKills.Count > 0)
                {
                    resultMessage += $" ({failedKills.Count} Fehler)";
                    _logger.LogWarning("Prozess-Beendigung teilweise fehlgeschlagen", 
                        $"Erfolgreich: {killedCount} | Fehlgeschlagen: {failedKills.Count} | Fehler: {string.Join(", ", failedKills)}");
                }
                else
                {
                    _logger.LogOperation("Prozess-Beendigung erfolgreich", $"Alle {killedCount} Prozesse beendet");
                }

                progress.Report(ProgressInfo.Indeterminate(resultMessage, 
                    failedKills.Count > 0 ? $"Fehler bei: {string.Join(", ", failedKills)}" : "Alle Prozesse erfolgreich beendet"));

            }, cancellationToken);
        }

        private static string GetUserSharePath(string shareRoot, string userName)
        {
            return Path.Combine(shareRoot, userName);
        }

        private static void EnsureDirectory(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }
    }


}