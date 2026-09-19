using Library;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Launcher
{
    public partial class MainForm : Form
    {
        //记录本次会话输入的密码，用于启动游戏时同步给客户端，而不必要求勾选"记住"
        private string _sessionPassword = string.Empty;
        //窗体正在关闭，忽略后续的状态/日志回调，避免关闭后又把游戏拉起来
        private volatile bool _closing;

        public MainForm()
        {
            InitializeComponent();
            Control.CheckForIllegalCrossThreadCalls = false;
        }

        private void label1_Click(object sender, EventArgs e)
        {

        }

        private void OnLog(string msg, bool pop, string key)
        {
            if (_closing) return;

            txtLog.AppendText($"{msg}\r\n");

            if (pop && key != null && key != "创建账号" && key != "修改密码" && key != "创建角色") 
                MessageBox.Show(this, msg, key, MessageBoxButtons.OK, MessageBoxIcon.Error);

            if (key == "删除角色")
            {
                MessageBox.Show(this, msg, key, MessageBoxButtons.OK, pop ? MessageBoxIcon.Error : MessageBoxIcon.Information);
                UpdateCharacter();
                pnCharacter.Enabled = true;
            }
        }

        private void OnMainStatusChanged()
        {
            if (_closing) return;

            if (CEnvir.MainStep == CEnvir.MainStepType.Ready)
                gpBase.Enabled = true;
            else gpBase.Enabled = false;

            if (CEnvir.MainStep == CEnvir.MainStepType.Connected)
                gpAccount.Enabled = true;
            else gpAccount.Enabled = false;

            if (CEnvir.MainStep == CEnvir.MainStepType.Upgrading)
            { 
                progress.Value = 0;
                OnLog("开始更新客户端...", false, "更新客户端");
            }

            if (CEnvir.MainStep == CEnvir.MainStepType.Upgraded)
            {
                progress.Value = 100;
                //timer1.Enabled = false;
                gpAccount.Enabled = true;
            }
            else gpAccount.Enabled = false;

            if (CEnvir.MainStep == CEnvir.MainStepType.Logon)
            {
                UpdateCharacter();

                pnCharacter.Enabled = true;
            }
            else
                pnCharacter.Enabled = false;

            if (CEnvir.MainStep ==  CEnvir.MainStepType.Stop)
            {
                StartGame();
            }
        }

        private void UpdateCharacter()
        {
            cbCharacter.Items.Clear();

            foreach (var character in CEnvir.SelectCharacters)
            {
                string gender = Functions.GetEnumDesc(character.Gender);
                string cls = Functions.GetEnumDesc(character.Class);

                cbCharacter.Items.Add($"【{character.CharacterName}】 {character.Level}级{gender}{cls}  最后登录：{character.LastLogin.ToString()}");
            }

            if (CEnvir.SelectCharacters.Count > 0)
                cbCharacter.SelectedIndex = 0;

            btnDelCharacter.Enabled = CEnvir.SelectCharacters.Count > 0;
            btnStart.Enabled = CEnvir.SelectCharacters.Count > 0;
        }
        private void RecommandScreen()
        {
            Screen s = Screen.PrimaryScreen;

            if (ckFullScreen.Checked)
            {
                Size p = s.Bounds.Size;
                txtWidth.Text = $"{p.Width}";
                txtHeight.Text = $"{p.Height}";
            }
            else
            {
                var bar = this.Height - this.ClientSize.Height;
                Size p = s.WorkingArea.Size;
                txtWidth.Text = $"{p.Width}";
                txtHeight.Text = $"{p.Height - bar}";
            }
        }
        private void MainForm_Load(object sender, EventArgs e)
        {
            txtHost.Text = Config.IPAddress;
            txtPort.Text = $"{Config.Port}";
            txtClientUrl.Text = Config.ClientUrl;
            txtWidth.Text = $"{Config.GameSize.Width}";
            txtHeight.Text = $"{Config.GameSize.Height}";
            ckFullScreen.Checked = Config.FullScreen;
            ckRember.Checked = Config.Remember;

            txtAccount.Text = Config.Account;

            if (Config.Remember)
                txtPassword.Text = Config.Password;

            progress.Maximum = 100;

            CEnvir.LogEvent += OnLog;
            CEnvir.MainStepChanged += OnMainStatusChanged;
            CEnvir.Initialize();

            if (string.IsNullOrEmpty(txtWidth.Text.Trim()) || string.IsNullOrEmpty(txtHeight.Text.Trim()))
                RecommandScreen();
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            _closing = true;
            this.Enabled = false;
            CEnvir.MainStepChanged -= OnMainStatusChanged;
            txtLog.AppendText($"正在关闭启动器...\r\n");

            CEnvir.Stop();

            //等待工作线程结束，但设上限兜底，避免关闭按钮把界面永久挂起
            int guard = 0;
            while (CEnvir.MainStep != CEnvir.MainStepType.Stop && ++guard < 100)
                Thread.Sleep(200);

            CEnvir.LogEvent -= OnLog;
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            if (CEnvir.MainStep == CEnvir.MainStepType.Upgrading && CEnvir.UpgradeTotalSize > 0)
            {
                // 夹到 0~100，避免总量为 0 时除零或超出 ProgressBar 取值范围
                int val = (int)(CEnvir.UpgradedSize * 100 / CEnvir.UpgradeTotalSize);
                if (val < 0) val = 0;
                else if (val > 100) val = 100;

                if (val != progress.Value)
                {
                    progress.Value = val;
                    progress.Refresh();
                }

            }
        }

        private void btnRecommand_Click(object sender, EventArgs e)
        {
            RecommandScreen();
        }

        private void btnConnect_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(txtHost.Text) || string.IsNullOrEmpty(txtPort.Text))
            {
                MessageBox.Show("主机和端口都不能为空！", "连接", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (!int.TryParse(txtPort.Text, out int port) || !int.TryParse(txtWidth.Text, out int width) || !int.TryParse(txtHeight.Text, out int height))
            {
                MessageBox.Show("分辨率和端口号都必须是整数", "连接", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Config.Port = port;
            Config.IPAddress = txtHost.Text;
            Config.ClientUrl = txtClientUrl.Text.Trim();
            Config.Remember = ckRember.Checked;
            Config.GameSize = new Size(width, height);
            Config.FullScreen = ckFullScreen.Checked;

            ConfigReader.Save();

            CEnvir.Connect();
        }

        private void txtWidth_KeyPress(object sender, KeyPressEventArgs e)
        {
            // 放行退格、粘贴等控制键
            if (e.KeyChar < ' ') return;
            if (e.KeyChar < '0' || e.KeyChar > '9') e.Handled = true;
        }

        private void txtHeight_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar < ' ') return;
            if (e.KeyChar < '0' || e.KeyChar > '9') e.Handled = true;
        }

        private void txtHost_KeyPress(object sender, KeyPressEventArgs e)
        {
            // 放行退格、粘贴(Ctrl+V)等控制键，否则无法用粘贴输入主机地址
            if (e.KeyChar < ' ')
            {
                e.Handled = false;
                return;
            }

            if ((e.KeyChar >= '0' && e.KeyChar <= '9')
                || (e.KeyChar >= 'a' && e.KeyChar <= 'z')
                || (e.KeyChar >= 'A' && e.KeyChar <= 'Z')
                || e.KeyChar == ':' || e.KeyChar == '.' || e.KeyChar == '-' || e.KeyChar == '_') e.Handled = false;
            else e.Handled = true;

        }

        private void txtPort_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar < ' ') return;
            if (e.KeyChar < '0' || e.KeyChar > '9') e.Handled = true;
        }

        private void btnLogin_Click(object sender, EventArgs e)
        {
            if (!Globals.EMailRegex.IsMatch(txtAccount.Text))
            {
                MessageBox.Show("账号格式错误，必须是邮箱地址", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (!Globals.PasswordRegex.IsMatch(txtPassword.Text))
            {
                MessageBox.Show($"密码格式错误，必须是 {Globals.MinPasswordLength} - {Globals.MaxPasswordLength} 位非空白字符", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Config.Account = txtAccount.Text;
            Config.Remember = ckRember.Checked;
            _sessionPassword = txtPassword.Text;
            // 取消“记住密码”时必须清掉已保存的密码，否则下次仍会把旧密码写回 ini
            Config.Password = ckRember.Checked ? txtPassword.Text : string.Empty;

            ConfigReader.Save();
            gpAccount.Enabled = false;
            CEnvir.Login(txtPassword.Text);
        }

        private void lkCreateAccount_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            NewAccountForm diag = new NewAccountForm();
            diag.ShowDialog(this);
            
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(txtHost.Text) || string.IsNullOrEmpty(txtPort.Text))
            {
                MessageBox.Show("主机和端口都不能为空！", "保存设置", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (!int.TryParse(txtPort.Text, out int port) || !int.TryParse(txtWidth.Text, out int width) || !int.TryParse(txtHeight.Text, out int height))
            {
                MessageBox.Show("分辨率和端口号都必须是整数", "保存设置", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Config.Port = port;
            Config.IPAddress = txtHost.Text;
            Config.ClientUrl = txtClientUrl.Text.Trim();
            Config.Remember = ckRember.Checked;
            Config.GameSize = new Size(width, height);
            Config.FullScreen = ckFullScreen.Checked;

            ConfigReader.Save();
            MessageBox.Show(this, "设置保存成功", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void lkChangePassword_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            ChangePasswordForm diag = new ChangePasswordForm();
            diag.ShowDialog(this);
        }

        private void btnCreateCharacter_Click(object sender, EventArgs e)
        {
            CreateCharacterForm diag = new CreateCharacterForm();
            diag.ShowDialog(this);
            UpdateCharacter();
        }

        private void btnDelCharacter_Click(object sender, EventArgs e)
        {
            var character = CEnvir.SelectCharacters[cbCharacter.SelectedIndex];
            var gender = Functions.GetEnumDesc(character.Gender);
            var cls = Functions.GetEnumDesc(character.Class);


            var result = MessageBox.Show(this, $"确定要删除 {character.Level}级{gender}{cls} {character.CharacterName} 吗？", "删除角色", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                pnCharacter.Enabled = false;
                CEnvir.DeleteCharacter(character.CharacterIndex);
            }
        }

        private void StartGame()
        {
            var character = CEnvir.SelectCharacters[cbCharacter.SelectedIndex];

            try
            {
                // 用启动器所在目录作为工作目录，避免从其它路径启动时找不到 Legend.exe / Data
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(CEnvir.RootPath, "Legend.exe"),
                    Arguments = $" -QuickGame -Host:{CEnvir.IpServer.ToString()} -Port:{CEnvir.RealPort} -FullScreen:{Config.FullScreen} -GameSize:{Config.GameSize.Width}x{Config.GameSize.Height} -Account:{Config.Account} -Remember:{Config.Remember} -Password:{(!string.IsNullOrEmpty(_sessionPassword) ? _sessionPassword : Config.Password)} -SelectChar:{character.CharacterIndex} -LauncherHash:{CEnvir.LauncherHash} -NeedFlushDns:{Config.NeedFlushDns}",
                    WorkingDirectory = CEnvir.RootPath,
                    UseShellExecute = true,
                };

                Process.Start(startInfo);
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "启动异常", MessageBoxButtons.OK, MessageBoxIcon.Error);
                CEnvir.Log(ex.Message);
                CEnvir.Log(ex.StackTrace);
                this.Enabled = true;
            }
        }

        private void btnStart_Click(object sender, EventArgs e)
        {
            txtLog.AppendText($"正在启动游戏...\r\n");
            this.Enabled = false;
            CEnvir.Stop();
        }

        private void btnAbout_Click(object sender, EventArgs e)
        {
            AboutBox1 diag = new AboutBox1();
            diag.ShowDialog(this);
        }

        private void ckFullScreen_CheckedChanged(object sender, EventArgs e)
        {
            //if (string.IsNullOrEmpty(txtWidth.Text.Trim()) || string.IsNullOrEmpty(txtHeight.Text.Trim()))
            //    RecommandScreen();
        }

    }
}
