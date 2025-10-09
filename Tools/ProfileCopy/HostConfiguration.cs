using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace QGISProfileTool
{
    public class HostConfiguration
    {
        private readonly Dictionary<string, string> _settings;
        private readonly Dictionary<string, Dictionary<string, string>> _scenarios;
        private static HostConfiguration _instance;
        private static readonly object _lock = new object();

        private HostConfiguration()
        {
            _settings = new Dictionary<string, string>();
            _scenarios = new Dictionary<string, Dictionary<string, string>>();
            LoadConfiguration();
        }

        public static HostConfiguration Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new HostConfiguration();
                    }
                }
                return _instance;
            }
        }

        private void LoadConfiguration()
        {
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "host.local");
            
            // Setze Standardwerte
            _settings["ACTIVE_SCENARIO"] = "QGIS_Default";
            _settings["APPLICATION_TITLE"] = "QGIS Profile Backup & Restore Tool";
            _settings["DEFAULT_SHARE"] = @"\\SERVER\Freigabe\QGISProfiles";
            _settings["DEBUG_MODE"] = "false";
            _settings["LOG_LEVEL"] = "Info";
            _settings["BACKUP_RETENTION_DAYS"] = "30";
            _settings["AUTO_BACKUP_ENABLED"] = "true";

            if (File.Exists(configPath))
            {
                try
                {
                    string[] lines = File.ReadAllLines(configPath);
                    string currentSection = "";
                    Dictionary<string, string>? currentScenario = null;

                    foreach (string line in lines)
                    {
                        string trimmedLine = line.Trim();
                        
                        // Ignoriere Kommentare und leere Zeilen
                        if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith("#"))
                            continue;

                        // Prüfe auf Sektion [SectionName]
                        var sectionMatch = Regex.Match(trimmedLine, @"^\[(.+)\]$");
                        if (sectionMatch.Success)
                        {
                            currentSection = sectionMatch.Groups[1].Value;
                            currentScenario = new Dictionary<string, string>();
                            _scenarios[currentSection] = currentScenario;
                            continue;
                        }

                        // Prüfe auf Key=Value
                        var parts = trimmedLine.Split('=', 2);
                        if (parts.Length == 2)
                        {
                            string key = parts[0].Trim();
                            string value = ExpandEnvironmentVariables(parts[1].Trim());

                            if (currentScenario != null)
                            {
                                // Füge zu aktueller Sektion hinzu
                                currentScenario[key] = value;
                            }
                            else
                            {
                                // Füge zu Hauptkonfiguration hinzu
                                _settings[key] = value;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.Forms.MessageBox.Show($"Fehler beim Laden der Konfiguration: {ex.Message}", 
                        "Konfigurationsfehler", System.Windows.Forms.MessageBoxButtons.OK, 
                        System.Windows.Forms.MessageBoxIcon.Warning);
                }
            }
        }

        private string ExpandEnvironmentVariables(string value)
        {
            // Erweitere Umgebungsvariablen wie %APPDATA%, %USERNAME%
            return Environment.ExpandEnvironmentVariables(value);
        }

        public string GetSetting(string key, string defaultValue = "")
        {
            return _settings.TryGetValue(key, out string? value) ? value : defaultValue;
        }

        public string GetScenarioSetting(string scenarioName, string key, string defaultValue = "")
        {
            if (_scenarios.TryGetValue(scenarioName, out Dictionary<string, string>? scenario))
            {
                return scenario.TryGetValue(key, out string? value) ? value : defaultValue;
            }
            return defaultValue;
        }

        public bool GetBoolSetting(string key, bool defaultValue = false)
        {
            string value = GetSetting(key);
            return bool.TryParse(value, out bool result) ? result : defaultValue;
        }

        public int GetIntSetting(string key, int defaultValue = 0)
        {
            string value = GetSetting(key);
            return int.TryParse(value, out int result) ? result : defaultValue;
        }

        public string[] GetScenarioProcessNames(string scenarioName)
        {
            string processNames = GetScenarioSetting(scenarioName, "PROCESS_NAMES", "qgis-bin,qgis");
            return processNames.Split(',', StringSplitOptions.RemoveEmptyEntries)
                             .Select(p => p.Trim())
                             .ToArray();
        }

        public string GetScenarioZipPostfix(string scenarioName)
        {
            return GetScenarioSetting(scenarioName, "ZIP_POSTFIX", scenarioName);
        }

        public string[] GetAvailableScenarios()
        {
            return _scenarios.Keys.ToArray();
        }

        // Aktives Szenario Properties
        public string ActiveScenario => GetSetting("ACTIVE_SCENARIO", "QGIS_Default");
        public string ApplicationTitle => GetSetting("APPLICATION_TITLE", "QGIS Profile Backup & Restore Tool");
        
        public string ActiveSourcePath => GetScenarioSetting(ActiveScenario, "SOURCE_PATH", 
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QGIS", "QGIS3", "profiles"));
        
        public string ActiveTargetShare => GetScenarioSetting(ActiveScenario, "TARGET_SHARE", @"\\SERVER\Freigabe\QGISProfiles");
        
        public string[] ActiveProcessNames => GetScenarioProcessNames(ActiveScenario);
        
        public string ActiveScenarioTitle => GetScenarioSetting(ActiveScenario, "SCENARIO_TITLE", "Default Scenario");
        
        public string ActiveZipPostfix => GetScenarioZipPostfix(ActiveScenario);

        // Sicherheitseinstellungen
        public int ProcessKillDelayMs => GetIntSetting("PROCESS_KILL_DELAY_MS", 2000);
        public bool ShowKillWarning => GetBoolSetting("SHOW_KILL_WARNING", true);
        public bool ShowAllBackups => GetBoolSetting("SHOW_ALL_BACKUPS", false);

        // Legacy Properties für Rückwärtskompatibilität
        public string DefaultShare => ActiveTargetShare;
        public bool DebugMode => GetBoolSetting("DEBUG_MODE");
        public string LogLevel => GetSetting("LOG_LEVEL");
        public int BackupRetentionDays => GetIntSetting("BACKUP_RETENTION_DAYS", 30);
        public bool AutoBackupEnabled => GetBoolSetting("AUTO_BACKUP_ENABLED", true);
    }
}