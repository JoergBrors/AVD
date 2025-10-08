using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QGISProfileTool
{
    public partial class ProgressForm : Form
    {
        private readonly System.Windows.Forms.Timer _animationTimer;
        private int _animationStep = 0;
        private readonly string[] _animationFrames = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
        private CancellationTokenSource? _cancellationTokenSource;
        
        public ProgressForm()
        {
            InitializeComponent();
            _animationTimer = new System.Windows.Forms.Timer();
            _animationTimer.Interval = 100; // 100ms für flüssige Animation
            _animationTimer.Tick += AnimationTimer_Tick;
        }

        private void InitializeComponent()
        {
            this.SuspendLayout();
            
            // Form Eigenschaften
            this.Text = "QGIS Backup/Restore - Progress";
            this.Size = new Size(500, 200);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ControlBox = false;
            this.TopMost = true;
            
            // Animation Label
            var lblAnimation = new Label
            {
                Name = "lblAnimation",
                Text = "⠋",
                Font = new Font("Consolas", 24F, FontStyle.Bold),
                ForeColor = Color.DodgerBlue,
                Location = new Point(20, 30),
                Size = new Size(50, 40),
                TextAlign = ContentAlignment.MiddleCenter
            };

            // Status Label
            var lblStatus = new Label
            {
                Name = "lblStatus",
                Text = "Initialisiere...",
                Font = new Font("Segoe UI", 11F, FontStyle.Regular),
                Location = new Point(80, 35),
                Size = new Size(380, 25),
                TextAlign = ContentAlignment.MiddleLeft
            };

            // Detail Label
            var lblDetail = new Label
            {
                Name = "lblDetail",
                Text = "",
                Font = new Font("Segoe UI", 9F, FontStyle.Regular),
                ForeColor = Color.Gray,
                Location = new Point(80, 60),
                Size = new Size(380, 20),
                TextAlign = ContentAlignment.MiddleLeft
            };

            // Progress Bar
            var progressBar = new ProgressBar
            {
                Name = "progressBar",
                Location = new Point(20, 100),
                Size = new Size(440, 23),
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 50
            };

            // Cancel Button
            var btnCancel = new Button
            {
                Name = "btnCancel",
                Text = "Abbrechen",
                Location = new Point(385, 135),
                Size = new Size(75, 25),
                DialogResult = DialogResult.Cancel
            };
            btnCancel.Click += BtnCancel_Click;

            // Controls zur Form hinzufügen
            this.Controls.AddRange(new Control[] {
                lblAnimation, lblStatus, lblDetail, progressBar, btnCancel
            });

            this.ResumeLayout(false);
        }

        public void SetCancellationTokenSource(CancellationTokenSource cancellationTokenSource)
        {
            _cancellationTokenSource = cancellationTokenSource;
        }

        public void UpdateStatus(string status, string detail = "")
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => UpdateStatus(status, detail)));
                return;
            }

            var lblStatus = this.Controls["lblStatus"] as Label;
            var lblDetail = this.Controls["lblDetail"] as Label;

            if (lblStatus != null)
                lblStatus.Text = status;
            
            if (lblDetail != null)
                lblDetail.Text = detail;
        }

        public void SetProgressMode(bool determinate, int value = 0, int maximum = 100)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => SetProgressMode(determinate, value, maximum)));
                return;
            }

            var progressBar = this.Controls["progressBar"] as ProgressBar;
            if (progressBar != null)
            {
                if (determinate)
                {
                    progressBar.Style = ProgressBarStyle.Continuous;
                    progressBar.Maximum = maximum;
                    progressBar.Value = Math.Min(value, maximum);
                }
                else
                {
                    progressBar.Style = ProgressBarStyle.Marquee;
                }
            }
        }

        public void StartAnimation()
        {
            _animationTimer.Start();
        }

        public void StopAnimation()
        {
            _animationTimer.Stop();
        }

        private void AnimationTimer_Tick(object? sender, EventArgs e)
        {
            var lblAnimation = this.Controls["lblAnimation"] as Label;
            if (lblAnimation != null)
            {
                _animationStep = (_animationStep + 1) % _animationFrames.Length;
                lblAnimation.Text = _animationFrames[_animationStep];
            }
        }

        private void BtnCancel_Click(object? sender, EventArgs e)
        {
            _cancellationTokenSource?.Cancel();
            this.DialogResult = DialogResult.Cancel;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            StopAnimation();
            base.OnFormClosing(e);
        }

        // Static Helper-Methode für einfache Verwendung
        public static async Task<T> ShowProgressAsync<T>(
            IWin32Window parent,
            string title,
            Func<IProgress<ProgressInfo>, CancellationToken, Task<T>> operation)
        {
            using var progressForm = new ProgressForm();
            progressForm.Text = title;
            
            // Zentriere den Dialog relativ zur Hauptanwendung
            if (parent is Form parentForm)
            {
                progressForm.StartPosition = FormStartPosition.Manual;
                int x = parentForm.Location.X + (parentForm.Width - progressForm.Width) / 2;
                int y = parentForm.Location.Y + (parentForm.Height - progressForm.Height) / 2;
                
                // Stelle sicher, dass der Dialog auf dem Bildschirm bleibt
                var screen = Screen.FromControl(parentForm);
                x = Math.Max(screen.WorkingArea.Left, Math.Min(x, screen.WorkingArea.Right - progressForm.Width));
                y = Math.Max(screen.WorkingArea.Top, Math.Min(y, screen.WorkingArea.Bottom - progressForm.Height));
                
                progressForm.Location = new Point(x, y);
            }
            
            using var cts = new CancellationTokenSource();
            progressForm.SetCancellationTokenSource(cts);

            var progress = new Progress<ProgressInfo>(info =>
            {
                progressForm.UpdateStatus(info.Status, info.Detail);
                if (info.IsIndeterminate)
                {
                    progressForm.SetProgressMode(false);
                }
                else
                {
                    progressForm.SetProgressMode(true, info.Current, info.Maximum);
                }
            });

            progressForm.StartAnimation();

            var operationTask = Task.Run(() => operation(progress, cts.Token), cts.Token);
            
            progressForm.Show(parent);

            try
            {
                var result = await operationTask;
                progressForm.DialogResult = DialogResult.OK;
                return result;
            }
            catch (OperationCanceledException)
            {
                progressForm.DialogResult = DialogResult.Cancel;
                throw;
            }
            finally
            {
                progressForm.StopAnimation();
                progressForm.Close();
            }
        }
    }

    public class ProgressInfo
    {
        public string Status { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public bool IsIndeterminate { get; set; } = true;
        public int Current { get; set; } = 0;
        public int Maximum { get; set; } = 100;

        public static ProgressInfo Indeterminate(string status, string detail = "")
        {
            return new ProgressInfo { Status = status, Detail = detail, IsIndeterminate = true };
        }

        public static ProgressInfo Determinate(string status, int current, int maximum, string detail = "")
        {
            return new ProgressInfo 
            { 
                Status = status, 
                Detail = detail, 
                IsIndeterminate = false, 
                Current = current, 
                Maximum = maximum 
            };
        }
    }
}