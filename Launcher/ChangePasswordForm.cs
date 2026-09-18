using Library;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Launcher
{
    public partial class ChangePasswordForm : Form
    {
        public ChangePasswordForm()
        {
            InitializeComponent();
        }

        private void ChangePasswordForm_Load(object sender, EventArgs e)
        {
            CEnvir.LogEvent += OnLog;

        }

        private void ChangePasswordForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            CEnvir.LogEvent -= OnLog;

        }

        private void OnLog(string msg, bool pop, string key)
        {
            if (key != "修改密码") return;

            MessageBox.Show(this, msg, key, MessageBoxButtons.OK, pop ? MessageBoxIcon.Error : MessageBoxIcon.Information);

            if (pop)
                pnMain.Enabled = true;
            else
            {
                Thread.Sleep(300);
                this.Close();
            }
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void textBox_TextChanged(object sender, EventArgs e)
        {
            btnChange.Enabled = !string.IsNullOrEmpty(txtAccount.Text) 
                && !string.IsNullOrEmpty(txtNew.Text)
                && !string.IsNullOrEmpty(txtConfirm.Text)
                && !string.IsNullOrEmpty(txtOriginal.Text);
        }

        private void btnChange_Click(object sender, EventArgs e)
        {
            if (!Globals.EMailRegex.IsMatch(txtAccount.Text))
            {
                MessageBox.Show("账号格式不符合要求", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (!Globals.PasswordRegex.IsMatch(txtOriginal.Text))
            {
                MessageBox.Show("原密码格式不符合要求", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (!Globals.PasswordRegex.IsMatch(txtNew.Text))
            {
                MessageBox.Show("新密码格式不符合要求", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (txtNew.Text != txtConfirm.Text)
            {
                MessageBox.Show("两次输入密码不一致", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            pnMain.Enabled = false;
            CEnvir.ChangePassword(txtAccount.Text, txtOriginal.Text, txtNew.Text);
        }
    }
}
