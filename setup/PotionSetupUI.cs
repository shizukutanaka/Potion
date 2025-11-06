using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;

namespace Potion.Setup
{
    public partial class MainForm : Form
    {
        private readonly TextBox txtInstallPath;
        private readonly CheckBox chkCreateDesktopShortcut;
        private readonly CheckBox chkStartService;
        private readonly Button btnInstall;
        private readonly Button btnCancel;
        private readonly ProgressBar progressBar;
        private readonly Label lblStatus;

        public MainForm()
        {
            InitializeComponent();
            SetupControls();
            LoadSettings();
        }

        private void SetupControls()
        {
            this.Text = "Potion Self-Healing Service - インストール";
            this.Size = new Size(600, 400);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;

            var lblTitle = new Label
            {
                Text = "Potion Self-Healing Service インストール",
                Font = new Font("Segoe UI", 16, FontStyle.Bold),
                Location = new Point(20, 20),
                AutoSize = true
            };

            var lblDescription = new Label
            {
                Text = "Windowsシステムの自動診断・修復サービスをインストールします。",
                Location = new Point(20, 60),
                Size = new Size(540, 40)
            };

            var lblInstallPath = new Label
            {
                Text = "インストール先:",
                Location = new Point(20, 120),
                AutoSize = true
            };

            txtInstallPath = new TextBox
            {
                Text = GetDefaultInstallPath(),
                Location = new Point(120, 117),
                Size = new Size(400, 25)
            };

            var btnBrowse = new Button
            {
                Text = "参照...",
                Location = new Point(530, 115),
                Size = new Size(50, 25)
            };
            btnBrowse.Click += BtnBrowse_Click;

            chkCreateDesktopShortcut = new CheckBox
            {
                Text = "デスクトップショートカットを作成",
                Location = new Point(20, 160),
                Checked = true
            };

            chkStartService = new CheckBox
            {
                Text = "インストール後にサービスを開始",
                Location = new Point(20, 190),
                Checked = true
            };

            progressBar = new ProgressBar
            {
                Location = new Point(20, 250),
                Size = new Size(540, 25),
                Visible = false
            };

            lblStatus = new Label
            {
                Location = new Point(20, 280),
                Size = new Size(540, 40),
                Text = "インストールの準備ができています。"
            };

            btnInstall = new Button
            {
                Text = "インストール",
                Location = new Point(380, 330),
                Size = new Size(100, 35),
                DialogResult = DialogResult.OK
            };
            btnInstall.Click += BtnInstall_Click;

            btnCancel = new Button
            {
                Text = "キャンセル",
                Location = new Point(490, 330),
                Size = new Size(100, 35),
                DialogResult = DialogResult.Cancel
            };

            this.Controls.AddRange(new Control[]
            {
                lblTitle, lblDescription, lblInstallPath, txtInstallPath, btnBrowse,
                chkCreateDesktopShortcut, chkStartService, progressBar, lblStatus,
                btnInstall, btnCancel
            });

            this.AcceptButton = btnInstall;
            this.CancelButton = btnCancel;
        }

        private string GetDefaultInstallPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Potion");
        }

        private void LoadSettings()
        {
            try
            {
                var settingsPath = Path.Combine(Path.GetTempPath(), "PotionSetup.json");
                if (File.Exists(settingsPath))
                {
                    var settings = JsonSerializer.Deserialize<SetupSettings>(File.ReadAllText(settingsPath));
                    if (settings != null)
                    {
                        txtInstallPath.Text = settings.InstallPath ?? GetDefaultInstallPath();
                        chkCreateDesktopShortcut.Checked = settings.CreateDesktopShortcut;
                        chkStartService.Checked = settings.StartService;
                    }
                }
            }
            catch (Exception ex)
            {
                // 設定読み込みエラーは無視
                Console.WriteLine($"設定読み込みエラー: {ex.Message}");
            }
        }

        private void SaveSettings()
        {
            try
            {
                var settings = new SetupSettings
                {
                    InstallPath = txtInstallPath.Text,
                    CreateDesktopShortcut = chkCreateDesktopShortcut.Checked,
                    StartService = chkStartService.Checked
                };

                var settingsPath = Path.Combine(Path.GetTempPath(), "PotionSetup.json");
                File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                // 設定保存エラーは無視
                Console.WriteLine($"設定保存エラー: {ex.Message}");
            }
        }

        private void BtnBrowse_Click(object sender, EventArgs e)
        {
            using var folderBrowser = new FolderBrowserDialog
            {
                Description = "インストール先を選択してください",
                SelectedPath = txtInstallPath.Text,
                ShowNewFolderButton = true
            };

            if (folderBrowser.ShowDialog() == DialogResult.OK)
            {
                txtInstallPath.Text = folderBrowser.SelectedPath;
            }
        }

        private async void BtnInstall_Click(object sender, EventArgs e)
        {
            if (!ValidateInputs())
            {
                return;
            }

            SaveSettings();
            await PerformInstallationAsync();
        }

