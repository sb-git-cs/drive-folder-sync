using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DriveFolderSync
{
    public class HostForm : Form
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string reason);
        [DllImport("user32.dll")]
        static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);

        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly Queue<string> watchQueue = new Queue<string>();
        readonly Dictionary<string, DateTime> pending = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        readonly object gate = new object();
        readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        volatile bool pullDone;
        volatile bool checkingUpdate;
        volatile bool inTick;
        volatile bool shuttingDown;
        DateTime pullDeadline;
        DateTime nextPullTry;
        readonly HashSet<string> refreshed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Mutex hostMutex;
        bool mutexOwned;

        public static void Run()
        {
            bool owned = false;
            Mutex mutex = new Mutex(false, @"Local\DriveFolderSyncHost");
            try { owned = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned)
            {
                Store.Log("host already running");
                return;
            }
            HostForm form = new HostForm();
            form.hostMutex = mutex;
            form.mutexOwned = true;
            Application.Run(form);
        }

        public HostForm()
        {
            Text = "Drive Folder Sync";
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.Manual;
            Location = new System.Drawing.Point(-32000, -32000);
            Size = new System.Drawing.Size(1, 1);
            pullDeadline = DateTime.Now.AddMinutes(8);
            File.WriteAllText(Store.PidFile, System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
            Store.Log("host starting");

            Theme.Init();
            tray.Icon = Theme.AppIconSized(SystemInformation.SmallIconSize.Width);
            tray.Text = "Drive Folder Sync";
            tray.Visible = true;
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripItem open = menu.Items.Add("Open Drive Folder Sync", Theme.Glyph(Glyphs.FolderOpen, Theme.S(16), Theme.Text), new EventHandler(Open_Click));
            open.Font = Theme.Strong;
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Refresh all from cloud", Theme.Glyph(Glyphs.Download, Theme.S(16), Theme.Text), new EventHandler(Pull_Click));
            menu.Items.Add("Upload all to cloud", Theme.Glyph(Glyphs.Upload, Theme.S(16), Theme.Text), new EventHandler(Push_Click));
            foreach (ToolStripItem item in menu.Items)
            {
                item.ImageScaling = ToolStripItemImageScaling.None;
                if (item is ToolStripMenuItem) item.Padding = new Padding(0, Theme.S(4), 0, Theme.S(4));
            }
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += new EventHandler(Open_Click);

            foreach (string root in Store.WatchRoots())
            {
                if (!Directory.Exists(root)) continue;
                FileSystemWatcher watcher = new FileSystemWatcher();
                watcher.Path = root;
                watcher.IncludeSubdirectories = true;
                watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
                watcher.InternalBufferSize = 65536;
                watcher.Created += new FileSystemEventHandler(Watcher_Created);
                watcher.Renamed += new RenamedEventHandler(Watcher_Renamed);
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }

            timer.Interval = 2000;
            timer.Tick += new EventHandler(Timer_Tick);
            timer.Start();
            Store.Log("host ready");
            if (Store.IsInstalledCopy() && UpdateManager.AutomaticEnabled)
            {
                checkingUpdate = true;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { if (UpdateManager.CheckAndInstall(Store.Log, delegate { return !shuttingDown && UpdateManager.AutomaticEnabled; })) shuttingDown = true; }
                    finally { checkingUpdate = false; }
                });
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0016 && m.WParam == IntPtr.Zero)
            {
                shuttingDown = false;
                Store.Log("shutdown canceled; automatic refresh remains active");
            }
            if (m.Msg == 0x0011 || (m.Msg == 0x0016 && m.WParam != IntPtr.Zero))
            {
                OnShutdown();
                if (m.Msg == 0x0011)
                {
                    m.Result = (IntPtr)1;
                    return;
                }
            }
            base.WndProc(ref m);
        }

        void OnShutdown()
        {
            if (shuttingDown) return;
            shuttingDown = true;
            Store.Log("shutdown sync starting");
            ShutdownBlockReasonCreate(Handle, "Backing up attached folders to the cloud");
            try
            {
                DateTime until = DateTime.UtcNow.AddSeconds(20);
                while (inTick && DateTime.UtcNow < until) Thread.Sleep(200);
                SyncEngine.SyncAll("Push");
                Thread.Sleep(5000);
                Store.Log("shutdown sync finished");
            }
            catch (Exception ex)
            {
                Store.Log("shutdown sync error: " + ex.Message);
            }
            finally
            {
                ShutdownBlockReasonDestroy(Handle);
            }
        }

        void Timer_Tick(object sender, EventArgs e)
        {
            if (inTick || shuttingDown || checkingUpdate) return;
            inTick = true;
            ThreadPool.QueueUserWorkItem(new WaitCallback(Tick));
        }

        void Tick(object state)
        {
            try
            {
                if (!pullDone && !shuttingDown && DateTime.Now >= nextPullTry)
                {
                    nextPullTry = DateTime.Now.AddSeconds(15);
                    using (SyncEngine.LockState(120))
                    {
                        List<LinkFolder> links = Store.ReadLinks();
                        List<Provider> providers = Store.DetectProviders();
                        foreach (LinkFolder folder in links)
                        {
                            if (shuttingDown || refreshed.Contains(folder.Id)) continue;
                            Provider provider = Store.FindProvider(providers, folder.ProviderId);
                            if (provider == null || !provider.IsOnline) continue;
                            try
                            {
                                SyncStats stats = SyncEngine.SyncLink(folder, "Pull", false);
                                if (stats.Errors == 0) refreshed.Add(folder.Id);
                            }
                            catch (Exception ex)
                            {
                                folder.LastErrors = 1;
                                Store.Log("boot refresh deferred for " + folder.Local + ": " + ex.Message);
                            }
                        }
                        Store.SaveLinks(links);
                        pullDone = true;
                        foreach (LinkFolder folder in links) if (!refreshed.Contains(folder.Id)) pullDone = false;
                    }
                    if (pullDone) Store.Log("boot refresh finished");
                    else if (DateTime.Now > pullDeadline)
                    {
                        pullDone = true;
                        Store.Log("boot refresh finished for available folders; unavailable folders will retry next sign-in");
                    }
                }
                DrainWatch();
            }
            catch (Exception ex)
            {
                Store.Log("host tick error: " + ex.Message);
            }
            finally
            {
                inTick = false;
            }
        }

        void Watcher_Created(object sender, FileSystemEventArgs e) { Enqueue(e.FullPath); }
        void Watcher_Renamed(object sender, RenamedEventArgs e) { Enqueue(e.FullPath); }

        void Enqueue(string path)
        {
            lock (gate) { watchQueue.Enqueue(path); }
        }

        void DrainWatch()
        {
            while (true)
            {
                string next = null;
                lock (gate)
                {
                    if (watchQueue.Count == 0) break;
                    next = watchQueue.Dequeue();
                }
                Note(next);
            }
            List<string> ready = new List<string>();
            DateTime now = DateTime.Now;
            foreach (string key in new List<string>(pending.Keys))
            {
                if ((now - pending[key]).TotalSeconds >= 20) ready.Add(key);
            }
            ready.Sort(delegate(string a, string b) { return a.Length.CompareTo(b.Length); });
            foreach (string dir in ready)
            {
                bool covered = false;
                foreach (string other in ready)
                {
                    if (!string.Equals(other, dir, StringComparison.OrdinalIgnoreCase)
                        && dir.StartsWith(other.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                        covered = true;
                }
                if (covered) continue;
                pending.Remove(dir);
                TryAutoLink(dir);
            }
        }

        void Note(string full)
        {
            if (string.IsNullOrEmpty(full) || Store.Excluded(full)) return;
            string root = null;
            foreach (string watch in Store.WatchRoots())
            {
                if (Store.IsUnder(full, watch) && !string.Equals(Store.Norm(full), Store.Norm(watch), StringComparison.OrdinalIgnoreCase))
                {
                    root = watch;
                    break;
                }
            }
            if (root == null) return;
            string dir = full;
            if (!Directory.Exists(full)) dir = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(dir)) return;
            for (int i = 0; i < 8; i++)
            {
                string parent = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(parent) || string.Equals(Store.Norm(parent), Store.Norm(root), StringComparison.OrdinalIgnoreCase)) break;
                DirectoryInfo info = new DirectoryInfo(parent);
                if (!info.Exists) break;
                if (info.CreationTime > DateTime.Now.AddMinutes(-15)) dir = parent;
                else break;
            }
            if (string.Equals(Store.Norm(dir), Store.Norm(root), StringComparison.OrdinalIgnoreCase)) return;
            if (Store.Excluded(dir)) return;
            List<LinkFolder> links = Store.ReadLinks();
            if (SyncEngine.FindLink(dir, links) != null) return;
            pending[dir] = DateTime.Now;
        }

        void TryAutoLink(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                if (SyncEngine.FindLink(dir, Store.ReadLinks()) != null) return;
                Provider match = null;
                string relative = null;
                int matches = 0;
                foreach (Provider provider in Store.DetectProviders())
                {
                    string rel = SyncEngine.FindRelative(dir, provider);
                    if (string.IsNullOrEmpty(rel)) continue;
                    matches++;
                    match = provider;
                    relative = rel;
                }
                if (matches != 1) return;
                LinkFolder linked = SyncEngine.Link(dir, match.Id, relative);
                Store.Log("auto-linked " + dir + " <-> " + relative);
                try { tray.ShowBalloonTip(4000, "Drive Folder Sync", "Will keep " + dir + " in sync. It uploads on shutdown and refreshes when you sign in.", ToolTipIcon.Info); }
                catch { }
                if (pullDone && linked != null)
                {
                    using (SyncEngine.LockState(120))
                    {
                        List<LinkFolder> links = Store.ReadLinks();
                        foreach (LinkFolder folder in links)
                        {
                            if (string.Equals(folder.Id, linked.Id, StringComparison.OrdinalIgnoreCase))
                                SyncEngine.SyncLink(folder, "Pull", false);
                        }
                        Store.SaveLinks(links);
                    }
                }
            }
            catch (Exception ex)
            {
                Store.Log("watch link failed for " + dir + ": " + ex.Message);
            }
        }

        void Open_Click(object sender, EventArgs e)
        {
            try { System.Diagnostics.Process.Start(Application.ExecutablePath); }
            catch (Exception ex) { Store.Log("open failed: " + ex.Message); }
        }

        void Pull_Click(object sender, EventArgs e) { ThreadPool.QueueUserWorkItem(delegate { try { SyncEngine.SyncAll("Pull"); } catch (Exception ex) { Store.Log(ex.Message); } }); }
        void Push_Click(object sender, EventArgs e) { ThreadPool.QueueUserWorkItem(delegate { try { SyncEngine.SyncAll("Push"); } catch (Exception ex) { Store.Log(ex.Message); } }); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            timer.Stop();
            foreach (FileSystemWatcher watcher in watchers)
            {
                try { watcher.EnableRaisingEvents = false; watcher.Dispose(); }
                catch { }
            }
            tray.Visible = false;
            tray.Dispose();
            try { if (File.Exists(Store.PidFile)) File.Delete(Store.PidFile); }
            catch { }
            if (mutexOwned)
            {
                try { hostMutex.ReleaseMutex(); }
                catch { }
            }
            Store.Log("host stopped");
            base.OnFormClosing(e);
        }
    }
}
