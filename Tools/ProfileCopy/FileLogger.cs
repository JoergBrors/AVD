using System;
using System.IO;
using System.Threading;

namespace QGISProfileTool
{
    public class FileLogger
    {
        private static FileLogger? _instance;
        private static readonly object _lock = new object();
        private readonly string _logFilePath;
        private readonly object _fileLock = new object();

        private FileLogger()
        {
            // Log-Datei im temporären Verzeichnis
            string logDirectory = Path.Combine(Path.GetTempPath(), "QGISProfileTool_Logs");
            
            // Log-Verzeichnis erstellen falls es nicht existiert
            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }
            
            string logFileName = $"QGISProfileTool_{DateTime.Now:yyyyMMdd}.log";
            _logFilePath = Path.Combine(logDirectory, logFileName);
            
            // Alte Log-Dateien löschen (älter als 30 Tage)
            CleanupOldLogs(logDirectory);
            
            // Initialer Log-Eintrag
            WriteToFile("=== QGIS Profile Backup & Restore Tool gestartet ===");
            WriteToFile($"Version: {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
            WriteToFile($"Benutzer: {Environment.UserName}");
            WriteToFile($"Computer: {Environment.MachineName}");
            WriteToFile($"OS: {Environment.OSVersion}");
            WriteToFile($"Log-Datei: {_logFilePath}");
            WriteToFile("================================================");
        }

        public static FileLogger Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new FileLogger();
                    }
                }
                return _instance;
            }
        }

        public void LogInfo(string message, string? details = null)
        {
            WriteLog("INFO", message, details);
        }

        public void LogWarning(string message, string? details = null)
        {
            WriteLog("WARN", message, details);
        }

        public void LogError(string message, Exception? exception = null, string? details = null)
        {
            string errorDetails = details ?? "";
            if (exception != null)
            {
                errorDetails += $" | Exception: {exception.Message}";
                if (!string.IsNullOrEmpty(exception.StackTrace))
                {
                    errorDetails += $" | StackTrace: {exception.StackTrace}";
                }
            }
            WriteLog("ERROR", message, errorDetails);
        }

        public void LogOperation(string operation, string status, string? details = null)
        {
            WriteLog("OPER", $"{operation} → {status}", details);
        }

        private void WriteLog(string level, string message, string? details = null)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string threadId = Thread.CurrentThread.ManagedThreadId.ToString("D2");
            
            string logEntry = $"[{timestamp}] [{level}] [T{threadId}] {message}";
            if (!string.IsNullOrEmpty(details))
            {
                logEntry += $" | {details}";
            }

            WriteToFile(logEntry);
        }

        private void WriteToFile(string logEntry)
        {
            try
            {
                lock (_fileLock)
                {
                    File.AppendAllText(_logFilePath, logEntry + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                // Fallback: Versuche ins Temp-Verzeichnis zu schreiben
                try
                {
                    string fallbackPath = Path.Combine(Path.GetTempPath(), "QGISProfileTool_Error.log");
                    File.AppendAllText(fallbackPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] LOG ERROR: {ex.Message} | Original: {logEntry}{Environment.NewLine}");
                }
                catch
                {
                    // Wenn auch das fehlschlägt, ignoriere (können nichts mehr tun)
                }
            }
        }

        public string GetLogFilePath()
        {
            return _logFilePath;
        }

        public void LogSession(string sessionType, string action)
        {
            WriteToFile($"--- {sessionType} {action} ---");
        }

        // Cleanup alte Log-Dateien (älter als 30 Tage)
        public void CleanupOldLogs()
        {
            string logDirectory = Path.GetDirectoryName(_logFilePath) ?? "";
            CleanupOldLogs(logDirectory);
        }

        private void CleanupOldLogs(string logDirectory)
        {
            try
            {
                if (Directory.Exists(logDirectory))
                {
                    var logFiles = Directory.GetFiles(logDirectory, "QGISProfileTool_*.log");
                    DateTime cutoffDate = DateTime.Now.AddDays(-30);
                    
                    foreach (string logFile in logFiles)
                    {
                        var fileInfo = new FileInfo(logFile);
                        if (fileInfo.LastWriteTime < cutoffDate)
                        {
                            try
                            {
                                File.Delete(logFile);
                                LogInfo("Alte Log-Datei gelöscht", logFile);
                            }
                            catch (Exception ex)
                            {
                                LogWarning("Konnte alte Log-Datei nicht löschen", $"{logFile} | {ex.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogWarning("Fehler beim Log-Cleanup", ex.Message);
            }
        }
    }
}