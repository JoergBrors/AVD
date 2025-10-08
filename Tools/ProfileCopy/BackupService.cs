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

        public BackupService()
        {
            _userName = Environment.UserName;
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
                progress.Report(ProgressInfo.Indeterminate("Validiere Eingaben...", ""));
                await Task.Delay(200, cancellationToken);

                // Validierung
                if (!Directory.Exists(localPath))
                    throw new InvalidOperationException("Lokales Profil nicht gefunden.");

                if (string.IsNullOrWhiteSpace(version))
                    throw new InvalidOperationException("Bitte Version angeben.");

                if (!Directory.Exists(shareRoot))
                    throw new InvalidOperationException("Share nicht erreichbar.");

                var hostConfig = HostConfiguration.Instance;

                // Prozesse beenden wenn gewünscht
                if (killProcesses)
                {
                    progress.Report(ProgressInfo.Indeterminate("Beende Prozesse vor Backup...", "⚠️ Warnung: Möglicher Datenverlust"));
                    await StopQGISAsync(progress, cancellationToken);
                    
                    // Kurze Pause damit Prozesse sauber beendet werden können und Dateien freigegeben werden
                    int delayMs = hostConfig.ProcessKillDelayMs;
                    progress.Report(ProgressInfo.Indeterminate("Warte auf Prozess-Beendigung...", $"Sicherheitsdelay: {delayMs}ms"));
                    await Task.Delay(delayMs, cancellationToken);
                }

                progress.Report(ProgressInfo.Indeterminate("Bereite Backup vor...", "Erstelle Verzeichnisse"));
                await Task.Delay(200, cancellationToken);

                string userFolder = GetUserSharePath(shareRoot, _userName);
                EnsureDirectory(userFolder);
                string zipName = BuildZipName(version, hostConfig.ActiveScenario);
                string zipTemp = Path.Combine(Path.GetTempPath(), zipName);
                string zipDest = Path.Combine(userFolder, zipName);

                try
                {
                    if (File.Exists(zipTemp))
                        File.Delete(zipTemp);

                    progress.Report(ProgressInfo.Indeterminate("Erstelle ZIP-Archive...", zipTemp));
                    
                    // ZIP-Erstellung mit Progress-Tracking
                    await CreateZipWithProgressAsync(localPath, zipTemp, progress, cancellationToken);

                    progress.Report(ProgressInfo.Indeterminate("Kopiere zum Server...", zipDest));
                    await Task.Delay(100, cancellationToken);

                    // Kopieren mit Progress für große Dateien
                    await CopyFileWithProgressAsync(zipTemp, zipDest, progress, cancellationToken);

                    progress.Report(ProgressInfo.Indeterminate("Backup erfolgreich!", $"Gespeichert: {zipDest}"));
                    await Task.Delay(500, cancellationToken);

                    return zipDest;
                }
                finally
                {
                    if (File.Exists(zipTemp))
                    {
                        try { File.Delete(zipTemp); } catch { }
                    }
                }
            }, cancellationToken);
        }

        public async Task RestoreBackupAsync(
            string zipPath, 
            string localPath, 
            bool killQGIS, 
            bool makeLocalBackup, 
            IProgress<ProgressInfo> progress, 
            CancellationToken cancellationToken)
        {
            await Task.Run(async () =>
            {
                progress.Report(ProgressInfo.Indeterminate("Validiere Restore...", zipPath));
                await Task.Delay(200, cancellationToken);

                if (!File.Exists(zipPath))
                    throw new InvalidOperationException("Sicherung nicht gefunden.");

                if (killQGIS)
                {
                    progress.Report(ProgressInfo.Indeterminate("Beende QGIS...", "Suche laufende Prozesse"));
                    await Task.Delay(200, cancellationToken);
                    await StopQGISAsync(progress, cancellationToken);
                }

                if (makeLocalBackup && Directory.Exists(localPath))
                {
                    progress.Report(ProgressInfo.Indeterminate("Erstelle lokales Backup...", "Sichere aktuelles Profil"));
                    string backupFolder = Path.Combine(Path.GetTempPath(), $"QGISBeforeRestore_{FormatTimeStamp()}");
                    await SafeBackupCurrentLocalAsync(localPath, backupFolder, progress, cancellationToken);
                }

                progress.Report(ProgressInfo.Indeterminate("Lösche altes Profil...", localPath));
                await Task.Delay(200, cancellationToken);

                if (Directory.Exists(localPath))
                {
                    await DeleteDirectoryAsync(localPath, progress, cancellationToken);
                }

                EnsureDirectory(localPath);

                progress.Report(ProgressInfo.Indeterminate("Extrahiere Backup...", "Stelle Profile wieder her"));
                await ExtractZipWithProgressAsync(zipPath, localPath, progress, cancellationToken);

                progress.Report(ProgressInfo.Indeterminate("Restore erfolgreich!", "Profile wiederhergestellt"));
                await Task.Delay(500, cancellationToken);

            }, cancellationToken);
        }

        public async Task<List<BackupItem>> GetBackupsAsync(
            string shareRoot, 
            string activeScenario,
            IProgress<ProgressInfo> progress, 
            CancellationToken cancellationToken)
        {
            return await Task.Run(async () =>
            {
                var results = new List<BackupItem>();

                progress.Report(ProgressInfo.Indeterminate($"Suche {activeScenario} Backups...", shareRoot));
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

                    if (newMatch.Success)
                    {
                        scenario = newMatch.Groups[1].Value;
                        version = newMatch.Groups[2].Value;
                        if (DateTime.TryParseExact(newMatch.Groups[3].Value, "yyyyMMdd-HHmm", null, 
                            System.Globalization.DateTimeStyles.None, out DateTime dt))
                        {
                            zeitstempel = dt;
                        }
                    }
                    else if (oldMatch.Success)
                    {
                        // Altes Format - zeige nur wenn Default-Szenario aktiv ist
                        scenario = "QGIS_Default";
                        version = oldMatch.Groups[1].Value;
                        if (DateTime.TryParseExact(oldMatch.Groups[2].Value, "yyyyMMdd-HHmm", null, 
                            System.Globalization.DateTimeStyles.None, out DateTime dt))
                        {
                            zeitstempel = dt;
                        }
                    }

                    // Nur Backups des aktiven Szenarios anzeigen
                    if (scenario.Equals(activeScenario, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new BackupItem
                        {
                            FullName = file,
                            Datei = fileInfo.Name,
                            Version = version,
                            Scenario = scenario,
                            Zeitstempel = zeitstempel,
                            GroesseBytes = fileInfo.Length
                        });
                        scenarioMatches++;
                    }

                    // Kleine Verzögerung für UI-Responsiveness
                    if (i % 10 == 0)
                        await Task.Delay(10, cancellationToken);
                }

                progress.Report(ProgressInfo.Indeterminate($"Gefunden: {scenarioMatches} {activeScenario} Backups", 
                    $"({totalFiles - scenarioMatches} andere Szenarien ignoriert)"));
                await Task.Delay(200, cancellationToken);

                return results;
            }, cancellationToken);
        }

        private async Task CreateZipWithProgressAsync(string sourceDir, string zipPath, IProgress<ProgressInfo> progress, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
                int totalFiles = files.Length;
                int processedFiles = 0;

                using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                
                foreach (string file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string relativePath = Path.GetRelativePath(sourceDir, file);
                    progress.Report(ProgressInfo.Determinate(
                        "Komprimiere Dateien...", 
                        processedFiles + 1, 
                        totalFiles, 
                        relativePath));

                    archive.CreateEntryFromFile(file, relativePath);
                    processedFiles++;
                }
            }, cancellationToken);
        }

        private async Task ExtractZipWithProgressAsync(string zipPath, string extractPath, IProgress<ProgressInfo> progress, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(zipPath);
                int totalEntries = archive.Entries.Count;
                int processedEntries = 0;

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
            }, cancellationToken);
        }

        private async Task CopyFileWithProgressAsync(string sourcePath, string destPath, IProgress<ProgressInfo> progress, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                const int bufferSize = 1024 * 1024; // 1MB Buffer
                var fileInfo = new FileInfo(sourcePath);
                long totalBytes = fileInfo.Length;
                long copiedBytes = 0;

                using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read);
                using var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write);
                
                var buffer = new byte[bufferSize];
                int bytesRead;

                while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    dest.Write(buffer, 0, bytesRead);
                    copiedBytes += bytesRead;

                    int percentage = (int)((copiedBytes * 100) / totalBytes);
                    progress.Report(ProgressInfo.Determinate(
                        "Kopiere zum Server...", 
                        percentage, 
                        100, 
                        $"{copiedBytes / (1024 * 1024)} MB / {totalBytes / (1024 * 1024)} MB"));
                }
            }, cancellationToken);
        }

        private async Task DeleteDirectoryAsync(string path, IProgress<ProgressInfo> progress, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
                int totalFiles = files.Length;

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
                    catch { } // Ignoriere Fehler beim Löschen einzelner Dateien
                }

                Directory.Delete(path, true);
            }, cancellationToken);
        }

        private async Task StopQGISAsync(IProgress<ProgressInfo> progress, CancellationToken cancellationToken)
        {
            await Task.Run(async () =>
            {
                var config = HostConfiguration.Instance;
                string[] processNames = config.ActiveProcessNames;
                var allProcesses = new List<Process>();

                // Sammle alle relevanten Prozesse
                foreach (string processName in processNames)
                {
                    try
                    {
                        var processes = Process.GetProcessesByName(processName);
                        allProcesses.AddRange(processes);
                        
                        progress.Report(ProgressInfo.Indeterminate($"Suche Prozesse...", 
                            $"Gefunden: {processes.Length}x {processName}"));
                        
                        await Task.Delay(100, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        progress.Report(ProgressInfo.Indeterminate($"Warnung bei {processName}", ex.Message));
                    }
                }

                if (allProcesses.Count == 0)
                {
                    progress.Report(ProgressInfo.Indeterminate("Keine Prozesse gefunden", 
                        $"Suchte: {string.Join(", ", processNames)}"));
                    return;
                }

                progress.Report(ProgressInfo.Indeterminate($"Beende {allProcesses.Count} Prozess(e)...", 
                    "Sende Beenden-Signal"));

                int processedCount = 0;
                foreach (var process in allProcesses)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    try
                    {
                        string processInfo = $"{process.ProcessName} (PID: {process.Id})";
                        progress.Report(ProgressInfo.Determinate($"Beende Prozesse...", 
                            processedCount + 1, allProcesses.Count, processInfo));

                        if (!process.HasExited)
                        {
                            // Versuche zuerst graceful shutdown
                            process.CloseMainWindow();
                            
                            // Warte kurz auf graceful shutdown
                            bool exited = await Task.Run(() => process.WaitForExit(3000), cancellationToken);
                            
                            if (!exited && !process.HasExited)
                            {
                                // Forceful kill wenn graceful nicht funktioniert
                                process.Kill();
                                await process.WaitForExitAsync(cancellationToken);
                            }
                            
                            // Zusätzliche Verifikation dass Prozess wirklich beendet ist
                            int verificationAttempts = 0;
                            while (!process.HasExited && verificationAttempts < 10)
                            {
                                await Task.Delay(500, cancellationToken);
                                try
                                {
                                    process.Refresh();
                                    if (!process.HasExited)
                                    {
                                        progress.Report(ProgressInfo.Indeterminate($"Prozess widersteht...", 
                                            $"Versuch {verificationAttempts + 1}/10: {process.ProcessName}"));
                                        process.Kill();
                                    }
                                }
                                catch { /* Prozess bereits weg */ }
                                verificationAttempts++;
                            }
                            
                            if (verificationAttempts >= 10)
                            {
                                progress.Report(ProgressInfo.Indeterminate("⚠️ Prozess-Warnung", 
                                    $"Prozess {process.ProcessName} konnte nicht sicher beendet werden"));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        progress.Report(ProgressInfo.Indeterminate($"Fehler bei Prozess", ex.Message));
                    }
                    finally
                    {
                        try { process.Dispose(); } catch { }
                        processedCount++;
                    }
                }

                progress.Report(ProgressInfo.Indeterminate("Prozesse beendet", 
                    $"{processedCount} von {allProcesses.Count} Prozesse gestoppt"));
                
            }, cancellationToken);
        }

        private async Task<string?> SafeBackupCurrentLocalAsync(string localPath, string destFolder, IProgress<ProgressInfo> progress, CancellationToken cancellationToken)
        {
            if (!Directory.Exists(localPath))
                return null;

            EnsureDirectory(destFolder);
            string zipPath = Path.Combine(destFolder, $"BeforeRestore_{FormatTimeStamp()}.zip");
            
            await CreateZipWithProgressAsync(localPath, zipPath, progress, cancellationToken);
            return zipPath;
        }

        private string GetUserSharePath(string share, string user) => Path.Combine(share, user);
        private void EnsureDirectory(string path) { if (!Directory.Exists(path)) Directory.CreateDirectory(path); }
        private string FormatTimeStamp() => DateTime.Now.ToString("yyyyMMdd-HHmm");
        private string BuildZipName(string version, string scenario) => $"QGISProfiles_{scenario}_{version}_{FormatTimeStamp()}.zip";
    }
}