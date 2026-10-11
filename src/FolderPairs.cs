using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace DriveFolderSync
{
    public partial class MainForm
    {
        readonly FlowLayoutPanel driveTabs = new FlowLayoutPanel();
        readonly ComboBox pairPicker = new ComboBox();
        readonly Label cloudLocation = new Label();
        FlatButton attachButton;
        string activeProviderId = "";
        string activeLinkId = "";
        bool updatingPairs;
        string tabSignature;

        sealed class PairChoice
        {
            public LinkFolder Folder;
            public override string ToString()
            {
                if (Folder == null) return "Select an attached folder";
                string cloud = string.IsNullOrEmpty(Folder.RemoteRelative) ? "Drive root" : Folder.RemoteRelative;
                return Folder.Local + "  →  " + cloud;
            }
        }

        Control BuildDriveTabs()
        {
            driveTabs.Dock = DockStyle.Top;
            driveTabs.Height = Theme.S(58);
            driveTabs.AutoScroll = true;
            driveTabs.WrapContents = false;
            driveTabs.Padding = Theme.Pad(10, 5, 10, 3);
            driveTabs.BackColor = Theme.Chrome;
            driveTabs.AccessibleName = "Cloud drives";
            return driveTabs;
        }

        void RefreshDriveTabs()
        {
            string signature = "";
            foreach (Provider provider in providers) signature += provider.Id + "|" + provider.Title + "|" + provider.Root + "\n";
            if (signature == tabSignature) { SelectDriveTab(); return; }
            tabSignature = signature;
            driveTabs.SuspendLayout();
            while (driveTabs.Controls.Count > 0) driveTabs.Controls[0].Dispose();
            foreach (Provider provider in providers) AddDriveTab(provider.Title, provider.Id, provider.Root);
            driveTabs.ResumeLayout();
            SelectDriveTab();
        }

        void AddDriveTab(string title, string id, string key)
        {
            Button tab = new Button();
            tab.Text = title;
            tab.Tag = id;
            tab.AccessibleName = title;
            tab.UseMnemonic = false;
            tab.AutoSize = true;
            tab.Height = Theme.S(32);
            tab.MinimumSize = Theme.S(90, 32);
            tab.Padding = Theme.Pad(10, 0, 10, 0);
            tab.Margin = Theme.Pad(0, 0, 6, 0);
            tab.Font = Theme.Body;
            tab.FlatStyle = FlatStyle.Flat;
            tab.FlatAppearance.BorderSize = 1;
            tab.FlatAppearance.BorderColor = Theme.Border;
            tab.Click += delegate { Navigate(key, true); };
            tips.SetToolTip(tab, key.StartsWith("vfs:") ? title : key);
            driveTabs.Controls.Add(tab);
        }

        void SelectDriveTab()
        {
            string selected = activeProviderId;
            foreach (Button tab in driveTabs.Controls)
            {
                bool active = (string)tab.Tag == selected;
                tab.BackColor = active ? Theme.RowSelected : Theme.Chrome;
                tab.ForeColor = Theme.Text;
                tab.Font = active ? Theme.Strong : Theme.Body;
            }
        }

        Control BuildCloudToolbar()
        {
            Panel bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = Theme.S(48);
            bar.BackColor = Theme.Pane;
            bar.Padding = Theme.Pad(12, 6, 12, 6);
            cloudLocation.Dock = DockStyle.Fill;
            cloudLocation.Font = Theme.Body;
            cloudLocation.ForeColor = Theme.TextMuted;
            cloudLocation.TextAlign = ContentAlignment.MiddleLeft;
            cloudLocation.AutoEllipsis = true;
            bar.Controls.Add(cloudLocation);
            return bar;
        }

        Control BuildPairToolbar()
        {
            TableLayoutPanel bar = new TableLayoutPanel();
            bar.Dock = DockStyle.Top;
            bar.Height = Theme.S(48);
            bar.BackColor = Theme.Pane;
            bar.Padding = Theme.Pad(12, 6, 12, 6);
            bar.ColumnCount = 2;
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pairPicker.DropDownStyle = ComboBoxStyle.DropDownList;
            pairPicker.DrawMode = DrawMode.OwnerDrawFixed;
            pairPicker.ItemHeight = Theme.S(22);
            pairPicker.DrawItem += delegate(object sender, DrawItemEventArgs e)
            {
                e.DrawBackground();
                string text = e.Index >= 0 && e.Index < pairPicker.Items.Count ? pairPicker.Items[e.Index].ToString() : "Select an attached folder";
                Theme.DrawText(e.Graphics, text, Theme.Body, Rectangle.Inflate(e.Bounds, -Theme.S(6), 0), Theme.Text, TextFormatFlags.Left);
                e.DrawFocusRectangle();
            };
            pairPicker.Font = Theme.Body;
            pairPicker.Dock = DockStyle.Fill;
            pairPicker.Margin = Theme.Pad(0, 3, 10, 0);
            pairPicker.DropDownWidth = Theme.S(600);
            pairPicker.AccessibleName = "Attached local folder";
            pairPicker.SelectedIndexChanged += delegate
            {
                if (updatingPairs || busy) return;
                PairChoice choice = pairPicker.SelectedItem as PairChoice;
                if (choice != null && choice.Folder != null) Navigate(choice.Folder.Local, true);
            };
            attachButton = new FlatButton("Attach folder…", true);
            attachButton.Margin = new Padding(0);
            attachButton.Click += AttachFolder_Click;
            tips.SetToolTip(attachButton, "Choose a folder from any drive on this PC");
            bar.Controls.Add(pairPicker, 0, 0);
            bar.Controls.Add(attachButton, 1, 0);
            return bar;
        }

        void UpdateFolderControls(Page page)
        {
            cloudLocation.Text = page == null || string.IsNullOrEmpty(page.ProviderId) ? "Choose a cloud drive above" : page.DriveKey;
            tips.SetToolTip(cloudLocation, cloudLocation.Text);
            activeProviderId = page == null ? "" : page.ProviderId;
            activeLinkId = page == null ? "" : page.LinkId;
            links = Store.ReadLinks();
            updatingPairs = true;
            try
            {
                pairPicker.Items.Clear();
                pairPicker.Items.Add(new PairChoice());
                int selected = 0;
                foreach (LinkFolder link in links)
                {
                    if (!string.Equals(link.ProviderId, activeProviderId, StringComparison.OrdinalIgnoreCase)) continue;
                    pairPicker.Items.Add(new PairChoice { Folder = link });
                    if (link.Id == activeLinkId) selected = pairPicker.Items.Count - 1;
                }
                pairPicker.SelectedIndex = selected;
            }
            finally { updatingPairs = false; }
            RefreshDriveTabs();
            SelectDriveTab();
            UpdatePairActions();
        }

        void UpdatePairActions()
        {
            LinkFolder active = links.Find(delegate(LinkFolder link) { return link.Id == activeLinkId; });
            Provider provider = Store.FindProvider(providers, activeProviderId);
            bool available = !busy && active != null && provider != null && provider.IsOnline;
            uploadButton.Enabled = available && Directory.Exists(active.Local);
            refreshButton.Enabled = available && Directory.Exists(Store.ResolveRemote(active, providers) ?? "");
            pairPicker.Enabled = !busy && pairPicker.Items.Count > 1;
            attachButton.Enabled = !busy;
        }

        void CheckForUpdates(bool automatic = false)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateManager.CheckAndInstall(delegate(string message)
                {
                    try { BeginInvoke(new Action(delegate { ShowAction(message, "info"); })); }
                    catch (InvalidOperationException) { }
                }, delegate { return !busy && !IsDisposed && (!automatic || UpdateManager.AutomaticEnabled); });
            });
        }

        void AttachFolder_Click(object sender, EventArgs e)
        {
            if (busy) return;
            using (FolderBrowserDialog picker = new FolderBrowserDialog())
            {
                picker.Description = "Choose a local folder to back up. It can be on any drive and stays in its current location.";
                picker.ShowNewFolderButton = true;
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                List<Provider> found = Store.DetectProviders();
                Provider provider = Store.FindProvider(found, activeProviderId);
                if (provider == null)
                    foreach (Provider candidate in found) if (candidate.IsOnline) { provider = candidate; break; }
                string relative = "";
                Store.Place place = Store.FindPlace(current, found);
                if (place != null && provider != null && place.Provider.Id == provider.Id) relative = place.Relative;
                string leaf = new DirectoryInfo(picker.SelectedPath).Name;
                relative = Path.Combine(relative, leaf);
                using (LinkDialog dialog = new LinkDialog(found, provider, picker.SelectedPath, relative))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    AttachAndBackup(dialog.LocalPath, dialog.Chosen.Id, dialog.RelativePath);
                }
            }
        }

        void AttachAndBackup(string local, string providerId, string remoteRelative)
        {
        SetBusy(true, "Attaching folder and backing up…");
        ThreadPool.QueueUserWorkItem(delegate
        {
            string error = null;
            LinkFolder attached = null;
            SyncStats stats = null;
            try
            {
                attached = SyncEngine.Link(local, providerId, remoteRelative);
                using (SyncEngine.LockState(180))
                {
                    List<LinkFolder> saved = Store.ReadLinks();
                    attached = saved.Find(delegate(LinkFolder link) { return link.Id == attached.Id; });
                    stats = SyncEngine.SyncLink(attached, "Push", false);
                    Store.SaveLinks(saved);
                }
            }
            catch (Exception ex) { error = ex.Message; }
            try
            {
                BeginInvoke(new Action(delegate
                {
                    SetBusy(false, null);
                    BuildTree();
                    if (attached != null) Navigate(attached.Local, true);
                    if (error != null) Dialogs.Error(this, "Couldn't finish the initial backup", error);
                    else ShowAction(Summary(stats), stats.Errors > 0 ? "cloud" : stats.Conflict > 0 ? "warn" : "same");
                }));
            }
            catch (InvalidOperationException) { }
        });
        }

        bool StartPairTransfer(bool toLocal, string destination, string[] sources)
        {
            LinkFolder link = CurrentLink();
            if (link == null) return false;
            List<Provider> found = Store.DetectProviders();
            string cloud = Store.ResolveRemote(link, found);
            string targetRoot = toLocal ? link.Local : cloud;
            string sourceRoot = toLocal ? cloud : link.Local;
            if (string.IsNullOrEmpty(targetRoot) || !Store.IsUnder(destination, targetRoot))
            {
                ShowAction("Drop inside the selected folder pair.", "warn");
                return true;
            }
            SetBusy(true, "Copying to " + (toLocal ? "this PC…" : "the cloud…"));
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                try
                {
                    using (SyncEngine.LockState(180))
                    {
                        Provider provider = Store.FindProvider(found, link.ProviderId);
                        if (provider == null || !provider.IsOnline || !Directory.Exists(link.Local))
                            throw new IOException("The selected folder pair is not available.");
                        List<string> dirs = new List<string>();
                        List<string> files = new List<string>();
                        foreach (string source in sources)
                        {
                            if (!Store.IsUnder(source, sourceRoot) || Store.Norm(source) == Store.Norm(sourceRoot))
                                throw new IOException("Drag files or subfolders from the other side of this folder pair.");
                            string rel = Store.Relative(Path.Combine(destination, Path.GetFileName(source)), targetRoot);
                            SyncEngine.CopyInto(source, targetRoot, rel, dirs, files);
                        }
                        if (link.Partial) SyncEngine.RememberSelection(link.ProviderId, link.Local, dirs, files);
                    }
                }
                catch (Exception ex) { error = ex.Message; }
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        SetBusy(false, null);
                        RefreshAll();
                        if (error != null) Dialogs.Error(this, "Couldn't copy", error);
                        else ShowAction("Copied to the selected folder pair", "same");
                    }));
                }
                catch (InvalidOperationException) { }
            });
            return true;
        }
    }
}
