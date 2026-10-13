using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DriveFolderSync
{
    public static class Installer
    {
        public static void Install(bool startUi)
        {
            using (SyncEngine.LockState(180)) InstallCore(startUi);
        }

        static void InstallCore(bool startUi)
        {
            Store.EnsureDirs();
            string source = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string dest = Store.InstallExe;
            KillHost(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkDriveSync", "host.pid"));
            KillHost(Store.PidFile);
            KillOtherCopies();
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            {
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    try
                    {
                        File.Copy(source, dest, true);
                        break;
                    }
                    catch (IOException)
                    {
                        if (attempt == 7) throw;
                        Thread.Sleep(400);
                    }
                }
            }
            WriteTask(dest);
            WriteShortcut(dest);
            WriteContextMenu(dest);
            WriteUninstallEntry(dest);
            Store.Log("installed Drive Folder Sync " + Store.Version);
            for (int wait = 0; wait < 10 && !Store.IsHostRunning(); wait++)
            {
                Thread.Sleep(200);
            }
            if (!Store.IsHostRunning())
            {
                Process.Start(new ProcessStartInfo(dest, "/host") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
            }
            if (startUi) Process.Start(new ProcessStartInfo(dest) { UseShellExecute = true });
        }

        public static void Uninstall(bool quiet)
        {
            Uninstall(quiet, null);
        }

        // Returns true when the app was removed; the caller should exit so its executable can be deleted.
        public static bool Uninstall(bool quiet, IWin32Window owner)
        {
            if (!quiet)
            {
                bool answer = Dialogs.Confirm(owner, "Remove Drive Folder Sync?",
                    "This removes the app, the sign-in task, and the right-click command for this Windows user. Your files, in the cloud and on this PC, are not deleted.",
                    null, "Uninstall", MessageKind.Warning);
                if (!answer) return false;
            }
            IDisposable stateLock = null;
            try { stateLock = SyncEngine.LockState(quiet ? 180 : 30); }
            catch (TimeoutException)
            {
                if (!quiet)
                {
                    Dialogs.Error(owner, "A sync is still running", "Drive Folder Sync was not removed. Try again when the current sync finishes.");
                    return false;
                }
                Store.Log("uninstall continued while a sync was busy");
            }
            try
            {
                KillHost(Store.PidFile);
                KillOtherCopies();
                RemoveTasks();
                try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\shell\DriveFolderSync", false); }
                catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); }
                catch { }
                string shortcut = ShortcutPath();
                if (File.Exists(shortcut)) File.Delete(shortcut);
                Store.Log("uninstalled Drive Folder Sync shortcuts and startup");
            }
            finally
            {
                if (stateLock != null) stateLock.Dispose();
            }
            if (!quiet) Dialogs.Success(owner, "Drive Folder Sync was removed", "Your files were not deleted. Your settings stay in " + Store.DefaultRoot + " in case you reinstall.");
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
            return true;
        }

        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DriveFolderSync";

        // Lists the app in Settings > Apps so it can be removed like other programs.
        static void WriteUninstallEntry(string exe)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", "Drive Folder Sync");
                key.SetValue("DisplayVersion", Store.Version);
                key.SetValue("Publisher", "SB");
                key.SetValue("DisplayIcon", exe + ",0");
                key.SetValue("InstallLocation", Path.GetDirectoryName(exe));
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                key.SetValue("URLInfoAbout", "https://github.com/sb-git-cs/drive-folder-sync");
                key.SetValue("UninstallString", "\"" + exe + "\" /uninstall");
                key.SetValue("QuietUninstallString", "\"" + exe + "\" /uninstall /quiet");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)Math.Max(1, new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
            }
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
                + "Register-ScheduledTask -TaskName 'DriveFolderSync' -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null\r\n"
                + "Start-ScheduledTask -TaskName 'DriveFolderSync' -ErrorAction SilentlyContinue\r\n";
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
                key.SetValue("", "Attach to Drive Folder Sync");
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
        static readonly string[] Included = new string[]
        {
            "Starts when you sign in, refreshes from the cloud, and backs up local changes",
            "Backs up attached folders when Windows shuts down",
            "Adds “Attach to Drive Folder Sync” to the folder right-click menu",
            "Adds Drive Folder Sync to the Start menu"
        };

        readonly ListBox drives = new ListBox();
        readonly FlatButton install = new FlatButton("Install", true);
        readonly FlatButton cancel = new FlatButton("Cancel", false);
        readonly FlatButton uninstall = new FlatButton("Uninstall", false);
        readonly ProgressBar progress = new ProgressBar();
        readonly Label note = new Label();
        readonly Panel body = new Panel();
        readonly Panel done = new Panel();
        readonly Panel footer = new Panel();
        readonly int pad = Theme.S(32);
        bool finished;

        public SetupForm()
        {
            Theme.Init();
            Text = "Install Drive Folder Sync";
            Icon = Theme.AppIcon;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.Body;
            BackColor = Theme.Window;
            int width = Theme.S(620);
            int inner = width - pad * 2;

            Panel hero = new Panel();
            hero.Dock = DockStyle.Top;
            hero.Height = Theme.S(128);
            hero.Paint += new PaintEventHandler(Hero_Paint);

            body.Dock = DockStyle.Fill;
            body.BackColor = Theme.Window;
            int y = Theme.S(24);
            Label drivesTitle = AddLabel(body, "Drives found on this PC", Theme.Strong, Theme.Text, pad, y, inner);
            y += drivesTitle.Height + Theme.S(8);

            Panel frame = new Panel();
            frame.BackColor = Theme.Window;
            frame.Padding = Theme.Pad(4, 4, 4, 4);
            frame.SetBounds(pad, y, inner, Theme.S(44) * 3 + Theme.S(10));
            frame.Paint += new PaintEventHandler(delegate(object sender, PaintEventArgs e)
            {
                Theme.DrawRound(e.Graphics, Theme.Border, new Rectangle(0, 0, frame.Width, frame.Height), Theme.S(6), 1f);
            });
            drives.Dock = DockStyle.Fill;
            drives.BorderStyle = BorderStyle.None;
            drives.DrawMode = DrawMode.OwnerDrawFixed;
            drives.ItemHeight = Theme.S(44);
            drives.IntegralHeight = false;
            drives.SelectionMode = SelectionMode.None;
            drives.BackColor = Theme.Window;
            drives.DrawItem += new DrawItemEventHandler(Drives_DrawItem);
            frame.Controls.Add(drives);
            body.Controls.Add(frame);
            y += frame.Height + Theme.S(22);

            Label includedTitle = AddLabel(body, "What gets installed", Theme.Strong, Theme.Text, pad, y, inner);
            y += includedTitle.Height + Theme.S(6);
            int listTop = y;
            int lineHeight = Theme.S(26);
            body.Paint += new PaintEventHandler(delegate(object sender, PaintEventArgs e)
            {
                for (int i = 0; i < Included.Length; i++)
                {
                    int top = listTop + i * lineHeight;
                    Theme.DrawGlyph(e.Graphics, Glyphs.Check, Theme.S(14), Theme.Accent, pad + Theme.S(2), top + (lineHeight - Theme.S(14)) / 2);
                    Theme.DrawText(e.Graphics, Included[i], Theme.Body, new Rectangle(pad + Theme.S(26), top, inner - Theme.S(26), lineHeight), Theme.Text, TextFormatFlags.Left);
                }
            });
            y += Included.Length * lineHeight;
            y += Theme.S(12);
            note.Font = Theme.Body;
            note.ForeColor = Theme.TextMuted;
            note.UseMnemonic = false;
            note.SetBounds(pad, y, inner, (Theme.Body.Height + Theme.S(2)) * 2);
            body.Controls.Add(note);
            y += note.Height + Theme.S(20);

            done.Dock = DockStyle.Fill;
            done.BackColor = Theme.Window;
            done.Visible = false;
            done.Paint += new PaintEventHandler(Done_Paint);

            footer.Dock = DockStyle.Bottom;
            footer.Height = Theme.S(68);
            footer.BackColor = Theme.Chrome;
            footer.Paint += new PaintEventHandler(delegate(object sender, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            });
            progress.Style = ProgressBarStyle.Marquee;
            progress.MarqueeAnimationSpeed = 30;
            progress.Visible = false;
            install.Click += new EventHandler(Install_Click);
            cancel.Click += new EventHandler(delegate { Close(); });
            uninstall.Click += new EventHandler(Uninstall_Click);
            footer.Controls.Add(progress);
            footer.Controls.Add(uninstall);
            footer.Controls.Add(cancel);
            footer.Controls.Add(install);
            footer.Resize += new EventHandler(delegate { LayoutFooter(); });
            AcceptButton = install;
            CancelButton = cancel;

            Controls.Add(body);
            Controls.Add(done);
            Controls.Add(hero);
            Controls.Add(footer);
            ClientSize = new Size(width, hero.Height + y + footer.Height);
            Load += new EventHandler(SetupForm_Load);
        }

        static Label AddLabel(Control parent, string text, Font font, Color color, int x, int y, int width)
        {
            Label label = new Label();
            label.Font = font;
            label.ForeColor = color;
            label.UseMnemonic = false;
            label.Text = text;
            Size size = TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            label.SetBounds(x, y, width, size.Height + Theme.S(2));
            parent.Controls.Add(label);
            return label;
        }

        void LayoutFooter()
        {
            int middle = (footer.Height - install.Height) / 2;
            install.Width = Math.Max(Theme.S(110), Theme.Measure(install.Text, Theme.Body) + Theme.S(36));
            install.Location = new Point(footer.Width - pad - install.Width, middle);
            cancel.Location = new Point(install.Left - Theme.S(8) - cancel.Width, middle);
            uninstall.Location = new Point(pad, middle);
            progress.SetBounds(pad, (footer.Height - Theme.S(6)) / 2, Theme.S(180), Theme.S(6));
        }

        void SetupForm_Load(object sender, EventArgs e)
        {
            drives.Items.Clear();
            List<Provider> providers = Store.DetectProviders();
            if (providers.Count == 0) drives.Items.Add("No Google Drive, OneDrive, or Dropbox folder was found. You can add one after install.");
            foreach (Provider provider in providers) drives.Items.Add(provider);
            bool installed = File.Exists(Store.InstallExe);
            install.Text = installed ? "Reinstall" : "Install";
            uninstall.Visible = installed;
            note.Text = "Installs for " + Environment.UserName + " only. Nothing is copied until you attach a folder."
                + (installed ? "\r\nA copy is already installed for this user. Reinstall to update it." : "");
            LayoutFooter();
            install.Focus();
        }

        void Hero_Paint(object sender, PaintEventArgs e)
        {
            Control hero = (Control)sender;
            Graphics g = e.Graphics;
            Rectangle r = hero.ClientRectangle;
            using (LinearGradientBrush brush = new LinearGradientBrush(r, Color.FromArgb(0, 103, 192), Color.FromArgb(0, 62, 138), LinearGradientMode.ForwardDiagonal))
                g.FillRectangle(brush, r);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush ring = new SolidBrush(Color.FromArgb(18, 255, 255, 255)))
            {
                g.FillEllipse(ring, r.Right - Theme.S(170), -Theme.S(70), Theme.S(240), Theme.S(240));
                g.FillEllipse(ring, r.Right - Theme.S(90), Theme.S(40), Theme.S(150), Theme.S(150));
            }
            int iconSize = Theme.S(56);
            int iconTop = (r.Height - iconSize) / 2;
            using (Icon icon = Theme.AppIconSized(iconSize)) g.DrawIcon(icon, new Rectangle(pad, iconTop, iconSize, iconSize));
            int x = pad + iconSize + Theme.S(18);
            int titleHeight = Theme.Display.Height;
            int top = (r.Height - titleHeight - Theme.Body.Height - Theme.S(4)) / 2;
            TextRenderer.DrawText(g, "Drive Folder Sync", Theme.Display, new Point(x - Theme.S(2), top), Color.White, TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "Keep the folders you choose in sync with Google Drive, OneDrive, and Dropbox", Theme.Body,
                new Rectangle(x, top + titleHeight + Theme.S(4), r.Width - x - Theme.S(16), Theme.Body.Height + Theme.S(4)), Color.FromArgb(214, 232, 250),
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            string version = "Version " + Store.Version;
            Size size = TextRenderer.MeasureText(version, Theme.Small);
            TextRenderer.DrawText(g, version, Theme.Small, new Point(r.Width - size.Width - Theme.S(14), Theme.S(12)), Color.FromArgb(190, 215, 242), TextFormatFlags.NoPrefix);
        }

        void Done_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int circle = Theme.S(56);
            int y = Theme.S(48);
            Rectangle badge = new Rectangle((done.Width - circle) / 2, y, circle, circle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush brush = new SolidBrush(Theme.Success)) g.FillEllipse(brush, badge);
            Theme.DrawGlyphCentered(g, Glyphs.Check, Theme.S(26), Color.White, badge);
            y += circle + Theme.S(20);
            TextRenderer.DrawText(g, "Drive Folder Sync is installed", Theme.Title, new Rectangle(0, y, done.Width, Theme.Title.Height + Theme.S(4)), Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
            y += Theme.Title.Height + Theme.S(12);
            TextRenderer.DrawText(g, "It starts when you sign in, and the app is opening now.\r\nTo back up a folder later, right-click it in File Explorer and choose “Attach to Drive Folder Sync”.",
                Theme.Body, new Rectangle(pad, y, done.Width - pad * 2, done.Height - y), Theme.TextMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }

        void Drives_DrawItem(object sender, DrawItemEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush back = new SolidBrush(Theme.Window)) g.FillRectangle(back, e.Bounds);
            if (e.Index < 0 || e.Index >= drives.Items.Count) return;
            Provider provider = drives.Items[e.Index] as Provider;
            Rectangle r = e.Bounds;
            if (provider == null)
            {
                Theme.DrawGlyph(g, Glyphs.Info, Theme.S(16), Theme.TextMuted, r.X + Theme.S(12), r.Y + (r.Height - Theme.S(16)) / 2);
                Theme.DrawText(g, Convert.ToString(drives.Items[e.Index]), Theme.Body, new Rectangle(r.X + Theme.S(38), r.Y, r.Width - Theme.S(46), r.Height), Theme.TextMuted, TextFormatFlags.Left);
                return;
            }
            Rectangle tile = new Rectangle(r.X + Theme.S(8), r.Y + (r.Height - Theme.S(32)) / 2, Theme.S(32), Theme.S(32));
            Theme.FillRound(g, Theme.Chrome, tile, Theme.S(6));
            int size = SystemInformation.SmallIconSize.Width;
            Icon icon = ShellImages.SmallIcon(ShellImages.RealIndex(provider.Root), provider.Root, true, true);
            if (icon != null) g.DrawIcon(icon, new Rectangle(tile.X + (tile.Width - size) / 2, tile.Y + (tile.Height - size) / 2, size, size));
            string state = provider.IsOnline ? "Connected" : "Not connected";
            string kind = provider.IsOnline ? "same" : "warn";
            int stateWidth = Theme.StatusWidth(state, kind);
            int x = tile.Right + Theme.S(12);
            int textWidth = r.Right - x - stateWidth - Theme.S(16);
            string name = provider.Name + (string.IsNullOrEmpty(provider.Detail) || provider.Detail == provider.Root ? "" : "  ·  " + provider.Detail);
            int lineHeight = Theme.Strong.Height;
            int top = r.Y + (r.Height - lineHeight - Theme.Small.Height - Theme.S(2)) / 2;
            Theme.DrawText(g, name, Theme.Strong, new Rectangle(x, top, textWidth, lineHeight), Theme.Text, TextFormatFlags.Left);
            Theme.DrawText(g, provider.Root, Theme.Small, new Rectangle(x, top + lineHeight + Theme.S(2), textWidth, Theme.Small.Height), Theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.PathEllipsis);
            Theme.DrawStatus(g, new Rectangle(r.Right - stateWidth - Theme.S(10), r.Y, stateWidth, r.Height), state, kind);
            if (e.Index < drives.Items.Count - 1)
                using (Pen pen = new Pen(Theme.Border)) g.DrawLine(pen, x, r.Bottom - 1, r.Right - Theme.S(8), r.Bottom - 1);
        }

        void Install_Click(object sender, EventArgs e)
        {
            if (finished)
            {
                Close();
                return;
            }
            install.Enabled = false;
            install.Text = "Installing…";
            cancel.Enabled = false;
            uninstall.Visible = false;
            progress.Visible = true;
            Cursor = Cursors.AppStarting;
            LayoutFooter();
            Thread worker = new Thread(delegate()
            {
                string error = null;
                try { Installer.Install(true); }
                catch (Exception ex) { error = ex.Message; }
                try { BeginInvoke(new Action(delegate { Installed(error); })); }
                catch (InvalidOperationException) { }
            });
            worker.SetApartmentState(ApartmentState.STA);
            worker.IsBackground = true;
            worker.Start();
        }

        void Installed(string error)
        {
            progress.Visible = false;
            Cursor = Cursors.Default;
            install.Enabled = true;
            if (error != null)
            {
                install.Text = File.Exists(Store.InstallExe) ? "Reinstall" : "Install";
                cancel.Enabled = true;
                uninstall.Visible = File.Exists(Store.InstallExe);
                LayoutFooter();
                Dialogs.Error(this, "Installation didn't finish", error);
                return;
            }
            finished = true;
            body.Visible = false;
            done.Visible = true;
            cancel.Visible = false;
            install.Text = "Close";
            CancelButton = install;
            LayoutFooter();
            install.Focus();
        }

        void Uninstall_Click(object sender, EventArgs e)
        {
            if (!Installer.Uninstall(false, this)) return;
            install.Text = "Install";
            uninstall.Visible = false;
            note.Text = "Installs for " + Environment.UserName + " only. Nothing is copied until you attach a folder.";
            LayoutFooter();
        }
    }
}
