using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
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

    class MirrorChild
    {
        public string Name;
        public bool CloudIsDir;
        public bool LocalIsDir;
        public FileInfo CloudFile;
        public FileInfo LocalFile;
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
        public string Detail;
        public long Bytes = -1;
        public DateTime ModifiedAt;
        public DateTime SyncedAt;
        public bool RealIcon;
        public int Icon = -1;
    }

    class ViewInfo
    {
        public string Title = "";
        public string Detail = "";
        public string Tip = "";
        public string Badge = "";
        public string BadgeKind = "none";
        public bool Legend;
        public bool Virtual;
    }

    static class ShellImages
    {
        const uint ShgfiIcon = 0x100;
        const uint ShgfiSysIconIndex = 0x4000;
        const uint ShgfiSmallIcon = 0x1;
        const uint ShgfiUseFileAttributes = 0x10;
        const uint ShgfiPidl = 0x8;
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
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
        static extern IntPtr SHGetFileInfoPidl(IntPtr pidl, uint dwFileAttributes, ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);
        [DllImport("shell32.dll")]
        static extern int SHGetSpecialFolderLocation(IntPtr hwnd, int csidl, out IntPtr pidl);
        [DllImport("ole32.dll")]
        static extern void CoTaskMemFree(IntPtr pv);
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        static IntPtr smallList;
        static int thisPc = -2;
        static readonly Dictionary<int, Icon> icons = new Dictionary<int, Icon>();

        public static void Attach(TreeView tree)
        {
            ShFileInfo info = new ShFileInfo();
            smallList = SHGetFileInfo("C:\\", 0x10, ref info, (uint)Marshal.SizeOf(typeof(ShFileInfo)), ShgfiSysIconIndex | ShgfiSmallIcon);
            if (smallList == IntPtr.Zero) return;
            SendMessage(tree.Handle, TvmSetImageList, IntPtr.Zero, smallList);
        }

        public static int Index(string path, bool isDir)
        {
            ShFileInfo info = new ShFileInfo();
            uint attr = isDir ? 0x10u : 0x80u;
            SHGetFileInfo(string.IsNullOrEmpty(path) ? "C:\\" : path, attr, ref info, (uint)Marshal.SizeOf(typeof(ShFileInfo)), ShgfiSysIconIndex | ShgfiSmallIcon | ShgfiUseFileAttributes);
            return info.iIcon;
        }

        // The icon Explorer shows for this folder, such as the Google Drive or OneDrive icon.
        public static int RealIndex(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return Index(path, true);
            ShFileInfo info = new ShFileInfo();
            IntPtr result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(typeof(ShFileInfo)), ShgfiSysIconIndex | ShgfiSmallIcon);
            return result == IntPtr.Zero ? Index(path, true) : info.iIcon;
        }

        public static int ThisPc()
        {
            if (thisPc != -2) return thisPc;
            thisPc = Index("C:\\", true);
            IntPtr pidl;
            if (SHGetSpecialFolderLocation(IntPtr.Zero, 0x11, out pidl) == 0 && pidl != IntPtr.Zero)
            {
                ShFileInfo info = new ShFileInfo();
                if (SHGetFileInfoPidl(pidl, 0, ref info, (uint)Marshal.SizeOf(typeof(ShFileInfo)), ShgfiPidl | ShgfiSysIconIndex | ShgfiSmallIcon) != IntPtr.Zero)
                    thisPc = info.iIcon;
                CoTaskMemFree(pidl);
            }
            return thisPc;
        }

        public static Icon SmallIcon(int index, string path, bool isDir, bool real)
        {
            Icon icon;
            if (index >= 0 && icons.TryGetValue(index, out icon)) return icon;
            ShFileInfo info = new ShFileInfo();
            uint flags = ShgfiIcon | ShgfiSmallIcon | ShgfiSysIconIndex;
            if (!real || string.IsNullOrEmpty(path) || !Directory.Exists(path)) flags |= ShgfiUseFileAttributes;
            SHGetFileInfo(string.IsNullOrEmpty(path) ? "C:\\" : path, isDir ? 0x10u : 0x80u, ref info, (uint)Marshal.SizeOf(typeof(ShFileInfo)), flags);
            icon = null;
            if (info.hIcon != IntPtr.Zero)
            {
                using (Icon temp = Icon.FromHandle(info.hIcon)) icon = (Icon)temp.Clone();
                DestroyIcon(info.hIcon);
            }
            icons[info.iIcon] = icon;
            if (index >= 0) icons[index] = icon;
            return icon;
        }
    }

    public class MainForm : Form
    {
        readonly NavTree tree = new NavTree();
        readonly FileList list = new FileList();
        readonly ToolTip tips = new ToolTip();
        readonly AddressBar address = new AddressBar();
        readonly TextField search = new TextField(Glyphs.Search, true, "Search");
        readonly ViewHeader header;
        readonly EmptyState empty = new EmptyState();
        readonly SplitContainer split = new SplitContainer();
        readonly ToolStrip commands = new ToolStrip();
        readonly StatusStrip status = new StatusStrip();
        readonly ToolStripStatusLabel countLabel = new ToolStripStatusLabel();
        readonly ToolStripStatusLabel selectionLabel = new ToolStripStatusLabel();
        readonly ToolStripStatusLabel actionLabel = new ToolStripStatusLabel();
        readonly ToolStripProgressBar progress = new ToolStripProgressBar();
        readonly ContextMenuStrip listMenu = new ContextMenuStrip();
        readonly ContextMenuStrip treeMenu = new ContextMenuStrip();
        IconButton backButton;
        IconButton forwardButton;
        IconButton upButton;
        ToolStripButton refreshButton;
        ToolStripButton uploadButton;
        ToolStripButton replicaButton;
        ToolStripButton linkButton;
        ToolStripDropDownButton syncAllButton;
        ToolStripMenuItem stopItem;
        readonly Stack<string> back = new Stack<string>();
        readonly Stack<string> forward = new Stack<string>();
        readonly List<Row> rows = new List<Row>();
        List<Provider> providers = new List<Provider>();
        List<LinkFolder> links = new List<LinkFolder>();
        ViewInfo view = new ViewInfo();
        string current = "vfs:pc";
        string loadError;
        bool loading;
        bool suppressTree;
        bool suppressNav;
        bool sizingColumns;
        bool selectionPending;
        int loadToken;
        int sortColumn;
        bool sortAsc = true;
        bool userSorted;
        int hotIndex = -1;
        int savedSplit;
        bool busy;
        readonly string startupLink;

        public MainForm(string linkPath)
        {
            Theme.Init();
            startupLink = linkPath;
            header = new ViewHeader(tips);
            Text = "Drive Folder Sync";
            Icon = Theme.AppIcon;
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.Body;
            BackColor = Theme.Window;
            Size = Theme.S(1180, 740);
            MinimumSize = Theme.S(760, 480);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            RestoreWindow();

            BuildCommandBar();
            Panel nav = BuildNavRow();
            BuildContent();
            BuildStatusBar();

            Controls.Add(split);
            Controls.Add(nav);
            Controls.Add(commands);
            Controls.Add(status);

            HandleCreated += new EventHandler(delegate
            {
                ShellImages.Attach(tree);
                Native.Caption(this, Theme.Chrome);
            });
            Shown += new EventHandler(delegate
            {
                try { split.SplitterDistance = savedSplit > Theme.S(160) ? savedSplit : Theme.S(280); }
                catch { }
                list.Focus();
            });
            Load += new EventHandler(MainForm_Load);
            FormClosing += new FormClosingEventHandler(delegate { SaveWindow(); });
        }

        void BuildCommandBar()
        {
            commands.GripStyle = ToolStripGripStyle.Hidden;
            commands.Renderer = Theme.Renderer;
            commands.BackColor = Theme.Chrome;
            commands.AutoSize = false;
            commands.Height = Theme.S(48);
            commands.Dock = DockStyle.Top;
            commands.Padding = Theme.Pad(10, 0, 10, 0);
            commands.ImageScalingSize = Theme.S(16, 16);
            commands.Font = Theme.Body;
            commands.ShowItemToolTips = true;

            refreshButton = Command("Refresh from cloud", Glyphs.Download, "Refresh this linked folder from the cloud", delegate { RunSync("Pull", true); });
            uploadButton = Command("Upload to cloud", Glyphs.Upload, "Upload this linked folder to the cloud", delegate { RunSync("Push", true); });
            replicaButton = Command("Local replica", Glyphs.SaveLocal, "Copy only the selected folders and files to this PC", new EventHandler(Replica_Click));
            linkButton = Command("Link folder", Glyphs.Link, "Keep a local copy of this folder in sync with the cloud", new EventHandler(Link_Click));
            ToolStripButton addDrive = Command("Add drive", Glyphs.MapDrive, "Add a OneDrive, Dropbox, or other cloud folder on this PC", new EventHandler(AddDrive_Click));

            syncAllButton = new ToolStripDropDownButton("Sync all");
            Style(syncAllButton, Glyphs.Sync, "Refresh or upload every linked folder");
            syncAllButton.DropDownItems.Add(MenuItem("Refresh all linked folders", Glyphs.Download, delegate { RunSync("Pull", false); }));
            syncAllButton.DropDownItems.Add(MenuItem("Upload all linked folders", Glyphs.Upload, delegate { RunSync("Push", false); }));

            ToolStripDropDownButton more = new ToolStripDropDownButton();
            more.DisplayStyle = ToolStripItemDisplayStyle.Image;
            more.Image = Theme.Glyph(Glyphs.More, Theme.S(16), Theme.Text);
            more.ImageScaling = ToolStripItemImageScaling.None;
            more.ShowDropDownArrow = false;
            more.Alignment = ToolStripItemAlignment.Right;
            more.DropDownDirection = ToolStripDropDownDirection.BelowLeft;
            more.Padding = Theme.Pad(8, 6, 8, 6);
            more.ToolTipText = "More options";
            more.AccessibleName = "More options";
            stopItem = MenuItem("Stop syncing this folder", Glyphs.Cancel, new EventHandler(Unlink_Click));
            more.DropDownItems.Add(stopItem);
            more.DropDownItems.Add(MenuItem("Open log folder", Glyphs.FolderOpen, new EventHandler(Log_Click)));
            more.DropDownItems.Add(new ToolStripSeparator());
            more.DropDownItems.Add(MenuItem("Uninstall...", Glyphs.Delete, delegate { Installer.Uninstall(false, this); }));
            more.DropDownItems.Add(MenuItem("About Drive Folder Sync", Glyphs.Info, new EventHandler(About_Click)));
            more.DropDownOpening += new EventHandler(delegate { stopItem.Enabled = CurrentLink() != null; });

            commands.Items.Add(refreshButton);
            commands.Items.Add(uploadButton);
            commands.Items.Add(Separator());
            commands.Items.Add(replicaButton);
            commands.Items.Add(linkButton);
            commands.Items.Add(addDrive);
            commands.Items.Add(Separator());
            commands.Items.Add(syncAllButton);
            commands.Items.Add(more);
        }

        static ToolStripSeparator Separator()
        {
            ToolStripSeparator separator = new ToolStripSeparator();
            separator.Margin = Theme.Pad(6, 0, 6, 0);
            return separator;
        }

        static ToolStripButton Command(string text, char glyph, string tip, EventHandler click)
        {
            ToolStripButton button = new ToolStripButton(text);
            Style(button, glyph, tip);
            button.Click += click;
            return button;
        }

        static void Style(ToolStripItem item, char glyph, string tip)
        {
            item.Image = Theme.Glyph(glyph, Theme.S(16), Theme.Text);
            item.ImageScaling = ToolStripItemImageScaling.None;
            item.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            item.TextImageRelation = TextImageRelation.ImageBeforeText;
            item.Padding = Theme.Pad(8, 6, 10, 6);
            item.Margin = Theme.Pad(0, 0, 2, 0);
            item.ToolTipText = tip;
        }

        static ToolStripMenuItem MenuItem(string text, char glyph, EventHandler click)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            if (glyph != '\0') item.Image = Theme.Glyph(glyph, Theme.S(16), Theme.Text);
            item.ImageScaling = ToolStripItemImageScaling.None;
            item.Padding = new Padding(0, Theme.S(4), 0, Theme.S(4));
            if (click != null) item.Click += click;
            return item;
        }

        Panel BuildNavRow()
        {
            Panel nav = new Panel();
            nav.Dock = DockStyle.Top;
            nav.Height = Theme.S(48);
            nav.BackColor = Theme.Chrome;
            backButton = new IconButton(Glyphs.Back, "Back (Alt+Left)", tips);
            forwardButton = new IconButton(Glyphs.Forward, "Forward (Alt+Right)", tips);
            upButton = new IconButton(Glyphs.Up, "Up to the parent folder (Alt+Up)", tips);
            IconButton reload = new IconButton(Glyphs.Refresh, "Refresh (F5)", tips);
            backButton.Click += new EventHandler(delegate { GoBack(); });
            forwardButton.Click += new EventHandler(delegate { GoForward(); });
            upButton.Click += new EventHandler(delegate { GoUp(); });
            reload.Click += new EventHandler(delegate { RefreshAll(); });
            IconButton[] buttons = new IconButton[] { backButton, forwardButton, upButton, reload };
            foreach (IconButton button in buttons)
            {
                button.BackColor = Theme.Chrome;
                nav.Controls.Add(button);
            }
            address.BackColor = Theme.Chrome;
            address.Navigate += new Action<string>(delegate(string key) { Navigate(key, true); });
            address.Submit += new Action<string>(GoTo);
            address.EditEnded += new EventHandler(delegate { list.Focus(); });
            search.BackColor = Theme.Chrome;
            search.QueryChanged += new EventHandler(delegate { ShowRows(); });
            search.Box.KeyDown += new KeyEventHandler(delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    list.Focus();
                    if (list.Items.Count > 0 && list.SelectedItems.Count == 0)
                    {
                        list.Items[0].Selected = true;
                        list.Items[0].Focused = true;
                    }
                }
            });
            nav.Controls.Add(address);
            nav.Controls.Add(search);
            nav.Resize += new EventHandler(delegate
            {
                int top = Theme.S(4);
                int x = Theme.S(10);
                foreach (IconButton button in buttons)
                {
                    button.Location = new Point(x, top);
                    x += button.Width + Theme.S(2);
                }
                x += Theme.S(8);
                int searchWidth = Math.Max(Theme.S(200), Math.Min(Theme.S(300), nav.Width / 4));
                search.SetBounds(nav.Width - Theme.S(12) - searchWidth, top, searchWidth, Theme.S(32));
                address.SetBounds(x, top, Math.Max(Theme.S(120), search.Left - Theme.S(10) - x), Theme.S(32));
            });
            nav.Paint += new PaintEventHandler(delegate(object sender, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, nav.Height - 1, nav.Width, nav.Height - 1);
            });
            return nav;
        }

        void BuildContent()
        {
            split.Dock = DockStyle.Fill;
            split.FixedPanel = FixedPanel.Panel1;
            split.SplitterWidth = Theme.S(4);
            split.BackColor = Theme.Window;
            split.TabStop = false;
            split.Panel1MinSize = Theme.S(160);
            split.Panel1.BackColor = Theme.Pane;
            split.Panel1.Padding = Theme.Pad(6, 8, 2, 8);
            split.Panel2.BackColor = Theme.Window;
            split.SplitterMoved += new SplitterEventHandler(delegate { if (split.Focused) list.Focus(); });
            split.Panel1.Paint += new PaintEventHandler(delegate(object sender, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, split.Panel1.Width - 1, 0, split.Panel1.Width - 1, split.Panel1.Height);
            });
            split.Panel1.Resize += new EventHandler(delegate { split.Panel1.Invalidate(); });

            tree.Dock = DockStyle.Fill;
            tree.BorderStyle = BorderStyle.None;
            tree.BackColor = Theme.Pane;
            tree.ForeColor = Theme.Text;
            tree.Font = Theme.Body;
            tree.HideSelection = false;
            tree.ShowLines = false;
            tree.ShowRootLines = true;
            tree.ShowPlusMinus = true;
            tree.FullRowSelect = true;
            tree.HotTracking = true;
            tree.ShowNodeToolTips = true;
            tree.ItemHeight = Theme.S(28);
            tree.Indent = Theme.S(16);
            tree.BeforeExpand += new TreeViewCancelEventHandler(Tree_BeforeExpand);
            tree.AfterSelect += new TreeViewEventHandler(Tree_AfterSelect);
            tree.NodeMouseClick += new TreeNodeMouseClickEventHandler(delegate(object sender, TreeNodeMouseClickEventArgs e)
            {
                if (e.Button == MouseButtons.Right) tree.SelectedNode = e.Node;
            });
            treeMenu.Opening += new CancelEventHandler(TreeMenu_Opening);
            tree.ContextMenuStrip = treeMenu;
            split.Panel1.Controls.Add(tree);

            header.Dock = DockStyle.Top;

            Panel listHost = new Panel();
            listHost.Dock = DockStyle.Fill;
            listHost.BackColor = Theme.Window;
            listHost.Padding = Theme.Pad(10, 0, 0, 0);

            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.OwnerDraw = true;
            list.MultiSelect = true;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.BorderStyle = BorderStyle.None;
            list.HeaderStyle = ColumnHeaderStyle.Clickable;
            list.Font = Theme.Body;
            list.BackColor = Theme.Window;
            list.ForeColor = Theme.Text;
            ImageList spacer = new ImageList();
            spacer.ImageSize = new Size(1, Theme.S(32));
            list.SmallImageList = spacer;
            list.Columns.Add("Name", Theme.S(320));
            list.Columns.Add("Status", Theme.S(200));
            list.Columns.Add("Size", Theme.S(90), HorizontalAlignment.Right);
            list.Columns.Add("Date modified", Theme.S(150));
            list.Columns.Add("Last synced", Theme.S(150));
            list.DrawColumnHeader += new DrawListViewColumnHeaderEventHandler(delegate(object sender, DrawListViewColumnHeaderEventArgs e) { e.DrawDefault = true; });
            list.DrawItem += new DrawListViewItemEventHandler(List_DrawItem);
            list.DrawSubItem += new DrawListViewSubItemEventHandler(List_DrawSubItem);
            list.ColumnClick += new ColumnClickEventHandler(List_ColumnClick);
            list.ItemActivate += new EventHandler(List_ItemActivate);
            list.MouseMove += new MouseEventHandler(List_MouseMove);
            list.MouseLeave += new EventHandler(delegate { SetHot(-1); });
            list.GotFocus += new EventHandler(delegate { list.Invalidate(); });
            list.LostFocus += new EventHandler(delegate { list.Invalidate(); });
            list.SelectedIndexChanged += new EventHandler(delegate
            {
                if (selectionPending) return;
                selectionPending = true;
                BeginInvoke(new Action(delegate
                {
                    selectionPending = false;
                    UpdateStatus();
                }));
            });
            list.Resize += new EventHandler(delegate { FitColumns(); PlaceEmpty(); });
            list.ColumnWidthChanged += new ColumnWidthChangedEventHandler(delegate(object sender, ColumnWidthChangedEventArgs e) { if (e.ColumnIndex != 0) FitColumns(); });
            listMenu.Opening += new CancelEventHandler(ListMenu_Opening);
            list.ContextMenuStrip = listMenu;
            list.Controls.Add(empty);

            listHost.Controls.Add(list);
            split.Panel2.Controls.Add(listHost);
            split.Panel2.Controls.Add(header);
        }

        void BuildStatusBar()
        {
            status.Renderer = Theme.Renderer;
            status.BackColor = Theme.Window;
            status.SizingGrip = false;
            status.Font = Theme.Body;
            status.Padding = Theme.Pad(12, 2, 12, 2);
            status.AutoSize = false;
            status.Height = Theme.S(30);
            countLabel.ForeColor = Theme.TextMuted;
            selectionLabel.ForeColor = Theme.TextMuted;
            selectionLabel.Margin = Theme.Pad(16, 0, 0, 0);
            ToolStripStatusLabel spring = new ToolStripStatusLabel();
            spring.Spring = true;
            progress.Style = ProgressBarStyle.Marquee;
            progress.MarqueeAnimationSpeed = 30;
            progress.Size = Theme.S(120, 6);
            progress.Margin = Theme.Pad(0, 0, 10, 0);
            progress.Visible = false;
            actionLabel.ForeColor = Theme.TextMuted;
            actionLabel.ImageScaling = ToolStripItemImageScaling.None;
            actionLabel.TextImageRelation = TextImageRelation.ImageBeforeText;
            status.Items.Add(countLabel);
            status.Items.Add(selectionLabel);
            status.Items.Add(spring);
            status.Items.Add(progress);
            status.Items.Add(actionLabel);
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
            bool typing = address.Editing || search.Box.Focused;
            bool handled = true;
            if (e.KeyCode == Keys.F5) RefreshAll();
            else if (e.Alt && e.KeyCode == Keys.Left) GoBack();
            else if (e.Alt && e.KeyCode == Keys.Right) GoForward();
            else if (e.Alt && e.KeyCode == Keys.Up) GoUp();
            else if (e.KeyCode == Keys.Back && !typing && !e.Alt && !e.Control) GoBack();
            else if ((e.Control && e.KeyCode == Keys.F) || e.KeyCode == Keys.F3)
            {
                search.Box.Focus();
                search.Box.SelectAll();
            }
            else if ((e.Control && e.KeyCode == Keys.L) || (e.Alt && e.KeyCode == Keys.D) || e.KeyCode == Keys.F4) address.BeginEdit();
            else if (e.Control && e.KeyCode == Keys.A && list.Focused)
            {
                list.BeginUpdate();
                foreach (ListViewItem item in list.Items) item.Selected = true;
                list.EndUpdate();
            }
            else handled = false;
            if (handled)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            base.OnKeyDown(e);
        }

        void RefreshAll()
        {
            links = Store.ReadLinks();
            LoadCurrent();
        }

        void BuildTree()
        {
            providers = Store.DetectProviders();
            links = Store.ReadLinks();
            tree.BeginUpdate();
            tree.Nodes.Clear();
            NodeTag pcTag = new NodeTag();
            pcTag.Key = "vfs:pc";
            pcTag.Loaded = true;
            TreeNode pc = new TreeNode("This PC");
            pc.Tag = pcTag;
            pc.ImageIndex = ShellImages.ThisPc();
            pc.SelectedImageIndex = pc.ImageIndex;
            foreach (Provider provider in providers)
            {
                NodeTag tag = new NodeTag();
                tag.Key = provider.Root;
                tag.FullPath = provider.Root;
                TreeNode node = MakeDirNode(provider.Title, tag);
                node.ImageIndex = ShellImages.RealIndex(provider.Root);
                node.SelectedImageIndex = node.ImageIndex;
                node.ToolTipText = provider.Root;
                pc.Nodes.Add(node);
                string replica = Store.ReplicaRoot(provider);
                NodeTag replicaTag = new NodeTag();
                replicaTag.Key = replica;
                replicaTag.FullPath = replica;
                TreeNode replicaNode = MakeDirNode("Local replica  ·  " + Store.ReplicaLeaf(provider), replicaTag);
                replicaNode.ToolTipText = replica + "\r\nSelected folders and files from " + provider.Root;
                pc.Nodes.Add(replicaNode);
            }
            NodeTag linkedTag = new NodeTag();
            linkedTag.Key = "vfs:linked";
            linkedTag.Loaded = true;
            TreeNode linked = new TreeNode("Linked folders");
            linked.Tag = linkedTag;
            linked.ImageIndex = ShellImages.Index(Store.LocalRoot(), true);
            linked.SelectedImageIndex = linked.ImageIndex;
            foreach (LinkFolder folder in links)
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
            suppressTree = true;
            try
            {
                TreeNode selected = FindNode(tree.Nodes, current);
                if (selected != null) tree.SelectedNode = selected;
            }
            finally { suppressTree = false; }
            tree.EndUpdate();
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
                Array.Sort(dirs, delegate(DirectoryInfo a, DirectoryInfo b) { return NaturalCompare(a.Name, b.Name); });
                foreach (DirectoryInfo dir in dirs)
                {
                    if (SyncEngine.SkipName(dir.Name, true)) continue;
                    if ((dir.Attributes & FileAttributes.Hidden) != 0) continue;
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
            {
                back.Push(current);
                forward.Clear();
            }
            current = key;
            char glyph;
            List<AddressBar.Crumb> crumbs = Crumbs(key, out glyph);
            address.SetPath(crumbs, Display(key), glyph);
            string title = crumbs.Count > 0 ? crumbs[crumbs.Count - 1].Text : Display(key);
            header.Set(title, key.StartsWith("vfs:") ? "" : key, null, "", "none", false);
            search.Query = "";
            search.SetCue("Search " + title);
            suppressTree = true;
            try
            {
                tree.SelectedNode = FindNode(tree.Nodes, key);
            }
            finally { suppressTree = false; }
            UpdateNavButtons();
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

        static AddressBar.Crumb Crumb(string text, string key)
        {
            AddressBar.Crumb crumb = new AddressBar.Crumb();
            crumb.Text = text;
            crumb.Key = key;
            return crumb;
        }

        // Friendly breadcrumb: This PC > Dropbox > Projects, rather than the raw path.
        List<AddressBar.Crumb> Crumbs(string key, out char glyph)
        {
            List<AddressBar.Crumb> crumbs = new List<AddressBar.Crumb>();
            crumbs.Add(Crumb("This PC", "vfs:pc"));
            glyph = Glyphs.Pc;
            if (key == "vfs:pc") return crumbs;
            if (key == "vfs:linked")
            {
                crumbs.Add(Crumb("Linked folders", "vfs:linked"));
                glyph = Glyphs.Link;
                return crumbs;
            }
            string root = null;
            string label = null;
            bool viaLinked = false;
            foreach (Provider provider in providers)
            {
                if (!string.IsNullOrEmpty(provider.Root) && Store.IsUnder(key, provider.Root) && (root == null || Store.Norm(provider.Root).Length > root.Length))
                {
                    root = Store.Norm(provider.Root);
                    label = provider.Name;
                    glyph = Glyphs.Cloud;
                }
                string replica = Store.ReplicaRoot(provider);
                if (Store.IsUnder(key, replica) && (root == null || Store.Norm(replica).Length > root.Length))
                {
                    root = Store.Norm(replica);
                    label = "Local replica  ·  " + Store.ReplicaLeaf(provider);
                    glyph = Glyphs.SaveLocal;
                }
            }
            if (root == null)
            {
                foreach (LinkFolder link in links)
                {
                    if (!Store.IsUnder(key, link.Local)) continue;
                    root = Store.Norm(link.Local);
                    label = Path.GetFileName(root);
                    glyph = Glyphs.Link;
                    viaLinked = true;
                    break;
                }
            }
            if (root == null)
            {
                string full = Store.Norm(key);
                string drive = "";
                try { drive = Path.GetPathRoot(full) ?? ""; }
                catch { }
                root = drive.TrimEnd('\\');
                label = root;
                glyph = Glyphs.Folder;
            }
            if (viaLinked) crumbs.Add(Crumb("Linked folders", "vfs:linked"));
            string rootKey = root.EndsWith(":") ? root + "\\" : root;
            crumbs.Add(Crumb(string.IsNullOrEmpty(label) ? rootKey : label, rootKey));
            string relative = Store.Relative(key, rootKey);
            if (!string.IsNullOrEmpty(relative))
            {
                string path = rootKey;
                foreach (string part in relative.Split('\\'))
                {
                    if (string.IsNullOrEmpty(part)) continue;
                    path = Path.Combine(path, part);
                    crumbs.Add(Crumb(part, path));
                }
            }
            return crumbs;
        }

        void UpdateNavButtons()
        {
            backButton.Enabled = back.Count > 0;
            forwardButton.Enabled = forward.Count > 0;
            upButton.Enabled = current != "vfs:pc";
        }

        void GoBack()
        {
            if (back.Count == 0) return;
            forward.Push(current);
            suppressNav = true;
            try { Navigate(back.Pop(), false); }
            finally { suppressNav = false; }
        }

        void GoForward()
        {
            if (forward.Count == 0) return;
            back.Push(current);
            suppressNav = true;
            try { Navigate(forward.Pop(), false); }
            finally { suppressNav = false; }
        }

        void GoUp()
        {
            if (current == "vfs:pc") return;
            if (current == "vfs:linked") { Navigate("vfs:pc", true); return; }
            foreach (Provider provider in providers)
            {
                if (string.Equals(Store.Norm(current), Store.Norm(provider.Root), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Store.Norm(current), Store.Norm(Store.ReplicaRoot(provider)), StringComparison.OrdinalIgnoreCase))
                {
                    Navigate("vfs:pc", true);
                    return;
                }
            }
            string parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent)) Navigate("vfs:pc", true);
            else Navigate(parent, true);
        }

        void GoTo(string input)
        {
            string text = (input ?? "").Trim().Trim('"');
            try { text = Environment.ExpandEnvironmentVariables(text); }
            catch { }
            if (text.Length == 0) return;
            if (string.Equals(text, "This PC", StringComparison.OrdinalIgnoreCase)) Navigate("vfs:pc", true);
            else if (text.StartsWith("This PC\\Linked", StringComparison.OrdinalIgnoreCase)) Navigate("vfs:linked", true);
            else if (Directory.Exists(text)) Navigate(text, true);
            else ShowAction("Windows can't find \u201C" + text + "\u201D.", "warn");
        }

        void LoadCurrent()
        {
            int token = ++loadToken;
            string key = current;
            loading = true;
            countLabel.Text = "Loading\u2026";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                List<Row> built = new List<Row>();
                ViewInfo info = new ViewInfo();
                List<Provider> found = null;
                try
                {
                    found = Store.DetectProviders();
                    built = BuildRows(key, info);
                }
                catch (Exception ex) { error = ex.Message; }
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (token != loadToken) return;
                        loading = false;
                        if (found != null) providers = found;
                        List<string> keep = SelectedPaths();
                        rows.Clear();
                        loadError = error;
                        if (error != null)
                        {
                            char glyph;
                            List<AddressBar.Crumb> crumbs = Crumbs(key, out glyph);
                            view = new ViewInfo();
                            view.Title = crumbs[crumbs.Count - 1].Text;
                            view.Detail = key.StartsWith("vfs:") ? "" : key;
                        }
                        else
                        {
                            rows.AddRange(built);
                            view = info;
                        }
                        header.Set(view.Title, view.Detail, view.Tip, view.Badge, view.BadgeKind, view.Legend);
                        search.SetCue("Search " + view.Title);
                        if (userSorted || !view.Virtual) SortRows();
                        list.SetSortArrow(userSorted || !view.Virtual ? sortColumn : -1, sortAsc);
                        ShowRows(keep);
                    }));
                }
                catch (InvalidOperationException) { }
            });
        }

        static string ParseLocal(string stamp, out DateTime when)
        {
            when = DateTime.MinValue;
            if (string.IsNullOrEmpty(stamp)) return "";
            DateTime parsed;
            if (DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed)) when = parsed.ToLocalTime();
            return SyncEngine.FormatWhen(stamp);
        }

        static void SetSynced(Row row, string stamp)
        {
            DateTime when;
            row.LastSynced = ParseLocal(stamp, out when);
            row.SyncedAt = when;
        }

        static void SetFile(Row row, FileSystemInfo info, bool isDir)
        {
            row.Modified = info.LastWriteTime.ToString("g");
            row.ModifiedAt = info.LastWriteTime;
            if (isDir) return;
            FileInfo file = info as FileInfo;
            if (file == null) return;
            row.Size = SyncEngine.FormatSize(file.Length);
            row.Bytes = file.Length;
        }

        List<Row> BuildRows(string key, ViewInfo info)
        {
            List<Row> built = new List<Row>();
            if (key == "vfs:pc")
            {
                List<Provider> found = Store.DetectProviders();
                foreach (Provider provider in found)
                {
                    Row row = new Row();
                    row.Name = provider.Title;
                    row.Status = provider.IsOnline ? "Connected" : "Not connected";
                    row.Kind = provider.IsOnline ? "same" : "warn";
                    row.FullPath = provider.Root;
                    row.NavigateKey = provider.Root;
                    row.IsDir = true;
                    row.RealIcon = true;
                    row.Modified = "";
                    row.Size = "";
                    row.LastSynced = "";
                    built.Add(row);
                    Row replica = new Row();
                    replica.Name = "Local replica  ·  " + Store.ReplicaLeaf(provider);
                    replica.Status = Directory.Exists(Store.ReplicaRoot(provider)) ? "Selected folders and files" : "Not created yet";
                    replica.Kind = "none";
                    replica.FullPath = Store.ReplicaRoot(provider);
                    replica.NavigateKey = Store.ReplicaRoot(provider);
                    replica.IsDir = true;
                    built.Add(replica);
                }
                Row linked = new Row();
                int count = Store.ReadLinks().Count;
                linked.Name = "Linked folders";
                linked.Status = count + " linked";
                linked.Kind = "none";
                linked.IsDir = true;
                linked.FullPath = Store.LocalRoot();
                linked.NavigateKey = "vfs:linked";
                built.Add(linked);
                info.Title = "This PC";
                info.Detail = "Open a drive, select folders or files, then choose Local replica. Only the selection is copied, and paths match the drive.";
                info.Badge = found.Count == 1 ? "1 drive" : found.Count + " drives";
                info.Virtual = true;
                return built;
            }
            if (key == "vfs:linked")
            {
                List<Provider> found = Store.DetectProviders();
                List<LinkFolder> folders = Store.ReadLinks();
                foreach (LinkFolder folder in folders)
                {
                    Row row = new Row();
                    row.Name = Path.GetFileName(folder.Local.TrimEnd('\\'));
                    if (string.IsNullOrEmpty(row.Name)) row.Name = folder.Local;
                    row.Detail = folder.Local;
                    row.IsDir = true;
                    row.FullPath = folder.Local;
                    row.NavigateKey = folder.Local;
                    SetSynced(row, folder.LastSync);
                    string remote = Store.ResolveRemote(folder, found);
                    if (string.IsNullOrEmpty(remote)) { row.Status = "Drive missing"; row.Kind = "warn"; }
                    else if (folder.LastErrors > 0) { row.Status = "Linked  ·  " + folder.LastErrors + (folder.LastErrors == 1 ? " error" : " errors"); row.Kind = "cloud"; }
                    else { row.Status = "Linked"; row.Kind = "same"; }
                    row.Modified = "";
                    row.Size = "";
                    built.Add(row);
                }
                info.Title = "Linked folders";
                info.Detail = "Linked folders upload when Windows shuts down and refresh from the cloud when you sign in.";
                info.Badge = folders.Count + " linked";
                info.Virtual = true;
                return built;
            }
            List<Provider> places = Store.DetectProviders();
            Store.Place place = Store.FindPlace(key, places);
            if (place != null) return BuildPlaceRows(place, info);
            if (!Directory.Exists(key)) throw new DirectoryNotFoundException("This folder is not available.");
            List<LinkFolder> links = Store.ReadLinks();
            LinkFolder link = SyncEngine.FindLink(key, links);
            List<Provider> found2 = Store.DetectProviders();
            Dictionary<string, Snap> state = null;
            string otherRoot = null;
            string relBase = "";
            bool viewingLocal = false;
            info.Title = Path.GetFileName(key.TrimEnd('\\'));
            if (string.IsNullOrEmpty(info.Title)) info.Title = key;
            info.Detail = key;
            info.Badge = "Not linked";
            if (link != null)
            {
                string remoteRoot = Store.ResolveRemote(link, found2);
                viewingLocal = Store.IsUnder(key, link.Local);
                string linkRoot = viewingLocal ? link.Local : remoteRoot;
                otherRoot = viewingLocal ? remoteRoot : link.Local;
                if (!string.IsNullOrEmpty(linkRoot)) relBase = Store.Relative(key, linkRoot) ?? "";
                state = Store.ReadState(Store.StatePath(link.Id));
                string when = SyncEngine.FormatWhen(link.LastSync);
                info.Badge = string.IsNullOrEmpty(when) ? "Linked, not synced yet" : "Synced " + when;
                info.BadgeKind = "info";
                if (link.LastErrors > 0)
                {
                    info.Badge = info.Badge + "  ·  " + link.LastErrors + (link.LastErrors == 1 ? " error" : " errors");
                    info.BadgeKind = "cloud";
                }
                string otherSide = string.IsNullOrEmpty(otherRoot) ? "" : Path.Combine(otherRoot, relBase);
                if (!string.IsNullOrEmpty(otherSide))
                    info.Tip = (viewingLocal ? "On this PC:  " : "In the cloud:  ") + key + "\r\n" + (viewingLocal ? "In the cloud:  " : "On this PC:  ") + otherSide;
                info.Legend = true;
            }
            DirectoryInfo dirInfo = new DirectoryInfo(key);
            foreach (FileSystemInfo child in dirInfo.GetFileSystemInfos())
            {
                bool isDir = (child.Attributes & FileAttributes.Directory) != 0;
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (SyncEngine.SkipName(child.Name, isDir)) continue;
                Row row = new Row();
                row.Name = child.Name;
                row.IsDir = isDir;
                row.FullPath = child.FullName;
                row.NavigateKey = isDir ? child.FullName : null;
                SetFile(row, child, isDir);
                row.Kind = "none";
                row.Status = isDir ? "Folder" : "";
                if (link != null && state != null)
                {
                    string rel = string.IsNullOrEmpty(relBase) ? child.Name : relBase + "\\" + child.Name;
                    if (isDir)
                    {
                        string kind = FolderKind(state, rel);
                        row.Kind = kind == "same" ? "none" : kind;
                        if (kind == "local") row.Status = "Has local changes";
                        else if (kind == "cloud") row.Status = "Has cloud changes";
                        else row.Status = "Folder";
                        SetSynced(row, link.LastSync);
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
                        SetSynced(row, string.IsNullOrEmpty(classified.LastSynced) ? link.LastSync : classified.LastSynced);
                    }
                }
                built.Add(row);
            }
            built.Sort(delegate(Row a, Row b)
            {
                if (a.IsDir != b.IsDir) return a.IsDir ? -1 : 1;
                return NaturalCompare(a.Name, b.Name);
            });
            return built;
        }

        List<Row> BuildPlaceRows(Store.Place place, ViewInfo info)
        {
            List<Row> built = new List<Row>();
            string when = "";
            List<LinkFolder> links = Store.ReadLinks();
            LinkFolder link = SyncEngine.FindLink(place.ReplicaPath, links);
            if (link == null) link = SyncEngine.FindLink(place.CloudPath, links);
            Dictionary<string, Snap> state = null;
            string relBase = "";
            if (link != null)
            {
                state = Store.ReadState(Store.StatePath(link.Id));
                string anchor = Store.IsUnder(place.ReplicaPath, link.Local) ? link.Local : Store.ResolveRemote(link, Store.DetectProviders());
                if (!string.IsNullOrEmpty(anchor))
                {
                    string here = Store.IsUnder(place.ReplicaPath, link.Local) ? place.ReplicaPath : place.CloudPath;
                    relBase = Store.Relative(here, anchor) ?? "";
                }
                when = SyncEngine.FormatWhen(link.LastSync);
            }
            if (string.IsNullOrEmpty(place.Relative))
                info.Title = place.ViewingReplica ? "Local replica  ·  " + Store.ReplicaLeaf(place.Provider) : place.Provider.Name;
            else
                info.Title = Path.GetFileName(place.Relative.TrimEnd('\\'));
            info.Detail = place.ViewingReplica ? place.ReplicaPath : place.CloudPath;
            info.Tip = "In the cloud:  " + place.CloudPath + "\r\nLocal replica:  " + place.ReplicaPath;
            info.Legend = true;
            if (link == null) info.Badge = "Not linked yet";
            else
            {
                info.Badge = string.IsNullOrEmpty(when) ? "Linked, not synced yet" : "Synced " + when;
                info.BadgeKind = "info";
                if (link.LastErrors > 0)
                {
                    info.Badge = info.Badge + "  ·  " + link.LastErrors + (link.LastErrors == 1 ? " error" : " errors");
                    info.BadgeKind = "cloud";
                }
            }

            Dictionary<string, MirrorChild> map = new Dictionary<string, MirrorChild>(StringComparer.OrdinalIgnoreCase);
            AddPlaceChildren(map, place.CloudPath, true);
            AddPlaceChildren(map, place.ReplicaPath, false);
            foreach (MirrorChild child in map.Values)
            {
                bool isDir = child.CloudIsDir || child.LocalIsDir;
                Row row = new Row();
                row.Name = child.Name;
                row.IsDir = isDir;
                string cloudChild = Path.Combine(place.CloudPath, child.Name);
                string localChild = Path.Combine(place.ReplicaPath, child.Name);
                if (place.ViewingReplica)
                {
                    row.NavigateKey = isDir ? localChild : null;
                    row.FullPath = child.LocalFile != null || child.LocalIsDir ? localChild : cloudChild;
                }
                else
                {
                    row.NavigateKey = isDir ? cloudChild : null;
                    row.FullPath = child.CloudFile != null || child.CloudIsDir ? cloudChild : localChild;
                }
                FileInfo shown = place.ViewingReplica ? (child.LocalFile ?? child.CloudFile) : (child.CloudFile ?? child.LocalFile);
                if (shown != null) SetFile(row, shown, false);
                bool onCloud = child.CloudIsDir || child.CloudFile != null;
                bool onLocal = child.LocalIsDir || child.LocalFile != null;
                if (isDir)
                {
                    row.Status = "Folder";
                    row.Kind = "none";
                    if (onCloud && !onLocal) { row.Status = "Only in the cloud"; row.Kind = "cloud"; }
                    else if (onLocal && !onCloud) { row.Status = "Only on this PC"; row.Kind = "local"; }
                    if (state != null)
                    {
                        string rel = string.IsNullOrEmpty(relBase) ? child.Name : relBase + "\\" + child.Name;
                        string kind = FolderKind(state, rel);
                        if (kind == "local") { row.Status = "Has local changes"; row.Kind = "local"; }
                        else if (kind == "cloud") { row.Status = "Has cloud changes"; row.Kind = "cloud"; }
                    }
                    if (link != null) SetSynced(row, link.LastSync);
                }
                else if (link != null && state != null)
                {
                    string rel = string.IsNullOrEmpty(relBase) ? child.Name : relBase + "\\" + child.Name;
                    FileRecord localRec = ToRecord(child.LocalFile, rel);
                    FileRecord remoteRec = ToRecord(child.CloudFile, rel);
                    Snap snap = state.ContainsKey(rel) ? state[rel] : null;
                    SyncEngine.RowStatus classified = SyncEngine.Classify(localRec, remoteRec, snap);
                    row.Kind = classified.Kind;
                    row.Status = classified.Text;
                    SetSynced(row, string.IsNullOrEmpty(classified.LastSynced) ? link.LastSync : classified.LastSynced);
                }
                else if (onCloud && !onLocal)
                {
                    row.Kind = "cloud";
                    row.Status = "Only in the cloud";
                }
                else if (onLocal && !onCloud)
                {
                    row.Kind = "local";
                    row.Status = "Only on this PC";
                }
                else
                {
                    row.Kind = "same";
                    row.Status = "In both places";
                    if (child.CloudFile != null && child.LocalFile != null && child.CloudFile.Length != child.LocalFile.Length)
                    {
                        row.Kind = "cloud";
                        row.Status = "Changed on both sides";
                    }
                }
                built.Add(row);
            }
            built.Sort(delegate(Row a, Row b)
            {
                if (a.IsDir != b.IsDir) return a.IsDir ? -1 : 1;
                return NaturalCompare(a.Name, b.Name);
            });
            return built;
        }

        static void AddPlaceChildren(Dictionary<string, MirrorChild> map, string dir, bool cloud)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            FileSystemInfo[] children;
            try { children = new DirectoryInfo(dir).GetFileSystemInfos(); }
            catch { return; }
            foreach (FileSystemInfo child in children)
            {
                bool isDir = (child.Attributes & FileAttributes.Directory) != 0;
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (SyncEngine.SkipName(child.Name, isDir)) continue;
                MirrorChild item;
                if (!map.TryGetValue(child.Name, out item))
                {
                    item = new MirrorChild();
                    item.Name = child.Name;
                    map[child.Name] = item;
                }
                if (cloud)
                {
                    if (isDir) item.CloudIsDir = true;
                    else item.CloudFile = child as FileInfo;
                }
                else if (isDir) item.LocalIsDir = true;
                else item.LocalFile = child as FileInfo;
            }
        }

        static FileRecord ToRecord(FileInfo file, string rel)
        {
            if (file == null) return null;
            FileRecord record = new FileRecord();
            record.Rel = rel;
            record.Full = file.FullName;
            record.Length = file.Length;
            record.MtimeUtc = file.LastWriteTimeUtc;
            return record;
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

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int StrCmpLogicalW(string left, string right);

        // Sorts "file2" before "file10", like File Explorer.
        static int NaturalCompare(string left, string right)
        {
            try { return StrCmpLogicalW(left ?? "", right ?? ""); }
            catch { return string.Compare(left, right, StringComparison.OrdinalIgnoreCase); }
        }

        List<string> SelectedPaths()
        {
            List<string> paths = new List<string>();
            foreach (ListViewItem item in list.SelectedItems)
            {
                Row row = item.Tag as Row;
                if (row != null) paths.Add(row.FullPath ?? row.NavigateKey ?? row.Name);
            }
            return paths;
        }

        List<Row> SelectedRows()
        {
            List<Row> selected = new List<Row>();
            foreach (ListViewItem item in list.SelectedItems)
            {
                Row row = item.Tag as Row;
                if (row != null) selected.Add(row);
            }
            return selected;
        }

        void ShowRows()
        {
            ShowRows(SelectedPaths());
        }

        void ShowRows(List<string> keep)
        {
            string query = search.Query.Trim();
            Dictionary<string, bool> wanted = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (keep != null) foreach (string path in keep) if (path != null) wanted[path] = true;
            hotIndex = -1;
            list.BeginUpdate();
            list.Items.Clear();
            ListViewItem focus = null;
            foreach (Row row in rows)
            {
                if (query.Length > 0 && (row.Name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                ListViewItem item = new ListViewItem(row.Name);
                item.SubItems.Add(row.Status ?? "");
                item.SubItems.Add(row.Size ?? "");
                item.SubItems.Add(row.Modified ?? "");
                item.SubItems.Add(row.LastSynced ?? "");
                item.Tag = row;
                if (row.Icon < 0)
                    row.Icon = row.RealIcon ? ShellImages.RealIndex(row.FullPath) : ShellImages.Index(string.IsNullOrEmpty(row.FullPath) ? "C:\\" : row.FullPath, row.IsDir);
                list.Items.Add(item);
                string path = row.FullPath ?? row.NavigateKey ?? row.Name;
                if (path != null && wanted.ContainsKey(path))
                {
                    item.Selected = true;
                    if (focus == null) focus = item;
                }
            }
            if (focus != null) focus.Focused = true;
            list.EndUpdate();
            FitColumns();
            UpdateEmpty(query);
            UpdateStatus();
        }

        void UpdateEmpty(string query)
        {
            bool show = !loading && list.Items.Count == 0;
            if (show)
            {
                if (loadError != null) empty.Set(Glyphs.Warning, "This folder isn't available", loadError);
                else if (query.Length > 0) empty.Set(Glyphs.Search, "No items match \u201C" + query + "\u201D", "Check the spelling, or clear the search to see everything in this folder.");
                else if (current == "vfs:linked") empty.Set(Glyphs.Link, "No linked folders yet", "Open a folder on a drive, then choose Link folder. Linked folders upload when Windows shuts down and refresh when you sign in.");
                else empty.Set(Glyphs.FolderOpen, "This folder is empty", "Nothing here yet. Files added on this PC or in the cloud show up here.");
            }
            empty.Visible = show;
            PlaceEmpty();
        }

        void PlaceEmpty()
        {
            int width = Math.Min(empty.Width, Math.Max(Theme.S(200), list.ClientSize.Width - Theme.S(40)));
            empty.Width = width;
            int headerHeight = Theme.S(28);
            int y = Math.Max(headerHeight + Theme.S(24), (list.ClientSize.Height - empty.Height) / 2 - Theme.S(20));
            empty.Location = new Point((list.ClientSize.Width - width) / 2, y);
        }

        void UpdateStatus()
        {
            int total = rows.Count;
            int shown = list.Items.Count;
            if (loading) countLabel.Text = "Loading\u2026";
            else if (shown != total) countLabel.Text = shown + " of " + total + " items";
            else countLabel.Text = total + (total == 1 ? " item" : " items");
            int selected = list.SelectedItems.Count;
            if (selected == 0)
            {
                selectionLabel.Text = "";
                return;
            }
            long bytes = 0;
            bool files = false;
            foreach (ListViewItem item in list.SelectedItems)
            {
                Row row = item.Tag as Row;
                if (row != null && !row.IsDir && row.Bytes >= 0)
                {
                    bytes += row.Bytes;
                    files = true;
                }
            }
            selectionLabel.Text = selected + " selected" + (files ? "  ·  " + SyncEngine.FormatSize(bytes) : "");
        }

        void ShowAction(string text, string kind)
        {
            actionLabel.Text = text ?? "";
            if (string.IsNullOrEmpty(text)) actionLabel.Image = null;
            else if (kind == "warn" || kind == "cloud") actionLabel.Image = Theme.Glyph(Glyphs.Warning, Theme.S(14), kind == "cloud" ? Theme.CloudText : Theme.WarnText);
            else if (kind == "same") actionLabel.Image = Theme.Glyph(Glyphs.Check, Theme.S(14), Theme.Success);
            else actionLabel.Image = null;
            actionLabel.ForeColor = kind == "cloud" ? Theme.CloudText : Theme.TextMuted;
        }

        void FitColumns()
        {
            if (sizingColumns || list.Columns.Count == 0) return;
            sizingColumns = true;
            try
            {
                int others = 0;
                for (int i = 1; i < list.Columns.Count; i++) others += list.Columns[i].Width;
                int width = Math.Max(Theme.S(220), list.ClientSize.Width - others);
                if (list.Columns[0].Width != width) list.Columns[0].Width = width;
            }
            finally { sizingColumns = false; }
        }

        void SetHot(int index)
        {
            if (index == hotIndex) return;
            int old = hotIndex;
            hotIndex = index;
            if (old >= 0 && old < list.Items.Count) list.Invalidate(list.Items[old].Bounds);
            if (index >= 0 && index < list.Items.Count) list.Invalidate(list.Items[index].Bounds);
        }

        void List_MouseMove(object sender, MouseEventArgs e)
        {
            ListViewItem item = list.GetItemAt(e.X, e.Y);
            SetHot(item == null ? -1 : item.Index);
        }

        void List_DrawItem(object sender, DrawListViewItemEventArgs e)
        {
            Color back = Theme.Window;
            if (e.Item.Selected) back = list.Focused ? Theme.RowSelected : Theme.RowSelectedIdle;
            else if (e.ItemIndex == hotIndex) back = Theme.RowHover;
            Rectangle row = new Rectangle(e.Bounds.X, e.Bounds.Y, Math.Max(e.Bounds.Width, list.ClientSize.Width - e.Bounds.X), e.Bounds.Height);
            using (SolidBrush brush = new SolidBrush(back)) e.Graphics.FillRectangle(brush, row);
        }

        void List_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            Row row = e.Item.Tag as Row;
            if (row == null) return;
            Graphics g = e.Graphics;
            Rectangle cell = e.Bounds;
            if (e.ColumnIndex == 0) cell.Width = list.Columns[0].Width;
            int inset = Theme.S(6);
            switch (e.ColumnIndex)
            {
                case 0:
                    int size = SystemInformation.SmallIconSize.Width;
                    int x = cell.X + Theme.S(8);
                    Icon icon = ShellImages.SmallIcon(row.Icon, row.FullPath, row.IsDir, row.RealIcon);
                    if (icon != null) g.DrawIcon(icon, new Rectangle(x, cell.Y + (cell.Height - size) / 2, size, size));
                    x += size + Theme.S(10);
                    int room = Math.Max(0, cell.Right - x - inset);
                    int nameWidth = Math.Min(room, Theme.Measure(row.Name, Theme.Body) + Theme.S(2));
                    Theme.DrawText(g, row.Name, Theme.Body, new Rectangle(x, cell.Y, nameWidth, cell.Height), Theme.Text, TextFormatFlags.Left);
                    if (!string.IsNullOrEmpty(row.Detail) && room - nameWidth > Theme.S(60))
                    {
                        int detailX = x + nameWidth + Theme.S(10);
                        Theme.DrawText(g, row.Detail, Theme.Small, new Rectangle(detailX, cell.Y, cell.Right - detailX - inset, cell.Height), Theme.TextFaint, TextFormatFlags.Left | TextFormatFlags.PathEllipsis);
                    }
                    break;
                case 1:
                    Theme.DrawStatus(g, new Rectangle(cell.X + inset, cell.Y, Math.Max(0, cell.Width - inset * 2), cell.Height), row.Status, row.Kind);
                    break;
                case 2:
                    Theme.DrawText(g, row.Size, Theme.Body, new Rectangle(cell.X + inset, cell.Y, Math.Max(0, cell.Width - inset * 3), cell.Height), Theme.TextMuted, TextFormatFlags.Right);
                    break;
                case 3:
                    Theme.DrawText(g, row.Modified, Theme.Body, new Rectangle(cell.X + inset, cell.Y, Math.Max(0, cell.Width - inset * 2), cell.Height), Theme.TextMuted, TextFormatFlags.Left);
                    break;
                default:
                    Theme.DrawText(g, row.LastSynced, Theme.Body, new Rectangle(cell.X + inset, cell.Y, Math.Max(0, cell.Width - inset * 2), cell.Height), Theme.TextMuted, TextFormatFlags.Left);
                    break;
            }
        }

        void List_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (sortColumn == e.Column && (userSorted || !view.Virtual)) sortAsc = !sortAsc;
            else { sortColumn = e.Column; sortAsc = true; }
            userSorted = true;
            SortRows();
            list.SetSortArrow(sortColumn, sortAsc);
            ShowRows();
        }

        void SortRows()
        {
            rows.Sort(delegate(Row a, Row b)
            {
                if (a.IsDir != b.IsDir) return a.IsDir ? -1 : 1;
                int result = 0;
                if (sortColumn == 1) result = string.Compare(a.Status ?? "", b.Status ?? "", StringComparison.OrdinalIgnoreCase);
                else if (sortColumn == 2) result = a.Bytes.CompareTo(b.Bytes);
                else if (sortColumn == 3) result = a.ModifiedAt.CompareTo(b.ModifiedAt);
                else if (sortColumn == 4) result = a.SyncedAt.CompareTo(b.SyncedAt);
                else result = NaturalCompare(a.Name, b.Name);
                if (!sortAsc) result = -result;
                if (result == 0 && sortColumn != 0) result = NaturalCompare(a.Name, b.Name);
                return result;
            });
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
                try { Process.Start(new ProcessStartInfo(row.FullPath) { UseShellExecute = true }); }
                catch (Exception ex) { Dialogs.Error(this, "Couldn't open " + row.Name, ex.Message); }
            }
        }

        static bool IsRealPath(string path)
        {
            return !string.IsNullOrEmpty(path) && !path.StartsWith("vfs:", StringComparison.OrdinalIgnoreCase);
        }

        LinkFolder CurrentLink()
        {
            string path = SelectedDirectory();
            if (string.IsNullOrEmpty(path)) path = current;
            if (!IsRealPath(path)) return null;
            return SyncEngine.FindLink(path, Store.ReadLinks());
        }

        void ShowInExplorer(string path)
        {
            try
            {
                if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                else if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Dialogs.Error(this, "Couldn't open File Explorer", ex.Message); }
        }

        void CopyPath(string path)
        {
            try
            {
                Clipboard.SetText(path);
                ShowAction("Copied the path", "same");
            }
            catch { }
        }

        void ListMenu_Opening(object sender, CancelEventArgs e)
        {
            listMenu.Items.Clear();
            List<Row> selected = SelectedRows();
            if (selected.Count > 0)
            {
                Row first = selected[0];
                ToolStripMenuItem open = MenuItem("Open", first.IsDir ? Glyphs.FolderOpen : Glyphs.OpenFile, new EventHandler(List_ItemActivate));
                open.Font = Theme.Strong;
                listMenu.Items.Add(open);
                if (selected.Count == 1 && IsRealPath(first.FullPath))
                {
                    string path = first.FullPath;
                    listMenu.Items.Add(MenuItem("Show in File Explorer", Glyphs.OpenLocal, delegate { ShowInExplorer(path); }));
                    listMenu.Items.Add(MenuItem("Copy path", Glyphs.Copy, delegate { CopyPath(path); }));
                }
                if (!view.Virtual || current == "vfs:pc")
                {
                    listMenu.Items.Add(new ToolStripSeparator());
                    listMenu.Items.Add(MenuItem("Local replica", Glyphs.SaveLocal, new EventHandler(Replica_Click)));
                    if (selected.Count == 1 && first.IsDir) listMenu.Items.Add(MenuItem("Link folder", Glyphs.Link, new EventHandler(Link_Click)));
                }
            }
            else
            {
                listMenu.Items.Add(MenuItem("Refresh", Glyphs.Refresh, delegate { RefreshAll(); }));
                if (IsRealPath(current) && Directory.Exists(current))
                {
                    string path = current;
                    listMenu.Items.Add(MenuItem("Open in File Explorer", Glyphs.OpenLocal, delegate { ShowInExplorer(path); }));
                    listMenu.Items.Add(MenuItem("Copy path", Glyphs.Copy, delegate { CopyPath(path); }));
                    listMenu.Items.Add(new ToolStripSeparator());
                    listMenu.Items.Add(MenuItem("Link this folder", Glyphs.Link, new EventHandler(Link_Click)));
                }
            }
            AddSyncItems(listMenu);
            if (listMenu.Items.Count > 0 && listMenu.Items[listMenu.Items.Count - 1] is ToolStripSeparator)
                listMenu.Items.RemoveAt(listMenu.Items.Count - 1);
            e.Cancel = listMenu.Items.Count == 0;
        }

        void TreeMenu_Opening(object sender, CancelEventArgs e)
        {
            treeMenu.Items.Clear();
            treeMenu.Items.Add(MenuItem("Refresh", Glyphs.Refresh, delegate { RefreshAll(); }));
            if (IsRealPath(current) && Directory.Exists(current))
            {
                string path = current;
                treeMenu.Items.Add(MenuItem("Open in File Explorer", Glyphs.OpenLocal, delegate { ShowInExplorer(path); }));
                treeMenu.Items.Add(MenuItem("Copy path", Glyphs.Copy, delegate { CopyPath(path); }));
                treeMenu.Items.Add(new ToolStripSeparator());
                treeMenu.Items.Add(MenuItem("Link this folder", Glyphs.Link, new EventHandler(Link_Click)));
            }
            AddSyncItems(treeMenu);
        }

        void AddSyncItems(ContextMenuStrip menu)
        {
            if (CurrentLink() == null) return;
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem pull = MenuItem("Refresh from cloud", Glyphs.Download, delegate { RunSync("Pull", true); });
            ToolStripMenuItem push = MenuItem("Upload to cloud", Glyphs.Upload, delegate { RunSync("Push", true); });
            pull.Enabled = !busy;
            push.Enabled = !busy;
            menu.Items.Add(pull);
            menu.Items.Add(push);
            menu.Items.Add(MenuItem("Stop syncing", Glyphs.Cancel, new EventHandler(Unlink_Click)));
        }

        sealed class ReplicaChoice
        {
            public Provider Provider;
            public bool RootSelected;
            public List<string> Dirs = new List<string>();
            public List<string> Files = new List<string>();
        }

        void Replica_Click(object sender, EventArgs e)
        {
            if (list.SelectedItems.Count == 0)
            {
                Dialogs.Info(this, "Select what to keep on this PC", "Select the folders and files to keep locally, then choose Local replica. The open folder is not copied on its own.");
                return;
            }
            List<Provider> found = Store.DetectProviders();
            Dictionary<string, ReplicaChoice> choices = new Dictionary<string, ReplicaChoice>(StringComparer.OrdinalIgnoreCase);
            foreach (ListViewItem item in list.SelectedItems)
            {
                Row row = item.Tag as Row;
                if (row == null) continue;
                string path = row.FullPath;
                if (string.IsNullOrEmpty(path)) path = row.NavigateKey;
                if (string.IsNullOrEmpty(path) || path.StartsWith("vfs:", StringComparison.OrdinalIgnoreCase)) continue;
                Store.Place place = Store.FindPlace(path, found);
                if (place == null || place.Provider == null || string.IsNullOrEmpty(place.Provider.Root)) continue;
                ReplicaChoice choice;
                if (!choices.TryGetValue(place.Provider.Id, out choice))
                {
                    choice = new ReplicaChoice();
                    choice.Provider = place.Provider;
                    choices[place.Provider.Id] = choice;
                }
                if (string.IsNullOrEmpty(place.Relative))
                {
                    if (row.IsDir) choice.RootSelected = true;
                    continue;
                }
                if (row.IsDir) AddChoice(choice.Dirs, place.Relative);
                else AddChoice(choice.Files, place.Relative);
            }
            if (choices.Count == 0)
            {
                Dialogs.Info(this, "Select items on a drive", "Local replica works with folders and files on a cloud drive. Open a drive, select what to keep, then try again.");
                return;
            }
            List<string> details = new List<string>();
            List<string> roots = new List<string>();
            int hidden = 0;
            foreach (ReplicaChoice choice in choices.Values)
            {
                roots.Add(Store.ReplicaRoot(choice.Provider));
                if (choice.RootSelected) details.Add("Folder\t(this folder only)");
                foreach (string dir in choice.Dirs)
                {
                    if (details.Count >= 40) { hidden++; continue; }
                    details.Add("Folder\t" + dir);
                }
                foreach (string file in choice.Files)
                {
                    if (details.Count >= 40) { hidden++; continue; }
                    details.Add("File\t" + file);
                }
            }
            if (hidden > 0) details.Add("and " + hidden + " more");
            string body = "Only the selected folders are created and only the selected files are copied. Folder paths match the drive.\r\n\r\nSaved to " + string.Join(", ", roots.ToArray());
            if (!Dialogs.Confirm(this, "Create a local replica?", body, details, "Create replica", MessageKind.Question))
                return;
            if (busy) return;
            SetBusy(true, "Creating the local replica\u2026");
            List<ReplicaChoice> chosen = new List<ReplicaChoice>(choices.Values);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                string openPath = null;
                int copied = 0;
                int made = 0;
                try
                {
                    foreach (ReplicaChoice choice in chosen)
                    {
                        string localRoot = Store.ReplicaRoot(choice.Provider);
                        string cloudRoot = choice.Provider.Root;
                        if (string.IsNullOrEmpty(cloudRoot) || !Directory.Exists(cloudRoot))
                            throw new IOException("The drive is not available.");
                        foreach (LinkFolder link in Store.ReadLinks())
                        {
                            if (link.Partial) continue;
                            if (string.Equals(Store.Norm(link.Local), Store.Norm(localRoot), StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("This replica is already linked as a whole folder. Stop syncing it before choosing individual files.");
                        }
                        SyncEngine.MaterializeSelection(cloudRoot, localRoot, choice.Dirs, choice.Files);
                        LinkFolder saved = SyncEngine.RememberSelection(choice.Provider.Id, localRoot, choice.Dirs, choice.Files);
                        if (saved == null || !saved.Partial)
                            throw new InvalidOperationException("This replica is already linked as a whole folder. Stop syncing it before choosing individual files.");
                        copied += choice.Files.Count;
                        made += choice.Dirs.Count;
                        if (choice.RootSelected) made++;
                        if (openPath == null) openPath = localRoot;
                    }
                }
                catch (Exception ex) { error = ex.Message; }
                int copiedFiles = copied;
                int madeFolders = made;
                BeginInvoke(new Action(delegate
                {
                    SetBusy(false, null);
                    BuildTree();
                    if (!string.IsNullOrEmpty(openPath)) Navigate(openPath, true);
                    else LoadCurrent();
                    if (error != null)
                    {
                        ShowAction("The local replica wasn't created", "cloud");
                        Dialogs.Error(this, "Couldn't create the local replica", error);
                    }
                    else ShowAction("Copied " + Plural(copiedFiles, "file") + " and created " + Plural(madeFolders, "folder"), "same");
                }));
            });
        }

        static string Plural(int count, string noun)
        {
            return count + " " + noun + (count == 1 ? "" : "s");
        }

        static void AddChoice(List<string> target, string relative)
        {
            if (string.IsNullOrEmpty(relative)) return;
            foreach (string existing in target)
            {
                if (string.Equals(existing, relative, StringComparison.OrdinalIgnoreCase)) return;
            }
            target.Add(relative);
        }

        void Link_Click(object sender, EventArgs e)
        {
            string path = SelectedDirectory();
            if (string.IsNullOrEmpty(path))
            {
                Dialogs.Info(this, "Choose a folder to link", "Open a folder in a drive, or a folder on this PC, then choose Link folder.");
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
            List<Provider> found = Store.DetectProviders();
            Provider inside = null;
            foreach (Provider provider in found)
            {
                if (provider.IsOnline && Store.IsUnder(path, provider.Root)) inside = provider;
            }
            string local = path;
            string relative = "";
            Provider chosen = inside;
            if (inside != null)
            {
                relative = Store.Relative(path, inside.Root) ?? "";
                local = Store.ReplicaPath(inside, relative);
            }
            else
            {
                foreach (Provider provider in found)
                {
                    string match = SyncEngine.FindRelative(path, provider);
                    if (!string.IsNullOrEmpty(match))
                    {
                        chosen = provider;
                        relative = match;
                        break;
                    }
                }
            }
            using (LinkDialog dialog = new LinkDialog(found, chosen, local, relative))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    LinkFolder folder = SyncEngine.Link(dialog.LocalPath, dialog.Chosen.Id, dialog.RelativePath);
                    RunSyncId("Pull", folder.Id);
                    BuildTree();
                    Navigate(dialog.LocalPath, true);
                }
                catch (Exception ex)
                {
                    Dialogs.Error(this, "Couldn't link the folder", ex.Message);
                }
            }
        }

        void Unlink_Click(object sender, EventArgs e)
        {
            string path = SelectedDirectory();
            if (string.IsNullOrEmpty(path)) path = current;
            LinkFolder folder = SyncEngine.FindLink(path, Store.ReadLinks());
            if (folder == null)
            {
                Dialogs.Info(this, "This folder isn't linked", "Only linked folders can stop syncing. Open a linked folder, then try again.");
                return;
            }
            if (!Dialogs.Confirm(this, "Stop syncing this folder?", folder.Local + "\r\n\r\nThe files stay on disk. They will no longer upload when Windows shuts down or refresh when you sign in.", null, "Stop syncing", MessageKind.Warning))
                return;
            SyncEngine.Unlink(folder.Id);
            BuildTree();
            LoadCurrent();
            ShowAction("Stopped syncing " + Path.GetFileName(folder.Local.TrimEnd('\\')), "same");
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
                ShowAction("Added " + provider.Name, "same");
            }
        }

        void Log_Click(object sender, EventArgs e)
        {
            Store.EnsureDirs();
            Process.Start(new ProcessStartInfo("explorer.exe", Store.Root) { UseShellExecute = true });
        }

        void About_Click(object sender, EventArgs e)
        {
            Dialogs.Info(this, "Drive Folder Sync " + Store.Version,
                "Local replica copies only the folders and files you select. Selected files keep the same folder path as the drive.\r\n\r\n"
                + "Green status: changed on this PC since the last sync.\r\nRed status: changed in the cloud, or changed on both sides.\r\n\r\n"
                + "Linked folders refresh when you sign in and upload when Windows shuts down.");
        }

        void SetBusy(bool on, string message)
        {
            busy = on;
            progress.Visible = on;
            Cursor = on ? Cursors.AppStarting : Cursors.Default;
            if (on) ShowAction(message, "none");
            foreach (ToolStripItem item in new ToolStripItem[] { refreshButton, uploadButton, replicaButton, linkButton, syncAllButton })
                item.Enabled = !on;
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
                    Dialogs.Info(this, "Link this folder first", "Only linked folders refresh from or upload to the cloud. Open the folder, then choose Link folder.");
                    return;
                }
                id = folder.Id;
            }
            RunSyncId(direction, id);
        }

        void RunSyncId(string direction, string id)
        {
            if (busy) return;
            SetBusy(true, direction == "Pull" ? "Refreshing from the cloud\u2026" : "Uploading to the cloud\u2026");
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
                            List<LinkFolder> all = Store.ReadLinks();
                            LinkFolder folder = null;
                            foreach (LinkFolder item in all)
                            {
                                if (string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) folder = item;
                            }
                            if (folder == null) throw new InvalidOperationException("That linked folder is gone.");
                            stats = SyncEngine.SyncLink(folder, direction, false);
                            Store.SaveLinks(all);
                        }
                    }
                }
                catch (Exception ex) { error = ex.Message; }
                BeginInvoke(new Action(delegate
                {
                    SetBusy(false, null);
                    if (error != null)
                    {
                        ShowAction("Sync didn't finish", "cloud");
                        Dialogs.Error(this, "Sync didn't finish", error);
                    }
                    else if (stats != null) ShowAction(Summary(stats), stats.Errors > 0 ? "cloud" : stats.Conflict > 0 ? "warn" : "same");
                    LoadCurrent();
                }));
            });
        }

        static string Summary(SyncStats stats)
        {
            List<string> parts = new List<string>();
            if (stats.Up > 0) parts.Add(stats.Up + " uploaded");
            if (stats.Down > 0) parts.Add(stats.Down + " downloaded");
            if (stats.Conflict > 0) parts.Add(Plural(stats.Conflict, "conflict"));
            if (stats.Errors > 0) parts.Add(Plural(stats.Errors, "error"));
            if (parts.Count == 0) return "Sync finished  ·  everything is up to date";
            return "Sync finished  ·  " + string.Join("  ·  ", parts.ToArray());
        }

        string WindowStatePath
        {
            get { return Path.Combine(Store.Root, "window.json"); }
        }

        void RestoreWindow()
        {
            Dictionary<string, object> saved = Store.ReadObject(WindowStatePath);
            if (saved == null) return;
            Rectangle bounds = new Rectangle(Store.GetInt(saved, "x"), Store.GetInt(saved, "y"), Store.GetInt(saved, "width"), Store.GetInt(saved, "height"));
            if (bounds.Width >= MinimumSize.Width && bounds.Height >= MinimumSize.Height)
            {
                foreach (Screen screen in Screen.AllScreens)
                {
                    Rectangle visible = Rectangle.Intersect(screen.WorkingArea, bounds);
                    if (visible.Width < Theme.S(200) || visible.Height < Theme.S(120)) continue;
                    StartPosition = FormStartPosition.Manual;
                    Bounds = bounds;
                    break;
                }
            }
            if (Store.GetBool(saved, "maximized")) WindowState = FormWindowState.Maximized;
            savedSplit = Store.GetInt(saved, "split");
        }

        void SaveWindow()
        {
            try
            {
                Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                Dictionary<string, object> map = new Dictionary<string, object>();
                map["x"] = bounds.X;
                map["y"] = bounds.Y;
                map["width"] = bounds.Width;
                map["height"] = bounds.Height;
                map["maximized"] = WindowState == FormWindowState.Maximized;
                map["split"] = split.SplitterDistance;
                Store.WriteObject(WindowStatePath, map);
            }
            catch { }
        }
    }

    public class LinkDialog : Form
    {
        readonly ComboBox drives = new ComboBox();
        readonly TextField localBox = new TextField('\0', false, "Local folder");
        readonly TextField cloudBox = new TextField('\0', false, "Cloud folder");
        readonly Label cloudHint = new Label();
        readonly Panel error = new Panel();
        readonly List<Provider> providers;

        public Provider Chosen;
        public string LocalPath;
        public string RelativePath;

        public LinkDialog(List<Provider> providers, Provider selected, string local, string relative)
        {
            Theme.Init();
            this.providers = providers;
            Text = "Link folder";
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

            int width = Theme.S(580);
            int pad = Theme.S(24);
            int field = width - pad * 2;
            int y = pad;

            Label title = AddLabel(this, "Link a folder", Theme.Heading, Theme.Text, pad, y, field);
            y += title.Height + Theme.S(6);
            Label help = AddLabel(this, "Keep a folder on this PC in sync with a folder in Google Drive, OneDrive, Dropbox, or another cloud folder. Changes on this PC upload when Windows shuts down, and signing in refreshes the local copy from the cloud.",
                Theme.Body, Theme.TextMuted, pad, y, field);
            y += help.Height + Theme.S(20);

            y = FieldLabel("Drive", pad, y);
            drives.DropDownStyle = ComboBoxStyle.DropDownList;
            drives.DrawMode = DrawMode.OwnerDrawFixed;
            drives.ItemHeight = Theme.S(26);
            drives.Font = Theme.Body;
            drives.FlatStyle = FlatStyle.Standard;
            drives.SetBounds(pad, y, field, drives.Height);
            drives.DrawItem += new DrawItemEventHandler(Drives_DrawItem);
            foreach (Provider provider in providers)
            {
                if (provider.IsOnline) drives.Items.Add(provider);
            }
            if (selected != null && drives.Items.Contains(selected)) drives.SelectedItem = selected;
            drives.SelectedIndexChanged += new EventHandler(delegate { UpdateCloudHint(); HideError(); });
            Controls.Add(drives);
            y += drives.Height + Theme.S(18);

            FlatButton browseWidth = new FlatButton("Browse\u2026", false);
            int boxWidth = field - browseWidth.Width - Theme.S(8);

            y = FieldLabel("Cloud folder", pad, y);
            cloudBox.SetBounds(pad, y, boxWidth, Theme.S(32));
            cloudBox.Box.Text = relative ?? "";
            cloudBox.Box.TextChanged += new EventHandler(delegate { HideError(); });
            Controls.Add(cloudBox);
            FlatButton cloudBrowse = new FlatButton("Browse\u2026", false);
            cloudBrowse.Location = new Point(pad + boxWidth + Theme.S(8), y);
            cloudBrowse.Click += new EventHandler(BrowseCloud);
            Controls.Add(cloudBrowse);
            y += cloudBox.Height + Theme.S(4);
            cloudHint.Font = Theme.Small;
            cloudHint.ForeColor = Theme.TextMuted;
            cloudHint.AutoEllipsis = true;
            cloudHint.UseMnemonic = false;
            cloudHint.SetBounds(pad, y, field, Theme.Small.Height + Theme.S(4));
            Controls.Add(cloudHint);
            y += cloudHint.Height + Theme.S(14);

            y = FieldLabel("Local folder", pad, y);
            localBox.SetBounds(pad, y, boxWidth, Theme.S(32));
            localBox.Box.Text = local ?? "";
            localBox.Box.TextChanged += new EventHandler(delegate { HideError(); });
            Controls.Add(localBox);
            FlatButton localBrowse = new FlatButton("Browse\u2026", false);
            localBrowse.Location = new Point(pad + boxWidth + Theme.S(8), y);
            localBrowse.Click += new EventHandler(BrowseLocal);
            Controls.Add(localBrowse);
            y += localBox.Height + Theme.S(4);
            Label localHint = AddLabel(this, "The copy on this PC. It is created if it doesn't exist yet.", Theme.Small, Theme.TextMuted, pad, y, field);
            y += localHint.Height + Theme.S(12);

            error.SetBounds(pad, y, field, Theme.S(24));
            error.Visible = false;
            error.Paint += new PaintEventHandler(delegate(object sender, PaintEventArgs e)
            {
                int size = Theme.S(14);
                Theme.DrawGlyph(e.Graphics, Glyphs.Warning, size, Theme.CloudText, 0, (error.Height - size) / 2);
                Theme.DrawText(e.Graphics, error.Text, Theme.Body, new Rectangle(size + Theme.S(8), 0, error.Width - size - Theme.S(8), error.Height), Theme.CloudText, TextFormatFlags.Left);
            });
            Controls.Add(error);
            y += error.Height + Theme.S(12);

            Panel footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = Theme.S(64);
            footer.BackColor = Theme.Chrome;
            footer.Paint += new PaintEventHandler(delegate(object sender, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            });
            FlatButton ok = new FlatButton("Link folder", true);
            ok.Location = new Point(width - pad - ok.Width, (footer.Height - ok.Height) / 2);
            ok.Click += new EventHandler(Ok_Click);
            FlatButton cancel = new FlatButton("Cancel", false);
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(ok.Left - Theme.S(8) - cancel.Width, ok.Top);
            footer.Controls.Add(ok);
            footer.Controls.Add(cancel);
            Controls.Add(footer);
            AcceptButton = ok;
            CancelButton = cancel;
            ClientSize = new Size(width, y + footer.Height);
            UpdateCloudHint();
            ActiveControl = drives.SelectedItem == null ? (Control)drives : ok;
        }

        static Label AddLabel(Control parent, string text, Font font, Color color, int x, int y, int width)
        {
            Label label = new Label();
            label.Font = font;
            label.ForeColor = color;
            label.UseMnemonic = false;
            label.AutoSize = false;
            label.Text = text;
            Size size = TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            label.SetBounds(x, y, width, size.Height + Theme.S(2));
            parent.Controls.Add(label);
            return label;
        }

        int FieldLabel(string text, int x, int y)
        {
            Label label = AddLabel(this, text, Theme.Strong, Theme.Text, x, y, Theme.S(300));
            return y + label.Height + Theme.S(6);
        }

        void Drives_DrawItem(object sender, DrawItemEventArgs e)
        {
            bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
            bool selected = (e.State & DrawItemState.Selected) != 0 && !edit;
            using (SolidBrush brush = new SolidBrush(selected ? Theme.RowSelected : Theme.Window)) e.Graphics.FillRectangle(brush, e.Bounds);
            if (e.Index < 0 || e.Index >= drives.Items.Count)
            {
                Theme.DrawText(e.Graphics, "Choose a drive", Theme.Body, Rectangle.Inflate(e.Bounds, -Theme.S(6), 0), Theme.TextFaint, TextFormatFlags.Left);
                return;
            }
            Provider provider = drives.Items[e.Index] as Provider;
            if (provider == null) return;
            int size = SystemInformation.SmallIconSize.Width;
            int x = e.Bounds.X + Theme.S(6);
            Icon icon = ShellImages.SmallIcon(ShellImages.RealIndex(provider.Root), provider.Root, true, true);
            if (icon != null) e.Graphics.DrawIcon(icon, new Rectangle(x, e.Bounds.Y + (e.Bounds.Height - size) / 2, size, size));
            x += size + Theme.S(8);
            Theme.DrawText(e.Graphics, provider.Title, Theme.Body, new Rectangle(x, e.Bounds.Y, e.Bounds.Right - x - Theme.S(4), e.Bounds.Height), Theme.Text, TextFormatFlags.Left);
        }

        void UpdateCloudHint()
        {
            Provider provider = drives.SelectedItem as Provider;
            cloudHint.Text = provider == null ? "Choose a drive first." : "A folder inside " + provider.Root + ". Leave it empty to use the whole drive.";
        }

        void ShowError(string message)
        {
            error.Text = message;
            error.Visible = true;
            error.Invalidate();
            System.Media.SystemSounds.Beep.Play();
        }

        void HideError()
        {
            error.Visible = false;
        }

        void BrowseCloud(object sender, EventArgs e)
        {
            Provider provider = drives.SelectedItem as Provider;
            if (provider == null)
            {
                ShowError("Choose a drive first.");
                return;
            }
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.SelectedPath = provider.Root;
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (!Store.IsUnder(dialog.SelectedPath, provider.Root))
                {
                    ShowError("Choose a folder inside " + provider.Root);
                    return;
                }
                cloudBox.Box.Text = Store.Relative(dialog.SelectedPath, provider.Root) ?? "";
            }
        }

        void BrowseLocal(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.ShowNewFolderButton = true;
                if (Directory.Exists(localBox.Box.Text)) dialog.SelectedPath = localBox.Box.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) localBox.Box.Text = dialog.SelectedPath;
            }
        }

        void Ok_Click(object sender, EventArgs e)
        {
            Chosen = drives.SelectedItem as Provider;
            LocalPath = localBox.Box.Text.Trim();
            RelativePath = cloudBox.Box.Text.Trim().Trim('\\');
            if (Chosen == null)
            {
                ShowError("Choose a drive.");
                drives.Focus();
                return;
            }
            if (string.IsNullOrEmpty(LocalPath))
            {
                ShowError("Choose a local folder.");
                localBox.Box.Focus();
                return;
            }
            string cloud;
            try { cloud = Path.Combine(Chosen.Root, RelativePath); }
            catch
            {
                ShowError("That cloud folder path isn't valid.");
                return;
            }
            if (string.Equals(Store.Norm(LocalPath), Store.Norm(cloud), StringComparison.OrdinalIgnoreCase))
            {
                ShowError("Choose a local folder that is separate from the cloud folder.");
                localBox.Box.Focus();
                return;
            }
            if (!Directory.Exists(cloud))
            {
                ShowError("That cloud folder was not found.");
                cloudBox.Box.Focus();
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
