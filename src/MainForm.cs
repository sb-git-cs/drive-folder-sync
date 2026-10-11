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

    public partial class MainForm : Form
    {
        readonly NavTree tree = new NavTree();
        readonly NavTree localTree = new NavTree();
        readonly FileList list = new FileList();
        readonly FileList localList = new FileList();
        readonly SplitContainer sides = new SplitContainer();
        readonly SplitContainer localSplit = new SplitContainer();
        readonly Label driveCaption = new Label();
        readonly Label localCaption = new Label();
        readonly EmptyState localEmpty = new EmptyState();
        readonly List<Row> localRows = new List<Row>();
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
        FileList hotList;
        int savedSplit;
        string paneDriveRoot = "";
        string paneLocalRoot = "";
        string driveKey = "vfs:pc";
        string localKey = "";
        volatile bool busy;
        bool closeWhenIdle;
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
            Size = Theme.S(1280, 760);
            MinimumSize = Theme.S(980, 560);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            RestoreWindow();

            BuildCommandBar();
            Panel nav = BuildNavRow();
            BuildContent();
            BuildStatusBar();

            header.Dock = DockStyle.Top;
            Controls.Add(sides);
            Controls.Add(header);
            Controls.Add(BuildDriveTabs());
            Controls.Add(nav);
            Controls.Add(commands);
            Controls.Add(status);

            HandleCreated += new EventHandler(delegate
            {
                ShellImages.Attach(tree);
                ShellImages.Attach(localTree);
                Native.Caption(this, Theme.Chrome);
            });
            Shown += new EventHandler(delegate
            {
                PlaceSplit(sides, Theme.S(280), Math.Max(Theme.S(280), (sides.Width - sides.SplitterWidth) / 2));
                PlaceSplit(split, Theme.S(120), savedSplit > Theme.S(120) ? savedSplit : Theme.S(170));
                PlaceSplit(localSplit, Theme.S(120), Theme.S(170));
                list.Focus();
            });
            Load += new EventHandler(MainForm_Load);
            FormClosing += new FormClosingEventHandler(MainForm_FormClosing);
        }

        // Closing mid-copy would end the process and leave a partially written file, so wait for the sync.
        void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (busy && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                if (closeWhenIdle) return;
                if (!Dialogs.Confirm(this, "A sync is still running", "Drive Folder Sync will close as soon as the current copy finishes. Your files are not affected.", null, "Close when finished", MessageKind.Info))
                    return;
                closeWhenIdle = true;
                Hide();
                return;
            }
            SaveWindow();
        }

        // Reopening the window while it waits to close keeps it open.
        protected override void OnVisibleChanged(EventArgs e)
        {
            if (Visible) closeWhenIdle = false;
            base.OnVisibleChanged(e);
        }

        // Opens the attach dialog for a folder chosen from the File Explorer right-click menu.
        public void RequestAttach(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                ShowAction("The folder you chose is no longer available.", "warn");
                return;
            }
            if (busy)
            {
                Dialogs.Info(this, "A sync is still running", "Try attaching " + path + " again when the current sync finishes.");
                return;
            }
            OfferLink(path);
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

            refreshButton = Command("Refresh from cloud", Glyphs.Download, "Download cloud changes into the selected attached folder", delegate { RunSync("Pull", true); });
            uploadButton = Command("Back up to cloud", Glyphs.Upload, "Upload changes from the selected attached folder to the cloud", delegate { RunSync("Push", true); });
            replicaButton = Command("Local replica", Glyphs.SaveLocal, "Copy only the selected folders and files to this PC", new EventHandler(Replica_Click));
            linkButton = Command("Attach folder", Glyphs.Link, "Back up a folder from any location on this PC", new EventHandler(AttachFolder_Click));
            ToolStripButton addDrive = Command("Add cloud drive", Glyphs.MapDrive, "Choose the folder used by your cloud desktop app. Use Attach folder for local backups.", new EventHandler(AddDrive_Click));

            syncAllButton = new ToolStripDropDownButton("Sync all");
            Style(syncAllButton, Glyphs.Sync, "Refresh or back up every attached folder");
            syncAllButton.DropDownItems.Add(MenuItem("Refresh all attached folders", Glyphs.Download, delegate { RunSync("Pull", false); }));
            syncAllButton.DropDownItems.Add(MenuItem("Back up all attached folders", Glyphs.Upload, delegate { RunSync("Push", false); }));

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
            ToolStripMenuItem autoUpdate = new ToolStripMenuItem("Install updates automatically at startup");
            autoUpdate.CheckOnClick = true;
            autoUpdate.Checked = UpdateManager.AutomaticEnabled;
            autoUpdate.CheckedChanged += delegate { UpdateManager.SetAutomatic(autoUpdate.Checked); };
            more.DropDownItems.Add(autoUpdate);
            more.DropDownItems.Add(MenuItem("Check and install updates", Glyphs.Download, delegate { CheckForUpdates(); }));
            more.DropDownItems.Add(MenuItem("View sync log", Glyphs.OpenFile, new EventHandler(ViewLog_Click)));
            more.DropDownItems.Add(MenuItem("Open log folder", Glyphs.FolderOpen, new EventHandler(Log_Click)));
            more.DropDownItems.Add(new ToolStripSeparator());
            more.DropDownItems.Add(MenuItem("Uninstall…", Glyphs.Delete, delegate
            {
                if (busy)
                {
                    Dialogs.Info(this, "A sync is still running", "Uninstall when the current sync finishes.");
                    return;
                }
                if (Installer.Uninstall(false, this)) Application.Exit();
            }));
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

        static void PlaceSplit(SplitContainer pane, int minimum, int distance)
        {
            try
            {
                int limit = pane.Width - pane.SplitterWidth - minimum;
                if (limit <= minimum) return;
                pane.Panel1MinSize = minimum;
                pane.Panel2MinSize = minimum;
                if (distance < minimum) distance = minimum;
                if (distance > limit) distance = limit;
                pane.SplitterDistance = distance;
            }
            catch { }
        }

        void BuildContent()
        {
            sides.Dock = DockStyle.Fill;
            sides.Orientation = Orientation.Vertical;
            sides.SplitterWidth = Theme.S(6);
            sides.BackColor = Theme.Border;
            sides.TabStop = false;
            sides.Panel1.BackColor = Theme.Window;
            sides.Panel2.BackColor = Theme.Window;

            ConfigureTree(tree);
            ConfigureTree(localTree);
            treeMenu.Opening += new CancelEventHandler(TreeMenu_Opening);
            tree.ContextMenuStrip = treeMenu;
            localTree.ContextMenuStrip = treeMenu;
            ConfigureList(list, empty);
            ConfigureList(localList, localEmpty);
            listMenu.Opening += new CancelEventHandler(ListMenu_Opening);

            Panel cloudPane = MakePane(driveCaption, "Drive", split, tree, list);
            cloudPane.Controls.Add(BuildCloudToolbar());
            sides.Panel1.Controls.Add(cloudPane);
            Panel localPane = MakePane(localCaption, "Local folders", localSplit, localTree, localList);
            localPane.Controls.Add(BuildPairToolbar());
            sides.Panel2.Controls.Add(localPane);
            WireTreeDrop(tree, false);
            WireTreeDrop(localTree, true);
        }

        void WireTreeDrop(NavTree view, bool localSide)
        {
            view.AllowDrop = true;
            view.DragEnter += new DragEventHandler(List_DragEnter);
            view.DragOver += new DragEventHandler(List_DragOver);
            view.DragDrop += new DragEventHandler(delegate(object sender, DragEventArgs e)
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                string[] sources = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (sources == null || sources.Length == 0) return;
                string dest = localSide ? localKey : driveKey;
                Point point = view.PointToClient(new Point(e.X, e.Y));
                TreeNode node = view.GetNodeAt(point);
                if (node != null)
                {
                    NodeTag tag = node.Tag as NodeTag;
                    if (tag != null && IsRealPath(tag.FullPath)) dest = tag.FullPath;
                }
                if (!IsRealPath(dest))
                {
                    ShowAction("Drop onto a folder inside the selected folder pair.", "warn");
                    return;
                }
                StartTransfer(localSide, dest, sources);
            });
        }

        void ConfigureTree(NavTree view)
        {
            view.Dock = DockStyle.Fill;
            view.BorderStyle = BorderStyle.None;
            view.BackColor = Theme.Pane;
            view.ForeColor = Theme.Text;
            view.Font = Theme.Body;
            view.HideSelection = false;
            view.ShowLines = false;
            view.ShowRootLines = true;
            view.ShowPlusMinus = true;
            view.FullRowSelect = true;
            view.HotTracking = true;
            view.ShowNodeToolTips = true;
            view.ItemHeight = Theme.S(28);
            view.Indent = Theme.S(16);
            view.BeforeExpand += new TreeViewCancelEventHandler(Tree_BeforeExpand);
            view.AfterSelect += new TreeViewEventHandler(Tree_AfterSelect);
            view.NodeMouseClick += new TreeNodeMouseClickEventHandler(delegate(object sender, TreeNodeMouseClickEventArgs e)
            {
                if (e.Button == MouseButtons.Right) view.SelectedNode = e.Node;
            });
        }

        void ConfigureList(FileList view, EmptyState blank)
        {
            view.Dock = DockStyle.Fill;
            view.View = View.Details;
            view.OwnerDraw = true;
            view.MultiSelect = true;
            view.FullRowSelect = true;
            view.HideSelection = false;
            view.BorderStyle = BorderStyle.None;
            view.HeaderStyle = ColumnHeaderStyle.Clickable;
            view.Font = Theme.Body;
            view.BackColor = Theme.Window;
            view.ForeColor = Theme.Text;
            view.AllowDrop = true;
            ImageList spacer = new ImageList();
            spacer.ImageSize = new Size(1, Theme.S(32));
            view.SmallImageList = spacer;
            view.Columns.Add("Name", Theme.S(240));
            view.Columns.Add("Status", Theme.S(150));
            view.Columns.Add("Size", Theme.S(80), HorizontalAlignment.Right);
            view.Columns.Add("Date modified", Theme.S(130));
            view.Columns.Add("Last synced", Theme.S(130));
            view.DrawColumnHeader += new DrawListViewColumnHeaderEventHandler(delegate(object sender, DrawListViewColumnHeaderEventArgs e) { e.DrawDefault = true; });
            view.DrawItem += new DrawListViewItemEventHandler(List_DrawItem);
            view.DrawSubItem += new DrawListViewSubItemEventHandler(List_DrawSubItem);
            view.ColumnClick += new ColumnClickEventHandler(List_ColumnClick);
            view.ItemActivate += new EventHandler(List_ItemActivate);
            view.ItemDrag += new ItemDragEventHandler(List_ItemDrag);
            view.DragEnter += new DragEventHandler(List_DragEnter);
            view.DragOver += new DragEventHandler(List_DragOver);
            view.DragDrop += new DragEventHandler(List_DragDrop);
            view.MouseMove += new MouseEventHandler(List_MouseMove);
            view.MouseLeave += new EventHandler(delegate { SetHot(view, -1); });
            view.GotFocus += new EventHandler(delegate { view.Invalidate(); });
            view.LostFocus += new EventHandler(delegate { view.Invalidate(); });
            view.SelectedIndexChanged += new EventHandler(delegate
            {
                if (selectionPending) return;
                selectionPending = true;
                BeginInvoke(new Action(delegate
                {
                    selectionPending = false;
                    UpdateStatus();
                }));
            });
            view.Resize += new EventHandler(delegate { FitColumns(view); PlaceEmpty(view, blank); });
            view.ColumnWidthChanged += new ColumnWidthChangedEventHandler(delegate(object sender, ColumnWidthChangedEventArgs e)
            {
                if (e.ColumnIndex != 0) FitColumns(view);
            });
            view.ContextMenuStrip = listMenu;
            view.Controls.Add(blank);
        }

        Panel MakePane(Label caption, string title, SplitContainer inner, NavTree sideTree, FileList sideList)
        {
            caption.Text = title;
            caption.Dock = DockStyle.Top;
            caption.Height = Theme.S(36);
            caption.Font = Theme.Strong;
            caption.ForeColor = Theme.Text;
            caption.BackColor = Theme.Pane;
            caption.TextAlign = ContentAlignment.MiddleLeft;
            caption.Padding = Theme.Pad(12, 0, 8, 0);
            caption.AutoEllipsis = true;
            caption.UseMnemonic = false;

            inner.Dock = DockStyle.Fill;
            inner.FixedPanel = FixedPanel.Panel1;
            inner.SplitterWidth = Theme.S(4);
            inner.BackColor = Theme.Window;
            inner.TabStop = false;
            inner.Panel1.BackColor = Theme.Pane;
            inner.Panel1.Padding = Theme.Pad(4, 4, 2, 4);
            inner.Panel2.BackColor = Theme.Window;
            inner.Panel2.Padding = Theme.Pad(8, 0, 4, 0);
            inner.Panel1.Controls.Add(sideTree);
            Panel listHost = new Panel();
            listHost.Dock = DockStyle.Fill;
            listHost.BackColor = Theme.Window;
            listHost.Controls.Add(sideList);
            inner.Panel2.Controls.Add(listHost);

            Panel host = new Panel();
            host.Dock = DockStyle.Fill;
            host.BackColor = Theme.Window;
            host.Controls.Add(inner);
            host.Controls.Add(caption);
            return host;
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
            if (Store.IsInstalledCopy() && UpdateManager.AutomaticEnabled) CheckForUpdates(true);
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
            else if (e.Control && e.KeyCode == Keys.A && (list.Focused || localList.Focused))
            {
                FileList view = ActiveList();
                view.BeginUpdate();
                foreach (ListViewItem item in view.Items) item.Selected = true;
                view.EndUpdate();
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
            RefreshDriveTabs();
            paneDriveRoot = "";
            paneLocalRoot = "";
            LoadCurrent();
        }

        void BuildTree()
        {
            providers = Store.DetectProviders();
            links = Store.ReadLinks();
            paneDriveRoot = "";
            paneLocalRoot = "";
            RefreshDriveTabs();
        }

        void ApplyTrees(string driveRoot, string localRoot, string selectDrive, string selectLocal)
        {
            bool same = string.Equals(paneDriveRoot, driveRoot ?? "", StringComparison.OrdinalIgnoreCase)
                && string.Equals(paneLocalRoot, localRoot ?? "", StringComparison.OrdinalIgnoreCase);
            suppressTree = true;
            try
            {
                if (!same)
                {
                    paneDriveRoot = driveRoot ?? "";
                    paneLocalRoot = localRoot ?? "";
                    FillTree(tree, paneDriveRoot, true);
                    FillTree(localTree, paneLocalRoot, false);
                }
                Reveal(tree, paneDriveRoot, selectDrive);
                Reveal(localTree, paneLocalRoot, selectLocal);
            }
            finally { suppressTree = false; }
        }

        void FillTree(TreeView view, string root, bool driveSide)
        {
            view.BeginUpdate();
            view.Nodes.Clear();
            if (root == "vfs:pc")
            {
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
                }
                NodeTag linkedTag = new NodeTag();
                linkedTag.Key = "vfs:linked";
                linkedTag.Loaded = true;
                TreeNode linked = new TreeNode("Attached folders");
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
                view.Nodes.Add(pc);
                pc.Expand();
            }
            else if (root == "vfs:replicas")
            {
                NodeTag tag = new NodeTag();
                tag.Key = "vfs:replicas";
                tag.Loaded = true;
                TreeNode rootNode = new TreeNode("Attached local folders");
                rootNode.Tag = tag;
                rootNode.ImageIndex = ShellImages.Index(Store.LocalRoot(), true);
                rootNode.SelectedImageIndex = rootNode.ImageIndex;
                foreach (LinkFolder folder in links)
                {
                    NodeTag child = new NodeTag();
                    child.Key = folder.Local;
                    child.FullPath = folder.Local;
                    TreeNode node = MakeDirNode(Path.GetFileName(folder.Local.TrimEnd('\\')), child);
                    node.ToolTipText = folder.Local;
                    rootNode.Nodes.Add(node);
                }
                view.Nodes.Add(rootNode);
                rootNode.Expand();
            }
            else if (!string.IsNullOrEmpty(root) && root != "vfs:linked")
            {
                NodeTag tag = new NodeTag();
                tag.Key = root;
                tag.FullPath = root;
                string title = Path.GetFileName(root.TrimEnd('\\'));
                if (driveSide)
                {
                    foreach (Provider provider in providers)
                    {
                        if (string.Equals(Store.Norm(provider.Root), Store.Norm(root), StringComparison.OrdinalIgnoreCase))
                            title = provider.Title;
                    }
                }
                else title = "Local folder  ·  " + (string.IsNullOrEmpty(title) ? "replica" : title);
                TreeNode node = MakeDirNode(title, tag);
                if (Directory.Exists(root)) node.ImageIndex = ShellImages.RealIndex(root);
                node.SelectedImageIndex = node.ImageIndex;
                node.ToolTipText = root;
                view.Nodes.Add(node);
                if (Directory.Exists(root)) node.Expand();
            }
            view.EndUpdate();
        }

        void Reveal(TreeView view, string root, string full)
        {
            if (view.Nodes.Count == 0 || string.IsNullOrEmpty(full)) return;
            if (full == "vfs:pc" || full == "vfs:linked" || full == "vfs:replicas" || string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            {
                TreeNode found = FindNode(view.Nodes, full);
                if (found == null) found = view.Nodes[0];
                view.SelectedNode = found;
                if (found != null) found.EnsureVisible();
                return;
            }
            TreeNode node = FindNode(view.Nodes, root);
            if (node == null) node = view.Nodes[0];
            string relative = Store.Relative(full, root);
            if (node != null && !string.IsNullOrEmpty(relative))
            {
                foreach (string part in relative.Split('\\'))
                {
                    if (string.IsNullOrEmpty(part)) continue;
                    NodeTag tag = node.Tag as NodeTag;
                    if (tag != null && !tag.Loaded) node.Expand();
                    TreeNode next = null;
                    foreach (TreeNode child in node.Nodes)
                    {
                        if (string.Equals(child.Text, part, StringComparison.OrdinalIgnoreCase)) { next = child; break; }
                    }
                    if (next == null) break;
                    node = next;
                }
            }
            view.SelectedNode = node;
            if (node != null) node.EnsureVisible();
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
            if (tag == null || string.IsNullOrEmpty(tag.Key) || tag.Key == "vfs:replicas") return;
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
            activeLinkId = "";
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

        sealed class Page
        {
            public List<Row> Drive = new List<Row>();
            public List<Row> Local = new List<Row>();
            public ViewInfo Info = new ViewInfo();
            public string DriveRoot = "";
            public string LocalRoot = "";
            public string DriveKey = "";
            public string LocalKey = "";
            public string DriveCaption = "Drive";
            public string LocalCaption = "Local folders";
            public string ProviderId = "";
            public string LinkId = "";
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
                Page page = new Page();
                List<Provider> found = null;
                try
                {
                    found = Store.DetectProviders();
                    page = BuildPage(key, found);
                }
                catch (Exception ex) { error = ex.Message; }
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (token != loadToken) return;
                        loading = false;
                        if (found != null) providers = found;
                        List<string> keepDrive = SelectedPaths(list);
                        List<string> keepLocal = SelectedPaths(localList);
                        rows.Clear();
                        localRows.Clear();
                        loadError = error;
                        if (error != null || page == null)
                        {
                            char glyph;
                            List<AddressBar.Crumb> crumbs = Crumbs(key, out glyph);
                            view = new ViewInfo();
                            view.Title = crumbs[crumbs.Count - 1].Text;
                            view.Detail = key.StartsWith("vfs:") ? "" : key;
                            driveKey = key;
                            localKey = "";
                        }
                        else
                        {
                            rows.AddRange(page.Drive);
                            localRows.AddRange(page.Local);
                            view = page.Info;
                            driveKey = page.DriveKey;
                            localKey = page.LocalKey;
                            driveCaption.Text = page.DriveCaption;
                            localCaption.Text = page.LocalCaption;
                            ApplyTrees(page.DriveRoot, page.LocalRoot, page.DriveKey, page.LocalKey);
                        }
                        UpdateFolderControls(error == null ? page : null);
                        header.Set(view.Title, view.Detail, view.Tip, view.Badge, view.BadgeKind, view.Legend);
                        search.SetCue("Search " + view.Title);
                        if (userSorted || !view.Virtual) SortRows();
                        list.SetSortArrow(userSorted || !view.Virtual ? sortColumn : -1, sortAsc);
                        localList.SetSortArrow(userSorted || !view.Virtual ? sortColumn : -1, sortAsc);
                        ShowRows(keepDrive, keepLocal);
                    }));
                }
                catch (InvalidOperationException) { }
            });
        }

        Page BuildPage(string key, List<Provider> found)
        {
            Page page = new Page();
            if (key == "vfs:pc")
            {
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
                    page.Drive.Add(row);
                }
                foreach (LinkFolder folder in Store.ReadLinks())
                {
                    Row attached = new Row();
                    attached.Name = new DirectoryInfo(folder.Local).Name;
                    attached.Detail = folder.Local;
                    attached.Status = Directory.Exists(folder.Local) ? "Attached" : "Not available";
                    attached.Kind = Directory.Exists(folder.Local) ? "none" : "warn";
                    attached.FullPath = folder.Local;
                    attached.NavigateKey = folder.Local;
                    attached.IsDir = true;
                    page.Local.Add(attached);
                }
                Row linked = new Row();
                int count = Store.ReadLinks().Count;
                linked.Name = "Attached folders";
                linked.Status = count + " attached";
                linked.Kind = "none";
                linked.IsDir = true;
                linked.FullPath = Store.LocalRoot();
                linked.NavigateKey = "vfs:linked";
                page.Drive.Add(linked);
                page.Info.Title = "This PC";
                page.Info.Detail = "Select a drive tab, then attach a local folder from any location on this PC.";
                page.Info.Badge = found.Count == 1 ? "1 drive" : found.Count + " drives";
                page.Info.Virtual = true;
                page.DriveRoot = "vfs:pc";
                page.LocalRoot = "vfs:replicas";
                page.DriveKey = "vfs:pc";
                page.LocalKey = "vfs:replicas";
                page.DriveCaption = "Drive";
                page.LocalCaption = "Attached local folders";
                return page;
            }
            Store.Place place = Store.FindPlace(key, found);
            if (place != null)
            {
                LinkFolder link = place.Link;
                page.ProviderId = place.Provider.Id;
                page.LinkId = link == null ? "" : link.Id;
                page.DriveRoot = place.Provider.Root;
                page.DriveKey = place.CloudPath;
                page.DriveCaption = place.Provider.Name + " drive data";
                page.Info.Title = place.Provider.Name + " drive data";
                page.Info.Legend = true;
                if (link == null)
                {
                    page.Drive = ListFolder(place.CloudPath, null, "", false, null, null);
                    page.Info.Detail = "Attach a local folder from any drive, or select an existing attachment on the right.";
                    page.LocalCaption = "Local folders  ·  No attachment selected";
                    page.Info.Badge = "Not attached";
                }
                else
                {
                    page.LocalRoot = place.LocalRoot;
                    page.LocalKey = place.ReplicaPath;
                    page.LocalCaption = "Local folder  ·  " + new DirectoryInfo(link.Local).Name;
                    Dictionary<string, Snap> state = Store.ReadState(Store.StatePath(link.Id));
                    page.Drive = ListFolder(place.CloudPath, place.ReplicaPath, place.LinkRelative, false, link, state);
                    page.Local = ListFolder(place.ReplicaPath, place.CloudPath, place.LinkRelative, true, link, state);
                    page.Info.Detail = "Backs up at shutdown · Refreshes at sign-in · Cloud copies are kept after local deletion";
                    page.Info.Tip = "Cloud: " + place.CloudPath + "\r\nLocal: " + place.ReplicaPath;
                    string when = SyncEngine.FormatWhen(link.LastSync);
                    page.Info.Badge = string.IsNullOrEmpty(when) ? "Attached · Not synced yet" : "Synced " + when;
                    page.Info.BadgeKind = "info";
                    if (!place.Provider.IsOnline || !Directory.Exists(link.Local))
                    {
                        page.Info.Badge = "Folder unavailable";
                        page.Info.BadgeKind = "cloud";
                    }
                    else if (!Directory.Exists(place.CloudPath))
                    {
                        page.Info.Badge = "Missing in the cloud";
                        page.Info.BadgeKind = "cloud";
                    }
                    else if (link.LastErrors > 0)
                    {
                        page.Info.Badge = "Last sync had " + Plural(link.LastErrors, "error") + " · see the log";
                        page.Info.BadgeKind = "cloud";
                    }
                }
                return page;
            }
            page.Info = new ViewInfo();
            page.Drive = BuildRows(key, page.Info);
            page.DriveKey = key;
            page.DriveRoot = key == "vfs:linked" ? "vfs:linked" : key;
            page.LocalRoot = "";
            page.LocalKey = "";
            page.DriveCaption = page.Info.Title;
            page.LocalCaption = "Local folders";
            return page;
        }

        List<Row> ListFolder(string folder, string otherFolder, string relBase, bool folderIsLocal, LinkFolder link, Dictionary<string, Snap> state)
        {
            List<Row> built = new List<Row>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return built;
            FileSystemInfo[] children;
            try { children = new DirectoryInfo(folder).GetFileSystemInfos(); }
            catch (Exception ex) { throw new IOException("Could not read " + folder, ex); }
            foreach (FileSystemInfo child in children)
            {
                bool isDir = (child.Attributes & FileAttributes.Directory) != 0;
                if (SyncEngine.IsRedirect(child.FullName, child.Attributes)) continue;
                if (SyncEngine.SkipName(child.Name, isDir)) continue;
                Row row = new Row();
                row.Name = child.Name;
                row.IsDir = isDir;
                row.FullPath = child.FullName;
                row.NavigateKey = isDir ? child.FullName : null;
                SetFile(row, child, isDir);
                string rel = string.IsNullOrEmpty(relBase) ? child.Name : relBase + "\\" + child.Name;
                string otherPath = string.IsNullOrEmpty(otherFolder) ? null : Path.Combine(otherFolder, child.Name);
                bool otherExists = !string.IsNullOrEmpty(otherPath) && (isDir ? Directory.Exists(otherPath) : File.Exists(otherPath));
                if (isDir)
                {
                    row.Status = "Folder";
                    row.Kind = "none";
                    if (!otherExists)
                    {
                        row.Status = folderIsLocal && link != null ? "Missing in the cloud" : (folderIsLocal ? "Only on this PC" : "Only in cloud");
                        row.Kind = folderIsLocal ? "local" : "cloud";
                    }
                    if (link != null && otherExists) SetSynced(row, link.LastSync);
                }
                else if (link != null && state != null)
                {
                    FileInfo file = (FileInfo)child;
                    FileRecord here = new FileRecord();
                    here.Rel = rel;
                    here.Full = child.FullName;
                    here.Length = file.Length;
                    here.MtimeUtc = file.LastWriteTimeUtc;
                    FileRecord localRec = folderIsLocal ? here : null;
                    FileRecord remoteRec = folderIsLocal ? null : here;
                    if (otherExists)
                    {
                        FileInfo other = new FileInfo(otherPath);
                        FileRecord otherRec = new FileRecord();
                        otherRec.Rel = rel;
                        otherRec.Full = otherPath;
                        otherRec.Length = other.Length;
                        otherRec.MtimeUtc = other.LastWriteTimeUtc;
                        if (folderIsLocal) remoteRec = otherRec;
                        else localRec = otherRec;
                    }
                    Snap snap = state.ContainsKey(rel) ? state[rel] : null;
                    try
                    {
                        SyncEngine.RowStatus classified = SyncEngine.Classify(localRec, remoteRec, snap);
                        row.Kind = classified.Kind;
                        row.Status = classified.Text;
                        SetSynced(row, classified.LastSynced);
                    }
                    catch (IOException) { row.Kind = "warn"; row.Status = "Cannot compare"; }
                    catch (UnauthorizedAccessException) { row.Kind = "warn"; row.Status = "Cannot compare"; }
                }
                else if (!otherExists)
                {
                    row.Status = folderIsLocal ? "Only on this PC" : "Only in cloud";
                    row.Kind = folderIsLocal ? "local" : "cloud";
                }
                else
                {
                    row.Kind = "same";
                    row.Status = "In both places";
                    if (!isDir && File.Exists(otherPath))
                    {
                        FileInfo other = new FileInfo(otherPath);
                        FileInfo file = child as FileInfo;
                        if (file != null && other.Length != file.Length)
                        {
                            row.Kind = "cloud";
                            row.Status = "Changed on both sides";
                        }
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
                linked.Name = "Attached folders";
                linked.Status = count + " attached";
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
                    string remote = Store.ResolveRemote(folder, found);
                    if (string.IsNullOrEmpty(remote)) { row.Status = "Drive missing"; row.Kind = "warn"; }
                    else if (!Directory.Exists(remote)) { row.Status = "Missing in the cloud"; row.Kind = "local"; }
                    else if (!Directory.Exists(folder.Local)) { row.Status = "Local folder missing"; row.Kind = "cloud"; }
                    else if (folder.LastErrors > 0) { row.Status = "Attached  ·  " + folder.LastErrors + (folder.LastErrors == 1 ? " error" : " errors"); row.Kind = "cloud"; }
                    else { row.Status = "Attached"; row.Kind = "same"; SetSynced(row, folder.LastSync); }
                    row.Modified = "";
                    row.Size = "";
                    built.Add(row);
                }
                info.Title = "Attached folders";
                info.Detail = "Attached folders back up when Windows shuts down and refresh from the cloud when you sign in.";
                info.Badge = folders.Count + " attached";
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
            info.Badge = "Not attached";
            if (link != null)
            {
                string remoteRoot = Store.ResolveRemote(link, found2);
                viewingLocal = Store.IsUnder(key, link.Local);
                string linkRoot = viewingLocal ? link.Local : remoteRoot;
                otherRoot = viewingLocal ? remoteRoot : link.Local;
                if (!string.IsNullOrEmpty(linkRoot)) relBase = Store.Relative(key, linkRoot) ?? "";
                state = Store.ReadState(Store.StatePath(link.Id));
                string when = SyncEngine.FormatWhen(link.LastSync);
                info.Badge = string.IsNullOrEmpty(when) ? "Attached, not synced yet" : "Synced " + when;
                info.BadgeKind = "info";
                if (string.IsNullOrEmpty(remoteRoot) || !Directory.Exists(remoteRoot))
                {
                    info.Badge = "Missing in the cloud";
                    info.BadgeKind = "cloud";
                }
                else if (link.LastErrors > 0)
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
                        string otherPath = string.IsNullOrEmpty(otherRoot) ? null : Path.Combine(otherRoot, rel);
                        if (!string.IsNullOrEmpty(otherPath) && Directory.Exists(otherPath)) SetSynced(row, link.LastSync);
                        else
                        {
                            row.Kind = viewingLocal ? "local" : "cloud";
                            row.Status = viewingLocal ? "Missing in the cloud" : "Only in cloud";
                        }
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
                        SetSynced(row, classified.LastSynced);
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
            if (link == null) info.Badge = "Not attached";
            else
            {
                info.Badge = string.IsNullOrEmpty(when) ? "Attached, not synced yet" : "Synced " + when;
                info.BadgeKind = "info";
                if (!place.Provider.IsOnline || !Directory.Exists(place.ReplicaPath))
                {
                    info.Badge = "Folder unavailable";
                    info.BadgeKind = "cloud";
                }
                else if (!Directory.Exists(place.CloudPath))
                {
                    info.Badge = "Missing in the cloud";
                    info.BadgeKind = "cloud";
                }
                else if (link.LastErrors > 0)
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
                    if (onCloud && !onLocal) { row.Status = "Only in cloud"; row.Kind = "cloud"; }
                    else if (onLocal && !onCloud) { row.Status = "Only on this PC"; row.Kind = "local"; }
                    if (state != null && onCloud && onLocal)
                    {
                        string rel = string.IsNullOrEmpty(relBase) ? child.Name : relBase + "\\" + child.Name;
                        string kind = FolderKind(state, rel);
                        if (kind == "local") { row.Status = "Has local changes"; row.Kind = "local"; }
                        else if (kind == "cloud") { row.Status = "Has cloud changes"; row.Kind = "cloud"; }
                    }
                    if (link != null && onCloud && onLocal) SetSynced(row, link.LastSync);
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
                    SetSynced(row, classified.LastSynced);
                }
                else if (onCloud && !onLocal)
                {
                    row.Kind = "cloud";
                    row.Status = "Only in cloud";
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

        FileList ActiveList()
        {
            if (localList.Focused) return localList;
            return list;
        }

        List<string> SelectedPaths(FileList view)
        {
            List<string> paths = new List<string>();
            foreach (ListViewItem item in view.SelectedItems)
            {
                Row row = item.Tag as Row;
                if (row != null) paths.Add(row.FullPath ?? row.NavigateKey ?? row.Name);
            }
            return paths;
        }

        List<Row> SelectedRows(FileList view)
        {
            List<Row> selected = new List<Row>();
            foreach (ListViewItem item in view.SelectedItems)
            {
                Row row = item.Tag as Row;
                if (row != null) selected.Add(row);
            }
            return selected;
        }

        void ShowRows()
        {
            ShowRows(SelectedPaths(list), SelectedPaths(localList));
        }

        void ShowRows(List<string> keepDrive, List<string> keepLocal)
        {
            string query = search.Query.Trim();
            hotList = null;
            hotIndex = -1;
            FillList(list, rows, keepDrive, query);
            FillList(localList, localRows, keepLocal, query);
            UpdateEmpty(empty, list, query, false);
            UpdateEmpty(localEmpty, localList, query, true);
            UpdateStatus();
        }

        void FillList(FileList view, List<Row> source, List<string> keep, string query)
        {
            Dictionary<string, bool> wanted = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (keep != null) foreach (string path in keep) if (path != null) wanted[path] = true;
            view.BeginUpdate();
            view.Items.Clear();
            ListViewItem focus = null;
            foreach (Row row in source)
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
                view.Items.Add(item);
                string path = row.FullPath ?? row.NavigateKey ?? row.Name;
                if (path != null && wanted.ContainsKey(path))
                {
                    item.Selected = true;
                    if (focus == null) focus = item;
                }
            }
            if (focus != null) focus.Focused = true;
            view.EndUpdate();
            FitColumns(view);
        }

        void UpdateEmpty(EmptyState blank, FileList view, string query, bool localSide)
        {
            bool show = !loading && view.Items.Count == 0;
            if (show)
            {
                if (!localSide && loadError != null) blank.Set(Glyphs.Warning, "This folder isn't available", loadError);
                else if (query.Length > 0) blank.Set(Glyphs.Search, "No items match \u201C" + query + "\u201D", "Check the spelling, or clear the search to see everything in this folder.");
                else if (!localSide && current == "vfs:linked") blank.Set(Glyphs.Link, "No attached folders yet", "Choose Attach folder to back up a folder from this PC. Attached folders back up when Windows shuts down and refresh when you sign in.");
                else if (!localSide && IsRealPath(driveKey) && !Directory.Exists(driveKey))
                    blank.Set(Glyphs.Warning, "Cloud folder isn't available", "Check the drive connection. If this folder was moved or trashed, restore it and refresh.");
                else if (localSide && !string.IsNullOrEmpty(localKey) && localKey != "vfs:replicas" && !Directory.Exists(localKey))
                    blank.Set(Glyphs.SaveLocal, "Nothing copied here yet", "Drag folders or files from the drive. They keep the same path.");
                else if (localSide && string.IsNullOrEmpty(localKey))
                    blank.Set(Glyphs.Link, "Attach a local folder", "Choose any folder on this PC. Its files stay where they are and back up to the selected drive.");
                else blank.Set(Glyphs.FolderOpen, "This folder is empty", localSide ? "Drag files or folders here from the drive." : "Drag files or folders here from the local folder.");
            }
            blank.Visible = show;
            PlaceEmpty(view, blank);
        }

        void PlaceEmpty(FileList view, EmptyState blank)
        {
            int width = Math.Min(blank.Width, Math.Max(Theme.S(180), view.ClientSize.Width - Theme.S(24)));
            blank.Width = width;
            int headerHeight = Theme.S(28);
            int y = Math.Max(headerHeight + Theme.S(16), (view.ClientSize.Height - blank.Height) / 2 - Theme.S(12));
            blank.Location = new Point(Math.Max(0, (view.ClientSize.Width - width) / 2), y);
        }

        void UpdateStatus()
        {
            FileList view = ActiveList();
            List<Row> source = view == localList ? localRows : rows;
            int total = source.Count;
            int shown = view.Items.Count;
            if (loading) countLabel.Text = "Loading\u2026";
            else if (shown != total) countLabel.Text = shown + " of " + total + " items";
            else countLabel.Text = total + (total == 1 ? " item" : " items");
            int selected = list.SelectedItems.Count + localList.SelectedItems.Count;
            if (selected == 0)
            {
                selectionLabel.Text = "";
                return;
            }
            long bytes = 0;
            bool files = false;
            foreach (FileList side in new FileList[] { list, localList })
            {
                foreach (ListViewItem item in side.SelectedItems)
                {
                    Row row = item.Tag as Row;
                    if (row != null && !row.IsDir && row.Bytes >= 0)
                    {
                        bytes += row.Bytes;
                        files = true;
                    }
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

        void FitColumns(FileList view)
        {
            if (sizingColumns || view.Columns.Count == 0) return;
            sizingColumns = true;
            try
            {
                int others = 0;
                for (int i = 1; i < view.Columns.Count; i++) others += view.Columns[i].Width;
                int width = Math.Max(Theme.S(140), view.ClientSize.Width - others);
                if (view.Columns[0].Width != width) view.Columns[0].Width = width;
            }
            finally { sizingColumns = false; }
        }

        void SetHot(FileList view, int index)
        {
            if (hotList == view && index == hotIndex) return;
            FileList oldList = hotList;
            int old = hotIndex;
            hotList = view;
            hotIndex = index;
            if (oldList != null && old >= 0 && old < oldList.Items.Count) oldList.Invalidate(oldList.Items[old].Bounds);
            if (view != null && index >= 0 && index < view.Items.Count) view.Invalidate(view.Items[index].Bounds);
        }

        void List_MouseMove(object sender, MouseEventArgs e)
        {
            FileList view = (FileList)sender;
            ListViewItem item = view.GetItemAt(e.X, e.Y);
            SetHot(view, item == null ? -1 : item.Index);
        }

        void List_DrawItem(object sender, DrawListViewItemEventArgs e)
        {
            FileList view = (FileList)sender;
            Color back = Theme.Window;
            if (e.Item.Selected) back = view.Focused ? Theme.RowSelected : Theme.RowSelectedIdle;
            else if (hotList == view && e.ItemIndex == hotIndex) back = Theme.RowHover;
            Rectangle row = new Rectangle(e.Bounds.X, e.Bounds.Y, Math.Max(e.Bounds.Width, view.ClientSize.Width - e.Bounds.X), e.Bounds.Height);
            using (SolidBrush brush = new SolidBrush(back)) e.Graphics.FillRectangle(brush, row);
        }

        void List_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            FileList view = (FileList)sender;
            Row row = e.Item.Tag as Row;
            if (row == null) return;
            Graphics g = e.Graphics;
            Rectangle cell = e.Bounds;
            if (e.ColumnIndex == 0) cell.Width = view.Columns[0].Width;
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
            localList.SetSortArrow(sortColumn, sortAsc);
            ShowRows();
        }

        static int CompareRows(Row a, Row b, int column, bool ascending)
        {
            if (a.IsDir != b.IsDir) return a.IsDir ? -1 : 1;
            int result = 0;
            if (column == 1) result = string.Compare(a.Status ?? "", b.Status ?? "", StringComparison.OrdinalIgnoreCase);
            else if (column == 2) result = a.Bytes.CompareTo(b.Bytes);
            else if (column == 3) result = a.ModifiedAt.CompareTo(b.ModifiedAt);
            else if (column == 4) result = a.SyncedAt.CompareTo(b.SyncedAt);
            else result = NaturalCompare(a.Name, b.Name);
            if (!ascending) result = -result;
            if (result == 0 && column != 0) result = NaturalCompare(a.Name, b.Name);
            return result;
        }

        void SortRows()
        {
            rows.Sort(delegate(Row a, Row b) { return CompareRows(a, b, sortColumn, sortAsc); });
            localRows.Sort(delegate(Row a, Row b) { return CompareRows(a, b, sortColumn, sortAsc); });
        }

        void List_ItemActivate(object sender, EventArgs e)
        {
            FileList view = sender as FileList;
            if (view == null) view = ActiveList();
            if (view.SelectedItems.Count == 0) return;
            Row row = view.SelectedItems[0].Tag as Row;
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
            List<LinkFolder> saved = Store.ReadLinks();
            foreach (LinkFolder folder in saved) if (folder.Id == activeLinkId) return folder;
            return SyncEngine.FindLink(path, saved);
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

        void List_ItemDrag(object sender, ItemDragEventArgs e)
        {
            FileList view = (FileList)sender;
            List<string> paths = new List<string>();
            foreach (ListViewItem item in view.SelectedItems)
            {
                Row row = item.Tag as Row;
                if (row == null || !IsRealPath(row.FullPath)) continue;
                if (!File.Exists(row.FullPath) && !Directory.Exists(row.FullPath)) continue;
                paths.Add(row.FullPath);
            }
            if (paths.Count == 0) return;
            view.DoDragDrop(new DataObject(DataFormats.FileDrop, paths.ToArray()), DragDropEffects.Copy);
        }

        void List_DragEnter(object sender, DragEventArgs e)
        {
            e.Effect = !busy && !string.IsNullOrEmpty(activeLinkId) && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        void List_DragOver(object sender, DragEventArgs e)
        {
            e.Effect = !busy && !string.IsNullOrEmpty(activeLinkId) && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        void List_DragDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] sources = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (sources == null || sources.Length == 0) return;
            FileList target = (FileList)sender;
            string dest = DropFolder(target, e);
            if (string.IsNullOrEmpty(dest))
            {
                ShowAction("Drop onto a folder inside the selected folder pair.", "warn");
                return;
            }
            StartTransfer(target == localList, dest, sources);
        }

        string DropFolder(FileList target, DragEventArgs e)
        {
            Point point = target.PointToClient(new Point(e.X, e.Y));
            ListViewItem item = target.GetItemAt(point.X, point.Y);
            if (item != null)
            {
                Row row = item.Tag as Row;
                if (row != null && row.IsDir && IsRealPath(row.FullPath)) return row.FullPath;
            }
            string folder = target == localList ? localKey : driveKey;
            if (!IsRealPath(folder)) return null;
            return folder;
        }

        void StartTransfer(bool toLocal, string destFolder, string[] sources)
        {
            if (busy) return;
            if (!StartPairTransfer(toLocal, destFolder, sources))
                ShowAction("Select an attached folder before copying between panes.", "warn");
        }

        void ListMenu_Opening(object sender, CancelEventArgs e)
        {
            listMenu.Items.Clear();
            FileList sourceList = listMenu.SourceControl as FileList;
            if (sourceList == null) sourceList = ActiveList();
            List<Row> selected = SelectedRows(sourceList);
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
                    if (selected.Count == 1 && first.IsDir) listMenu.Items.Add(MenuItem("Attach folder…", Glyphs.Link, new EventHandler(Link_Click)));
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
                    listMenu.Items.Add(MenuItem("Attach this folder…", Glyphs.Link, new EventHandler(Link_Click)));
                }
            }
            string syncPath = sourceList == localList ? localKey : driveKey;
            if (selected.Count == 1 && selected[0].IsDir) syncPath = selected[0].FullPath;
            AddSyncItems(listMenu, syncPath);
            if (listMenu.Items.Count > 0 && listMenu.Items[listMenu.Items.Count - 1] is ToolStripSeparator)
                listMenu.Items.RemoveAt(listMenu.Items.Count - 1);
            e.Cancel = listMenu.Items.Count == 0;
        }

        void TreeMenu_Opening(object sender, CancelEventArgs e)
        {
            treeMenu.Items.Clear();
            TreeView sourceTree = treeMenu.SourceControl as TreeView;
            NodeTag selected = sourceTree == null || sourceTree.SelectedNode == null ? null : sourceTree.SelectedNode.Tag as NodeTag;
            string path = selected == null ? current : selected.Key;
            treeMenu.Items.Add(MenuItem("Refresh", Glyphs.Refresh, delegate { RefreshAll(); }));
            if (IsRealPath(path) && Directory.Exists(path))
            {
                treeMenu.Items.Add(MenuItem("Open in File Explorer", Glyphs.OpenLocal, delegate { ShowInExplorer(path); }));
                treeMenu.Items.Add(MenuItem("Copy path", Glyphs.Copy, delegate { CopyPath(path); }));
                treeMenu.Items.Add(new ToolStripSeparator());
                treeMenu.Items.Add(MenuItem("Attach this folder…", Glyphs.Link, new EventHandler(Link_Click)));
            }
            AddSyncItems(treeMenu, path);
        }

        void AddSyncItems(ContextMenuStrip menu, string path)
        {
            if (!IsRealPath(path)) return;
            LinkFolder folder = SyncEngine.FindLink(path, Store.ReadLinks());
            if (folder == null) return;
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem pull = MenuItem("Refresh from cloud", Glyphs.Download, delegate { RunSyncId("Pull", folder.Id); });
            ToolStripMenuItem push = MenuItem("Back up to cloud", Glyphs.Upload, delegate { RunSyncId("Push", folder.Id); });
            pull.Enabled = !busy;
            push.Enabled = !busy;
            menu.Items.Add(pull);
            menu.Items.Add(push);
            ToolStripMenuItem remove = MenuItem("Remove synced folder", Glyphs.Cancel, delegate { RemoveSyncedFolder(folder); });
            remove.Enabled = !busy;
            remove.ToolTipText = "Stop syncing " + folder.Local + ". Local and cloud files are kept.";
            menu.Items.Add(remove);
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
            FileList sourceList = ActiveList();
            if (sourceList.SelectedItems.Count == 0)
            {
                Dialogs.Info(this, "Select what to keep on this PC", "Select the folders and files to keep locally, then choose Local replica. The open folder is not copied on its own.");
                return;
            }
            List<Provider> found = Store.DetectProviders();
            Dictionary<string, ReplicaChoice> choices = new Dictionary<string, ReplicaChoice>(StringComparer.OrdinalIgnoreCase);
            foreach (ListViewItem item in sourceList.SelectedItems)
            {
                Row row = item.Tag as Row;
                if (row == null) continue;
                string path = row.FullPath;
                if (string.IsNullOrEmpty(path)) path = row.NavigateKey;
                if (string.IsNullOrEmpty(path) || path.StartsWith("vfs:", StringComparison.OrdinalIgnoreCase)) continue;
                Store.Place place = Store.FindPlace(path, found);
                if (place == null || place.Provider == null || string.IsNullOrEmpty(place.Provider.Root)) continue;
                if (place.Link != null && !place.Link.Partial)
                {
                    Dialogs.Info(this, "This folder is already attached", "Use Refresh from cloud to copy into its attached local folder.");
                    return;
                }
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
                                throw new InvalidOperationException("This replica is already attached as a whole folder. Remove that attachment before choosing individual files.");
                        }
                        SyncEngine.MaterializeSelection(cloudRoot, localRoot, choice.Dirs, choice.Files);
                        LinkFolder saved = SyncEngine.RememberSelection(choice.Provider.Id, localRoot, choice.Dirs, choice.Files);
                        if (saved == null || !saved.Partial)
                            throw new InvalidOperationException("This replica is already attached as a whole folder. Remove that attachment before choosing individual files.");
                        copied += choice.Files.Count;
                        made += choice.Dirs.Count;
                        if (choice.RootSelected) made++;
                        if (openPath == null) openPath = localRoot;
                    }
                }
                catch (Exception ex) { error = ex.Message; }
                int copiedFiles = copied;
                int madeFolders = made;
                try
                {
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
                }
                catch (InvalidOperationException) { }
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
                Dialogs.Info(this, "Choose a folder to attach", "Open or select a folder, then choose Attach folder.");
                return;
            }
            OfferLink(path);
        }

        string SelectedDirectory()
        {
            FileList sourceList = ActiveList();
            if (sourceList.SelectedItems.Count == 1)
            {
                Row row = sourceList.SelectedItems[0].Tag as Row;
                if (row != null && row.IsDir && !string.IsNullOrEmpty(row.FullPath) && Directory.Exists(row.FullPath))
                    return row.FullPath;
            }
            if (current != "vfs:pc" && current != "vfs:linked" && Directory.Exists(current)) return current;
            return null;
        }

        void OfferLink(string path)
        {
            if (busy) return;
            List<Provider> found = Store.DetectProviders();
            Provider chosen = Store.FindProvider(found, activeProviderId);
            if (chosen == null)
                foreach (Provider provider in found) if (provider.IsOnline) { chosen = provider; break; }
            string local = path;
            string relative = new DirectoryInfo(path).Name;
            foreach (Provider provider in found)
            {
                if (!Store.IsUnder(path, provider.Root)) continue;
                chosen = provider;
                relative = Store.Relative(path, provider.Root) ?? "";
                local = Store.ReplicaPath(provider, relative);
                break;
            }
            using (LinkDialog dialog = new LinkDialog(found, chosen, local, relative))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                AttachAndBackup(dialog.LocalPath, dialog.Chosen.Id, dialog.RelativePath);
            }
        }

        void Unlink_Click(object sender, EventArgs e)
        {
            RemoveSyncedFolder(CurrentLink());
        }

        void RemoveSyncedFolder(LinkFolder folder)
        {
            if (busy) return;
            if (folder == null)
            {
                Dialogs.Info(this, "This folder isn't attached", "Open an attached folder or select it in the folder picker, then try again.");
                return;
            }
            string remote = Store.ResolveRemote(folder, Store.DetectProviders());
            string details = "Local: " + folder.Local + "\r\nCloud: " + (remote ?? folder.RemoteRelative);
            if (!Dialogs.Confirm(this, "Remove this synced folder?", details + "\r\n\r\nThis removes the entire folder attachment. Local and cloud files are kept. Automatic backup and sign-in refresh stop for this attachment.", null, "Remove from sync", MessageKind.Warning))
                return;
            try { SyncEngine.Unlink(folder.Id); }
            catch (Exception ex) { Dialogs.Error(this, "Couldn't remove synced folder", ex.Message); return; }
            BuildTree();
            LoadCurrent();
            ShowAction("Stopped syncing " + Path.GetFileName(folder.Local.TrimEnd('\\')), "same");
        }

        void AddDrive_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose the folder used by your cloud desktop app, such as Google Drive\\My Drive or OneDrive. To back up a local folder, use Attach folder.";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string root = dialog.SelectedPath;
                string problem = null;
                if (string.Equals(Store.Norm(root), Store.Norm(Path.GetPathRoot(Path.GetFullPath(root))), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetPathRoot(Path.GetFullPath(root)), Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase))
                    problem = "The Windows drive itself can't be a cloud drive. Choose the folder your cloud desktop app syncs.";
                foreach (Provider existing in Store.DetectProviders())
                {
                    if (problem != null) break;
                    if (Store.IsUnder(root, existing.Root) || Store.IsUnder(existing.Root, root))
                        problem = "This folder overlaps " + existing.Name + " (" + existing.Root + "), which is already a cloud drive.";
                }
                foreach (LinkFolder folder in Store.ReadLinks())
                {
                    if (problem != null) break;
                    if (Store.IsUnder(root, folder.Local) || Store.IsUnder(folder.Local, root))
                        problem = "This folder overlaps the attached local folder " + folder.Local + ". To back up a local folder, use Attach folder instead.";
                }
                if (problem != null)
                {
                    Dialogs.Error(this, "Couldn't add this cloud drive", problem);
                    return;
                }
                Provider provider = new Provider();
                provider.Root = Store.Norm(root);
                provider.Name = new DirectoryInfo(root).Name;
                provider.Kind = "Folder";
                if (provider.Name.IndexOf("dropbox", StringComparison.OrdinalIgnoreCase) >= 0) provider.Kind = "Dropbox";
                if (provider.Name.IndexOf("onedrive", StringComparison.OrdinalIgnoreCase) >= 0) provider.Kind = "OneDrive";
                provider.Detail = provider.Root;
                provider.Id = provider.Kind.ToLowerInvariant() + ":" + provider.Root.ToLowerInvariant();
                try { using (SyncEngine.LockState(30)) Store.SaveCustomProvider(provider); }
                catch (Exception ex)
                {
                    Dialogs.Error(this, "Couldn't add this cloud drive", ex.Message);
                    return;
                }
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

        void ViewLog_Click(object sender, EventArgs e)
        {
            try
            {
                if (!File.Exists(Store.LogFile)) Store.Log("log opened");
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + Store.LogFile + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Dialogs.Error(this, "Couldn't open the sync log", ex.Message); }
        }

        void About_Click(object sender, EventArgs e)
        {
            Dialogs.Info(this, "Drive Folder Sync " + Store.Version,
                "Choose a cloud drive tab, then attach a local folder from any drive. The cloud folder is on the left and the attached local folder is on the right. Drag files or folders between them to copy them to the same path.\r\n\r\n"
                + "Attached folders refresh from the cloud when you sign in and back up when Windows shuts down. Deleting a local file keeps its cloud copy. "
                + "Deleting files in the cloud never deletes them on this PC.\r\n\r\n"
                + "Green status: changed on this PC since the last sync.\r\nRed status: changed in the cloud, or changed on both sides.");
        }

        void SetBusy(bool on, string message)
        {
            busy = on;
            if (!on && closeWhenIdle)
            {
                BeginInvoke(new Action(Close));
                return;
            }
            progress.Visible = on;
            Cursor = on ? Cursors.AppStarting : Cursors.Default;
            if (on) ShowAction(message, "none");
            foreach (ToolStripItem item in new ToolStripItem[] { refreshButton, uploadButton, replicaButton, linkButton, syncAllButton })
                item.Enabled = !on;
            attachButton.Enabled = !on;
            pairPicker.Enabled = !on;
            UpdatePairActions();
        }

        void RunSync(string direction, bool one)
        {
            string id = null;
            if (one)
            {
                string path = SelectedDirectory();
                if (string.IsNullOrEmpty(path)) path = current;
                LinkFolder folder = CurrentLink();
                if (folder == null)
                {
                    Dialogs.Info(this, "Attach this folder first", "Only attached folders refresh from or back up to the cloud. Choose Attach folder to pair a local folder with a cloud folder.");
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
                            if (folder == null) throw new InvalidOperationException("That attached folder was removed.");
                            stats = SyncEngine.SyncLink(folder, direction, false);
                            Store.SaveLinks(all);
                        }
                    }
                }
                catch (Exception ex) { error = ex.Message; }
                try
                {
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
                }
                catch (InvalidOperationException) { }
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
            Dictionary<string, object> saved;
            try { saved = Store.ReadObject(WindowStatePath); }
            catch (IOException) { return; }
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
        readonly ToolTip tips = new ToolTip();
        readonly Panel error = new Panel();
        readonly List<Provider> providers;

        public Provider Chosen;
        public string LocalPath;
        public string RelativePath;

        public LinkDialog(List<Provider> providers, Provider selected, string local, string relative)
        {
            Theme.Init();
            this.providers = providers;
            Text = "Attach folder";
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

            Label title = AddLabel(this, "Attach a local folder", Theme.Heading, Theme.Text, pad, y, field);
            y += title.Height + Theme.S(6);
            Label help = AddLabel(this, "Your folder stays where it is. Attach it to an existing or new cloud folder and start a backup. Changes back up at shutdown and refresh at sign-in. Cloud copies are kept if you delete local files.",
                Theme.Body, Theme.TextMuted, pad, y, field);
            y += help.Height + Theme.S(20);

            y = FieldLabel("Cloud drive", pad, y);
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
            if (selected != null)
                foreach (Provider candidate in drives.Items)
                    if (candidate.Id == selected.Id) { drives.SelectedItem = candidate; break; }
            drives.SelectedIndexChanged += new EventHandler(delegate { UpdateCloudHint(); HideError(); });
            Controls.Add(drives);
            y += drives.Height + Theme.S(18);

            FlatButton browseWidth = new FlatButton("Browse\u2026", false);
            int boxWidth = field - browseWidth.Width - Theme.S(8);

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
            Label localHint = AddLabel(this, "Any folder on this PC, including another drive. Files stay in this location.", Theme.Small, Theme.TextMuted, pad, y, field);
            y += localHint.Height + Theme.S(12);

            y = FieldLabel("Cloud folder", pad, y);
            cloudBox.SetBounds(pad, y, boxWidth, Theme.S(32));
            cloudBox.Box.Text = relative ?? "";
            cloudBox.Box.TextChanged += new EventHandler(delegate { HideError(); UpdateCloudHint(); });
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

            error.SetBounds(pad, y, field, Theme.S(24));
            error.Visible = false;
            error.Paint += new PaintEventHandler(delegate(object sender, PaintEventArgs e)
            {
                int size = Theme.S(14);
                Theme.DrawGlyph(e.Graphics, Glyphs.Warning, size, Theme.CloudText, 0, (error.Height - size) / 2);
                TextRenderer.DrawText(e.Graphics, error.Text, Theme.Body, new Rectangle(size + Theme.S(8), 0, error.Width - size - Theme.S(8), error.Height), Theme.CloudText, TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
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
            FlatButton ok = new FlatButton("Attach & back up", true);
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
            Disposed += delegate { tips.Dispose(); };
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
            if (provider == null)
            {
                cloudHint.Text = "Choose a drive first.";
                return;
            }
            string relative = cloudBox.Box.Text.Trim().Trim('\\');
            string target = provider.Root;
            try { target = Path.Combine(provider.Root, relative); }
            catch (ArgumentException) { }
            string shown = provider.Name + (relative.Length > 0 ? "\\" + relative : "");
            cloudHint.Text = Directory.Exists(target)
                ? "Uses the existing folder " + shown + ". Files that differ are kept on both sides for review."
                : "Creates the folder " + shown + " for this backup.";
            tips.SetToolTip(cloudHint, cloudHint.Text);
        }

        void ShowError(string message)
        {
            error.Text = message;
            int textWidth = error.Width - Theme.S(22);
            int height = Math.Max(Theme.S(24), TextRenderer.MeasureText(message, Theme.Body, new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + Theme.S(4));
            ClientSize = new Size(ClientSize.Width, ClientSize.Height + height - error.Height);
            error.Height = height;
            error.Visible = true;
            error.Invalidate();
            System.Media.SystemSounds.Beep.Play();
        }

        void HideError()
        {
            error.Visible = false;
            ClientSize = new Size(ClientSize.Width, ClientSize.Height + Theme.S(24) - error.Height);
            error.Height = Theme.S(24);
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
            RelativePath = cloudBox.Box.Text.Trim();
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
            try
            {
                string cloud = SyncEngine.ValidateLink(LocalPath, Chosen.Id, RelativePath, Store.ReadLinks());
                LocalPath = Path.GetFullPath(LocalPath);
                RelativePath = Store.Relative(cloud, Chosen.Root) ?? "";
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
