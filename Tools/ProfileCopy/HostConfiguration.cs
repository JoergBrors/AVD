using System;
using System.Collections.Generic;
using System.IO;

namespace QGISProfileTool
{
    public class HostConfiguration
    {
        private readonly Dictionary<string, string> _settings;
        private static HostConfiguration _instance;
        private static readonly object _lock = new object();

        private HostConfiguration()
        {
            _settings = new Dictionary<string, string>();
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
            _settings["DEFAULT_SHARE"] = @"\\SERVER\Freigabe\QGISProfiles";
            _settings["DEBUG_MODE"] = "false";
            _settings["LOG_LEVEL"] = "Info";
            _settings["BACKUP_RETENTION_DAYS"] = "30";
            _settings["AUTO_BACKUP_ENABLED"] = "true";

            if (File.Exists(configPath))
            {
                try
                {
                    foreach (string line in File.ReadAllLines(configPath))
                    {
                        if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#"))
                            continue;

                        var parts = line.Split('=', 2);
                        if (parts.Length == 2)
                        {
                            string key = parts[0].Trim();
                            string value = parts[1].Trim();
                            _settings[key] = value;
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

        public string GetSetting(string key, string defaultValue = "")
        {
            return _settings.TryGetValue(key, out string value) ? value : defaultValue;
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

        // Convenience Properties
        public string DefaultShare => GetSetting("DEFAULT_SHARE");
        public bool DebugMode => GetBoolSetting("DEBUG_MODE");
        public string LogLevel => GetSetting("LOG_LEVEL");
        public int BackupRetentionDays => GetIntSetting("BACKUP_RETENTION_DAYS", 30);
        public bool AutoBackupEnabled => GetBoolSetting("AUTO_BACKUP_ENABLED", true);
    }
}