using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DriveFolderSync
{
    public class NodeTag
    {
        public string Key;
        public string FullPath;
        public bool Loaded;
    }

    public class Row
    {
        public string Name;
        public string Status;
        public string Kind;
        public string Size;
        public string Modified;
        public string LastSynced;
        public string FullPath;
        public bool IsDir;
        public string NavigateKey;
    }

    static class ShellImages
    {
        const uint ShgfiSysIconIndex = 0x4000;
        const uint ShgfiSmallIcon = 0x1;
        const uint ShgfiUseFileAttributes = 0x10;
        const int LvmSetImageList = 0x1003;
        const int TvmSetImageList = 0x1109;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ShFileInfo
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        static IntPtr smallList;

        public static void Attach(ListView list, TreeView tree)
        {
            ShFileInfo info = new ShFileInfo();
            smallList = SHGetFileInfo("C:\\", 0x10, ref info, (uint)Marshal.SizeOf(typeof(ShFileInfo)), ShgfiSysIconIndex | ShgfiSmallIcon);
            if (smallList == IntPtr.Zero) return;
            SendMessage(list.Handle, LvmSetImageList, (IntPtr)1, smallList);
            SendMessage(tree.Handle, TvmSetImageList, IntPtr.Zero, smallList);
        }

        public static int Index(string path, bool isDir)
        {
            ShFileInfo info = new ShFileInfo();
            uint attr = isDir ? 0x10u : 0x80u;
            SHGetFileInfo(string.IsNullOrEmpty(path) ? "C:\\" : path, attr, ref info, (uint)Marshal.SizeOf(typeof(ShFileInfo)), ShgfiSysIconIndex | ShgfiSmallIcon | ShgfiUseFileAttributes);
            return info.iIcon;
        }
    }

    public class MainForm : Form
    {
        readonly TreeView tree = new TreeView();
        readonly ListView list = new ListView();
        readonly TextBox address = new TextBox();
        readonly ToolStripStatusLabel statusText = new ToolStripStatusLabel();
        readonly ToolStripStatusLabel statusSync = new ToolStripStatusLabel();
        readonly Stack<string> back = new Stack<string>();
        readonly List<Row> rows = new List<Row>();
        string current = "vfs:pc";
        bool suppressTree;
        bool suppressNav;
        int loadToken;
        int sortColumn;
        bool sortAsc = true;
        bool busy;
        readonly string startupLink;

        public MainForm(string linkPath)
        {
            startupLink = linkPath;
            Text = "Drive Folder Sync";
            Width = 1100;
            Height = 700;
            StartPosition = FormStartPosition.CenterScreen;
            Font = SystemFonts.MessageBoxFont;
            KeyPreview = true;

            MenuStrip menu = new MenuStrip();
            ToolStripMenuItem file = new ToolStripMenuItem("File");
            file.DropDownItems.Add("Link this folder", null, new EventHandler(Link_Click));
            file.DropDownItems.Add("Stop syncing this folder", null, new EventHandler(Unlink_Click));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add("Add a drive folder...", null, new EventHandler(AddDrive_Click));
            file.DropDownItems.Add("Open log folder", null, new EventHandler(Log_Click));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add("Exit", null, new EventHandler(delegate { Close(); }));
            ToolStripMenuItem view = new ToolStripMenuItem("View");
            view.DropDownItems.Add("Refresh", null, new EventHandler(delegate { LoadCurrent(); }));
            ToolStripMenuItem sync = new ToolStripMenuItem("Sync");
            sync.DropDownItems.Add("Refresh this folder from the cloud", null, new EventHandler(delegate { RunSync("Pull", true); }));
            sync.DropDownItems.Add("Upload this folder to the cloud", null, new EventHandler(delegate { RunSync("Push", true); }));
            sync.DropDownItems.Add(new ToolStripSeparator());
            sync.DropDownItems.Add("Refresh all linked folders", null, new EventHandler(delegate { RunSync("Pull", false); }));
            sync.DropDownItems.Add("Upload all linked folders", null, new EventHandler(delegate { RunSync("Push", false); }));
            ToolStripMenuItem tools = new ToolStripMenuItem("Tools");
            tools.DropDownItems.Add("Uninstall...", null, new EventHandler(delegate { Installer.Uninstall(false); }));
            ToolStripMenuItem help = new ToolStripMenuItem("Help");
            help.DropDownItems.Add("About", null, new EventHandler(About_Click));
            menu.Items.Add(file);
            menu.Items.Add(view);
            menu.Items.Add(sync);
            menu.Items.Add(tools);
            menu.Items.Add(help);
            MainMenuStrip = menu;

            ToolStrip bar = new ToolStrip();
            bar.GripStyle = ToolStripGripStyle.Hidden;
            bar.Items.Add(new ToolStripButton("Back", null, new EventHandler(delegate { GoBack(); })));
            bar.Items.Add(new ToolStripButton("Up", null, new EventHandler(delegate { GoUp(); })));
            ToolStripControlHost host = new ToolStripControlHost(address);
            host.AutoSize = false;
            host.Width = 520;
            address.Dock = DockStyle.Fill;
            address.KeyDown += new KeyEventHandler(Address_KeyDown);
            bar.Items.Add(host);
            bar.Items.Add(new ToolStripButton("Refresh", null, new EventHandler(delegate { LoadCurrent(); })));
            bar.Items.Add(new ToolStripSeparator());
            bar.Items.Add(new ToolStripButton("Refresh from cloud", null, new EventHandler(delegate { RunSync("Pull", true); })));
            bar.Items.Add(new ToolStripButton("Upload to cloud", null, new EventHandler(delegate { RunSync("Push", true); })));
            bar.Items.Add(new ToolStripButton("Link folder", null, new EventHandler(Link_Click)));
            bar.Items.Add(new ToolStripButton("Add drive", null, new EventHandler(AddDrive_Click)));

            Panel legend = new Panel();
            legend.Height = 28;
            legend.Dock = DockStyle.Top;
            legend.BackColor = Color.FromArgb(245, 245, 245);
            Label legendText = new Label();
            legendText.AutoSize = true;
            legendText.Location = new Point(8, 6);
            legendText.Text = "Green";
            legendText.ForeColor = Color.FromArgb(22, 130, 48);
            Label legendLocal = new Label();
            legendLocal.AutoSize = true;
            legendLocal.Location = new Point(52, 6);
            legendLocal.Text = "changed on this PC";
            Label legendRed = new Label();
            legendRed.AutoSize = true;
            legendRed.Location = new Point(190, 6);
            legendRed.Text = "Red";
            legendRed.ForeColor = Color.FromArgb(192, 32, 32);
            Label legendCloud = new Label();
            legendCloud.AutoSize = true;
            legendCloud.Location = new Point(220, 6);
            legendCloud.Text = "changed in the cloud     Last synced is the last refresh or upload of that linked folder";
            legend.Controls.Add(legendText);
            legend.Controls.Add(legendLocal);
            legend.Controls.Add(legendRed);
            legend.Controls.Add(legendCloud);

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            Shown += new EventHandler(delegate
            {
                try { split.SplitterDistance = 260; }
                catch { }
            });
            tree.Dock = DockStyle.Fill;
            tree.HideSelection = false;
            tree.ShowLines = true;
            tree.ShowPlusMinus = true;
            tree.ShowNodeToolTips = true;
            tree.BeforeExpand += new TreeViewCancelEventHandler(Tree_BeforeExpand);
            tree.AfterSelect += new TreeViewEventHandler(Tree_AfterSelect);
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.Columns.Add("Name", 280);
            list.Columns.Add("Status", 180);
            list.Columns.Add("Size", 90);
            list.Columns.Add("Date modified", 150);
            list.Columns.Add("Last synced", 150);
            list.ColumnClick += new ColumnClickEventHandler(List_ColumnClick);
            list.ItemActivate += new EventHandler(List_ItemActivate);
            split.Panel1.Controls.Add(tree);
            split.Panel2.Controls.Add(list);

            StatusStrip status = new StatusStrip();
            statusText.Spring = true;
            statusText.TextAlign = ContentAlignment.MiddleLeft;
            statusSync.Text = "";
            status.Items.Add(statusText);
            status.Items.Add(statusSync);

            Controls.Add(split);
            Controls.Add(legend);
            Controls.Add(bar);
            Controls.Add(menu);
            Controls.Add(status);

            HandleCreated += new EventHandler(delegate { ShellImages.Attach(list, tree); });
            Load += new EventHandler(MainForm_Load);
        }

        void MainForm_Load(object sender, EventArgs e)
        {
            BuildTree();
            Navigate("vfs:pc", false);
            if (!string.IsNullOrEmpty(startupLink) && Directory.Exists(startupLink))
                BeginInvoke(new Action(delegate { OfferLink(startupLink); }));
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5) { LoadCurrent(); e.Handled = true; }
            else if (e.Alt && e.KeyCode == Keys.Left) { GoBack(); e.Handled = true; }
            else if (e.Alt && e.KeyCode == Keys.Up) { GoUp(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        void BuildTree()
        {
            tree.Nodes.Clear();
            NodeTag pcTag = new NodeTag();
            pcTag.Key = "vfs:pc";
            pcTag.Loaded = true;
            TreeNode pc = new TreeNode("This PC");
            pc.Tag = pcTag;
            pc.ImageIndex = ShellImages.Index("C:\\", true);
            pc.SelectedImageIndex = pc.ImageIndex;
            foreach (Provider provider in Store.DetectProviders())
            {
                NodeTag tag = new NodeTag();
                tag.Key = provider.Root;
                tag.FullPath = provider.Root;
                TreeNode node = MakeDirNode(provider.Title, tag);
                node.ToolTipText = provider.Root;
                pc.Nodes.Add(node);
            }
            NodeTag linkedTag = new NodeTag();
            linkedTag.Key = "vfs:linked";
            linkedTag.Loaded = true;
            TreeNode linked = new TreeNode("Linked folders");
            linked.Tag = linkedTag;
            linked.ImageIndex = ShellImages.Index(Store.LocalRoot(), true);
            linked.SelectedImageIndex = linked.ImageIndex;
            foreach (LinkFolder folder in Store.ReadLinks())
            {
                NodeTag tag = new NodeTag();
                tag.Key = folder.Local;
                tag.FullPath = folder.Local;
                TreeNode node = MakeDirNode(Path.GetFileName(folder.Local.TrimEnd('\\')), tag);
                node.ToolTipText = folder.Local;
                linked.Nodes.Add(node);
            }
            pc.Nodes.Add(linked);
            tree.Nodes.Add(pc);
            pc.Expand();
        }

        TreeNode MakeDirNode(string name, NodeTag tag)
        {
            TreeNode node = new TreeNode(string.IsNullOrEmpty(name) ? tag.FullPath : name);
            node.Tag = tag;
            node.ImageIndex = ShellImages.Index(tag.FullPath, true);
            node.SelectedImageIndex = node.ImageIndex;
            if (!string.IsNullOrEmpty(tag.FullPath) && Directory.Exists(tag.FullPath))
                node.Nodes.Add(new TreeNode());
            else
                tag.Loaded = true;
            return node;
        }

        void Tree_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            NodeTag tag = e.Node.Tag as NodeTag;
            if (tag == null || tag.Loaded) return;
            e.Node.Nodes.Clear();
            tag.Loaded = true;
            if (string.IsNullOrEmpty(tag.FullPath) || !Directory.Exists(tag.FullPath)) return;
            try
            {
                DirectoryInfo[] dirs = new DirectoryInfo(tag.FullPath).GetDirectories();
                Array.Sort(dirs, delegate(DirectoryInfo a, DirectoryInfo b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
                foreach (DirectoryInfo dir in dirs)
                {
                    if (SyncEngine.SkipName(dir.Name, true)) continue;
                    NodeTag child = new NodeTag();
                    child.Key = dir.FullName;
                    child.FullPath = dir.FullName;
                    e.Node.Nodes.Add(MakeDirNode(dir.Name, child));
                }
            }
            catch (Exception ex)
            {
                Store.Log("could not list " + tag.FullPath + ": " + ex.Message);
            }
        }

        void Tree_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (suppressTree) return;
            NodeTag tag = e.Node.Tag as NodeTag;
            if (tag == null || string.IsNullOrEmpty(tag.Key)) return;
            Navigate(tag.Key, true);
        }

        void Navigate(string key, bool remember)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (remember && !suppressNav && !string.Equals(current, key, StringComparison.OrdinalIgnoreCase))
                back.Push(current);
            current = key;
            address.Text = Display(key);
            suppressTree = true;
            try
            {
                TreeNode node = FindNode(tree.Nodes, key);
                if (node != null) tree.SelectedNode = node;
            }
            finally { suppressTree = false; }
            LoadCurrent();
        }

        TreeNode FindNode(TreeNodeCollection nodes, string key)
        {
            foreach (TreeNode node in nodes)
            {
                NodeTag tag = node.Tag as NodeTag;
                if (tag != null && string.Equals(tag.Key, key, StringComparison.OrdinalIgnoreCase)) return node;
                TreeNode child = FindNode(node.Nodes, key);
                if (child != null) return child;
            }
            return null;
        }

        string Display(string key)
        {
            if (key == "vfs:pc") return "This PC";
            if (key == "vfs:linked") return "This PC\\Linked folders";
            return key;
        }

        void GoBack()
        {
            if (back.Count == 0) return;
            suppressNav = true;
            try { Navigate(back.Pop(), false); }
            finally { suppressNav = false; }
        }

        void GoUp()
        {
            if (current == "vfs:pc") return;
            if (current == "vfs:linked") { Navigate("vfs:pc", true); return; }
            foreach (Provider provider in Store.DetectProviders())
            {
                if (string.Equals(Store.Norm(current), Store.Norm(provider.Root), StringComparison.OrdinalIgnoreCase))
                {
                    Navigate("vfs:pc", true);
                    return;
                }
            }
            string parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent)) Navigate("vfs:pc", true);
            else Navigate(parent, true);
        }

        void Address_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            string text = address.Text.Trim();
            if (string.Equals(text, "This PC", StringComparison.OrdinalIgnoreCase)) Navigate("vfs:pc", true);
            else if (text.StartsWith("This PC\\Linked", StringComparison.OrdinalIgnoreCase)) Navigate("vfs:linked", true);
            else if (Directory.Exists(text)) Navigate(text, true);
            else statusText.Text = "That folder was not found.";
        }

        void LoadCurrent()
        {
            int token = ++loadToken;
            statusText.Text = "Reading...";
            string key = current;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                List<Row> built = new List<Row>();
                string syncLine = "";
                try { built = BuildRows(key, out syncLine); }
                catch (Exception ex) { error = ex.Message; }
                BeginInvoke(new Action(delegate
                {
                    if (token != loadToken) return;
                    rows.Clear();
                    list.Items.Clear();
                    if (error != null)
                    {
                        statusText.Text = error;
                        statusSync.Text = "";
                        return;
                    }
                    rows.AddRange(built);
                    ShowRows();
                    statusText.Text = rows.Count + (rows.Count == 1 ? " item" : " items");
                    statusSync.Text = syncLine;
                }));
            });
        }

        List<Row> BuildRows(string key, out string syncLine)
        {
            syncLine = "Not linked";
            List<Row> built = new List<Row>();
            if (key == "vfs:pc")
            {
                foreach (Provider provider in Store.DetectProviders())
                {
                    Row row = new Row();
                    row.Name = provider.Title;
                    row.Status = provider.IsOnline ? "Connected" : "Not connected";
                    row.Kind = "none";
                    row.FullPath = provider.Root;
                    row.NavigateKey = provider.Root;
                    row.IsDir = true;
                    row.Modified = "";
                    row.Size = "";
                    row.LastSynced = "";
                    built.Add(row);
                }
                Row linked = new Row();
                linked.Name = "Linked folders";
                linked.Status = Store.ReadLinks().Count + " linked";
                linked.Kind = "none";
                linked.IsDir = true;
                linked.NavigateKey = "vfs:linked";
                built.Add(linked);
                syncLine = "Choose a cloud folder, then Link folder";
                return built;
            }
            if (key == "vfs:linked")
            {
                List<Provider> providers = Store.DetectProviders();
                foreach (LinkFolder folder in Store.ReadLinks())
                {
                    Row row = new Row();
                    row.Name = folder.Local;
                    row.IsDir = true;
                    row.FullPath = folder.Local;
                    row.NavigateKey = folder.Local;
                    row.LastSynced = SyncEngine.FormatWhen(folder.LastSync);
                    string remote = Store.ResolveRemote(folder, providers);
                    row.Status = string.IsNullOrEmpty(remote) ? "Drive missing" : "Linked";
                    row.Kind = folder.LastErrors > 0 ? "cloud" : "same";
                    row.Modified = "";
                    row.Size = "";
                    built.Add(row);
                }
                syncLine = "Linked folders upload on shutdown and refresh when you sign in";
                return built;
            }
            if (!Directory.Exists(key)) throw new DirectoryNotFoundException("This folder is not available.");
            List<LinkFolder> links = Store.ReadLinks();
            LinkFolder link = SyncEngine.FindLink(key, links);
            List<Provider> found = Store.DetectProviders();
            Dictionary<string, Snap> state = null;
            string otherRoot = null;
            string relBase = "";
            bool viewingLocal = false;
            if (link != null)
            {
                string remoteRoot = Store.ResolveRemote(link, found);
                viewingLocal = Store.IsUnder(key, link.Local);
                string linkRoot = viewingLocal ? link.Local : remoteRoot;
                otherRoot = viewingLocal ? remoteRoot : link.Local;
                if (!string.IsNullOrEmpty(linkRoot)) relBase = Store.Relative(key, linkRoot) ?? "";
                state = Store.ReadState(Store.StatePath(link.Id));
                string when = SyncEngine.FormatWhen(link.LastSync);
                syncLine = string.IsNullOrEmpty(when) ? "Linked, not synced yet" : "Last synced " + when;
                if (link.LastErrors > 0) syncLine = syncLine + " (" + link.LastErrors + " errors)";
            }
            DirectoryInfo info = new DirectoryInfo(key);
            foreach (FileSystemInfo child in info.GetFileSystemInfos())
            {
                bool isDir = (child.Attributes & FileAttributes.Directory) != 0;
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (SyncEngine.SkipName(child.Name, isDir)) continue;
                Row row = new Row();
                row.Name = child.Name;
                row.IsDir = isDir;
                row.FullPath = child.FullName;
                row.NavigateKey = isDir ? child.FullName : null;
                row.Modified = child.LastWriteTime.ToString("g");
                row.Kind = "none";
                row.Status = isDir ? "Folder" : "";
                if (!isDir)
                {
                    FileInfo file = (FileInfo)child;
                    row.Size = SyncEngine.FormatSize(file.Length);
                }
                if (link != null && state != null)
                {
                    string rel = string.IsNullOrEmpty(relBase) ? child.Name : relBase + "\\" + child.Name;
                    if (isDir)
                    {
                        string kind = FolderKind(state, rel);
                        row.Kind = kind;
                        if (kind == "local") row.Status = "Has local changes";
                        else if (kind == "cloud") row.Status = "Has cloud changes";
                        else row.Status = "Folder";
                        row.LastSynced = SyncEngine.FormatWhen(link.LastSync);
                    }
                    else
                    {
                        FileRecord localRec = null;
                        FileRecord remoteRec = null;
                        FileRecord here = new FileRecord();
                        here.Rel = rel;
                        here.Full = child.FullName;
                        FileInfo file = (FileInfo)child;
                        here.Length = file.Length;
                        here.MtimeUtc = file.LastWriteTimeUtc;
                        if (viewingLocal) localRec = here;
                        else remoteRec = here;
                        string otherPath = string.IsNullOrEmpty(otherRoot) ? null : Path.Combine(otherRoot, rel);
                        if (!string.IsNullOrEmpty(otherPath) && File.Exists(otherPath))
                        {
                            FileInfo other = new FileInfo(otherPath);
                            FileRecord otherRec = new FileRecord();
                            otherRec.Rel = rel;
                            otherRec.Full = otherPath;
                            otherRec.Length = other.Length;
                            otherRec.MtimeUtc = other.LastWriteTimeUtc;
                            if (viewingLocal) remoteRec = otherRec;
                            else localRec = otherRec;
                        }
                        Snap snap = state.ContainsKey(rel) ? state[rel] : null;
                        SyncEngine.RowStatus classified = SyncEngine.Classify(localRec, remoteRec, snap);
                        row.Kind = classified.Kind;
                        row.Status = classified.Text;
                        row.LastSynced = SyncEngine.FormatWhen(string.IsNullOrEmpty(classified.LastSynced) ? link.LastSync : classified.LastSynced);
                    }
                }
                built.Add(row);
            }
            built.Sort(delegate(Row a, Row b)
            {
                if (a.IsDir != b.IsDir) return a.IsDir ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return built;
        }

        static string FolderKind(Dictionary<string, Snap> state, string rel)
        {
            string prefix = rel + "\\";
            bool local = false;
            bool cloud = false;
            foreach (string key in state.Keys)
            {
                if (!string.Equals(key, rel, StringComparison.OrdinalIgnoreCase) && !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                Snap snap = state[key];
                if (snap.Pending) local = true;
                if (snap.Hold) cloud = true;
            }
            if (cloud) return "cloud";
            if (local) return "local";
            return "same";
        }

        void ShowRows()
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (Row row in rows)
            {
                ListViewItem item = new ListViewItem(row.Name);
                item.SubItems.Add(row.Status ?? "");
                item.SubItems.Add(row.Size ?? "");
                item.SubItems.Add(row.Modified ?? "");
                item.SubItems.Add(row.LastSynced ?? "");
                item.Tag = row;
                item.ImageIndex = ShellImages.Index(string.IsNullOrEmpty(row.FullPath) ? "C:\\" : row.FullPath, row.IsDir);
                if (row.Kind == "local") item.ForeColor = Color.FromArgb(22, 130, 48);
                else if (row.Kind == "cloud") item.ForeColor = Color.FromArgb(192, 32, 32);
                list.Items.Add(item);
            }
            list.EndUpdate();
        }

        void List_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (sortColumn == e.Column) sortAsc = !sortAsc;
            else { sortColumn = e.Column; sortAsc = true; }
            rows.Sort(delegate(Row a, Row b)
            {
                int dir = sortAsc ? 1 : -1;
                if (a.IsDir != b.IsDir) return a.IsDir ? -1 : 1;
                string left = ColumnText(a, sortColumn);
                string right = ColumnText(b, sortColumn);
                return dir * string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
            });
            ShowRows();
        }

        static string ColumnText(Row row, int column)
        {
            if (column == 1) return row.Status;
            if (column == 2) return row.Size;
            if (column == 3) return row.Modified;
            if (column == 4) return row.LastSynced;
            return row.Name;
        }

        void List_ItemActivate(object sender, EventArgs e)
        {
            if (list.SelectedItems.Count == 0) return;
            Row row = list.SelectedItems[0].Tag as Row;
            if (row == null) return;
            if (row.IsDir)
            {
                string key = string.IsNullOrEmpty(row.NavigateKey) ? row.FullPath : row.NavigateKey;
                if (!string.IsNullOrEmpty(key)) Navigate(key, true);
            }
            else if (!string.IsNullOrEmpty(row.FullPath) && File.Exists(row.FullPath))
            {
                Process.Start(new ProcessStartInfo(row.FullPath) { UseShellExecute = true });
            }
        }

        void Link_Click(object sender, EventArgs e)
        {
            string path = SelectedDirectory();
            if (string.IsNullOrEmpty(path))
            {
                MessageBox.Show(this, "Open a folder in a drive, or a folder on this PC, then link it.", "Drive Folder Sync");
                return;
            }
            OfferLink(path);
        }

        string SelectedDirectory()
        {
            if (list.SelectedItems.Count == 1)
            {
                Row row = list.SelectedItems[0].Tag as Row;
                if (row != null && row.IsDir && !string.IsNullOrEmpty(row.FullPath) && Directory.Exists(row.FullPath))
                    return row.FullPath;
            }
            if (current != "vfs:pc" && current != "vfs:linked" && Directory.Exists(current)) return current;
            return null;
        }

        void OfferLink(string path)
        {
            List<Provider> providers = Store.DetectProviders();
            Provider inside = null;
            foreach (Provider provider in providers)
            {
                if (provider.IsOnline && Store.IsUnder(path, provider.Root)) inside = provider;
            }
            string local = path;
            string relative = "";
            Provider chosen = inside;
            if (inside != null)
            {
                relative = Store.Relative(path, inside.Root) ?? "";
                string safe = SafeName(inside.Detail);
                if (string.IsNullOrEmpty(safe)) safe = SafeName(inside.Name);
                string tail = string.IsNullOrEmpty(relative) ? SafeName(inside.Name) : relative;
                local = Path.Combine(Store.LocalRoot(), safe, tail);
            }
            else
            {
                foreach (Provider provider in providers)
                {
                    string found = SyncEngine.FindRelative(path, provider);
                    if (!string.IsNullOrEmpty(found))
                    {
                        chosen = provider;
                        relative = found;
                        break;
                    }
                }
            }
            using (LinkDialog dialog = new LinkDialog(providers, chosen, local, relative))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (string.Equals(Store.Norm(dialog.LocalPath), Store.Norm(Path.Combine(dialog.Chosen.Root, dialog.RelativePath)), StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(this, "Choose a local folder that is separate from the cloud folder.", "Drive Folder Sync");
                    return;
                }
                if (!Directory.Exists(Path.Combine(dialog.Chosen.Root, dialog.RelativePath)))
                {
                    MessageBox.Show(this, "That cloud folder was not found.", "Drive Folder Sync");
                    return;
                }
                string cloud = Path.Combine(dialog.Chosen.Root, dialog.RelativePath);
                DialogResult answer = MessageBox.Show(this,
                    "Keep a local copy in sync with this cloud folder?\r\n\r\nCloud: " + cloud + "\r\nLocal: " + dialog.LocalPath
                    + "\r\n\r\nChanges on this PC upload when Windows shuts down. Signing in refreshes the local copy from the cloud.",
                    "Drive Folder Sync", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                if (answer != DialogResult.OK) return;
                try
                {
                    LinkFolder folder = SyncEngine.Link(dialog.LocalPath, dialog.Chosen.Id, dialog.RelativePath);
                    RunSyncId("Pull", folder.Id);
                    BuildTree();
                    Navigate(dialog.LocalPath, true);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Drive Folder Sync", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        static string SafeName(string value)
        {
            if (string.IsNullOrEmpty(value)) return "drive";
            foreach (char bad in Path.GetInvalidFileNameChars()) value = value.Replace(bad, '-');
            return value.Trim();
        }

        void Unlink_Click(object sender, EventArgs e)
        {
            string path = SelectedDirectory();
            if (string.IsNullOrEmpty(path)) path = current;
            LinkFolder folder = SyncEngine.FindLink(path, Store.ReadLinks());
            if (folder == null)
            {
                MessageBox.Show(this, "This folder is not linked.", "Drive Folder Sync");
                return;
            }
            DialogResult answer = MessageBox.Show(this, "Stop syncing " + folder.Local + "?\r\nThe files stay on disk.", "Drive Folder Sync", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK) return;
            SyncEngine.Unlink(folder.Id);
            BuildTree();
            LoadCurrent();
        }

        void AddDrive_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose a OneDrive, Dropbox, or other cloud folder on this PC";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string root = dialog.SelectedPath;
                Provider provider = new Provider();
                provider.Root = Store.Norm(root);
                provider.Name = new DirectoryInfo(root).Name;
                provider.Kind = "Folder";
                if (provider.Name.IndexOf("dropbox", StringComparison.OrdinalIgnoreCase) >= 0) provider.Kind = "Dropbox";
                if (provider.Name.IndexOf("onedrive", StringComparison.OrdinalIgnoreCase) >= 0) provider.Kind = "OneDrive";
                provider.Detail = provider.Root;
                provider.Id = provider.Kind.ToLowerInvariant() + ":" + provider.Root.ToLowerInvariant();
                Store.SaveCustomProvider(provider);
                BuildTree();
                Navigate(provider.Root, true);
            }
        }

        void Log_Click(object sender, EventArgs e)
        {
            Store.EnsureDirs();
            Process.Start(new ProcessStartInfo("explorer.exe", Store.Root) { UseShellExecute = true });
        }

        void About_Click(object sender, EventArgs e)
        {
            MessageBox.Show(this,
                "Drive Folder Sync " + Store.Version + "\r\n\r\nGreen files changed on this PC since the last sync.\r\nRed files changed in the cloud, or both sides changed.\r\n\r\nLinked folders refresh when you sign in and upload when Windows shuts down.",
                "Drive Folder Sync");
        }

        void RunSync(string direction, bool one)
        {
            string id = null;
            if (one)
            {
                string path = SelectedDirectory();
                if (string.IsNullOrEmpty(path)) path = current;
                LinkFolder folder = SyncEngine.FindLink(path, Store.ReadLinks());
                if (folder == null)
                {
                    MessageBox.Show(this, "Link a folder first. Open it in the list, then choose Link folder.", "Drive Folder Sync");
                    return;
                }
                id = folder.Id;
            }
            RunSyncId(direction, id);
        }

        void RunSyncId(string direction, string id)
        {
            if (busy) return;
            busy = true;
            UseWaitCursor = true;
            statusText.Text = direction == "Pull" ? "Refreshing from the cloud..." : "Uploading to the cloud...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                SyncStats stats = null;
                try
                {
                    if (string.IsNullOrEmpty(id)) stats = SyncEngine.SyncAll(direction);
                    else
                    {
                        using (SyncEngine.LockState(180))
                        {
                            List<LinkFolder> links = Store.ReadLinks();
                            LinkFolder folder = null;
                            foreach (LinkFolder item in links)
                            {
                                if (string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) folder = item;
                            }
                            if (folder == null) throw new InvalidOperationException("That linked folder is gone.");
                            stats = SyncEngine.SyncLink(folder, direction, false);
                            Store.SaveLinks(links);
                        }
                    }
                }
                catch (Exception ex) { error = ex.Message; }
                BeginInvoke(new Action(delegate
                {
                    busy = false;
                    UseWaitCursor = false;
                    if (error != null) MessageBox.Show(this, error, "Drive Folder Sync", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    else if (stats != null)
                    {
                        statusText.Text = "Uploaded " + stats.Up + ", downloaded " + stats.Down + ", conflicts " + stats.Conflict + ", errors " + stats.Errors;
                    }
                    LoadCurrent();
                }));
            });
        }
    }

    public class LinkDialog : Form
    {
        readonly ComboBox drives = new ComboBox();
        readonly TextBox localBox = new TextBox();
        readonly TextBox cloudBox = new TextBox();
        readonly List<Provider> providers;

        public Provider Chosen;
        public string LocalPath;
        public string RelativePath;

        public LinkDialog(List<Provider> providers, Provider selected, string local, string relative)
        {
            this.providers = providers;
            Text = "Link folder";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(560, 250);
            Font = SystemFonts.MessageBoxFont;

            Label help = new Label();
            help.Text = "The local folder is the copy on this PC. The cloud folder is the Google Drive, OneDrive, Dropbox, or other folder it syncs with.";
            help.Location = new Point(12, 12);
            help.Size = new Size(530, 36);

            Label driveLabel = new Label();
            driveLabel.Text = "Drive";
            driveLabel.Location = new Point(12, 58);
            driveLabel.AutoSize = true;
            drives.DropDownStyle = ComboBoxStyle.DropDownList;
            drives.Location = new Point(110, 54);
            drives.Width = 430;
            foreach (Provider provider in providers)
            {
                if (provider.IsOnline) drives.Items.Add(provider);
            }
            drives.DisplayMember = "Title";
            drives.SelectedItem = selected;

            Label cloudLabel = new Label();
            cloudLabel.Text = "Cloud folder";
            cloudLabel.Location = new Point(12, 96);
            cloudLabel.AutoSize = true;
            cloudBox.Location = new Point(110, 92);
            cloudBox.Width = 340;
            cloudBox.Text = relative ?? "";
            Button cloudBrowse = new Button();
            cloudBrowse.Text = "Browse";
            cloudBrowse.Location = new Point(458, 90);
            cloudBrowse.Click += new EventHandler(BrowseCloud);

            Label localLabel = new Label();
            localLabel.Text = "Local folder";
            localLabel.Location = new Point(12, 134);
            localLabel.AutoSize = true;
            localBox.Location = new Point(110, 130);
            localBox.Width = 340;
            localBox.Text = local ?? "";
            Button localBrowse = new Button();
            localBrowse.Text = "Browse";
            localBrowse.Location = new Point(458, 128);
            localBrowse.Click += new EventHandler(BrowseLocal);

            Button ok = new Button();
            ok.Text = "Link";
            ok.Location = new Point(372, 200);
            ok.Click += new EventHandler(Ok_Click);
            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(458, 200);
            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(help);
            Controls.Add(driveLabel);
            Controls.Add(drives);
            Controls.Add(cloudLabel);
            Controls.Add(cloudBox);
            Controls.Add(cloudBrowse);
            Controls.Add(localLabel);
            Controls.Add(localBox);
            Controls.Add(localBrowse);
            Controls.Add(ok);
            Controls.Add(cancel);
        }

        void BrowseCloud(object sender, EventArgs e)
        {
            Provider provider = drives.SelectedItem as Provider;
            if (provider == null) return;
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.SelectedPath = provider.Root;
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (!Store.IsUnder(dialog.SelectedPath, provider.Root))
                {
                    MessageBox.Show(this, "Choose a folder inside " + provider.Root, "Drive Folder Sync");
                    return;
                }
                cloudBox.Text = Store.Relative(dialog.SelectedPath, provider.Root) ?? "";
            }
        }

        void BrowseLocal(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.ShowNewFolderButton = true;
                if (Directory.Exists(localBox.Text)) dialog.SelectedPath = localBox.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) localBox.Text = dialog.SelectedPath;
            }
        }

        void Ok_Click(object sender, EventArgs e)
        {
            Chosen = drives.SelectedItem as Provider;
            LocalPath = localBox.Text.Trim();
            RelativePath = cloudBox.Text.Trim().Trim('\\');
            if (Chosen == null || string.IsNullOrEmpty(LocalPath))
            {
                MessageBox.Show(this, "Choose a drive and a local folder.", "Drive Folder Sync");
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
