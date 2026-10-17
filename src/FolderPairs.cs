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
        readonly CheckBox syncedBrowsingCheck = new CheckBox();
        readonly Label cloudLocation = new Label();
        FlatButton attachButton;
        FlatButton uploadDirectButton;
        FlatButton cloudDownloadButton;
        FlatButton cloudNewFolderButton;
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
            foreach (Provider provider in providers) AddDriveTab(provider);
            driveTabs.ResumeLayout();
            SelectDriveTab();
        }

        void AddDriveTab(Provider provider)
        {
            string title = provider.Title;
            string id = provider.Id;
            string key = provider.Root;
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
            // Only drives saved in the settings can be removed; detected cloud apps reappear on their own.
            if (provider.Custom)
            {
                ContextMenuStrip menu = new ContextMenuStrip();
                ToolStripMenuItem remove = MenuItem("Remove added cloud drive", Glyphs.Cancel, delegate { RemoveAddedDrive(id, key); });
                menu.Items.Add(remove);
                menu.Opening += delegate
                {
                    bool attached = false;
                    foreach (LinkFolder folder in Store.ReadLinks()) if (folder.ProviderId == id) attached = true;
                    remove.Enabled = !busy && !attached;
                    remove.ToolTipText = attached ? "Remove this drive's synced folder attachments first." : "Remove this saved drive entry. Files are kept.";
                };
                tab.ContextMenuStrip = menu;
                tab.Disposed += delegate { menu.Dispose(); };
            }
            driveTabs.Controls.Add(tab);
        }

        void RemoveAddedDrive(string id, string root)
        {
            if (busy) return;
            if (!Dialogs.Confirm(this, "Remove this added cloud drive?", root + "\r\n\r\nOnly the saved drive entry is removed. All files are kept. Local folders to back up belong under Attach folder.", null, "Remove drive", MessageKind.Warning)) return;
            try
            {
                foreach (LinkFolder folder in Store.ReadLinks())
                    if (folder.ProviderId == id) throw new InvalidOperationException("Remove this drive's synced folder attachments first.");
                Store.RemoveCustomProvider(id);
            }
            catch (Exception ex) { Dialogs.Error(this, "Couldn't remove added cloud drive", ex.Message); return; }
            BuildTree();
            Navigate("vfs:pc", true);
            ShowAction("Removed added cloud drive", "same");
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
            TableLayoutPanel bar = new TableLayoutPanel();
            bar.Dock = DockStyle.Top;
            bar.Height = Theme.S(48);
            bar.BackColor = Theme.Pane;
            bar.Padding = Theme.Pad(12, 6, 12, 6);
            bar.ColumnCount = 3;
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            cloudLocation.Dock = DockStyle.Fill;
            cloudLocation.Font = Theme.Body;
            cloudLocation.ForeColor = Theme.TextMuted;
            cloudLocation.TextAlign = ContentAlignment.MiddleLeft;
            cloudLocation.AutoEllipsis = true;
            cloudDownloadButton = new FlatButton("← Download to PC", false);
            cloudDownloadButton.Margin = Theme.Pad(4, 0, 4, 0);
            cloudDownloadButton.Click += delegate { DownloadSelected(); };
            tips.SetToolTip(cloudDownloadButton, "Download selected cloud files/folders to the local folder");
            cloudNewFolderButton = new FlatButton("+ New Folder", false);
            cloudNewFolderButton.Margin = new Padding(0);
            cloudNewFolderButton.Click += delegate { CreateNewCloudFolder(); };
            tips.SetToolTip(cloudNewFolderButton, "Create a new folder in this cloud drive");
            bar.Controls.Add(cloudLocation, 0, 0);
            bar.Controls.Add(cloudDownloadButton, 1, 0);
            bar.Controls.Add(cloudNewFolderButton, 2, 0);
            return bar;
        }

        Control BuildPairToolbar()
        {
            TableLayoutPanel bar = new TableLayoutPanel();
            bar.Dock = DockStyle.Top;
            bar.Height = Theme.S(48);
            bar.BackColor = Theme.Pane;
            bar.Padding = Theme.Pad(12, 6, 12, 6);
            bar.ColumnCount = 4;
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
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
            pairPicker.Margin = Theme.Pad(0, 3, 8, 0);
            pairPicker.DropDownWidth = Theme.S(600);
            pairPicker.AccessibleName = "Attached local folder";
            pairPicker.SelectedIndexChanged += delegate
            {
                if (updatingPairs || busy) return;
                PairChoice choice = pairPicker.SelectedItem as PairChoice;
                if (choice != null && choice.Folder != null) Navigate(choice.Folder.Local, true);
            };

            syncedBrowsingCheck.Text = "Synced browsing";
            syncedBrowsingCheck.Font = Theme.Body;
            syncedBrowsingCheck.ForeColor = Theme.Text;
            syncedBrowsingCheck.BackColor = Color.Transparent;
            syncedBrowsingCheck.AutoSize = true;
            syncedBrowsingCheck.Dock = DockStyle.Fill;
            syncedBrowsingCheck.Margin = Theme.Pad(0, 4, 8, 0);
            syncedBrowsingCheck.Checked = syncedBrowsing;
            syncedBrowsingCheck.Cursor = Cursors.Hand;
            syncedBrowsingCheck.AccessibleName = "Synced browsing";
            tips.SetToolTip(syncedBrowsingCheck, "Synced browsing: when checked, navigating one pane opens the matching folder in the other. When unchecked, both panes navigate independently.");
            syncedBrowsingCheck.CheckedChanged += delegate
            {
                if (syncedBrowsingCheck.Checked != syncedBrowsing)
                {
                    SetSyncedBrowsingState(syncedBrowsingCheck.Checked);
                }
            };

            uploadDirectButton = new FlatButton("Upload to Drive →", false);
            uploadDirectButton.Margin = Theme.Pad(0, 0, 6, 0);
            uploadDirectButton.Click += delegate { UploadSelected(); };
            tips.SetToolTip(uploadDirectButton, "Upload selected local files/folders to the cloud folder");
            attachButton = new FlatButton("Attach folder…", true);
            attachButton.Margin = new Padding(0);
            attachButton.Click += AttachFolder_Click;
            tips.SetToolTip(attachButton, "Choose a folder from any drive on this PC");
            bar.Controls.Add(pairPicker, 0, 0);
            bar.Controls.Add(syncedBrowsingCheck, 1, 0);
            bar.Controls.Add(uploadDirectButton, 2, 0);
            bar.Controls.Add(attachButton, 3, 0);
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
            bool isGoogle = provider != null && provider.Root.StartsWith("gdrive:", StringComparison.OrdinalIgnoreCase);
            string remote = active == null ? null : Store.ResolveRemote(active, providers);
            bool remoteReady = available && (isGoogle ? provider.IsOnline : Directory.Exists(remote ?? ""));
            uploadButton.Enabled = (available && Directory.Exists(active.Local)) || (!busy && isGoogle && provider.IsOnline);
            refreshButton.Enabled = remoteReady || (!busy && isGoogle && provider.IsOnline);
            pairPicker.Enabled = !busy && pairPicker.Items.Count > 1;
            attachButton.Enabled = !busy;
            if (syncedBrowsingCheck != null) syncedBrowsingCheck.Enabled = true;
            if (uploadDirectButton != null) uploadDirectButton.Enabled = !busy;
            if (cloudDownloadButton != null) cloudDownloadButton.Enabled = !busy;
            if (cloudNewFolderButton != null) cloudNewFolderButton.Enabled = !busy;
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
                    if (!EnsureGoogleSignIn(dialog.Chosen)) return;
                    AttachAndBackup(dialog.LocalPath, dialog.Chosen.Id, dialog.RelativePath);
                }
            }
        }

        void AttachAndBackup(string local, string providerId, string remoteRelative)
        {
        SetBusy(true, "Attaching folder and backing up…");
        SyncProgress tracked = new SyncProgress("Push");
        TrackProgress(tracked);
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
                    stats = SyncEngine.SyncLink(attached, "Push", false, tracked);
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
            if (link == null) return StartDirectTransfer(toLocal, destination, sources);
            List<Provider> found = Store.DetectProviders();
            string cloud = Store.ResolveRemote(link, found);
            string targetRoot = toLocal ? link.Local : cloud;
            string sourceRoot = toLocal ? cloud : link.Local;
            if (string.IsNullOrEmpty(targetRoot) || !Store.IsUnder(destination, targetRoot))
            {
                if (StartDirectTransfer(toLocal, destination, sources)) return true;
                ShowAction("Drop inside the selected folder pair.", "warn");
                return true;
            }
            List<string> existing = new List<string>();
            foreach (string source in sources)
            {
                string target = Path.Combine(destination, Path.GetFileName(source.TrimEnd('\\')));
                if (File.Exists(target)) existing.Add("File\t" + Path.GetFileName(target));
                else if (Directory.Exists(target)) existing.Add("Folder\t" + Path.GetFileName(target));
            }
            if (existing.Count > 0 && !Dialogs.Confirm(this,
                existing.Count == 1 ? "Replace an existing item?" : "Replace " + existing.Count + " existing items?",
                "These names already exist in " + destination + ". Files with the same name are replaced and folders are merged. Other files are kept.",
                existing, "Replace", MessageKind.Warning))
                return true;
            Provider pairDrive = Store.FindProvider(found, link.ProviderId);
            bool google = GoogleAuth.EmailOf(pairDrive) != null;
            if (google && !EnsureGoogleSignIn(pairDrive)) return true;
            SetBusy(true,"Copying to " + (toLocal ? "this PC…" : "the cloud…"));
            SyncProgress tracked = null;
            if (google)
            {
                tracked = new SyncProgress(toLocal ? "Pull" : "Push");
                tracked.StartFolder(link.Local, 1, 1);
                TrackProgress(tracked);
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                int skipped = 0;
                try
                {
                    using (SyncEngine.LockState(180))
                    {
                        Provider provider = Store.FindProvider(found, link.ProviderId);
                        if (provider == null || !provider.IsOnline || !Directory.Exists(link.Local))
                            throw new IOException("The selected folder pair is not available.");
                        List<string> dirs = new List<string>();
                        List<string> files = new List<string>();
                        List<KeyValuePair<string, string>> items = new List<KeyValuePair<string, string>>();
                        foreach (string source in sources)
                        {
                            if (!Store.IsUnder(source, sourceRoot) || Store.Norm(source) == Store.Norm(sourceRoot))
                                throw new IOException("Drag files or subfolders from the other side of this folder pair.");
                            string rel = Store.Relative(Path.Combine(destination, Path.GetFileName(source)), targetRoot);
                            // Google Drive items move through the API instead of Google Drive for desktop's cache.
                            if (google) items.Add(new KeyValuePair<string, string>(toLocal ? Store.Relative(source, cloud) : source, rel));
                            else skipped += SyncEngine.CopyInto(source, targetRoot, rel, dirs, files);
                        }
                        if (google)
                            skipped += SyncEngine.TransferThroughRemote(SyncEngine.RemoteFor(provider, link.RemoteRelative, cloud),
                                link.Local, toLocal, items, dirs, files, tracked);
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
                        else if (skipped > 0) ShowAction("Copied to the selected folder pair  ·  " + Plural(skipped, "linked item") + " skipped", "warn");
                        else ShowAction("Copied to the selected folder pair", "same");
                    }));
                }
                catch (InvalidOperationException) { }
            });
            return true;
        }

        bool StartDirectTransfer(bool toLocal, string destination, string[] sources)
        {
            if (sources == null || sources.Length == 0) return false;
            List<Provider> found = Store.DetectProviders();
            Provider gProvider = null;
            string googlePath = (destination != null && destination.StartsWith("gdrive:", StringComparison.OrdinalIgnoreCase))
                ? destination
                : (sources.Length > 0 && sources[0].StartsWith("gdrive:", StringComparison.OrdinalIgnoreCase) ? sources[0] : null);

            if (googlePath != null)
            {
                foreach (Provider p in found)
                {
                    if (p.Root.StartsWith("gdrive:", StringComparison.OrdinalIgnoreCase) &&
                        googlePath.StartsWith(p.Root, StringComparison.OrdinalIgnoreCase))
                    {
                        gProvider = p;
                        break;
                    }
                }
            }
            if (gProvider == null && !string.IsNullOrEmpty(activeProviderId))
                gProvider = Store.FindProvider(found, activeProviderId);
            if (gProvider == null || !gProvider.Root.StartsWith("gdrive:", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Provider p in found)
                {
                    if (p.Root.StartsWith("gdrive:", StringComparison.OrdinalIgnoreCase)) { gProvider = p; break; }
                }
            }
            bool involvesGoogle = (destination != null && destination.StartsWith("gdrive:", StringComparison.OrdinalIgnoreCase))
                || (sources.Length > 0 && sources[0].StartsWith("gdrive:", StringComparison.OrdinalIgnoreCase))
                || (gProvider != null && gProvider.IsOnline);

            if (involvesGoogle)
            {
                if (gProvider == null)
                {
                    if (!EnsureGoogleSignIn(null)) return true;
                    found = Store.DetectProviders();
                    if (googlePath != null)
                    {
                        foreach (Provider p in found)
                        {
                            if (p.Root.StartsWith("gdrive:", StringComparison.OrdinalIgnoreCase) &&
                                googlePath.StartsWith(p.Root, StringComparison.OrdinalIgnoreCase))
                            {
                                gProvider = p;
                                break;
                            }
                        }
                    }
                    if (gProvider == null)
                    {
                        foreach (Provider p in found)
                        {
                            if (p.Root.StartsWith("gdrive:", StringComparison.OrdinalIgnoreCase)) { gProvider = p; break; }
                        }
                    }
                    if (gProvider == null) return true;
                }

                string email = GoogleAuth.EmailOf(gProvider);
                if (email == null || !EnsureGoogleSignIn(gProvider)) return true;
                GoogleDriveClient client = GoogleAuth.Client(email);
                SetBusy(true, toLocal ? "Downloading from Google Drive…" : "Uploading to Google Drive…");
                SyncProgress tracked = new SyncProgress(toLocal ? "Pull" : "Push");
                TrackProgress(tracked);
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string error = null;
                    int count = 0;
                    try
                    {
                        string rootId = client.RootId();
                        if (toLocal)
                        {
                            string localDest = destination;
                            if (string.IsNullOrEmpty(localDest) || !Directory.Exists(localDest))
                            {
                                localDest = !string.IsNullOrEmpty(localKey) && Directory.Exists(localKey) ? localKey : Store.LocalRoot();
                            }
                            Directory.CreateDirectory(localDest);
                            foreach (string source in sources)
                            {
                                if (tracked.Canceled) break;
                                string rel = Store.Relative(source, gProvider.Root);
                                if (string.IsNullOrEmpty(rel)) rel = Path.GetFileName(source.TrimEnd('\\'));
                                string parentRel = Path.GetDirectoryName(rel);
                                string name = Path.GetFileName(rel);
                                string parentFolderId = string.IsNullOrEmpty(parentRel) ? rootId : client.ResolvePathFolderId(rootId, parentRel, false);
                                string folderId = parentFolderId != null ? client.FindFolder(parentFolderId, name) : null;
                                if (folderId != null)
                                {
                                    count += client.DownloadDirectoryDirect(folderId, Path.Combine(localDest, name), tracked);
                                }
                                else
                                {
                                    FileRecord rec = parentFolderId != null ? client.FindFile(parentFolderId, name) : null;
                                    if (rec != null)
                                    {
                                        client.Download(rec, Path.Combine(localDest, name), tracked);
                                        count++;
                                    }
                                }
                            }
                        }
                        else
                        {
                            string cloudDest = destination;
                            if (string.IsNullOrEmpty(cloudDest) || !cloudDest.StartsWith("gdrive:", StringComparison.OrdinalIgnoreCase))
                            {
                                cloudDest = (!string.IsNullOrEmpty(driveKey) && driveKey.StartsWith("gdrive:", StringComparison.OrdinalIgnoreCase)) ? driveKey : gProvider.Root;
                            }
                            string destRel = Store.Relative(cloudDest, gProvider.Root) ?? "";
                            string targetFolderId = string.IsNullOrEmpty(destRel) ? rootId : client.ResolvePathFolderId(rootId, destRel, true);
                            foreach (string source in sources)
                            {
                                if (tracked.Canceled) break;
                                if (Directory.Exists(source))
                                {
                                    count += client.UploadDirectoryDirect(source, targetFolderId, tracked);
                                }
                                else if (File.Exists(source))
                                {
                                    string fileName = Path.GetFileName(source);
                                    FileRecord existing = client.FindFile(targetFolderId, fileName);
                                    DateTime mtime = File.GetLastWriteTimeUtc(source);
                                    client.Upload(source, fileName, targetFolderId, existing == null ? null : existing.Id, mtime, tracked);
                                    count++;
                                }
                            }
                        }
                    }
                    catch (Exception ex) { error = ex.Message; }
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            SetBusy(false, null);
                            RefreshAll();
                            if (error != null) Dialogs.Error(this, "Transfer failed", error);
                            else ShowAction(toLocal ? "Downloaded " + Plural(count, "item") + " from Google Drive" : "Uploaded " + Plural(count, "item") + " to Google Drive", "same");
                        }));
                    }
                    catch (InvalidOperationException) { }
                });
                return true;
            }

            if (Directory.Exists(destination))
            {
                SetBusy(true, "Copying files…");
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string error = null;
                    int copied = 0;
                    try
                    {
                        List<string> dirs = new List<string>();
                        List<string> files = new List<string>();
                        foreach (string s in sources)
                        {
                            copied += SyncEngine.CopyInto(s, destination, Path.GetFileName(s.TrimEnd('\\')), dirs, files);
                        }
                    }
                    catch (Exception ex) { error = ex.Message; }
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            SetBusy(false, null);
                            RefreshAll();
                            if (error != null) Dialogs.Error(this, "Copy failed", error);
                            else ShowAction("Copied " + Plural(copied, "item"), "same");
                        }));
                    }
                    catch (InvalidOperationException) { }
                });
                return true;
            }

            return false;
        }

        void CreateNewCloudFolder()
        {
            if (busy) return;
            string targetKey = driveKey;
            Provider gProvider = null;
            if (!string.IsNullOrEmpty(targetKey) && targetKey.StartsWith("gdrive:", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Provider p in providers)
                {
                    if (p.Root.StartsWith("gdrive:", StringComparison.OrdinalIgnoreCase) &&
                        targetKey.StartsWith(p.Root, StringComparison.OrdinalIgnoreCase))
                    {
                        gProvider = p;
                        break;
                    }
                }
            }
            if (gProvider == null && !string.IsNullOrEmpty(activeProviderId))
                gProvider = Store.FindProvider(providers, activeProviderId);
            if (gProvider == null)
            {
                foreach (Provider p in providers)
                {
                    if (p.Root.StartsWith("gdrive:", StringComparison.OrdinalIgnoreCase)) { gProvider = p; break; }
                }
            }
            if (string.IsNullOrEmpty(targetKey) && gProvider != null) targetKey = gProvider.Root;
            if (string.IsNullOrEmpty(targetKey)) return;
            string folderName = Dialogs.Prompt(this, "New Cloud Folder", "Enter the name of the new folder:", "New folder");
            if (string.IsNullOrWhiteSpace(folderName)) return;
            folderName = folderName.Trim();
            if (targetKey.StartsWith("gdrive:", StringComparison.OrdinalIgnoreCase))
            {
                if (gProvider == null) return;
                string email = GoogleAuth.EmailOf(gProvider);
                if (string.IsNullOrEmpty(email) || !EnsureGoogleSignIn(gProvider)) return;
                GoogleDriveClient client = GoogleAuth.Client(email);
                SetBusy(true, "Creating cloud folder…");
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string error = null;
                    try
                    {
                        string rootId = client.RootId();
                        string rel = Store.Relative(targetKey, gProvider.Root) ?? "";
                        string parentId = string.IsNullOrEmpty(rel) ? rootId : client.ResolvePathFolderId(rootId, rel, true);
                        client.CreateFolder(parentId, folderName);
                    }
                    catch (Exception ex) { error = ex.Message; }
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            SetBusy(false, null);
                            if (error != null) Dialogs.Error(this, "Could not create folder", error);
                            else
                            {
                                RefreshAll();
                                ShowAction("Created cloud folder: " + folderName, "same");
                            }
                        }));
                    }
                    catch (InvalidOperationException) { }
                });
            }
            else if (Directory.Exists(targetKey))
            {
                try
                {
                    Directory.CreateDirectory(Path.Combine(targetKey, folderName));
                    RefreshAll();
                    ShowAction("Created folder: " + folderName, "same");
                }
                catch (Exception ex) { Dialogs.Error(this, "Could not create folder", ex.Message); }
            }
        }

        void UploadSelected()
        {
            if (busy) return;
            List<string> selected = SelectedPaths(localList);
            if (selected.Count == 0 && !string.IsNullOrEmpty(localKey) && Directory.Exists(localKey))
            {
                foreach (string file in Directory.GetFiles(localKey)) selected.Add(file);
                foreach (string dir in Directory.GetDirectories(localKey)) selected.Add(dir);
            }
            if (selected.Count == 0)
            {
                Dialogs.Info(this, "Select items to upload", "Choose files or folders on the left to upload to Google Drive.");
                return;
            }
            Provider gProvider = Store.FindProvider(providers, activeProviderId);
            if (gProvider == null)
            {
                foreach (Provider p in providers)
                {
                    if (p.Root.StartsWith("gdrive:", StringComparison.OrdinalIgnoreCase)) { gProvider = p; break; }
                }
            }
            if (gProvider != null && !gProvider.IsOnline)
            {
                if (!EnsureGoogleSignIn(gProvider)) return;
            }
            else if (gProvider == null && (string.IsNullOrEmpty(driveKey) || driveKey.StartsWith("gdrive:", StringComparison.OrdinalIgnoreCase)))
            {
                if (!EnsureGoogleSignIn(null)) return;
                providers = Store.DetectProviders();
                foreach (Provider p in providers)
                {
                    if (p.Root.StartsWith("gdrive:", StringComparison.OrdinalIgnoreCase)) { gProvider = p; break; }
                }
                if (gProvider != null && string.IsNullOrEmpty(driveKey))
                {
                    driveKey = gProvider.Root;
                }
            }
            StartTransfer(false, driveKey, selected.ToArray());
        }

        void DownloadSelected()
        {
            if (busy) return;
            List<string> selected = SelectedPaths(list);
            if (selected.Count == 0)
            {
                Dialogs.Info(this, "Select items to download", "Choose files or folders on the right to download to this PC.");
                return;
            }
            StartTransfer(true, localKey, selected.ToArray());
        }
    }
}