        private bool ValidateInputs()
        {
            if (string.IsNullOrWhiteSpace(txtInstallPath.Text))
            {
                MessageBox.Show("インストール先を指定してください。", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            try
            {
                var installPath = txtInstallPath.Text;
                var directory = new DirectoryInfo(installPath);

                // 親ディレクトリが存在するかチェック
                if (directory.Parent != null && !directory.Parent.Exists)
                {
                    MessageBox.Show("親ディレクトリが存在しません。", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
            }
            catch
            {
                MessageBox.Show("無効なインストールパスです。", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            return true;
        }

        private async Task PerformInstallationAsync()
        {
            btnInstall.Enabled = false;
            btnCancel.Enabled = false;
            progressBar.Visible = true;

            try
            {
                var installer = new PotionInstaller
                {
                    InstallPath = txtInstallPath.Text,
                    CreateDesktopShortcut = chkCreateDesktopShortcut.Checked,
                    StartService = chkStartService.Checked
                };

                installer.ProgressChanged += (sender, progress) =>
                {
                    progressBar.Value = Math.Min(100, Math.Max(0, progress));
                    lblStatus.Text = $"インストール中... {progress}%";
                    Application.DoEvents();
                };

                var success = await installer.InstallAsync();

                if (success)
                {
                    lblStatus.Text = "インストールが完了しました！";
                    MessageBox.Show("Potionのインストールが完了しました。", "インストール完了", MessageBoxButtons.OK, MessageBoxIcon.Information);

                    // デスクトップショートカット作成
                    if (chkCreateDesktopShortcut.Checked)
                    {
                        CreateDesktopShortcut();
                    }

                    this.DialogResult = DialogResult.OK;
                    this.Close();
                }
                else
                {
                    lblStatus.Text = "インストールに失敗しました。";
                    MessageBox.Show("インストールに失敗しました。ログファイルを確認してください。", "インストールエラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                lblStatus.Text = $"エラー: {ex.Message}";
                MessageBox.Show($"インストール中にエラーが発生しました: {ex.Message}", "インストールエラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnInstall.Enabled = true;
                btnCancel.Enabled = true;
                progressBar.Visible = false;
            }
        }

        private void CreateDesktopShortcut()
        {
            try
            {
                var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var shortcutPath = Path.Combine(desktopPath, "Potion Management Console.lnk");

                var shell = new IWshRuntimeLibrary.WshShell();
                var shortcut = (IWshRuntimeLibrary.IWshShortcut)shell.CreateShortcut(shortcutPath);

                shortcut.TargetPath = Path.Combine(txtInstallPath.Text, "Potion.ConfigTool.exe");
                shortcut.WorkingDirectory = txtInstallPath.Text;
                shortcut.Description = "Potion設定管理ツール";
                shortcut.IconLocation = Path.Combine(txtInstallPath.Text, "Potion.Service.exe,0");

                shortcut.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"デスクトップショートカットの作成に失敗しました: {ex.Message}", "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (progressBar.Visible)
            {
                if (MessageBox.Show("インストールをキャンセルしますか？", "確認", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }

            base.OnFormClosing(e);
        }
    }

    public class SetupSettings
    {
        public string? InstallPath { get; set; }
        public bool CreateDesktopShortcut { get; set; } = true;
        public bool StartService { get; set; } = true;
    }

    public class PotionInstaller
    {
        public string InstallPath { get; set; } = string.Empty;
        public bool CreateDesktopShortcut { get; set; } = true;
        public bool StartService { get; set; } = true;

        public event Action<int>? ProgressChanged;

        public async Task<bool> InstallAsync()
        {
            try
            {
                ReportProgress(10, "システム要件チェック中...");

                // .NET 8.0の確認
                if (!IsDotNetInstalled())
                {
                    throw new InvalidOperationException(".NET 8.0ランタイムがインストールされていません。https://dotnet.microsoft.com/download/dotnet/8.0 からインストールしてください。");
                }

                // 管理者権限の確認
                if (!IsAdministrator())
                {
                    throw new InvalidOperationException("管理者権限が必要です。");
                }

                ReportProgress(30, "ファイルをコピー中...");

                // インストールディレクトリの作成
                Directory.CreateDirectory(InstallPath);

                // サービスファイルのコピー（実際のパスは適切に設定）
                var serviceSourcePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Potion.Service.exe");
                var serviceDestPath = Path.Combine(InstallPath, "Potion.Service.exe");
                File.Copy(serviceSourcePath, serviceDestPath, true);

                // 設定ファイルのコピー
                var configSourcePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                var configDestPath = Path.Combine(InstallPath, "appsettings.json");
                File.Copy(configSourcePath, configDestPath, true);

                ReportProgress(60, "サービスを登録中...");

                // サービス登録（実際の実装では適切な方法で）
                RegisterService();

                ReportProgress(90, "インストールを完了中...");

                if (StartService)
                {
                    StartPotionService();
                }

                ReportProgress(100, "インストール完了");

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"インストールエラー: {ex.Message}");
                throw;
            }
        }

        private bool IsDotNetInstalled()
        {
            try
            {
                var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                });

                process.WaitForExit(5000);
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private bool IsAdministrator()
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        private void RegisterService()
        {
            // 実際の実装では適切なサービス登録方法を使用
            // ここでは簡易的な実装を示す
            Console.WriteLine("サービスを登録しました。");
        }

        private void StartPotionService()
        {
            // 実際の実装では適切なサービス開始方法を使用
            Console.WriteLine("Potionサービスを開始しました。");
        }

        private void ReportProgress(int progress, string message)
        {
            ProgressChanged?.Invoke(progress);
            Console.WriteLine($"[{progress}%] {message}");
        }
    }

    // 簡単なインストーラープログラム
    public static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var form = new MainForm();
            Application.Run(form);

            if (form.DialogResult == DialogResult.OK)
            {
                Console.WriteLine("インストールが完了しました。");
            }
        }
    }
}
