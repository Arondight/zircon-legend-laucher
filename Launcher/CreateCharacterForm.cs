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
    public partial class CreateCharacterForm : Form
    {
        public CreateCharacterForm()
        {
            InitializeComponent();
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void txtName_TextChanged(object sender, EventArgs e)
        {
            btnCreate.Enabled = !string.IsNullOrEmpty(txtName.Text);
        }

        private void CreateCharacterForm_Load(object sender, EventArgs e)
        {
            CEnvir.LogEvent += OnLog;

        }
        private void OnLog(string msg, bool pop, string key)
        {
            if (key != "创建角色") return;

            MessageBox.Show(this, msg, "创建角色", MessageBoxButtons.OK, pop ? MessageBoxIcon.Error : MessageBoxIcon.Information);

            if (pop)
                pnMain.Enabled = true;
            else
            {
                Thread.Sleep(300);
                this.Close();
            }
        }
        private void CreateCharacterForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            CEnvir.LogEvent -= OnLog;

        }

        private void btnCreate_Click(object sender, EventArgs e)
        {
            if (!Globals.CharacterReg.IsMatch(txtName.Text))
            {
                MessageBox.Show(this, "角色名称不符合要求", "创建角色", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            MirGender g = MirGender.Male;
            foreach(var item in gpGender.Controls)
            {
                if (item is RadioButton r && r.Checked && r.Tag is string key)
                {
                    g = (MirGender)int.Parse(key);
                    break;
                }
            }

            MirClass c = MirClass.Warrior;
            foreach(var item in gpClass.Controls)
            {
                if (item is RadioButton r && r.Checked && r.Tag is string key)
                {
                    c = (MirClass)int.Parse(key);
                    break;
                }
            }

            pnMain.Enabled = false;
            CEnvir.CreateCharacter(txtName.Text, g, c);
        }
    }
}
