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
                    selectedPair.Refresh();
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
                    form.Size = Theme.S(980, 560);
                    Application.DoEvents();
                    Capture(form, Path.Combine(output, "compact.png"));
                    using (LinkDialog dialog = new LinkDialog(Store.DetectProviders(), drive, local, @"Backups\new-project"))
                    {
                        dialog.StartPosition = FormStartPosition.Manual;
                        dialog.Location = new Point(-32000, -32000);
                        dialog.Show(form);
                        Application.DoEvents();
                        Capture(dialog, Path.Combine(output, "attach-folder.png"));
                        dialog.Close();
                    }
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
