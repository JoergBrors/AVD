using System.Diagnostics;
using System.IO.Compression;

namespace QGISProfileTool;

public partial class Form1 : Form
{
    private readonly string _userName;
    private readonly string _localProfile;
    private readonly HostConfiguration _config;
    private readonly BackupService _backupService;
    private readonly FileLogger _logger;

    public Form1()
    {
        InitializeComponent();
        _config = HostConfiguration.Instance;
        _userName = Environment.UserName;
        _localProfile = _config.ActiveSourcePath;
        _backupService = new BackupService();
        _logger = FileLogger.Instance;
        
        _logger.LogInfo("Anwendung gestartet", $"Benutzer: {_userName} | Aktives Szenario: {_config.ActiveScenario}");
        _logger.CleanupOldLogs();
        
        InitializeUI();
        
        _logger.LogInfo("UI initialisiert", $"Log-Datei: {_logger.GetLogFilePath()}");
    }

    private void InitializeUI()
    {
        this.Text = _config.ApplicationTitle;
        this.Size = new Size(820, 675); // Höher für zusätzliche Checkbox
        this.StartPosition = FormStartPosition.CenterScreen;

        // Szenario Label und ComboBox
        var lblScenario = new Label
        {
            Text = "Szenario:",
            Location = new Point(20, 20),
            Size = new Size(120, 23)
        };

        var cmbScenario = new ComboBox
        {
            Name = "cmbScenario",
            Location = new Point(150, 18),
            Size = new Size(250, 22),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        
        // Fülle Szenarien
        var scenarios = _config.GetAvailableScenarios();
        cmbScenario.Items.AddRange(scenarios);
        cmbScenario.SelectedItem = _config.ActiveScenario;
        cmbScenario.SelectedIndexChanged += CmbScenario_SelectedIndexChanged;

        // Status Label für aktuelles Szenario
        var lblScenarioInfo = new Label
        {
            Name = "lblScenarioInfo", 
            Text = _config.ActiveScenarioTitle,
            Location = new Point(420, 22),
            Size = new Size(270, 18),
            ForeColor = Color.DarkBlue,
            Font = new Font("Segoe UI", 8F, FontStyle.Italic)
        };

        // Fileshare Label und TextBox
        var lblShare = new Label
        {
            Text = "Fileshare (Root):",
            Location = new Point(20, 55),
            Size = new Size(120, 23)
        };

        var txtShare = new TextBox
        {
            Name = "txtShare",
            Location = new Point(150, 53),
            Size = new Size(540, 22),
            Text = _config.ActiveTargetShare
        };

        // Lokales Profil Label und TextBox
        var lblLocal = new Label
        {
            Text = "Lokales Profil:",
            Location = new Point(20, 90),
            Size = new Size(120, 23)
        };

        var txtLocal = new TextBox
        {
            Name = "txtLocal",
            Location = new Point(150, 88),
            Size = new Size(540, 22),
            Text = _localProfile
        };

        // Version Label und TextBox
        var lblVersion = new Label
        {
            Text = "Version:",
            Location = new Point(20, 125),
            Size = new Size(60, 23)
        };

        var txtVersion = new TextBox
        {
            Name = "txtVersion",
            Location = new Point(150, 123),
            Size = new Size(250, 22)
        };

        // Backup Button
        var btnBackup = new Button
        {
            Name = "btnBackup",
            Text = "Backup erstellen",
            Location = new Point(420, 119),
            Size = new Size(160, 30)
        };
        btnBackup.Click += BtnBackup_Click;

        // Refresh Button
        var btnRefresh = new Button
        {
            Name = "btnRefresh",
            Text = "Liste aktualisieren",
            Location = new Point(600, 119),
            Size = new Size(160, 30)
        };
        btnRefresh.Click += BtnRefresh_Click;

        // DataGridView für Backups
        var grid = new DataGridView
        {
            Name = "grid",
            Location = new Point(20, 165),
            Size = new Size(740, 280),
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        InitializeGridColumns(grid);

        // Show All Backups Checkbox (über den anderen Checkboxen)
        var chkShowAllBackups = new CheckBox
        {
            Name = "chkShowAllBackups",
            Text = "🔍 Alle Backups anzeigen (inkl. andere Szenarien)",
            Location = new Point(20, 450),
            Size = new Size(350, 20),
            Checked = _config.ShowAllBackups
        };
        chkShowAllBackups.CheckedChanged += ChkShowAllBackups_CheckedChanged;

        // Checkboxen
        var chkKillForBackup = new CheckBox
        {
            Name = "chkKillForBackup",
            Text = "Prozesse vor Backup beenden (⚠️ Datenverlust möglich)",
            Location = new Point(20, 475),
            Checked = false,
            Size = new Size(370, 23),
            ForeColor = Color.DarkRed
        };

        var chkKillQGIS = new CheckBox
        {
            Name = "chkKillQGIS",
            Text = "Prozesse vor Restore beenden",
            Location = new Point(20, 500),
            Checked = true,
            Size = new Size(220, 23)
        };

        var chkLocalSnap = new CheckBox
        {
            Name = "chkLocalSnap",
            Text = "Vorher lokale Sicherung erstellen",
            Location = new Point(250, 500),
            Checked = true,
            Size = new Size(230, 23)
        };

        // Restore Button
        var btnRestore = new Button
        {
            Name = "btnRestore",
            Text = "Ausgewählte Sicherung wiederherstellen",
            Location = new Point(490, 495),
            Size = new Size(270, 30)
        };
        btnRestore.Click += BtnRestore_Click;

        // Log TextBox (weiter nach unten verschoben)
        var txtLog = new TextBox
        {
            Name = "txtLog",
            Location = new Point(20, 535),
            Size = new Size(740, 130),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical
        };

        // Alle Controls zur Form hinzufügen
        this.Controls.AddRange(new Control[] {
            lblScenario, cmbScenario, lblScenarioInfo,
            lblShare, txtShare, lblLocal, txtLocal, lblVersion, txtVersion,
            btnBackup, btnRefresh, grid, chkKillForBackup, chkKillQGIS, chkLocalSnap, btnRestore, 
            chkShowAllBackups, txtLog
        });
    }

    private void InitializeGridColumns(DataGridView grid)
    {
        grid.Rows.Clear();
        grid.Columns.Clear();
        grid.AutoGenerateColumns = false;

        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Datei",
            HeaderText = "Datei",
            FillWeight = 40
        });

        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Version",
            HeaderText = "Version",
            FillWeight = 20
        });

        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Zeitstempel",
            HeaderText = "Zeitstempel",
            FillWeight = 25
        });

        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "GroesseMB",
            HeaderText = "Größe (MB)",
            FillWeight = 15
        });

        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "FullNameHidden",
            HeaderText = "FullNameHidden",
            Visible = false
        });
    }

    private async void BtnBackup_Click(object? sender, EventArgs e)
    {
        var txtLocal = this.Controls["txtLocal"] as TextBox;
        var txtShare = this.Controls["txtShare"] as TextBox;
        var txtVersion = this.Controls["txtVersion"] as TextBox;
        var txtLog = this.Controls["txtLog"] as TextBox;
        var chkKillForBackup = this.Controls["chkKillForBackup"] as CheckBox;

        if (txtLocal != null && txtShare != null && txtVersion != null && txtLog != null && chkKillForBackup != null)
        {
            bool killProcesses = chkKillForBackup.Checked;
            
            _logger.LogSession("BACKUP", "GESTARTET");
            _logger.LogInfo("Backup-Vorgang initiiert", 
                $"Quelle: {txtLocal.Text} | Ziel: {txtShare.Text} | Version: {txtVersion.Text} | Prozesse beenden: {killProcesses}");
            
            // Warnung anzeigen wenn Prozesse beendet werden sollen
            if (killProcesses && _config.ShowKillWarning)
            {
                var result = MessageBox.Show(
                    "⚠️ WARNUNG: Prozesse beenden vor Backup\n\n" +
                    $"Sie haben gewählt, laufende Prozesse ({string.Join(", ", _config.ActiveProcessNames)}) vor dem Backup zu beenden.\n\n" +
                    "⚠️ RISIKO - Dies kann zu DATENVERLUST führen wenn:\n" +
                    "• Ungespeicherte Änderungen in geöffneten Projekten existieren\n" +
                    "• Aktive Verarbeitungsprozesse laufen\n" +
                    "• Temporäre/Lock-Dateien nicht ordnungsgemäß geschlossen wurden\n" +
                    "• Plugins oder Extensions noch aktiv sind\n\n" +
                    "💡 EMPFEHLUNG:\n" +
                    "Speichern Sie alle Arbeiten manuell und schließen Sie die Anwendung normal, bevor Sie das Backup erstellen.\n\n" +
                    $"ℹ️ Sicherheitsdelay nach Prozess-Beendigung: {_config.ProcessKillDelayMs}ms\n\n" +
                    "Möchten Sie trotzdem fortfahren und die Prozesse zwangsweise beenden?",
                    "⚠️ Datenverlust-Warnung - Prozesse vor Backup beenden",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);

                if (result != DialogResult.Yes)
                {
                    txtLog.AppendText("Backup abgebrochen - Benutzer hat Prozess-Beendigung abgelehnt.\r\n");
                    _logger.LogWarning("Backup abgebrochen", "Benutzer lehnte Prozess-Beendigung ab");
                    return;
                }
                
                txtLog.AppendText($"⚠️ WARNUNG: Benutzer bestätigte zwangsweise Prozess-Beendigung vor Backup (Delay: {_config.ProcessKillDelayMs}ms).\r\n");
                _logger.LogWarning("Prozess-Beendigung bestätigt", $"Delay: {_config.ProcessKillDelayMs}ms | Prozesse: {string.Join(", ", _config.ActiveProcessNames)}");
            }

            await DoBackupAsync(txtLocal.Text, txtShare.Text, txtVersion.Text, killProcesses, txtLog);
            
            // Liste aktualisieren nach Backup
            var btnRefresh = this.Controls["btnRefresh"] as Button;
            btnRefresh?.PerformClick();
        }
    }

    private async void BtnRefresh_Click(object? sender, EventArgs e)
    {
        var txtShare = this.Controls["txtShare"] as TextBox;
        var txtLog = this.Controls["txtLog"] as TextBox;
        var grid = this.Controls["grid"] as DataGridView;

        if (txtShare != null && txtLog != null && grid != null)
        {
            await RefreshBackupListAsync(txtShare.Text, txtLog, grid);
        }
    }

    private async void BtnRestore_Click(object? sender, EventArgs e)
    {
        var txtLog = this.Controls["txtLog"] as TextBox;
        
        try
        {
            var grid = this.Controls["grid"] as DataGridView;
            var txtLocal = this.Controls["txtLocal"] as TextBox;
            var chkKillQGIS = this.Controls["chkKillQGIS"] as CheckBox;
            var chkLocalSnap = this.Controls["chkLocalSnap"] as CheckBox;

            if (grid == null || txtLocal == null || chkKillQGIS == null || chkLocalSnap == null || txtLog == null)
            {
                txtLog?.AppendText("FEHLER: UI-Elemente nicht gefunden.\r\n");
                return;
            }

            if (grid.SelectedRows.Count == 0)
            {
                MessageBox.Show("Bitte eine Sicherung auswählen.", "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            txtLog.AppendText($"Restore gestartet - Verarbeite ausgewählte Zeile...\r\n");

            // Sichere Grid-Zugriffe mit Exception Handling
            string? zipPath = null;
            string? fileName = "";
            
            try
            {
                zipPath = grid.SelectedRows[0].Cells["FullNameHidden"]?.Value?.ToString();
                fileName = grid.SelectedRows[0].Cells["Datei"]?.Value?.ToString() ?? "";
                txtLog.AppendText($"Ausgewählte Datei: '{fileName}', Pfad: '{zipPath}'\r\n");
            }
            catch (Exception ex)
            {
                txtLog.AppendText($"FEHLER beim Grid-Zugriff: {ex.Message}\r\n");
                MessageBox.Show($"Fehler beim Lesen der Grid-Daten: {ex.Message}", "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            
            if (string.IsNullOrEmpty(zipPath))
            {
                txtLog.AppendText("FEHLER: Backup-Pfad ist leer.\r\n");
                MessageBox.Show("Pfad fehlt.", "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Prüfe ob die gewählte Datei inkompatibel ist (rot eingefärbt)
            bool isIncompatible = false;
            try
            {
                isIncompatible = grid.SelectedRows[0].DefaultCellStyle.BackColor == System.Drawing.Color.LightCoral;
                txtLog.AppendText($"Kompatibilitätsprüfung: {(isIncompatible ? "INKOMPATIBEL" : "KOMPATIBEL")}\r\n");
            }
            catch (Exception ex)
            {
                txtLog.AppendText($"Warnung bei Kompatibilitätsprüfung: {ex.Message}\r\n");
            }

            if (isIncompatible)
            {
                var result = MessageBox.Show(
                    "⚠️ WARNUNG: Inkompatibles Backup ausgewählt!\n\n" +
                    $"Die gewählte Datei '{fileName}' gehört zu einem anderen Szenario.\n" +
                    "Das Restore könnte zu unerwarteten Problemen führen.\n\n" +
                    "Möchten Sie trotzdem fortfahren?\n\n" +
                    "💡 EMPFEHLUNG: Wählen Sie ein kompatibles Backup oder wechseln Sie zum passenden Szenario.",
                    "⚠️ Inkompatibles Backup - Bestätigung erforderlich",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);

                if (result != DialogResult.Yes)
                {
                    txtLog.AppendText($"Restore abgebrochen - Inkompatibles Backup '{fileName}' nicht bestätigt.\r\n");
                    return;
                }
                
                txtLog.AppendText($"⚠️ WARNUNG: Benutzer bestätigte Restore von inkompatiblem Backup '{fileName}'.\r\n");
            }

            txtLog.AppendText($"Starte Restore-Prozess für '{fileName}'...\r\n");
            _logger.LogSession("RESTORE", "GESTARTET");
            _logger.LogInfo("Restore initiiert", $"Datei: {fileName} | Pfad: {zipPath} | Ziel: {txtLocal.Text}");
            
            await DoRestoreAsync(zipPath, txtLocal.Text, chkKillQGIS.Checked, chkLocalSnap.Checked, txtLog);
        }
        catch (Exception ex)
        {
            string errorMsg = $"KRITISCHER FEHLER in BtnRestore_Click: {ex.Message}\r\nStackTrace: {ex.StackTrace}";
            txtLog?.AppendText($"{errorMsg}\r\n");
            _logger.LogError("Kritischer Restore-Fehler", ex, $"UI-Fehler in BtnRestore_Click");
            
            MessageBox.Show(
                $"Ein unerwarteter Fehler ist aufgetreten:\n\n{ex.Message}\n\nDetails wurden ins Log geschrieben.", 
                "Kritischer Fehler", 
                MessageBoxButtons.OK, 
                MessageBoxIcon.Error);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var btnRefresh = this.Controls["btnRefresh"] as Button;
        btnRefresh?.PerformClick();
    }

    private void DoBackup(string localPath, string shareRoot, string version, TextBox logBox)
    {
        if (!Directory.Exists(localPath))
        {
            MessageBox.Show("Lokales Profil nicht gefunden.", "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            MessageBox.Show("Bitte Version angeben.", "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!Directory.Exists(shareRoot))
        {
            MessageBox.Show("Share nicht erreichbar.", "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string userFolder = GetUserSharePath(shareRoot, _userName);
        EnsureDirectory(userFolder);

        string zipName = BuildZipName(version);
        string zipTemp = Path.Combine(Path.GetTempPath(), zipName);
        string zipDest = Path.Combine(userFolder, zipName);

        try
        {
            if (File.Exists(zipTemp))
                File.Delete(zipTemp);

            logBox.AppendText($"Backup → {zipTemp}\r\n");
            ZipFile.CreateFromDirectory(localPath, zipTemp);
            File.Copy(zipTemp, zipDest, true);
            logBox.AppendText($"Kopiert nach {zipDest}\r\n");
            MessageBox.Show($"Backup erfolgreich: {zipDest}", "Erfolg", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Fehler: {ex.Message}", "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (File.Exists(zipTemp))
                File.Delete(zipTemp);
        }
    }

    private void RefreshBackupList(string shareRoot, TextBox logBox, DataGridView grid)
    {
        try
        {
            logBox.AppendText("Aktualisiere Liste …\r\n");
            var items = GetBackups(shareRoot, logBox);

            grid.SuspendLayout();
            InitializeGridColumns(grid);
            
            foreach (var item in items.OrderByDescending(x => x.Zeitstempel))
            {
                string zeitString = item.Zeitstempel.ToString("yyyy-MM-dd HH:mm");
                grid.Rows.Add(item.Datei, item.Version, zeitString, item.GroesseBytes, item.FullName);
            }
            
            grid.ResumeLayout();
            grid.Refresh();

            logBox.AppendText($"Gefundene Sicherungen: {items.Count}\r\n");
            if (items.Count == 0)
            {
                logBox.AppendText("Hinweis: Keine ZIPs gefunden.\r\n");
            }
        }
        catch (Exception ex)
        {
            string msg = $"Fehler beim Laden: {ex.Message}";
            logBox.AppendText(msg + "\r\n");
            MessageBox.Show(msg, "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void DoRestore(string zipPath, string localPath, bool killQGIS, bool makeLocalBackup, TextBox logBox)
    {
        if (!File.Exists(zipPath))
        {
            MessageBox.Show("Sicherung nicht gefunden.", "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        try
        {
            if (killQGIS)
            {
                StopQGIS();
                logBox.AppendText("QGIS beendet.\r\n");
            }

            if (makeLocalBackup)
            {
                string? backupPath = SafeBackupCurrentLocal(localPath, Path.Combine(Path.GetTempPath(), $"QGISBeforeRestore_{FormatTimeStamp()}"));
                if (!string.IsNullOrEmpty(backupPath))
                    logBox.AppendText($"Lokale Sicherung: {backupPath}\r\n");
            }

            if (Directory.Exists(localPath))
            {
                Directory.Delete(localPath, true);
            }

            EnsureDirectory(localPath);
            ZipFile.ExtractToDirectory(zipPath, localPath);
            
            MessageBox.Show("Restore erfolgreich.", "Erfolg", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Fehler: {ex.Message}", "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private List<BackupItem> GetBackups(string shareRoot, TextBox logBox)
    {
        var results = new List<BackupItem>();
        
        if (!Directory.Exists(shareRoot))
        {
            logBox.AppendText("Share nicht erreichbar.\r\n");
            return results;
        }

        string userFolder = GetUserSharePath(shareRoot, _userName);
        if (!Directory.Exists(userFolder))
        {
            EnsureDirectory(userFolder);
            return results;
        }

        var files = Directory.GetFiles(userFolder, "*.zip");
        
        foreach (string file in files)
        {
            var fileInfo = new FileInfo(file);
            var match = System.Text.RegularExpressions.Regex.Match(fileInfo.Name, @"^QGISProfiles_(.+?)_(\d{8}-\d{4})\.zip$");
            
            string version = "(unbekannt)";
            DateTime zeitstempel = fileInfo.LastWriteTime;
            
            if (match.Success)
            {
                version = match.Groups[1].Value;
                if (DateTime.TryParseExact(match.Groups[2].Value, "yyyyMMdd-HHmm", null, System.Globalization.DateTimeStyles.None, out DateTime dt))
                {
                    zeitstempel = dt;
                }
            }

            results.Add(new BackupItem
            {
                FullName = file,
                Datei = fileInfo.Name,
                Version = version,
                Zeitstempel = zeitstempel,
                GroesseBytes = fileInfo.Length
            });
        }

        return results;
    }

    private string GetUserSharePath(string share, string user)
    {
        return Path.Combine(share, user);
    }

    private void EnsureDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
    }

    private bool IsQGISRunning()
    {
        return Process.GetProcessesByName("qgis").Length > 0;
    }

    private void StopQGIS()
    {
        var processes = Process.GetProcessesByName("qgis");
        foreach (var process in processes)
        {
            try
            {
                process.Kill();
                process.WaitForExit(5000); // 5 Sekunden warten
            }
            catch { /* Prozess bereits beendet oder Zugriff verweigert */ }
        }
    }

    private string FormatTimeStamp()
    {
        return DateTime.Now.ToString("yyyyMMdd-HHmm");
    }

    private string BuildZipName(string version)
    {
        return $"QGISProfiles_{version}_{FormatTimeStamp()}.zip";
    }

    private string? SafeBackupCurrentLocal(string localPath, string destFolder)
    {
        if (!Directory.Exists(localPath))
            return null;

        EnsureDirectory(destFolder);
        string zipPath = Path.Combine(destFolder, $"BeforeRestore_{FormatTimeStamp()}.zip");
        ZipFile.CreateFromDirectory(localPath, zipPath);
        return zipPath;
    }

    private void CmbScenario_SelectedIndexChanged(object? sender, EventArgs e)
    {
        var cmbScenario = sender as ComboBox;
        if (cmbScenario?.SelectedItem != null)
        {
            string selectedScenario = cmbScenario.SelectedItem.ToString()!;
            
            _logger.LogInfo("Szenario gewechselt", $"Neues Szenario: {selectedScenario}");
            
            // Aktualisiere die Pfade basierend auf dem gewählten Szenario
            var txtShare = this.Controls["txtShare"] as TextBox;
            var txtLocal = this.Controls["txtLocal"] as TextBox;
            var lblScenarioInfo = this.Controls["lblScenarioInfo"] as Label;
            var txtLog = this.Controls["txtLog"] as TextBox;

            string sourcePath = _config.GetScenarioSetting(selectedScenario, "SOURCE_PATH", "");
            string targetShare = _config.GetScenarioSetting(selectedScenario, "TARGET_SHARE", "");
            string[] processes = _config.GetScenarioProcessNames(selectedScenario);

            if (txtShare != null)
                txtShare.Text = targetShare;
                
            if (txtLocal != null)
                txtLocal.Text = sourcePath;
                
            if (lblScenarioInfo != null)
                lblScenarioInfo.Text = _config.GetScenarioSetting(selectedScenario, "SCENARIO_TITLE", selectedScenario);
                
            if (txtLog != null)
            {
                txtLog.AppendText($"Szenario gewechselt zu: {selectedScenario}\r\n");
                txtLog.AppendText($"Prozesse: {string.Join(", ", processes)}\r\n");
            }
            
            _logger.LogInfo("Szenario-Konfiguration geladen", 
                $"Quelle: {sourcePath} | Ziel: {targetShare} | Prozesse: {string.Join(", ", processes)}");
            
            // Aktualisiere auch die Backup-Liste für das neue Szenario
            var btnRefresh = this.Controls["btnRefresh"] as Button;
            btnRefresh?.PerformClick();
        }
    }

    private void ChkShowAllBackups_CheckedChanged(object? sender, EventArgs e)
    {
        // Liste automatisch aktualisieren wenn sich die Anzeige-Option ändert
        var btnRefresh = this.Controls["btnRefresh"] as Button;
        btnRefresh?.PerformClick();
    }

    // Neue asynchrone Methoden mit Fortschrittsdialog
    private async Task DoBackupAsync(string localPath, string shareRoot, string version, bool killProcesses, TextBox logBox)
    {
        try
        {
            _logger.LogOperation("BACKUP", "START", $"Quelle: {localPath} | Ziel: {shareRoot} | Version: {version}");
            
            string result = await ProgressForm.ShowProgressAsync(
                this,
                "Backup erstellen",
                async (progress, cancellationToken) =>
                {
                    return await _backupService.CreateBackupAsync(localPath, shareRoot, version, killProcesses, progress, cancellationToken);
                });

            logBox.AppendText($"Backup erfolgreich erstellt: {result}\r\n");
            _logger.LogOperation("BACKUP", "ERFOLG", $"Datei erstellt: {result}");
            _logger.LogSession("BACKUP", "BEENDET");
            
            MessageBox.Show($"Backup erfolgreich: {result}", "Erfolg", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            logBox.AppendText("Backup abgebrochen.\r\n");
            _logger.LogWarning("Backup abgebrochen", "Benutzer-Abbruch");
            _logger.LogSession("BACKUP", "ABGEBROCHEN");
        }
        catch (Exception ex)
        {
            logBox.AppendText($"Backup-Fehler: {ex.Message}\r\n");
            _logger.LogError("Backup-Fehler", ex, $"Pfad: {localPath} | Version: {version}");
            _logger.LogSession("BACKUP", "FEHLERHAFT");
            
            MessageBox.Show($"Fehler beim Backup: {ex.Message}", "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RefreshBackupListAsync(string shareRoot, TextBox logBox, DataGridView grid)
    {
        try
        {
            // Prüfe ob "Alle Backups anzeigen" aktiviert ist
            var chkShowAllBackups = this.Controls["chkShowAllBackups"] as CheckBox;
            bool showAllBackups = chkShowAllBackups?.Checked ?? false;
            
            // Temporär die Einstellung überschreiben
            var originalShowAll = _config.ShowAllBackups;
            if (showAllBackups != originalShowAll)
            {
                // Hier würde normalerweise eine temporäre Override-Logik stehen
                // Für Einfachheit verwenden wir den BackupService mit angepasster Logik
            }

            var items = await ProgressForm.ShowProgressAsync(
                this,
                showAllBackups ? "Lade alle Backups" : "Lade Backup-Liste",
                async (progress, cancellationToken) =>
                {
                    return await _backupService.GetBackupsAsync(shareRoot, _config.ActiveScenario, progress, cancellationToken, showAllBackups);
                });

            grid.SuspendLayout();
            InitializeGridColumns(grid);
            
            foreach (var item in items.OrderByDescending(x => x.Zeitstempel))
            {
                string zeitString = item.Zeitstempel.ToString("yyyy-MM-dd HH:mm");
                string displayName = item.Datei;
                
                // Füge Warnung hinzu wenn nicht kompatibel
                if (!item.IsCompatibleWithCurrentScenario && !string.IsNullOrEmpty(item.WarningMessage))
                {
                    displayName = "⚠️ " + displayName;
                }
                
                var rowIndex = grid.Rows.Add(displayName, item.Version, zeitString, item.GroesseMB, item.FullName);
                
                // Färbe inkompatible Zeilen rot ein
                if (!item.IsCompatibleWithCurrentScenario)
                {
                    grid.Rows[rowIndex].DefaultCellStyle.BackColor = System.Drawing.Color.LightCoral;
                    grid.Rows[rowIndex].DefaultCellStyle.ForeColor = System.Drawing.Color.DarkRed;
                }
            }
            
            grid.ResumeLayout();
            grid.Refresh();

            logBox.AppendText($"Gefundene Sicherungen: {items.Count}\r\n");
            if (items.Count == 0)
            {
                logBox.AppendText("Hinweis: Keine ZIPs gefunden.\r\n");
            }
        }
        catch (OperationCanceledException)
        {
            logBox.AppendText("Laden abgebrochen.\r\n");
        }
        catch (Exception ex)
        {
            string msg = $"Fehler beim Laden: {ex.Message}";
            logBox.AppendText(msg + "\r\n");
            MessageBox.Show(msg, "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task DoRestoreAsync(string zipPath, string localPath, bool killQGIS, bool makeLocalBackup, TextBox logBox)
    {
        try
        {
            _logger.LogOperation("RESTORE", "START", $"ZIP: {zipPath} | Ziel: {localPath} | Prozesse beenden: {killQGIS} | Backup: {makeLocalBackup}");
            
            await ProgressForm.ShowProgressAsync(
                this,
                "Profile wiederherstellen",
                async (progress, cancellationToken) =>
                {
                    await _backupService.RestoreBackupAsync(zipPath, localPath, killQGIS, makeLocalBackup, progress, cancellationToken);
                    return true;
                });

            logBox.AppendText("Restore erfolgreich abgeschlossen.\r\n");
            _logger.LogOperation("RESTORE", "ERFOLG", $"Profile wiederhergestellt nach: {localPath}");
            _logger.LogSession("RESTORE", "BEENDET");
            
            MessageBox.Show("Restore erfolgreich.", "Erfolg", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            logBox.AppendText("Restore abgebrochen.\r\n");
            _logger.LogWarning("Restore abgebrochen", "Benutzer-Abbruch");
            _logger.LogSession("RESTORE", "ABGEBROCHEN");
        }
        catch (Exception ex)
        {
            logBox.AppendText($"Restore-Fehler: {ex.Message}\r\n");
            _logger.LogError("Restore-Fehler", ex, $"ZIP: {zipPath} | Ziel: {localPath}");
            _logger.LogSession("RESTORE", "FEHLERHAFT");
            
            MessageBox.Show($"Fehler beim Restore: {ex.Message}", "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

public class BackupItem
{
    public required string FullName { get; set; }
    public required string Datei { get; set; }
    public required string Version { get; set; }
    public string Scenario { get; set; } = "";
    public DateTime Zeitstempel { get; set; }
    public long GroesseBytes { get; set; }
    public bool IsCompatibleWithCurrentScenario { get; set; } = true;
    public string WarningMessage { get; set; } = "";
    
    public string GroesseMB => (GroesseBytes / (1024.0 * 1024.0)).ToString("F2");
}
