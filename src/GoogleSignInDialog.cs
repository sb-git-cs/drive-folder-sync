using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace DriveFolderSync
{
    // Connects Google Drive accounts so backups upload straight from this PC to Google Drive.
    public class GoogleSignInDialog : Form
    {
        public const string SetupHelp = "https://github.com/sb-git-cs/drive-folder-sync#connect-google-drive";
        readonly TextField idBox = new TextField('\0', false, "Client ID");
        readonly TextField secretBox = new TextField('\0', false, "Client secret");
        readonly Panel accounts = new Panel();
        readonly Label status = new Label();
        readonly List<Provider> drives = new List<Provider>();
        readonly int pad = Theme.S(24);
        readonly int field = Theme.S(560) - Theme.S(48);
        bool working;

        public GoogleSignInDialog(List<Provider> providers)
        {
            Theme.Init();
            foreach (Provider provider in providers) if (GoogleAuth.EmailOf(provider) != null) drives.Add(provider);
            foreach (string email in GoogleAuth.GetAccounts())
            {
                bool found = false;
                foreach (Provider p in drives) if (string.Equals(GoogleAuth.EmailOf(p), email, StringComparison.OrdinalIgnoreCase)) found = true;
                if (!found)
                {
                    Provider p = new Provider();
                    p.Id = "google:" + email.ToLowerInvariant();
                    p.Kind = "GoogleDrive";
                    p.Name = "Google Drive";
                    p.Root = "gdrive:" + email.ToLowerInvariant();
                    p.Detail = email;
                    drives.Add(p);
                }
            }
            Text = "Google Drive sign-in";
            Icon = Theme.AppIcon;
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.Body;
            BackColor = Theme.Window;

            int width = Theme.S(560);
            int y = pad;
            Label title = AddLabel("Connect Google Drive", Theme.Heading, Theme.Text, y);
            y += title.Height + Theme.S(6);
            Label help = AddLabel("Backups upload straight from your folders to Google Drive, and refreshes download straight back. "
                + "Nothing goes through Google Drive for desktop, so no second copy of your files is kept on drive C.",
                Theme.Body, Theme.TextMuted, y);
            y += help.Height + Theme.S(10);
            LinkLabel how = new LinkLabel();
            how.Text = "Create a free sign-in client in Google Cloud (one time)";
            how.Font = Theme.Body;
            how.AutoSize = true;
            how.Location = new Point(pad, y);
            how.LinkClicked += delegate
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SetupHelp) { UseShellExecute = true }); }
                catch (Exception ex) { Store.Log("could not open help: " + ex.Message); }
            };
            Controls.Add(how);
            y += how.Height + Theme.S(16);

            y = Field("Client ID", idBox, GoogleAuth.ClientId, y);
            y = Field("Client secret", secretBox, GoogleAuth.ClientSecret, y);
            secretBox.Box.UseSystemPasswordChar = true;

            Label heading = AddLabel("Accounts", Theme.Strong, Theme.Text, y);
            y += heading.Height + Theme.S(6);
            accounts.SetBounds(pad, y, field, Math.Max(1, drives.Count) * Theme.S(44));
            Controls.Add(accounts);
            y += accounts.Height + Theme.S(8);

            status.Font = Theme.Body;
            status.ForeColor = Theme.TextMuted;
            status.UseMnemonic = false;
            status.SetBounds(pad, y, field, Theme.S(44));
            Controls.Add(status);
            y += status.Height + Theme.S(8);

            Panel footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = Theme.S(64);
            footer.BackColor = Theme.Chrome;
            footer.Paint += new PaintEventHandler(delegate(object sender, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            });
            FlatButton addAccount = new FlatButton("+ Add Google Account…", false);
            addAccount.Location = new Point(pad, (footer.Height - addAccount.Height) / 2);
            addAccount.Click += delegate
            {
                if (working) return;
                SignIn(null);
            };
            footer.Controls.Add(addAccount);
            FlatButton done = new FlatButton("Done", true);
            done.Location = new Point(width - pad - done.Width, (footer.Height - done.Height) / 2);
            done.Click += delegate
            {
                if (working)
                {
                    GoogleAuth.CancelSignIn();
                    working = false;
                }
                SaveClient();
                DialogResult = DialogResult.OK;
                Close();
            };
            footer.Controls.Add(done);

            FlatButton cancelBtn = new FlatButton("Cancel", false);
            cancelBtn.Location = new Point(done.Left - Theme.S(8) - cancelBtn.Width, (footer.Height - cancelBtn.Height) / 2);
            cancelBtn.Click += delegate
            {
                if (working)
                {
                    GoogleAuth.CancelSignIn();
                    working = false;
                    status.ForeColor = Theme.TextMuted;
                    status.Text = "Google sign-in was canceled.";
                    ShowAccounts();
                    return;
                }
                DialogResult = DialogResult.Cancel;
                Close();
            };
            footer.Controls.Add(cancelBtn);

            Controls.Add(footer);
            AcceptButton = done;
            CancelButton = cancelBtn;
            ClientSize = new Size(width, y + footer.Height);
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (working)
                {
                    working = false;
                    GoogleAuth.CancelSignIn();
                }
            };
            ShowAccounts();
        }

        Label AddLabel(string text, Font font, Color color, int y)
        {
            Label label = new Label();
            label.Font = font;
            label.ForeColor = color;
            label.UseMnemonic = false;
            label.Text = text;
            Size size = TextRenderer.MeasureText(text, font, new Size(field, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            label.SetBounds(pad, y, field, size.Height + Theme.S(2));
            Controls.Add(label);
            return label;
        }

        int Field(string name, TextField box, string value, int y)
        {
            Label label = AddLabel(name, Theme.Strong, Theme.Text, y);
            y += label.Height + Theme.S(6);
            box.SetBounds(pad, y, field, Theme.S(32));
            box.Box.Text = value ?? "";
            Controls.Add(box);
            return y + box.Height + Theme.S(14);
        }

        void SaveClient()
        {
            string id = idBox.Box.Text.Trim();
            string secret = secretBox.Box.Text.Trim();
            if (id != GoogleAuth.ClientId || secret != GoogleAuth.ClientSecret) GoogleAuth.SaveClient(id, secret);
        }

        void ShowAccounts()
        {
            accounts.Controls.Clear();
            if (drives.Count == 0)
            {
                Label none = new Label();
                none.Text = "No Google Drive accounts connected yet. Enter your client ID and secret above, then click + Add Google Account… below to sign in.";
                none.Font = Theme.Body;
                none.ForeColor = Theme.TextMuted;
                none.SetBounds(0, 0, field, accounts.Height);
                accounts.Controls.Add(none);
                return;
            }
            int y = 0;
            foreach (Provider drive in drives)
            {
                string email = GoogleAuth.EmailOf(drive);
                bool signedIn = GoogleAuth.IsSignedIn(email);
                FlatButton action = new FlatButton(signedIn ? "Sign out" : "Sign in", !signedIn);
                action.Location = new Point(field - action.Width, y + (Theme.S(40) - action.Height) / 2);
                action.Enabled = !working;
                string account = email;
                action.Click += delegate { if (signedIn) SignOut(account); else SignIn(account); };
                Label name = new Label();
                name.UseMnemonic = false;
                name.AutoEllipsis = true;
                name.Font = Theme.Body;
                name.ForeColor = Theme.Text;
                name.Text = email + (signedIn ? "  ·  connected" : "  ·  not connected");
                name.SetBounds(0, y, action.Left - Theme.S(8), Theme.S(40));
                name.TextAlign = ContentAlignment.MiddleLeft;
                accounts.Controls.Add(name);
                accounts.Controls.Add(action);
                y += Theme.S(44);
            }
        }

        void SignOut(string email)
        {
            GoogleAuth.SignOut(email);
            status.Text = "Signed out of " + email + ". Backups for this drive stop until you sign in again.";
            ShowAccounts();
        }

        void SignIn(string email)
        {
            if (idBox.Box.Text.Trim().Length == 0 || secretBox.Box.Text.Trim().Length == 0)
            {
                status.ForeColor = Theme.CloudText;
                status.Text = "Enter the client ID and secret first. Use the link above to create them.";
                return;
            }
            SaveClient();
            working = true;
            ShowAccounts();
            status.ForeColor = Theme.TextMuted;
            status.Text = string.IsNullOrEmpty(email) ? "Opening browser to sign in to Google Drive… Click Cancel to abort." : "Finish signing in as " + email + " in your browser… Click Cancel to abort.";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string result = null;
                string error = null;
                bool canceled = false;
                try { result = GoogleAuth.SignIn(email, 300); }
                catch (OperationCanceledException) { canceled = true; }
                catch (Exception ex) { error = ex.Message; }
                try
                {
                    if (IsDisposed) return;
                    BeginInvoke(new Action(delegate
                    {
                        if (IsDisposed) return;
                        working = false;
                        if (canceled)
                        {
                            status.ForeColor = Theme.TextMuted;
                            status.Text = "Google sign-in was canceled.";
                        }
                        else if (error != null)
                        {
                            status.ForeColor = Theme.CloudText;
                            status.Text = error;
                        }
                        else if (!string.IsNullOrEmpty(email) && !string.Equals(result, email, StringComparison.OrdinalIgnoreCase))
                        {
                            status.ForeColor = Theme.CloudText;
                            status.Text = "You signed in as " + result + ". Sign in again and choose " + email + " in the browser.";
                        }
                        else
                        {
                            status.ForeColor = Theme.TextMuted;
                            status.Text = "Connected " + result + ". Google Drive is ready to upload and sync.";
                            bool exists = false;
                            foreach (Provider p in drives) if (string.Equals(GoogleAuth.EmailOf(p), result, StringComparison.OrdinalIgnoreCase)) exists = true;
                            if (!exists)
                            {
                                Provider p = new Provider();
                                p.Id = "google:" + result.ToLowerInvariant();
                                p.Kind = "GoogleDrive";
                                p.Name = "Google Drive";
                                p.Root = "gdrive:" + result.ToLowerInvariant();
                                p.Detail = result;
                                drives.Add(p);
                                accounts.Height = Math.Max(1, drives.Count) * Theme.S(44);
                            }
                        }
                        ShowAccounts();
                        try { Activate(); } catch { }
                    }));
                }
                catch (Exception) { }
            });
        }
    }
}
