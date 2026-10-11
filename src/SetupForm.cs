using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DriveFolderSync
{
    public static class Installer
    {
        public static void Install(bool startUi)
        {
            Store.EnsureDirs();
            string source = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string dest = Store.InstallExe;
            KillHost(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkDriveSync", "host.pid"));
            KillHost(Store.PidFile);
            KillOtherCopies();
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                File.Copy(source, dest, true);
            WriteTask(dest);
            WriteShortcut(dest);
            WriteContextMenu(dest);
            Store.Log("installed Drive Folder Sync " + Store.Version);
            Process.Start(new ProcessStartInfo(dest, "/host") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            if (startUi) Process.Start(new ProcessStartInfo(dest) { UseShellExecute = true });
        }

        public static void Uninstall(bool quiet)
        {
            if (!quiet)
            {
                DialogResult answer = MessageBox.Show(
                    "Remove Drive Folder Sync from this user account? Linked files on disk are left where they are.",
                    "Drive Folder Sync",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;
            }
            KillHost(Store.PidFile);
            RemoveTasks();
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\shell\DriveFolderSync", false); }
            catch { }
            string shortcut = ShortcutPath();
            if (File.Exists(shortcut)) File.Delete(shortcut);
            Store.Log("uninstalled Drive Folder Sync shortcuts and startup");
            string exe = Store.InstallExe;
            if (File.Exists(exe))
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = "cmd.exe";
                info.Arguments = "/c ping 127.0.0.1 -n 3 > nul & del /f /q \"" + exe + "\"";
                info.CreateNoWindow = true;
                info.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(info);
            }
            if (!quiet) MessageBox.Show("Drive Folder Sync has been removed. Your folders were not deleted.", "Drive Folder Sync");
        }

        static void KillOtherCopies()
        {
            int self = Process.GetCurrentProcess().Id;
            foreach (Process process in Process.GetProcessesByName("DriveFolderSync"))
            {
                if (process.Id == self) continue;
                try
                {
                    process.Kill();
                    process.WaitForExit(8000);
                }
                catch { }
            }
        }

        static void KillHost(string pidFile)
        {
            try
            {
                if (!File.Exists(pidFile)) return;
                int pid;
                if (!int.TryParse(File.ReadAllText(pidFile).Trim(), out pid)) return;
                Process process = Process.GetProcessById(pid);
                if (process != null && !process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(8000);
                }
            }
            catch { }
            try { if (File.Exists(pidFile)) File.Delete(pidFile); }
            catch { }
        }

        static void WriteTask(string exe)
        {
            string script = "Unregister-ScheduledTask -TaskName 'WorkDriveSync' -Confirm:$false -ErrorAction SilentlyContinue\r\n"
                + "Unregister-ScheduledTask -TaskName 'DriveFolderSync' -Confirm:$false -ErrorAction SilentlyContinue\r\n"
                + "$action = New-ScheduledTaskAction -Execute $env:DFS_EXE -Argument '/host'\r\n"
                + "$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME\r\n"
                + "$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)\r\n"
                + "Register-ScheduledTask -TaskName 'DriveFolderSync' -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null\r\n";
            int code = RunPowerShell(script, exe);
            if (code != 0) throw new InvalidOperationException("Windows did not create the sign-in task.");
        }

        static void RemoveTasks()
        {
            RunPowerShell("Unregister-ScheduledTask -TaskName 'WorkDriveSync' -Confirm:$false -ErrorAction SilentlyContinue\r\n"
                + "Unregister-ScheduledTask -TaskName 'DriveFolderSync' -Confirm:$false -ErrorAction SilentlyContinue\r\n", null);
        }

        static int RunPowerShell(string script, string exe)
        {
            string path = Path.Combine(Path.GetTempPath(), "DriveFolderSync-task.ps1");
            File.WriteAllText(path, script, Encoding.UTF8);
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell\\v1.0\\powershell.exe");
            info.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + path + "\"";
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            if (!string.IsNullOrEmpty(exe)) info.EnvironmentVariables["DFS_EXE"] = exe;
            Process process = Process.Start(info);
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit(30000);
            try { File.Delete(path); } catch { }
            if (!process.HasExited) return -1;
            if (process.ExitCode != 0 && !string.IsNullOrEmpty(error)) Store.Log("task script: " + error);
            return process.ExitCode;
        }

        static void WriteShortcut(string exe)
        {
            string path = ShortcutPath();
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
            Type shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { exe });
            shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(exe) });
            shortcutType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "Drive Folder Sync" });
            shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
        }

        static string ShortcutPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Drive Folder Sync.lnk");
        }

        static void WriteContextMenu(string exe)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\DriveFolderSync"))
            {
                key.SetValue("", "Link folder for sync");
                key.SetValue("Icon", exe + ",0");
            }
            using (RegistryKey command = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\DriveFolderSync\command"))
            {
                command.SetValue("", "\"" + exe + "\" /link \"%1\"");
            }
        }

    }

    public class SetupForm : Form
    {
        readonly ListBox drives = new ListBox();
        readonly CheckBox startup = new CheckBox();
        readonly CheckBox menu = new CheckBox();
        readonly Button install = new Button();
        readonly Button uninstall = new Button();
        readonly Label result = new Label();

        public SetupForm()
        {
            Text = "Install Drive Folder Sync";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new System.Drawing.Size(560, 470);
            Font = System.Drawing.SystemFonts.MessageBoxFont;

            Panel header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 78;
            header.BackColor = System.Drawing.Color.FromArgb(0, 90, 158);
            Label title = new Label();
            title.Text = "Drive Folder Sync";
            title.ForeColor = System.Drawing.Color.White;
            title.Font = new System.Drawing.Font(Font.FontFamily, 16f, System.Drawing.FontStyle.Regular);
            title.AutoSize = true;
            title.Location = new System.Drawing.Point(18, 14);
            Label subtitle = new Label();
            subtitle.Text = "An Explorer for the folders you sync with the cloud";
            subtitle.ForeColor = System.Drawing.Color.White;
            subtitle.AutoSize = true;
            subtitle.Location = new System.Drawing.Point(20, 46);
            header.Controls.Add(title);
            header.Controls.Add(subtitle);

            Label body = new Label();
            body.Text = "Installs for this Windows user. Linked folders refresh when you sign in and upload when Windows shuts down. Nothing is copied until you link a folder.";
            body.Location = new System.Drawing.Point(18, 96);
            body.Size = new System.Drawing.Size(524, 48);

            Label found = new Label();
            found.Text = "Drives found on this PC";
            found.Location = new System.Drawing.Point(18, 150);
            found.AutoSize = true;
            drives.Location = new System.Drawing.Point(18, 172);
            drives.Size = new System.Drawing.Size(524, 130);
            drives.IntegralHeight = false;

            startup.Text = "Start when I sign in";
            startup.Checked = true;
            startup.AutoSize = true;
            startup.Location = new System.Drawing.Point(18, 318);
            menu.Text = "Add \"Link folder for sync\" to the folder right-click menu";
            menu.Checked = true;
            menu.AutoSize = true;
            menu.Location = new System.Drawing.Point(18, 344);

            result.Location = new System.Drawing.Point(18, 376);
            result.Size = new System.Drawing.Size(360, 36);

            install.Text = "Install";
            install.Location = new System.Drawing.Point(348, 424);
            install.Size = new System.Drawing.Size(90, 28);
            install.Click += new EventHandler(Install_Click);
            uninstall.Text = "Uninstall";
            uninstall.Location = new System.Drawing.Point(444, 424);
            uninstall.Size = new System.Drawing.Size(90, 28);
            uninstall.Click += new EventHandler(Uninstall_Click);
            AcceptButton = install;

            Controls.Add(header);
            Controls.Add(body);
            Controls.Add(found);
            Controls.Add(drives);
            Controls.Add(startup);
            Controls.Add(menu);
            Controls.Add(result);
            Controls.Add(install);
            Controls.Add(uninstall);
            Load += new EventHandler(SetupForm_Load);
        }

        void SetupForm_Load(object sender, EventArgs e)
        {
            drives.Items.Clear();
            System.Collections.Generic.List<Provider> providers = Store.DetectProviders();
            if (providers.Count == 0) drives.Items.Add("No Google Drive, OneDrive, or Dropbox folder was found. You can add one after install.");
            foreach (Provider provider in providers) drives.Items.Add(provider.Title);
            if (File.Exists(Store.InstallExe))
            {
                install.Text = "Reinstall";
                result.Text = "A copy is already installed for this user.";
            }
        }

        void Install_Click(object sender, EventArgs e)
        {
            if (!startup.Checked || !menu.Checked)
            {
                MessageBox.Show(this, "Leave both options on so shutdown upload, sign-in refresh, and the right-click command are installed.", "Drive Folder Sync");
                startup.Checked = true;
                menu.Checked = true;
                return;
            }
            try
            {
                UseWaitCursor = true;
                install.Enabled = false;
                Installer.Install(true);
                result.Text = "Installed. The Explorer window is opening.";
                install.Text = "Close";
                install.Click -= new EventHandler(Install_Click);
                install.Click += new EventHandler(Close_Click);
                install.Enabled = true;
            }
            catch (Exception ex)
            {
                install.Enabled = true;
                MessageBox.Show(this, ex.Message, "Drive Folder Sync", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                UseWaitCursor = false;
            }
        }

        void Close_Click(object sender, EventArgs e)
        {
            Close();
        }

        void Uninstall_Click(object sender, EventArgs e)
        {
            Installer.Uninstall(false);
        }
    }
}
