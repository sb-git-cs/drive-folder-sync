using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace DriveFolderSync
{
    public static class UiSelfTest
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr window, int index);

        static void NoSideScroll(MainForm form, string when)
        {
            foreach (string name in new string[] { "list", "localList" })
            {
                FileList view = (FileList)Field(form, name);
                if ((GetWindowLong(view.Handle, -16) & 0x00100000) == 0) continue;
                List<string> widths = new List<string>();
                foreach (ColumnHeader column in view.Columns) widths.Add(column.Width.ToString());
                throw new Exception(name + " needs a horizontal scrollbar " + when + ": client=" + view.ClientSize.Width + " columns=" + string.Join(",", widths.ToArray()));
            }
        }

        // A running sync shows amount, speed and time left in the status bar, even at the minimum size.
        static void ShowSampleProgress(MainForm form, string output)
        {
            SyncProgress sample = new SyncProgress("Push");
            sample.StartFolder(@"D:\project-documents", 1, 2);
            sample.Plan(45, 1200L * 1024 * 1024);
            sample.BeginFile(@"bids\Draft Bid_Tech Bid.docx");
            sample.Copied(0);
            Thread.Sleep(1100);
            sample.Copied(96L * 1024 * 1024);
            MethodInfo track = typeof(MainForm).GetMethod("TrackProgress", BindingFlags.Instance | BindingFlags.NonPublic);
            track.Invoke(form, new object[] { sample });
            try
            {
                ToolStripStatusLabel label = (ToolStripStatusLabel)Field(form, "actionLabel");
                ToolStripProgressBar bar = (ToolStripProgressBar)Field(form, "progress");
                bar.Visible = true;
                foreach (Size size in new Size[] { Theme.S(980, 560), Theme.S(1280, 760) })
                {
                    form.Size = size;
                    WaitForLoad(form);
                    if (!label.Text.Contains("96 MB of 1.2 GB") || !label.Text.Contains("MB/s") || !label.Text.Contains("left") || !label.Text.Contains("file 1 of 45"))
                        throw new Exception("sync progress text was incomplete: " + label.Text);
                    if (label.Placement != ToolStripItemPlacement.Main || bar.Placement != ToolStripItemPlacement.Main)
                        throw new Exception("sync progress did not fit the status bar at " + size.Width + " px");
                    if (bar.Style != ProgressBarStyle.Continuous || bar.Value != 8) throw new Exception("progress bar did not show the percentage: " + bar.Value);
                }
                Capture(form, Path.Combine(output, "sync-progress.png"));
            }
            finally
            {
                track.Invoke(form, new object[] { null });
                ((ToolStripProgressBar)Field(form, "progress")).Visible = false;
                form.Size = Theme.S(980, 560);
                WaitForLoad(form);
            }
        }

        static object Field(object target, string name)
        {
            return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        }

        static void Navigate(MainForm form, string path)
        {
            form.GetType().GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { path, true });
            WaitForLoad(form);
        }

        static void WaitForLoad(MainForm form)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            do
            {
                Application.DoEvents();
                Thread.Sleep(10);
                if (DateTime.UtcNow > deadline) throw new Exception("Explorer loading timed out");
            } while ((bool)Field(form, "loading"));
            if (Field(form, "loadError") != null) throw new Exception("Explorer failed to load: " + Field(form, "loadError"));
            Application.DoEvents();
        }

        static ToolStripMenuItem RemoveItem(Control source, string text = "Remove synced folder")
        {
            ContextMenuStrip menu = source.ContextMenuStrip;
            menu.Show(source, new Point(10, 10));
            Application.DoEvents();
            ToolStripMenuItem found = null;
            foreach (ToolStripItem item in menu.Items)
                if (item.Text == text) found = item as ToolStripMenuItem;
            menu.Close();
            return found;
        }

        static void AnswerRemoval(ToolStripMenuItem item, string local, string cloud, bool confirm)
        {
            bool seen = false, correct = false;
            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer())
            {
                timer.Interval = 50;
                timer.Tick += delegate
                {
                    Form prompt = null;
                    foreach (Form open in Application.OpenForms) if (open is MessageDialog) { prompt = open; break; }
                    if (prompt == null) return;
                    timer.Stop();
                    seen = true;
                    foreach (Control control in prompt.Controls)
                        if (control is Label && control.Text.Contains(local) && control.Text.Contains(cloud)) correct = true;
                    if (correct && confirm) prompt.AcceptButton.PerformClick();
                    else prompt.CancelButton.PerformClick();
                };
                timer.Start();
                item.PerformClick();
            }
            if (!seen || !correct) throw new Exception("remove confirmation targeted the wrong local/cloud attachment");
        }

        // Runs an action that opens a message dialog, answers it, and returns the dialog heading.
        static string AnswerNext(bool confirm, Action action)
        {
            string heading = null;
            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer())
            {
                timer.Interval = 50;
                timer.Tick += delegate
                {
                    Form prompt = null;
                    foreach (Form open in Application.OpenForms) if (open is MessageDialog) { prompt = open; break; }
                    if (prompt == null) return;
                    timer.Stop();
                    foreach (Control control in prompt.Controls)
                        if (control is Label && control.Font == Theme.Heading) heading = control.Text;
                    if (confirm) prompt.AcceptButton.PerformClick();
                    else prompt.CancelButton.PerformClick();
                };
                timer.Start();
                action();
                timer.Stop();
            }
            return heading;
        }

        static void Capture(Form form, string path)
        {
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        public static int Run(string output)
        {
            string sandbox = Path.Combine(Path.GetTempPath(), "dfs-ui-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(output);
            Store.SetOverrides(sandbox, Path.Combine(sandbox, "sync.log"));
            try
            {
                Provider drive = new Provider();
                drive.Id = "ui:cloud"; drive.Name = "Project Cloud"; drive.Kind = "Folder";
                drive.Root = Path.Combine(sandbox, "Cloud drive"); drive.Detail = "work@example.com";
                Directory.CreateDirectory(drive.Root);
                Store.SaveCustomProvider(drive);
                string local = Path.Combine(sandbox, "Local projects", "project-documents");
                Directory.CreateDirectory(Path.Combine(local, "Notes"));
                File.WriteAllText(Path.Combine(local, "Brief.txt"), "Project brief");
                File.WriteAllText(Path.Combine(local, "Local draft.txt"), "New draft");
                LinkFolder pair = SyncEngine.Link(local, drive.Id, @"Backups\project-documents");
                SyncEngine.SyncLink(pair, "Push", true);
                string cloud = Store.ResolveRemote(pair, Store.DetectProviders());
                File.Delete(Path.Combine(local, "Local draft.txt"));
                File.WriteAllText(Path.Combine(cloud, "Cloud notes.txt"), "Cloud notes");
                File.WriteAllText(Path.Combine(local, "New task.txt"), "New local task");
                Store.SaveLinks(new List<LinkFolder> { pair });
                using (MainForm form = new MainForm(null))
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-32000, -32000);
                    form.Show();
                    WaitForLoad(form);
                    Capture(form, Path.Combine(output, "overview.png"));
                    Navigate(form, cloud);
                    if ((string)Field(form, "localKey") != local) throw new Exception("right pane did not open the attached local root");
                    FileList left = (FileList)Field(form, "list");
                    FileList right = (FileList)Field(form, "localList");
                    if (left.Items.Count != 4 || right.Items.Count != 3) throw new Exception("paired file lists did not show the correct roots");
                    bool cloudOnly = false;
                    foreach (ListViewItem item in left.Items)
                    {
                        Row row = item.Tag as Row;
                        if (row.Name == "Local draft.txt") cloudOnly = row.Status == "Only in cloud" && row.Kind == "cloud";
                    }
                    if (!cloudOnly) throw new Exception("cloud-only file was not highlighted in the explorer");
                    FlowLayoutPanel tabs = (FlowLayoutPanel)Field(form, "driveTabs");
                    if (!tabs.AutoScroll || tabs.WrapContents || tabs.Controls.Count != Store.DetectProviders().Count)
                        throw new Exception("horizontal bar does not contain exactly the cloud drives");
                    foreach (Button tab in tabs.Controls)
                    {
                        bool isDrive = false;
                        foreach (Provider provider in Store.DetectProviders()) if ((string)tab.Tag == provider.Id) isDrive = true;
                        if (!isDrive) throw new Exception("non-cloud tab found: " + tab.Text);
                    }
                    ComboBox selectedPair = (ComboBox)Field(form, "pairPicker");
                    if (selectedPair.SelectedIndex != 1 || string.IsNullOrEmpty(selectedPair.Text))
                        throw new Exception("folder selector has no selected text: index=" + selectedPair.SelectedIndex);
                    ViewInfo pairView = (ViewInfo)Field(form, "view");
                    if (pairView.Badge != "1 file waiting to back up" || pairView.BadgeKind != "local")
                        throw new Exception("pending upload badge was not displayed: " + pairView.Badge + ", kind=" + pairView.BadgeKind);
                    if (!pairView.Detail.Contains("Backs up at startup & shutdown"))
                        throw new Exception("lifecycle status detail was not displayed: " + pairView.Detail);
                    NoSideScroll(form, "at the default size");
                    Capture(form, Path.Combine(output, "folder-pair.png"));
                    Navigate(form, Path.Combine(local, "Notes"));
                    if ((string)Field(form, "driveKey") != Path.Combine(cloud, "Notes")) throw new Exception("local navigation did not follow the paired cloud path");
                    Navigate(form, drive.Root);
                    if ((string)Field(form, "localKey") != "") throw new Exception("unmapped cloud browsing displayed a default replica");
                    ComboBox choices = (ComboBox)Field(form, "pairPicker");
                    choices.SelectedIndex = 1;
                    WaitForLoad(form);
                    if ((string)Field(form, "localKey") != local) throw new Exception("folder selector did not open the selected pair");
                    if (((SplitContainer)Field(form, "split")).Top != ((SplitContainer)Field(form, "localSplit")).Top)
                        throw new Exception("paired file panes were not aligned");
                    ToolStripButton syncedBtn = (ToolStripButton)Field(form, "syncedBrowsingButton");
                    if (syncedBtn == null || !syncedBtn.Visible) throw new Exception("synced browsing button was not found or visible");
                    if (!syncedBtn.Checked) throw new Exception("synced browsing button should be checked by default");
                    CheckBox syncedCheck = (CheckBox)Field(form, "syncedBrowsingCheck");
                    if (syncedCheck == null || !syncedCheck.Visible) throw new Exception("synced browsing checkbox was not found or visible on front screen");
                    if (!syncedCheck.Checked) throw new Exception("synced browsing checkbox should be checked by default");
                    syncedBtn.PerformClick();
                    if (syncedBtn.Checked || (bool)Field(form, "syncedBrowsing") || syncedCheck.Checked)
                        throw new Exception("clicking synced browsing button did not disable synced browsing or update checkbox");
                    form.GetType().GetMethod("NavigateLocal", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(form, new object[] { Path.Combine(local, "Notes") });
                    WaitForLoad(form);
                    if ((string)Field(form, "localKey") != Path.Combine(local, "Notes"))
                        throw new Exception("NavigateLocal did not update localKey");
                    if ((string)Field(form, "driveKey") != cloud)
                        throw new Exception("driveKey changed during independent local navigation: " + Field(form, "driveKey"));
                    form.GetType().GetMethod("NavigateCloud", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(form, new object[] { drive.Root });
                    WaitForLoad(form);
                    if ((string)Field(form, "driveKey") != drive.Root)
                        throw new Exception("NavigateCloud did not update driveKey");
                    if ((string)Field(form, "localKey") != Path.Combine(local, "Notes"))
                        throw new Exception("localKey changed during independent cloud navigation: " + Field(form, "localKey"));
                    syncedBtn.PerformClick();
                    if (!syncedBtn.Checked || !(bool)Field(form, "syncedBrowsing") || !syncedCheck.Checked)
                        throw new Exception("clicking synced browsing button did not re-enable synced browsing or update checkbox");
                    syncedCheck.Checked = false;
                    if (syncedBtn.Checked || (bool)Field(form, "syncedBrowsing"))
                        throw new Exception("unchecking synced browsing checkbox did not disable synced browsing or update button");
                    syncedCheck.Checked = true;
                    if (!syncedBtn.Checked || !(bool)Field(form, "syncedBrowsing"))
                        throw new Exception("checking synced browsing checkbox did not enable synced browsing or update button");

                    // Regression test: toggling synced browsing or settings while a background sync holds LockState must NEVER hang or block
                    using (SyncEngine.LockState(5))
                    {
                        syncedCheck.Checked = false;
                        if (syncedBtn.Checked || (bool)Field(form, "syncedBrowsing"))
                            throw new Exception("unchecking synced browsing during sync did not update state");
                        syncedCheck.Checked = true;
                        if (!syncedBtn.Checked || !(bool)Field(form, "syncedBrowsing"))
                            throw new Exception("checking synced browsing during sync did not update state");
                        Store.SetBackupOnStartup(false);
                        Store.SetBackupOnStartup(true);
                        UpdateManager.SetAutomatic(false);
                        UpdateManager.SetAutomatic(true);
                        GoogleAuth.SaveClient("test-id", "test-secret");
                    }
                    if (Field(form, "uploadDirectButton") == null || Field(form, "cloudDownloadButton") == null || Field(form, "cloudNewFolderButton") == null)
                        throw new Exception("direct cloud action toolbar buttons are missing");
                    form.Size = Theme.S(980, 560);
                    WaitForLoad(form);
                    NoSideScroll(form, "at the minimum size");
                    Capture(form, Path.Combine(output, "compact.png"));
                    ShowSampleProgress(form, output);
                    Provider google = new Provider();
                    google.Id = "google:work@example.com"; google.Kind = "GoogleDrive"; google.Name = "Google Drive";
                    google.Root = drive.Root; google.Detail = "work@example.com (G:)";
                    using (GoogleSignInDialog signIn = new GoogleSignInDialog(new List<Provider> { google, drive }))
                    {
                        signIn.StartPosition = FormStartPosition.Manual;
                        signIn.Location = new Point(-32000, -32000);
                        signIn.Show(form);
                        Application.DoEvents();
                        Capture(signIn, Path.Combine(output, "google-sign-in.png"));
                        Panel rows = (Panel)Field(signIn, "accounts");
                        bool listed = false;
                        foreach (Control row in rows.Controls) if (row.Text.StartsWith("work@example.com")) listed = true;
                        if (!listed || rows.Controls.Count != 2) throw new Exception("sign-in dialog did not list exactly the Google Drive account");
                    }
                    using (LinkDialog dialog = new LinkDialog(Store.DetectProviders(), drive, local, @"Backups\new-project"))
                    {
                        dialog.StartPosition = FormStartPosition.Manual;
                        dialog.Location = new Point(-32000, -32000);
                        dialog.Show(form);
                        Application.DoEvents();
                        Capture(dialog, Path.Combine(output, "attach-folder.png"));
                        if (!((Label)Field(dialog, "cloudHint")).Text.StartsWith("Creates the folder Project Cloud\\Backups\\new-project"))
                            throw new Exception("attach dialog did not describe the new cloud folder");
                        ((TextField)Field(dialog, "cloudBox")).Box.Text = @"Backups\project-documents";
                        if (!((Label)Field(dialog, "cloudHint")).Text.StartsWith("Uses the existing folder"))
                            throw new Exception("attach dialog did not identify the existing cloud folder");
                        dialog.Close();
                    }
                    File.WriteAllText(Path.Combine(local, "Brief.txt"), "Local edit");
                    string replacePrompt = AnswerNext(false, delegate
                    {
                        form.GetType().GetMethod("StartPairTransfer", BindingFlags.Instance | BindingFlags.NonPublic)
                            .Invoke(form, new object[] { true, local, new string[] { Path.Combine(cloud, "Brief.txt") } });
                    });
                    if (replacePrompt == null || !replacePrompt.StartsWith("Replace"))
                        throw new Exception("dropping onto an existing file did not ask before replacing it");
                    if (File.ReadAllText(Path.Combine(local, "Brief.txt")) != "Local edit" || (bool)Field(form, "busy"))
                        throw new Exception("canceling the replace prompt still copied the file");
                    File.WriteAllText(Path.Combine(local, "Brief.txt"), "Project brief");

                    // Test uploading a file to the cloud folder via StartPairTransfer
                    string uploadFile = Path.Combine(local, "UploadTest.txt");
                    File.WriteAllText(uploadFile, "Testing upload file to cloud");
                    form.GetType().GetMethod("StartPairTransfer", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(form, new object[] { false, cloud, new string[] { uploadFile } });
                    DateTime waitUpload = DateTime.UtcNow.AddSeconds(10);
                    while ((bool)Field(form, "busy") && DateTime.UtcNow < waitUpload)
                    {
                        Application.DoEvents();
                        Thread.Sleep(10);
                    }
                    string cloudUploaded = Path.Combine(cloud, "UploadTest.txt");
                    if (!File.Exists(cloudUploaded)) throw new Exception("file upload to cloud failed: target file not created");
                    if (File.ReadAllText(cloudUploaded) != "Testing upload file to cloud") throw new Exception("file upload to cloud content mismatch");
                    File.Delete(cloudUploaded);
                    File.Delete(uploadFile);
                    string trash = Path.Combine(drive.Root, "Trash simulation");
                    Directory.CreateDirectory(trash);
                    Directory.Move(Path.Combine(cloud, "Notes"), Path.Combine(trash, "Notes"));
                    File.Move(Path.Combine(cloud, "Brief.txt"), Path.Combine(trash, "Brief.txt"));
                    Navigate(form, local);
                    bool missingFolder = false, missingFile = false;
                    foreach (ListViewItem item in right.Items)
                    {
                        Row row = item.Tag as Row;
                        bool missing = row.Status == "Missing in the cloud" && row.Kind == "local" && string.IsNullOrEmpty(row.LastSynced);
                        if (row.Name == "Notes") missingFolder = missing;
                        if (row.Name == "Brief.txt") missingFile = missing;
                    }
                    if (!missingFolder || !missingFile) throw new Exception("trashed cloud children retained synced status");
                    Navigate(form, Path.Combine(local, "Notes"));
                    if (((ViewInfo)Field(form, "view")).Badge != "Missing in the cloud") throw new Exception("trashed cloud subfolder retained synced badge");
                    Directory.Move(cloud, Path.Combine(trash, "project-documents"));
                    Navigate(form, local);
                    if (left.Items.Count != 0 || ((ViewInfo)Field(form, "view")).Badge != "Missing in the cloud")
                        throw new Exception("trashed cloud root retained synced badge");
                    if ((string)Field(Field(form, "empty"), "title") != "Cloud folder isn't available")
                        throw new Exception("missing cloud pane was shown as an empty folder");
                    ViewInfo legacyInfo = new ViewInfo();
                    List<Row> legacyRows = (List<Row>)form.GetType().GetMethod("BuildRows", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(form, new object[] { local, legacyInfo });
                    if (legacyInfo.Badge != "Missing in the cloud") throw new Exception("legacy folder badge retained synced status");
                    foreach (Row row in legacyRows)
                        if (row.Kind == "same" || !string.IsNullOrEmpty(row.LastSynced)) throw new Exception("legacy folder view restored a stale sync timestamp");
                    Capture(form, Path.Combine(output, "missing-cloud.png"));
                    Navigate(form, "vfs:linked");
                    Row attachment = left.Items[0].Tag as Row;
                    if (attachment.Status != "Missing in the cloud" || attachment.Kind == "same" || !string.IsNullOrEmpty(attachment.LastSynced))
                        throw new Exception("attached-folder overview showed a trashed cloud folder as synced");
                    if (!File.Exists(Path.Combine(local, "Brief.txt"))) throw new Exception("browsing trashed cloud folder changed local files");
                    string secondLocal = Path.Combine(sandbox, "Second project");
                    Directory.CreateDirectory(secondLocal);
                    File.WriteAllText(Path.Combine(secondLocal, "Keep.txt"), "Keep both copies");
                    LinkFolder second = SyncEngine.Link(secondLocal, drive.Id, @"Backups\second-project");
                    SyncEngine.SyncLink(second, "Push", true);
                    string secondCloud = Store.ResolveRemote(second, Store.DetectProviders());
                    Store.SaveLinks(new List<LinkFolder> { pair, second });
                    Navigate(form, "vfs:pc");
                    foreach (ListViewItem item in right.Items) item.Selected = ((Row)item.Tag).FullPath == secondLocal;
                    left.Focus(); // The context-menu source must win over the focused pane.
                    ToolStripMenuItem remove = RemoveItem(right);
                    if (remove == null || !remove.Enabled) throw new Exception("attached local folder has no remove context action");
                    AnswerRemoval(remove, secondLocal, secondCloud, false);
                    if (Store.ReadLinks().Count != 2 || !File.Exists(Store.StatePath(second.Id)))
                        throw new Exception("canceling removal changed the attachment");
                    Navigate(form, local);
                    TreeView localFolders = (TreeView)Field(form, "localTree");
                    remove = RemoveItem(localFolders);
                    if (remove == null) throw new Exception("attached local tree has no remove context action");
                    AnswerRemoval(remove, local, cloud, false);
                    TreeView cloudFolders = (TreeView)Field(form, "tree");
                    List<TreeNode> nodes = new List<TreeNode>();
                    foreach (TreeNode node in cloudFolders.Nodes) nodes.Add(node);
                    TreeNode secondNode = null;
                    for (int i = 0; i < nodes.Count; i++)
                    {
                        NodeTag tag = nodes[i].Tag as NodeTag;
                        if (tag != null && tag.Key == secondCloud) secondNode = nodes[i];
                        foreach (TreeNode child in nodes[i].Nodes) nodes.Add(child);
                    }
                    if (secondNode == null) throw new Exception("second cloud attachment is missing from the tree");
                    form.GetType().GetField("suppressTree", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, true);
                    cloudFolders.SelectedNode = secondNode;
                    form.GetType().GetField("suppressTree", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, false);
                    remove = RemoveItem(cloudFolders);
                    if (remove == null) throw new Exception("attached cloud tree has no remove context action");
                    AnswerRemoval(remove, secondLocal, secondCloud, false);
                    form.GetType().GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, true);
                    remove = RemoveItem(localFolders);
                    if (remove == null || remove.Enabled) throw new Exception("removal is enabled during sync");
                    form.GetType().GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, false);
                    Navigate(form, Path.GetDirectoryName(secondCloud));
                    foreach (ListViewItem item in left.Items) item.Selected = ((Row)item.Tag).FullPath == secondCloud;
                    remove = RemoveItem(left);
                    if (remove == null) throw new Exception("attached cloud folder has no remove context action");
                    AnswerRemoval(remove, secondLocal, secondCloud, true);
                    WaitForLoad(form);
                    List<LinkFolder> remaining = Store.ReadLinks();
                    if (remaining.Count != 1 || remaining[0].Id != pair.Id || File.Exists(Store.StatePath(second.Id)))
                        throw new Exception("removal did not detach only the right-clicked attachment");
                    if (!File.Exists(Path.Combine(secondLocal, "Keep.txt")) || !File.Exists(Path.Combine(secondCloud, "Keep.txt")))
                        throw new Exception("removal deleted local or cloud files");
                    if (RemoveItem(left) != null) throw new Exception("unlinked folder still has a remove sync action");
                    string mistakenRoot = Path.Combine(sandbox, "Local backup folder");
                    Directory.CreateDirectory(mistakenRoot);
                    File.WriteAllText(Path.Combine(mistakenRoot, "Local.txt"), "Preserve local documents");
                    Provider mistaken = new Provider();
                    mistaken.Id = "ui:mistaken"; mistaken.Name = "Local backup folder"; mistaken.Kind = "Folder"; mistaken.Root = mistakenRoot;
                    Store.SaveCustomProvider(mistaken);
                    form.GetType().GetMethod("RefreshAll", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
                    WaitForLoad(form);
                    Button mistakenTab = null, attachedTab = null;
                    foreach (Button tab in tabs.Controls)
                    {
                        if ((string)tab.Tag == mistaken.Id) mistakenTab = tab;
                        if ((string)tab.Tag == drive.Id) attachedTab = tab;
                    }
                    remove = RemoveItem(attachedTab, "Remove added cloud drive");
                    if (remove == null || remove.Enabled) throw new Exception("drive with synced attachments can be removed");
                    using (LinkDialog dialog = new LinkDialog(Store.DetectProviders(), drive, mistakenRoot, "new-backup"))
                    {
                        dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-32000, -32000);
                        dialog.Show(form);
                        dialog.AcceptButton.PerformClick();
                        Control error = (Control)Field(dialog, "error");
                        if (!error.Visible || !error.Text.Contains(mistakenRoot) || error.Height <= Theme.S(24))
                            throw new Exception("attachment conflict did not identify and display the configured root");
                        Capture(dialog, Path.Combine(output, "attachment-conflict.png"));
                        dialog.Close();
                    }
                    remove = RemoveItem(mistakenTab, "Remove added cloud drive");
                    if (remove == null || !remove.Enabled) throw new Exception("unused added drive has no removal action");
                    AnswerRemoval(remove, mistakenRoot, mistakenRoot, false);
                    if (Store.FindProvider(Store.DetectProviders(), mistaken.Id) == null) throw new Exception("cancel removed the added drive");
                    AnswerRemoval(remove, mistakenRoot, mistakenRoot, true);
                    WaitForLoad(form);
                    if (Store.FindProvider(Store.DetectProviders(), mistaken.Id) != null || !File.Exists(Path.Combine(mistakenRoot, "Local.txt")))
                        throw new Exception("removing drive registration failed or removed local files");
                    SyncEngine.ValidateLink(mistakenRoot, drive.Id, "new-backup", Store.ReadLinks());
                    if (Store.ReadLinks().Count != 1) throw new Exception("removing unused drive changed existing attachments");
                    form.Close();
                }
                File.WriteAllText(Path.Combine(output, "result.txt"), "UI self-test passed");
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "result.txt"), "UI self-test failed: " + ex);
                return 1;
            }
            finally
            {
                Store.SetOverrides(null, null);
                try { Directory.Delete(sandbox, true); } catch { }
            }
        }
    }
}
