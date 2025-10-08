using System.Diagnostics;
using System.IO.Compression;

namespace QGISProfileTool;

public partial class Form1 : Form
{
    private readonly string _userName;
    private readonly string _localProfile;
    private readonly HostConfiguration _config;

    public Form1()
    {
        InitializeComponent();
        _config = HostConfiguration.Instance;
        _userName = Environment.UserName;
        _localProfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
            "QGIS", "QGIS3", "profiles");
        
        InitializeUI();
    }

    private void InitializeUI()
    {
        this.Text = "QGIS Profile Backup & Restore Tool";
        this.Size = new Size(820, 610);
        this.StartPosition = FormStartPosition.CenterScreen;

        // Fileshare Label und TextBox
        var lblShare = new Label
        {
            Text = "Fileshare (Root):",
            Location = new Point(20, 20),
            Size = new Size(120, 23)
        };

        var txtShare = new TextBox
        {
            Name = "txtShare",
            Location = new Point(150, 18),
            Size = new Size(540, 22),
            Text = _config.DefaultShare
        };

        // Lokales Profil Label und TextBox
        var lblLocal = new Label
        {
            Text = "Lokales QGIS-Profil:",
            Location = new Point(20, 55),
            Size = new Size(120, 23)
        };

        var txtLocal = new TextBox
        {
            Name = "txtLocal",
            Location = new Point(150, 53),
            Size = new Size(540, 22),
            Text = _localProfile
        };

        // Version Label und TextBox
        var lblVersion = new Label
        {
            Text = "Version:",
            Location = new Point(20, 90),
            Size = new Size(60, 23)
        };

        var txtVersion = new TextBox
        {
            Name = "txtVersion",
            Location = new Point(150, 88),
            Size = new Size(250, 22)
        };

        // Backup Button
        var btnBackup = new Button
        {
            Name = "btnBackup",
            Text = "Backup erstellen",
            Location = new Point(420, 84),
            Size = new Size(160, 30)
        };
        btnBackup.Click += BtnBackup_Click;

        // Refresh Button
        var btnRefresh = new Button
        {
            Name = "btnRefresh",
            Text = "Liste aktualisieren",
            Location = new Point(600, 84),
            Size = new Size(160, 30)
        };
        btnRefresh.Click += BtnRefresh_Click;

        // DataGridView für Backups
        var grid = new DataGridView
        {
            Name = "grid",
            Location = new Point(20, 130),
            Size = new Size(740, 280),
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        InitializeGridColumns(grid);

        // Checkboxen
        var chkKillQGIS = new CheckBox
        {
            Name = "chkKillQGIS",
            Text = "QGIS vor Restore beenden",
            Location = new Point(20, 420),
            Checked = true,
            Size = new Size(200, 23)
        };

        var chkLocalSnap = new CheckBox
        {
            Name = "chkLocalSnap",
            Text = "Vorher lokale Sicherung erstellen",
            Location = new Point(220, 420),
            Checked = true,
            Size = new Size(230, 23)
        };

        // Restore Button
        var btnRestore = new Button
        {
            Name = "btnRestore",
            Text = "Ausgewählte Sicherung wiederherstellen",
            Location = new Point(460, 415),
            Size = new Size(300, 30)
        };
        btnRestore.Click += BtnRestore_Click;

        // Log TextBox
        var txtLog = new TextBox
        {
            Name = "txtLog",
            Location = new Point(20, 460),
            Size = new Size(740, 90),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical
        };

        // Alle Controls zur Form hinzufügen
        this.Controls.AddRange(new Control[] {
            lblShare, txtShare, lblLocal, txtLocal, lblVersion, txtVersion,
            btnBackup, btnRefresh, grid, chkKillQGIS, chkLocalSnap, btnRestore, txtLog
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
            Name = "GroesseBytes",
            HeaderText = "Größe (Bytes)",
            FillWeight = 15
        });

        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "FullNameHidden",
            HeaderText = "FullNameHidden",
            Visible = false
        });
    }

    private void BtnBackup_Click(object? sender, EventArgs e)
    {
        var txtLocal = this.Controls["txtLocal"] as TextBox;
        var txtShare = this.Controls["txtShare"] as TextBox;
        var txtVersion = this.Controls["txtVersion"] as TextBox;
        var txtLog = this.Controls["txtLog"] as TextBox;

        if (txtLocal != null && txtShare != null && txtVersion != null && txtLog != null)
        {
            DoBackup(txtLocal.Text, txtShare.Text, txtVersion.Text, txtLog);
            
            // Liste aktualisieren nach Backup
            var btnRefresh = this.Controls["btnRefresh"] as Button;
            btnRefresh?.PerformClick();
        }
    }

    private void BtnRefresh_Click(object? sender, EventArgs e)
    {
        var txtShare = this.Controls["txtShare"] as TextBox;
        var txtLog = this.Controls["txtLog"] as TextBox;
        var grid = this.Controls["grid"] as DataGridView;

        if (txtShare != null && txtLog != null && grid != null)
        {
            RefreshBackupList(txtShare.Text, txtLog, grid);
        }
    }

    private void BtnRestore_Click(object? sender, EventArgs e)
    {
        var grid = this.Controls["grid"] as DataGridView;
        var txtLocal = this.Controls["txtLocal"] as TextBox;
        var chkKillQGIS = this.Controls["chkKillQGIS"] as CheckBox;
        var chkLocalSnap = this.Controls["chkLocalSnap"] as CheckBox;
        var txtLog = this.Controls["txtLog"] as TextBox;

        if (grid == null || txtLocal == null || chkKillQGIS == null || chkLocalSnap == null || txtLog == null)
            return;

        if (grid.SelectedRows.Count == 0)
        {
            MessageBox.Show("Bitte eine Sicherung auswählen.", "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string? zipPath = grid.SelectedRows[0].Cells["FullNameHidden"].Value?.ToString();
        if (string.IsNullOrEmpty(zipPath))
        {
            MessageBox.Show("Pfad fehlt.", "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        DoRestore(zipPath, txtLocal.Text, chkKillQGIS.Checked, chkLocalSnap.Checked, txtLog);
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
}

public class BackupItem
{
    public required string FullName { get; set; }
    public required string Datei { get; set; }
    public required string Version { get; set; }
    public DateTime Zeitstempel { get; set; }
    public long GroesseBytes { get; set; }
}
