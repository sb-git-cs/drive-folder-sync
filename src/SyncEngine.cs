using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Runtime.InteropServices;

namespace DriveFolderSync
{
    public class SyncEngine
    {
        public const long FreeReserve = 10L * 1024 * 1024 * 1024;
        static readonly Regex ConflictName = new Regex(@"\.(drive|local)-conflict-\d{8}-\d{6}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Mutex StateMutex = new Mutex(false, @"Local\DriveFolderSyncState");

        public static IDisposable LockState(int seconds)
        {
            bool owned = false;
            try { owned = StateMutex.WaitOne(TimeSpan.FromSeconds(seconds)); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new TimeoutException("Another sync is still using the folder list.");
            return new MutexReleaser(StateMutex);
        }

        sealed class MutexReleaser : IDisposable
        {
            readonly Mutex mutex;
            public MutexReleaser(Mutex mutex) { this.mutex = mutex; }
            public void Dispose()
            {
                try { mutex.ReleaseMutex(); }
                catch { }
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct FindData
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation;
            public System.Runtime.InteropServices.ComTypes.FILETIME Access;
            public System.Runtime.InteropServices.ComTypes.FILETIME Write;
            public uint SizeHigh;
            public uint SizeLow;
            public uint Tag;
            public uint Reserved;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string Alternate;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFile(string path, out FindData data);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool FindClose(IntPtr handle);

        public static bool IsRedirect(string path, FileAttributes attributes)
        {
            if ((attributes & FileAttributes.ReparsePoint) == 0) return false;
            FindData data;
            IntPtr handle = FindFirstFile(LongPath(path), out data);
            if (handle == new IntPtr(-1)) throw new IOException("Could not inspect reparse point: " + path);
            try
            {
                // Windows' name-surrogate bit distinguishes junctions/symlinks from cloud placeholders.
                return (data.Tag & 0x20000000) != 0;
            }
            finally { FindClose(handle); }
        }

        public static bool SkipName(string name, bool isDir)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (isDir)
            {
                if (name.StartsWith(".", StringComparison.Ordinal)) return true;
                if (EqualsAny(name, "node_modules", ".git", "proposal_chrome", "$RECYCLE.BIN", ".Encrypted", ".shortcut-targets-by-id"))
                    return true;
                return false;
            }
            if (EqualsAny(name, "desktop.ini", "thumbs.db", "ehthumbs.db")) return true;
            if (name.StartsWith("~$", StringComparison.Ordinal)) return true;
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return true;
            if (ConflictName.IsMatch(name)) return true;
            return false;
        }

        static bool EqualsAny(string name, params string[] options)
        {
            foreach (string option in options)
            {
                if (string.Equals(name, option, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static string LongPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path.Substring(2);
            if (path.Length >= 240) return @"\\?\" + Path.GetFullPath(path);
            return path;
        }

        public static List<FileRecord> ListFiles(string root)
        {
            return ListFiles(root, null);
        }

        // Symbolic links and junctions are never followed. Their relative paths are added to
        // links so the caller can keep both sides of the pair away from them.
        public static List<FileRecord> ListFiles(string root, List<string> links)
        {
            List<FileRecord> list = new List<FileRecord>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return list;
            string rootFull = Store.Norm(root);
            Stack<string> stack = new Stack<string>();
            stack.Push(rootFull);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                FileSystemInfo[] children;
                try { children = new DirectoryInfo(LongPath(dir)).GetFileSystemInfos(); }
                catch (Exception ex) { throw new IOException("Could not scan " + dir + ". Sync stopped to protect files.", ex); }
                foreach (FileSystemInfo child in children)
                {
                    bool isDir = (child.Attributes & FileAttributes.Directory) != 0;
                    if (SkipName(child.Name, isDir)) continue;
                    if (IsRedirect(child.FullName, child.Attributes))
                    {
                        string linkRel = RelativeTo(child.FullName, rootFull);
                        if (links != null && linkRel != null) links.Add(linkRel);
                        continue;
                    }
                    if (isDir)
                    {
                        stack.Push(child.FullName);
                        continue;
                    }
                    FileInfo info = child as FileInfo;
                    if (info == null) continue;
                    string rel = RelativeTo(info.FullName, rootFull);
                    if (rel == null) continue;
                    FileRecord record = new FileRecord();
                    record.Rel = rel;
                    record.Full = info.FullName;
                    record.Length = info.Length;
                    record.MtimeUtc = info.LastWriteTimeUtc;
                    list.Add(record);
                }
            }
            return list;
        }

        static string RelativeTo(string full, string rootFull)
        {
            string rel = full;
            if (rel.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) rel = @"\\" + rel.Substring(8);
            else if (rel.StartsWith(@"\\?\", StringComparison.Ordinal)) rel = rel.Substring(4);
            if (rel.Length < rootFull.Length) return null;
            return rel.Substring(rootFull.Length).TrimStart('\\');
        }

        static bool UnderAny(string rel, Dictionary<string, bool> roots)
        {
            if (roots == null || roots.Count == 0) return false;
            string current = rel;
            while (!string.IsNullOrEmpty(current))
            {
                if (roots.ContainsKey(current)) return true;
                int slash = current.LastIndexOf('\\');
                if (slash < 0) return false;
                current = current.Substring(0, slash);
            }
            return false;
        }

        static void DropUnder(Dictionary<string, FileRecord> files, Dictionary<string, bool> roots)
        {
            if (roots.Count == 0) return;
            List<string> drop = new List<string>();
            foreach (string rel in files.Keys) if (UnderAny(rel, roots)) drop.Add(rel);
            foreach (string rel in drop) files.Remove(rel);
        }

        public static Dictionary<string, FileRecord> Index(List<FileRecord> files)
        {
            Dictionary<string, FileRecord> map = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (FileRecord file in files) map[file.Rel] = file;
            return map;
        }

        public static string Stamp(DateTime time)
        {
            return time.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        }

        public static bool SameSig(long length, DateTime mtime, string snapLength, string snapMtime)
        {
            if (string.IsNullOrEmpty(snapLength) || string.IsNullOrEmpty(snapMtime)) return false;
            long otherLength;
            if (!long.TryParse(snapLength, NumberStyles.Integer, CultureInfo.InvariantCulture, out otherLength)) return false;
            if (length != otherLength) return false;
            DateTime other;
            if (!DateTime.TryParse(snapMtime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out other)) return false;
            double diff = Math.Abs((mtime.ToUniversalTime() - other.ToUniversalTime()).TotalSeconds);
            return diff == 0;
        }

        public static Snap NewEntry(FileRecord local, FileRecord remote, bool pending, bool hold)
        {
            Snap snap = new Snap();
            snap.Pending = pending;
            snap.Hold = hold;
            if (local != null)
            {
                snap.Ll = local.Length.ToString(CultureInfo.InvariantCulture);
                snap.Lm = Stamp(local.MtimeUtc);
            }
            if (remote != null)
            {
                snap.Rl = remote.Length.ToString(CultureInfo.InvariantCulture);
                snap.Rm = Stamp(remote.MtimeUtc);
            }
            return snap;
        }

        public static bool SameContents(FileRecord left, FileRecord right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            if (string.IsNullOrEmpty(left.Full) || string.IsNullOrEmpty(right.Full)) return false;
            using (FileStream a = new FileStream(LongPath(left.Full), FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream b = new FileStream(LongPath(right.Full), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (a.Length != b.Length) return false;
                byte[] x = new byte[65536];
                byte[] y = new byte[65536];
                int count;
                while ((count = a.Read(x, 0, x.Length)) > 0)
                {
                    int read = 0;
                    while (read < count)
                    {
                        int got = b.Read(y, read, count - read);
                        if (got == 0) return false;
                        read += got;
                    }
                    for (int i = 0; i < count; i++) if (x[i] != y[i]) return false;
                }
                return b.ReadByte() == -1;
            }
        }

        static Snap SyncedEntry(FileRecord local, FileRecord remote)
        {
            Snap snap = NewEntry(local, remote, false, false);
            snap.Synced = Stamp(DateTime.UtcNow);
            return snap;
        }

        public static void Initialize(string local, string remote, string statePath)
        {
            Initialize(local, new FileSystemRemote(remote), statePath);
        }

        public static void Initialize(string local, IRemote remote, string statePath)
        {
            if (!remote.Prepare(false))
            {
                Store.SaveState(statePath, new Dictionary<string, Snap>(StringComparer.OrdinalIgnoreCase));
                return;
            }
            Dictionary<string, FileRecord> localFiles = Index(ListFiles(local));
            Dictionary<string, FileRecord> remoteFiles = remote.List(null);
            Dictionary<string, Snap> map = new Dictionary<string, Snap>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, bool> rels = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in localFiles.Keys) rels[key] = true;
            foreach (string key in remoteFiles.Keys) rels[key] = true;
            foreach (string rel in rels.Keys)
            {
                FileRecord left = localFiles.ContainsKey(rel) ? localFiles[rel] : null;
                FileRecord right = remoteFiles.ContainsKey(rel) ? remoteFiles[rel] : null;
                if (left == null || right == null) continue;
                bool hold = !remote.Same(left, right);
                map[rel] = NewEntry(left, right, false, hold);
            }
            Store.SaveState(statePath, map);
        }

        delegate uint CopyProgressRoutine(long totalSize, long transferred, long streamSize, long streamTransferred,
            uint streamNumber, uint reason, IntPtr source, IntPtr destination, IntPtr data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CopyFileEx(string existing, string target, CopyProgressRoutine progress, IntPtr data, IntPtr cancel, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool MoveFileEx(string existing, string target, uint flags);

        // Replaces destination with a finished temporary file in one step.
        public static void MoveIntoPlace(string partial, string destination)
        {
            if (!MoveFileEx(LongPath(partial), LongPath(destination), 0x1 | 0x8))
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException(new System.ComponentModel.Win32Exception(error).Message + " " + destination, unchecked((int)0x80070000) | error);
            }
        }

        public static void CopyFile(string source, string destination)
        {
            CopyFile(source, destination, null);
        }

        // Same as File.Copy with overwrite, but reports bytes copied while a large file is in progress.
        // The copy goes to a temporary file first, so an interrupted copy never leaves a partial file.
        public static void CopyFile(string source, string destination, SyncProgress progress)
        {
            string parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(LongPath(parent)))
                Directory.CreateDirectory(LongPath(parent));
            CopyProgressRoutine callback = null;
            if (progress != null)
                callback = delegate(long totalSize, long transferred, long streamSize, long streamTransferred,
                    uint streamNumber, uint reason, IntPtr from, IntPtr to, IntPtr data)
                {
                    progress.Copied(transferred);
                    return progress.Canceled ? 1u : 0u;
                };
            string partial = destination + ".dfs-partial.tmp";
            try
            {
                if (!CopyFileEx(LongPath(source), LongPath(partial), callback, IntPtr.Zero, IntPtr.Zero, 0))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (progress != null && progress.Canceled) throw new OperationCanceledException();
                    throw new IOException(new System.ComponentModel.Win32Exception(error).Message + " " + destination, unchecked((int)0x80070000) | error);
                }
                MoveIntoPlace(partial, destination);
            }
            catch
            {
                try { if (File.Exists(LongPath(partial))) File.Delete(LongPath(partial)); }
                catch (IOException) { }
                throw;
            }
            finally { GC.KeepAlive(callback); }
        }

        public static string ConflictRel(string rel, string kind)
        {
            string leaf = Path.GetFileName(rel);
            string dir = Path.GetDirectoryName(rel);
            string baseName = Path.GetFileNameWithoutExtension(leaf);
            string ext = Path.GetExtension(leaf);
            string name = baseName + "." + kind + "-conflict-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ext;
            if (string.IsNullOrEmpty(dir)) return name;
            return dir.TrimEnd('\\') + "\\" + name;
        }

        internal static FileRecord FromDisk(string path)
        {
            FileInfo info = new FileInfo(LongPath(path));
            FileRecord record = new FileRecord();
            record.Full = path;
            record.Length = info.Length;
            record.MtimeUtc = info.LastWriteTimeUtc;
            return record;
        }

        // Every folder under root as relative paths, skipping links and excluded names.
        public static List<string> ListDirectories(string root, Dictionary<string, bool> links)
        {
            List<string> found = new List<string>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return found;
            Stack<string> stack = new Stack<string>();
            stack.Push("");
            while (stack.Count > 0)
            {
                string rel = stack.Pop();
                string dir = string.IsNullOrEmpty(rel) ? root : Path.Combine(root, rel);
                DirectoryInfo[] dirs;
                try { dirs = new DirectoryInfo(LongPath(dir)).GetDirectories(); }
                catch (Exception ex) { throw new IOException("Could not scan folders in " + dir, ex); }
                foreach (DirectoryInfo child in dirs)
                {
                    if (SkipName(child.Name, true)) continue;
                    string childRel = string.IsNullOrEmpty(rel) ? child.Name : rel + "\\" + child.Name;
                    if (UnderAny(childRel, links) || IsRedirect(child.FullName, child.Attributes)) continue;
                    found.Add(childRel);
                    stack.Push(childRel);
                }
            }
            return found;
        }

        public static int MirrorDirectories(string cloud, string local)
        {
            return MirrorDirectories(cloud, local, null);
        }

        static int MirrorDirectories(string cloud, string local, Dictionary<string, bool> links)
        {
            if (string.IsNullOrEmpty(cloud) || !Directory.Exists(cloud)) return 0;
            if (string.IsNullOrEmpty(local)) return 0;
            Directory.CreateDirectory(local);
            int made = 0;
            Stack<string> stack = new Stack<string>();
            stack.Push("");
            while (stack.Count > 0)
            {
                string rel = stack.Pop();
                string cloudDir = string.IsNullOrEmpty(rel) ? cloud : Path.Combine(cloud, rel);
                string localDir = string.IsNullOrEmpty(rel) ? local : Path.Combine(local, rel);
                if (!Directory.Exists(localDir))
                {
                    Directory.CreateDirectory(localDir);
                    made++;
                }
                DirectoryInfo[] dirs;
                try { dirs = new DirectoryInfo(cloudDir).GetDirectories(); }
                catch (Exception ex) { throw new IOException("Could not scan folders in " + cloudDir, ex); }
                foreach (DirectoryInfo dir in dirs)
                {
                    if (SkipName(dir.Name, true)) continue;
                    string child = string.IsNullOrEmpty(rel) ? dir.Name : rel + "\\" + dir.Name;
                    if (UnderAny(child, links) || IsRedirect(dir.FullName, dir.Attributes)) continue;
                    stack.Push(child);
                }
            }
            Store.Log("mirrored folders into " + local + " (" + made + " new)");
            return made;
        }

        static bool IsLinkAt(string path)
        {
            string target = LongPath(path);
            if (!File.Exists(target) && !Directory.Exists(target)) return false;
            return IsRedirect(path, File.GetAttributes(target));
        }

        // Copies a dropped file or folder and returns how many symbolic links or junctions were
        // skipped. A link is never followed in the source or written through in the destination.
        public static int CopyInto(string source, string destRoot, string relative, List<string> dirs, List<string> files)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(destRoot) || string.IsNullOrEmpty(relative)) return 0;
            if (dirs == null) dirs = new List<string>();
            if (files == null) files = new List<string>();
            Directory.CreateDirectory(destRoot);
            string dest = Path.GetFullPath(Path.Combine(destRoot, relative));
            if (!Store.IsUnder(dest, destRoot)) throw new IOException("The copy must stay inside the selected folder.");
            if (IsLinkAt(source) || IsLinkAt(dest))
            {
                Store.Log("copy skipped a symbolic link or junction: " + source);
                return 1;
            }
            RejectReparsePath(source);
            RejectReparsePath(dest);
            if (File.Exists(source))
            {
                if (string.Equals(Store.Norm(source), Store.Norm(dest), StringComparison.OrdinalIgnoreCase)) return 0;
                CopyFile(source, dest);
                files.Add(relative);
                return 0;
            }
            if (!Directory.Exists(source)) return 0;
            if (Store.IsUnder(dest, source)) return 0;
            int skipped = 0;
            dirs.Add(relative);
            Directory.CreateDirectory(dest);
            Stack<string> pending = new Stack<string>();
            pending.Push("");
            while (pending.Count > 0)
            {
                string sub = pending.Pop();
                string from = string.IsNullOrEmpty(sub) ? source : Path.Combine(source, sub);
                string relHere = string.IsNullOrEmpty(sub) ? relative : relative + "\\" + sub;
                if (!string.IsNullOrEmpty(sub))
                {
                    dirs.Add(relHere);
                    Directory.CreateDirectory(Path.Combine(destRoot, relHere));
                }
                FileSystemInfo[] children;
                try { children = new DirectoryInfo(LongPath(from)).GetFileSystemInfos(); }
                catch (Exception ex) { throw new IOException("Could not copy folder " + from, ex); }
                foreach (FileSystemInfo child in children)
                {
                    bool isDir = (child.Attributes & FileAttributes.Directory) != 0;
                    if (SkipName(child.Name, isDir)) continue;
                    string childSub = string.IsNullOrEmpty(sub) ? child.Name : sub + "\\" + child.Name;
                    string childRel = relative + "\\" + childSub;
                    if (IsRedirect(child.FullName, child.Attributes) || IsLinkAt(Path.Combine(destRoot, childRel)))
                    {
                        Store.Log("copy skipped a symbolic link or junction: " + child.FullName);
                        skipped++;
                        continue;
                    }
                    if (isDir) pending.Push(childSub);
                    else
                    {
                        CopyFile(child.FullName, Path.Combine(destRoot, childRel));
                        files.Add(childRel);
                    }
                }
            }
            return skipped;
        }

        // Copies items dropped between the panes of a folder pair through its remote, so Google Drive
        // files move directly between this PC and Google Drive. Each item is a source (a local path when
        // uploading, a path relative to the cloud folder when downloading) and a destination relative
        // to the target root. Returns the number of links skipped.
        public static int TransferThroughRemote(IRemote remote, string localRoot, bool toLocal, List<KeyValuePair<string, string>> items,
            List<string> dirs, List<string> files, SyncProgress progress)
        {
            if (!remote.Prepare(!toLocal)) throw new IOException("The cloud folder is not available.");
            List<string> cloudLinks = new List<string>();
            Dictionary<string, FileRecord> cloud = remote.List(cloudLinks);
            Dictionary<string, bool> linkSet = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (string link in cloudLinks) linkSet[link] = true;
            List<KeyValuePair<FileRecord, string>> work = new List<KeyValuePair<FileRecord, string>>();
            List<string> folders = new List<string>();
            int skipped = 0;
            foreach (KeyValuePair<string, string> item in items)
            {
                string from = item.Key;
                string to = item.Value;
                if (toLocal)
                {
                    if (UnderAny(from, linkSet)) { skipped++; continue; }
                    FileRecord single;
                    if (cloud.TryGetValue(from, out single)) { work.Add(new KeyValuePair<FileRecord, string>(single, to)); continue; }
                    folders.Add(to);
                    foreach (string dir in remote.Directories(linkSet))
                        if (dir.StartsWith(from + "\\", StringComparison.OrdinalIgnoreCase)) folders.Add(to + dir.Substring(from.Length));
                    foreach (KeyValuePair<string, FileRecord> pair in cloud)
                        if (pair.Key.StartsWith(from + "\\", StringComparison.OrdinalIgnoreCase))
                            work.Add(new KeyValuePair<FileRecord, string>(pair.Value, to + pair.Key.Substring(from.Length)));
                    foreach (string link in cloudLinks) if (link.StartsWith(from + "\\", StringComparison.OrdinalIgnoreCase)) skipped++;
                }
                else
                {
                    if (IsLinkAt(from) || UnderAny(to, linkSet)) { skipped++; continue; }
                    if (File.Exists(LongPath(from))) { work.Add(new KeyValuePair<FileRecord, string>(FromDisk(from), to)); continue; }
                    if (!Directory.Exists(LongPath(from))) continue;
                    folders.Add(to);
                    foreach (string dir in ListDirectories(from, null)) folders.Add(to + "\\" + dir);
                    List<string> localLinks = new List<string>();
                    foreach (FileRecord file in ListFiles(from, localLinks)) work.Add(new KeyValuePair<FileRecord, string>(file, to + "\\" + file.Rel));
                    skipped += localLinks.Count;
                }
            }
            long bytes = 0;
            foreach (KeyValuePair<FileRecord, string> pair in work) bytes += pair.Key.Length;
            if (!toLocal)
            {
                long free = remote.FreeSpace();
                if (free >= 0 && bytes > free)
                    throw new IOException("Not enough space: this copy needs " + FormatSize(bytes) + ", but only " + FormatSize(free) + " is available in " + remote.Name + ".");
                remote.EnsureDirectories(folders);
            }
            else foreach (string folder in folders) Directory.CreateDirectory(LongPath(Path.Combine(localRoot, folder)));
            dirs.AddRange(folders);
            if (progress != null) progress.Plan(work.Count, bytes);
            foreach (KeyValuePair<FileRecord, string> pair in work)
            {
                if (progress != null && progress.Canceled) break;
                if (progress != null) progress.BeginFile(pair.Value);
                bool copied = false;
                if (!toLocal)
                {
                    remote.Upload(pair.Value, pair.Key, progress);
                    copied = true;
                }
                else if (IsLinkAt(Path.Combine(localRoot, pair.Value))) skipped++;
                else
                {
                    remote.Download(pair.Key, Path.Combine(localRoot, pair.Value), progress);
                    copied = true;
                }
                if (copied) files.Add(pair.Value);
                if (progress != null) progress.EndFile(pair.Key.Length, copied);
            }
            return skipped;
        }

        // Copies the chosen cloud files into the local replica, downloading Google Drive files directly.
        public static void MaterializeSelection(Provider provider, string localRoot, IList<string> dirs, IList<string> files)
        {
            if (GoogleAuth.EmailOf(provider) == null)
            {
                MaterializeSelection(provider.Root, localRoot, dirs, files);
                return;
            }
            if (string.IsNullOrEmpty(localRoot)) return;
            Directory.CreateDirectory(localRoot);
            if (dirs != null)
                foreach (string dir in dirs) Directory.CreateDirectory(LongPath(string.IsNullOrEmpty(dir) ? localRoot : Path.Combine(localRoot, dir)));
            if (files == null) return;
            IRemote remote = RemoteFor(provider, "", provider.Root);
            if (!remote.Prepare(false)) throw new IOException("The drive is not available.");
            foreach (KeyValuePair<string, FileRecord> pair in remote.ListAllowed(new List<string>(files)))
            {
                string dest = Path.Combine(localRoot, pair.Key);
                if (!File.Exists(LongPath(dest))) remote.Download(pair.Value, dest, null);
            }
        }

        public static void MaterializeSelection(string cloudRoot, string localRoot, IList<string> dirs, IList<string> files)
        {
            if (string.IsNullOrEmpty(localRoot)) return;
            Directory.CreateDirectory(localRoot);
            if (dirs != null)
            {
                foreach (string dir in dirs)
                {
                    string path = string.IsNullOrEmpty(dir) ? localRoot : Path.Combine(localRoot, dir);
                    Directory.CreateDirectory(path);
                }
            }
            if (files == null) return;
            foreach (string rel in files)
            {
                if (string.IsNullOrEmpty(rel) || string.IsNullOrEmpty(cloudRoot)) continue;
                string source = Path.Combine(cloudRoot, rel);
                string dest = Path.Combine(localRoot, rel);
                string parent = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                if (File.Exists(source) && !File.Exists(dest)) CopyFile(source, dest);
            }
        }

        public static SyncStats SyncFolder(string direction, string local, string remote, string statePath, bool cloudReadbackIsTruth)
        {
            return SyncFolder(direction, local, remote, statePath, cloudReadbackIsTruth, null);
        }

        public static SyncStats SyncFolder(string direction, string local, string remote, string statePath, bool cloudReadbackIsTruth, List<string> onlyFiles)
        {
            return SyncFolder(direction, local, remote, statePath, cloudReadbackIsTruth, onlyFiles, null);
        }

        sealed class CopyJob
        {
            public string Rel;
            public long Bytes;
            public Func<bool> Run;
        }

        // Decisions are made first and the copies run afterwards, so the total amount is known
        // before any data moves. Each job reports whether its file was copied.
        public static SyncStats SyncFolder(string direction, string local, string remote, string statePath, bool cloudReadbackIsTruth, List<string> onlyFiles, SyncProgress progress)
        {
            if (direction == "Pull" && string.IsNullOrEmpty(remote))
            {
                Store.Log("skip pull, cloud folder missing: " + remote);
                SyncStats missing = new SyncStats();
                missing.Errors++;
                return missing;
            }
            return SyncFolder(direction, local, new FileSystemRemote(remote), statePath, cloudReadbackIsTruth, onlyFiles, progress);
        }

        public static SyncStats SyncFolder(string direction, string local, IRemote remote, string statePath, bool cloudReadbackIsTruth, List<string> onlyFiles, SyncProgress progress)
        {
            SyncStats stats = new SyncStats();
            List<CopyJob> jobs = new List<CopyJob>();
            if (direction == "Push" && !Directory.Exists(local ?? ""))
            {
                Store.Log("skip push, local folder missing: " + local);
                stats.Errors++;
                return stats;
            }
            if (!remote.Prepare(direction == "Push" && onlyFiles == null))
            {
                Store.Log("skip " + direction.ToLowerInvariant() + ", cloud folder missing: " + remote.Name);
                stats.Errors++;
                return stats;
            }
            if (!Directory.Exists(local)) Directory.CreateDirectory(local);
            RejectReparsePath(local);

            Dictionary<string, FileRecord> localFiles;
            Dictionary<string, FileRecord> remoteFiles;
            Dictionary<string, bool> links = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (onlyFiles != null)
            {
                localFiles = IndexAllowed(local, onlyFiles);
                remoteFiles = remote.ListAllowed(onlyFiles);
            }
            else
            {
                // A link on either side is left alone, and nothing is copied into or through it.
                List<string> localLinks = new List<string>();
                List<string> remoteLinks = new List<string>();
                localFiles = Index(ListFiles(local, localLinks));
                remoteFiles = remote.List(remoteLinks);
                foreach (string rel in localLinks) links[rel] = true;
                foreach (string rel in remoteLinks) links[rel] = true;
                DropUnder(localFiles, links);
                DropUnder(remoteFiles, links);
                stats.Links = links.Count;
                if (links.Count > 0)
                    Store.Log("skipped " + links.Count + " symbolic links or junctions in " + local + ": " + string.Join(", ", new List<string>(links.Keys).ToArray()));
            }
            Dictionary<string, Snap> state = Store.ReadState(statePath);
            if (onlyFiles != null) DropKeys(state, AllowSet(onlyFiles));
            else if (direction == "Push") remote.EnsureDirectories(ListDirectories(local, links));
            else
            {
                int made = 0;
                foreach (string rel in remote.Directories(links))
                {
                    string dir = LongPath(Path.Combine(local, rel));
                    if (Directory.Exists(dir)) continue;
                    Directory.CreateDirectory(dir);
                    made++;
                }
                if (made > 0) Store.Log("created " + made + " folders in " + local + " to match the cloud");
            }

            if (direction == "Push")
            {
                List<string> localKeys = new List<string>(localFiles.Keys);
                foreach (string rel in localKeys)
                {
                    FileRecord localFile = localFiles[rel];
                    FileRecord remoteFile = remoteFiles.ContainsKey(rel) ? remoteFiles[rel] : null;
                    Snap snap = state.ContainsKey(rel) ? state[rel] : null;
                    if (snap != null && snap.Hold && remoteFile != null && remote.Same(localFile, remoteFile))
                    {
                        state[rel] = SyncedEntry(localFile, remoteFile);
                        continue;
                    }
                    bool localSame = snap != null && SameSig(localFile.Length, localFile.MtimeUtc, snap.Ll, snap.Lm);
                    bool pending = snap != null && snap.Pending;
                    if (pending && remoteFile != null && remote.Same(localFile, remoteFile))
                    {
                        state[rel] = SyncedEntry(localFile, remoteFile);
                        continue;
                    }
                    if (snap == null && remoteFile != null)
                    {
                        bool same = remote.Same(localFile, remoteFile);
                        state[rel] = same ? SyncedEntry(localFile, remoteFile) : NewEntry(localFile, remoteFile, false, true);
                        if (!same) stats.Conflict++;
                        continue;
                    }
                    if (localSame && !pending)
                    {
                        if (snap.Hold) stats.Conflict++;
                        else if (remoteFile != null && string.IsNullOrEmpty(snap.Synced) && remote.Same(localFile, remoteFile))
                            state[rel] = SyncedEntry(localFile, remoteFile);
                        continue;
                    }
                    bool remoteMoved = snap != null && remoteFile != null && !SameSig(remoteFile.Length, remoteFile.MtimeUtc, snap.Rl, snap.Rm);
                    if (remoteMoved && !pending)
                    {
                        string conflictRel = ConflictRel(rel, "local");
                        jobs.Add(new CopyJob { Rel = rel, Bytes = localFile.Length, Run = delegate
                        {
                            try
                            {
                                remote.Upload(conflictRel, localFile, progress);
                                state[rel] = NewEntry(localFile, remoteFile, false, true);
                                stats.Conflict++;
                                Store.Log("conflict kept newer cloud file, saved local copy: " + conflictRel);
                                return true;
                            }
                            catch (OperationCanceledException) { return false; }
                            catch (Exception ex)
                            {
                                stats.Errors++;
                                Store.Log("conflict copy failed " + rel + ": " + ex.Message);
                                return false;
                            }
                        } });
                        continue;
                    }
                    jobs.Add(new CopyJob { Rel = rel, Bytes = localFile.Length, Run = delegate
                    {
                        try
                        {
                            FileRecord remoteNow = remote.Upload(rel, localFile, progress);
                            if (cloudReadbackIsTruth)
                            {
                                state[rel] = SyncedEntry(localFile, remoteNow);
                            }
                            else
                            {
                                Snap entry = NewEntry(localFile, null, true, false);
                                entry.Synced = Stamp(DateTime.UtcNow);
                                if (snap != null)
                                {
                                    entry.Rl = snap.Rl;
                                    entry.Rm = snap.Rm;
                                }
                                state[rel] = entry;
                            }
                            stats.Up++;
                            Store.Log("uploaded " + rel);
                            return true;
                        }
                        catch (OperationCanceledException) { return false; }
                        catch (Exception ex)
                        {
                            stats.Errors++;
                            Store.Log("upload failed " + rel + ": " + ex.Message);
                            return false;
                        }
                    } });
                }
                // Backup retains cloud copies when a local file is deleted.

            }
            else
            {
                List<string> remoteKeys = new List<string>(remoteFiles.Keys);
                foreach (string rel in remoteKeys)
                {
                    FileRecord remoteFile = remoteFiles[rel];
                    FileRecord localFile = localFiles.ContainsKey(rel) ? localFiles[rel] : null;
                    Snap snap = state.ContainsKey(rel) ? state[rel] : null;
                    if (snap != null && snap.Hold && localFile != null && remote.Same(localFile, remoteFile))
                    {
                        state[rel] = SyncedEntry(localFile, remoteFile);
                        continue;
                    }
                    if (snap != null && snap.Pending && localFile != null)
                    {
                        bool driveUnchanged = !string.IsNullOrEmpty(snap.Rl) && SameSig(remoteFile.Length, remoteFile.MtimeUtc, snap.Rl, snap.Rm);
                        if (remote.Same(localFile, remoteFile))
                        {
                            state[rel] = SyncedEntry(localFile, remoteFile);
                            Store.Log("upload confirmed " + rel);
                        }
                        else if (driveUnchanged || localFile.MtimeUtc > remoteFile.MtimeUtc.AddSeconds(3))
                        {
                            Store.Log("keeping local copy until the cloud upload is confirmed: " + rel);
                        }
                        else
                        {
                            jobs.Add(new CopyJob { Rel = rel, Bytes = remoteFile.Length, Run = delegate
                            {
                                try
                                {
                                    string conflictRel = ConflictRel(rel, "drive");
                                    remote.Download(remoteFile, Path.Combine(local, conflictRel), progress);
                                    state[rel] = NewEntry(localFile, remoteFile, true, false);
                                    stats.Conflict++;
                                    Store.Log("saved cloud conflict " + conflictRel);
                                    return true;
                                }
                                catch (OperationCanceledException) { return false; }
                                catch (Exception ex)
                                {
                                    stats.Errors++;
                                    Store.Log("conflict save failed " + rel + ": " + ex.Message);
                                    return false;
                                }
                            } });
                        }
                        continue;
                    }
                    if (snap == null && localFile != null)
                    {
                        bool same = remote.Same(localFile, remoteFile);
                        state[rel] = same ? SyncedEntry(localFile, remoteFile) : NewEntry(localFile, remoteFile, false, true);
                        if (!same) stats.Conflict++;
                        continue;
                    }
                    bool remoteSame = snap != null && SameSig(remoteFile.Length, remoteFile.MtimeUtc, snap.Rl, snap.Rm);
                    bool localSame = localFile != null && snap != null && SameSig(localFile.Length, localFile.MtimeUtc, snap.Ll, snap.Lm);
                    if (snap != null && snap.Hold && remoteSame && localFile != null && localSame) { stats.Conflict++; continue; }
                    if (remoteSame && localFile != null && !(snap != null && snap.Hold && !localSame)) continue;
                    if (localFile != null && !localSame && snap != null)
                    {
                        jobs.Add(new CopyJob { Rel = rel, Bytes = remoteFile.Length, Run = delegate
                        {
                            try
                            {
                                string conflictRel = ConflictRel(rel, "drive");
                                remote.Download(remoteFile, Path.Combine(local, conflictRel), progress);
                                state[rel] = NewEntry(localFile, remoteFile, false, true);
                                stats.Conflict++;
                                Store.Log("kept local edits, saved cloud copy " + conflictRel);
                                return true;
                            }
                            catch (OperationCanceledException) { return false; }
                            catch (Exception ex)
                            {
                                stats.Errors++;
                                Store.Log("conflict save failed " + rel + ": " + ex.Message);
                                return false;
                            }
                        } });
                        continue;
                    }
                    if (localFile != null && localFile.MtimeUtc > remoteFile.MtimeUtc.AddSeconds(3) && remoteFile.Length != localFile.Length)
                    {
                        Snap entry = NewEntry(localFile, null, true, false);
                        if (snap != null)
                        {
                            entry.Rl = snap.Rl;
                            entry.Rm = snap.Rm;
                        }
                        state[rel] = entry;
                        Store.Log("kept newer local file " + rel);
                        continue;
                    }
                    jobs.Add(new CopyJob { Rel = rel, Bytes = remoteFile.Length, Run = delegate
                    {
                        try
                        {
                            string dest = Path.Combine(local, rel);
                            string volume = Path.GetPathRoot(dest);
                            DriveInfo disk = new DriveInfo(volume);
                            if ((disk.AvailableFreeSpace - remoteFile.Length) < FreeReserve)
                            {
                                stats.Errors++;
                                Store.Log("skipped download, low disk space: " + rel);
                                return false;
                            }
                            remote.Download(remoteFile, dest, progress);
                            FileRecord localNow = FromDisk(dest);
                            state[rel] = SyncedEntry(localNow, remoteFile);
                            stats.Down++;
                            Store.Log("downloaded " + rel);
                            return true;
                        }
                        catch (OperationCanceledException) { return false; }
                        catch (Exception ex)
                        {
                            stats.Errors++;
                            Store.Log("download failed " + rel + ": " + ex.Message);
                            return false;
                        }
                    } });
                }
                // Refresh never deletes local files. A file deleted in the cloud stays on this PC
                // and its snapshot is kept so it shows as Missing in the cloud.
                List<string> stateKeys = new List<string>(state.Keys);
                int kept = 0;
                foreach (string rel in stateKeys)
                {
                    if (remoteFiles.ContainsKey(rel)) continue;
                    Snap snap = state[rel];
                    FileRecord localFile = localFiles.ContainsKey(rel) ? localFiles[rel] : null;
                    if (localFile == null)
                    {
                        state.Remove(rel);
                        continue;
                    }
                    if (snap.Pending || snap.Hold || string.IsNullOrEmpty(snap.Rl)) continue;
                    if (!SameSig(localFile.Length, localFile.MtimeUtc, snap.Ll, snap.Lm))
                    {
                        Snap entry = NewEntry(localFile, null, true, false);
                        entry.Rl = snap.Rl;
                        entry.Rm = snap.Rm;
                        state[rel] = entry;
                        continue;
                    }
                    kept++;
                }
                if (kept > 0) Store.Log("kept " + kept + " local files in " + local + " that are no longer in the cloud folder");
            }

            long planned = 0;
            foreach (CopyJob job in jobs) planned += job.Bytes;
            if (direction == "Push" && planned > 0)
            {
                long free = remote.FreeSpace();
                if (free >= 0 && planned > free)
                {
                    Store.SaveState(statePath, state);
                    throw new IOException("Not enough space for this backup: it needs " + FormatSize(planned) + ", but only "
                        + FormatSize(free) + " is available in " + remote.Name + ".");
                }
            }
            if (progress != null) progress.Plan(jobs.Count, planned);
            DateTime saved = DateTime.UtcNow;
            int unsaved = 0;
            foreach (CopyJob job in jobs)
            {
                if (progress != null && progress.Canceled)
                {
                    stats.Canceled = true;
                    break;
                }
                if (progress != null) progress.BeginFile(job.Rel);
                bool copied = job.Run();
                if (progress != null) progress.EndFile(job.Bytes, copied);
                // Progress is recorded as it goes, so an interrupted run doesn't start over.
                if (++unsaved >= 100 || (DateTime.UtcNow - saved).TotalSeconds >= 30)
                {
                    Store.SaveState(statePath, state);
                    saved = DateTime.UtcNow;
                    unsaved = 0;
                }
            }
            if (progress != null && progress.Canceled) stats.Canceled = true;
            Store.SaveState(statePath, state);
            return stats;
        }

        public static SyncStats SyncLink(LinkFolder folder, string direction, bool cloudReadbackIsTruth)
        {
            return SyncLink(folder, direction, cloudReadbackIsTruth, null);
        }

        // Google Drive accounts are reached through the API so files never pass through Google Drive
        // for desktop's cache on C:. Other drives are mounted folders.
        public static IRemote RemoteFor(Provider provider, string remoteRelative, string remotePath)
        {
            string email = GoogleAuth.EmailOf(provider);
            if (email == null) return new FileSystemRemote(remotePath);
            if (!GoogleAuth.Configured || !GoogleAuth.IsSignedIn(email))
                throw new IOException("Sign in to Google Drive for " + email + " first: choose … → Google Drive sign-in. "
                    + "Backups go straight to Google Drive instead of through Google Drive for desktop, which keeps a second copy of every file on C:.");
            return new GoogleRemote(GoogleAuth.Client(email), email, remoteRelative);
        }

        public static SyncStats SyncLink(LinkFolder folder, string direction, bool cloudReadbackIsTruth, SyncProgress progress)
        {
            if (progress != null && progress.Folder != folder.Local) progress.StartFolder(folder.Local, 1, 1);
            List<Provider> providers = Store.DetectProviders();
            string remote = Store.ResolveRemote(folder, providers);
            if (string.IsNullOrEmpty(remote) || !Directory.Exists(Path.GetPathRoot(remote) ?? ""))
            {
                Store.Log(direction + " skipped, cloud folder is not available: " + folder.Local);
                folder.LastErrors = 1;
                SyncStats missing = new SyncStats();
                missing.Errors = 1;
                return missing;
            }
            Provider provider = Store.FindProvider(providers, folder.ProviderId);
            if (provider == null || !provider.IsOnline)
            {
                Store.Log(direction + " skipped, drive is not connected: " + folder.ProviderId);
                folder.LastErrors = 1;
                SyncStats offline = new SyncStats();
                offline.Errors = 1;
                return offline;
            }
            if (folder.Partial && folder.Dirs != null)
            {
                foreach (string dir in folder.Dirs)
                {
                    string path = string.IsNullOrEmpty(dir) ? folder.Local : Path.Combine(folder.Local, dir);
                    if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                }
            }
            List<string> onlyFiles = folder.Partial ? (folder.Files ?? new List<string>()) : null;
            SyncStats stats = SyncFolder(direction, folder.Local, RemoteFor(provider, folder.RemoteRelative, remote), Store.StatePath(folder.Id), cloudReadbackIsTruth, onlyFiles, progress);
            if (stats.Errors == 0 && stats.Conflict == 0) folder.LastSync = Stamp(DateTime.UtcNow);
            folder.LastErrors = stats.Errors;
            Store.Log(direction + " " + folder.Local + ": uploaded=" + stats.Up + " downloaded=" + stats.Down
                + " deleted=" + stats.Del + " conflicts=" + stats.Conflict + " errors=" + stats.Errors);
            return stats;
        }

        public static SyncStats SyncAll(string direction)
        {
            return SyncAll(direction, null);
        }

        public static SyncStats SyncAll(string direction, SyncProgress progress)
        {
            SyncStats total = new SyncStats();
            using (LockState(120))
            {
                List<LinkFolder> links = Store.ReadLinks();
                for (int index = 0; index < links.Count; index++)
                {
                    LinkFolder folder = links[index];
                    if (progress != null && progress.Canceled)
                    {
                        total.Canceled = true;
                        break;
                    }
                    if (progress != null) progress.StartFolder(folder.Local, index + 1, links.Count);
                    try
                    {
                        SyncStats stats = SyncLink(folder, direction, false, progress);
                        total.Up += stats.Up;
                        total.Down += stats.Down;
                        total.Del += stats.Del;
                        total.Conflict += stats.Conflict;
                        total.Errors += stats.Errors;
                        total.Links += stats.Links;
                        if (stats.Canceled) total.Canceled = true;
                    }
                    catch (Exception ex)
                    {
                        total.Errors++;
                        if (total.Problem == null) total.Problem = ex.Message;
                        folder.LastErrors = 1;
                        Store.Log(direction + " failed for " + folder.Local + ": " + ex.Message);
                    }
                }
                Store.SaveLinks(links);
            }
            return total;
        }

        static Dictionary<string, bool> AllowSet(List<string> onlyFiles)
        {
            Dictionary<string, bool> allow = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (onlyFiles == null) return allow;
            foreach (string rel in onlyFiles)
            {
                if (!string.IsNullOrEmpty(rel)) allow[rel] = true;
            }
            return allow;
        }

        internal static Dictionary<string, FileRecord> IndexAllowed(string root, List<string> onlyFiles)
        {
            Dictionary<string, FileRecord> map = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
            if (onlyFiles == null || string.IsNullOrEmpty(root)) return map;
            foreach (string rel in onlyFiles)
            {
                if (string.IsNullOrEmpty(rel)) continue;
                if (SkipName(Path.GetFileName(rel), false)) continue;
                string path = Path.GetFullPath(Path.Combine(root, rel));
                if (!Store.IsUnder(path, root)) throw new IOException("A selected file escapes its folder root.");
                try
                {
                    FileAttributes attributes = File.GetAttributes(LongPath(path));
                    if (IsRedirect(path, attributes)) throw new IOException("Cannot safely scan a symbolic link: " + path);
                    if ((attributes & FileAttributes.Directory) != 0) continue;
                }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                FileRecord record = FromDisk(path);
                record.Rel = rel;
                map[rel] = record;
            }
            return map;
        }

        static void DropKeys<T>(Dictionary<string, T> map, Dictionary<string, bool> allow)
        {
            List<string> drop = new List<string>();
            foreach (string key in map.Keys)
            {
                if (!allow.ContainsKey(key)) drop.Add(key);
            }
            foreach (string key in drop) map.Remove(key);
        }

        public static LinkFolder RememberSelection(string providerId, string localRoot, IList<string> dirs, IList<string> files)
        {
            localRoot = Store.Norm(localRoot);
            using (LockState(120))
            {
                List<LinkFolder> links = Store.ReadLinks();
                foreach (LinkFolder existing in links)
                {
                    if (existing.Partial) continue;
                    if (string.Equals(Store.Norm(existing.Local), localRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        Store.Log("replica selection left the existing full link unchanged: " + localRoot);
                        return existing;
                    }
                }
                LinkFolder folder = null;
                foreach (LinkFolder existing in links)
                {
                    if (!existing.Partial) continue;
                    if (!string.Equals(existing.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(Store.Norm(existing.Local), localRoot, StringComparison.OrdinalIgnoreCase)) folder = existing;
                }
                if (folder == null)
                {
                    folder = new LinkFolder();
                    folder.Id = Guid.NewGuid().ToString("n");
                    folder.Local = localRoot;
                    folder.ProviderId = providerId;
                    folder.RemoteRelative = "";
                    folder.Partial = true;
                    folder.Files = new List<string>();
                    folder.Dirs = new List<string>();
                    folder.Added = Stamp(DateTime.UtcNow);
                    if (!Directory.Exists(localRoot)) Directory.CreateDirectory(localRoot);
                    links.Add(folder);
                    Store.SaveState(Store.StatePath(folder.Id), new Dictionary<string, Snap>(StringComparer.OrdinalIgnoreCase));
                }
                if (folder.Files == null) folder.Files = new List<string>();
                if (folder.Dirs == null) folder.Dirs = new List<string>();
                AddUnique(folder.Dirs, dirs);
                AddUnique(folder.Files, files);
                Dictionary<string, Snap> state = Store.ReadState(Store.StatePath(folder.Id));
                List<Provider> providers = Store.DetectProviders();
                IRemote remote = RemoteFor(Store.FindProvider(providers, providerId), "", Store.ResolveRemote(folder, providers));
                if (files != null && remote.Prepare(false))
                {
                    List<string> wanted = new List<string>(files);
                    Dictionary<string, FileRecord> cloudFiles = remote.ListAllowed(wanted);
                    foreach (string rel in files)
                    {
                        if (string.IsNullOrEmpty(rel)) continue;
                        string localFile = Path.Combine(localRoot, rel);
                        FileRecord left = File.Exists(LongPath(localFile)) ? FromDisk(localFile) : null;
                        FileRecord right = cloudFiles.ContainsKey(rel) ? cloudFiles[rel] : null;
                        if (left == null && right == null) continue;
                        state[rel] = NewEntry(left, right, false, left != null && right != null && !remote.Same(left, right));
                    }
                }
                Store.SaveState(Store.StatePath(folder.Id), state);
                Store.SaveLinks(links);
                Store.Log("replica selection saved for " + localRoot);
                return folder;
            }
        }

        static void AddUnique(List<string> target, IList<string> more)
        {
            if (more == null) return;
            foreach (string rel in more)
            {
                if (string.IsNullOrEmpty(rel)) continue;
                bool found = false;
                foreach (string existing in target)
                {
                    if (string.Equals(existing, rel, StringComparison.OrdinalIgnoreCase)) found = true;
                }
                if (!found) target.Add(rel);
            }
        }

        public static string ValidateLink(string local, string providerId, string remoteRelative, List<LinkFolder> links)
        {
            if (string.IsNullOrWhiteSpace(local) || !Path.IsPathRooted(local)
                || (Path.GetPathRoot(local).Length < 3 && !local.StartsWith(@"\\", StringComparison.Ordinal)))
                throw new ArgumentException("Choose an absolute local folder path.");
            local = Path.GetFullPath(local);
            Provider provider = Store.FindProvider(Store.DetectProviders(), providerId);
            if (provider == null || !provider.IsOnline) throw new IOException("The selected drive is not connected.");
            if (Path.IsPathRooted(remoteRelative ?? "")) throw new ArgumentException("Choose a cloud folder inside the selected drive.");
            string remote = Path.GetFullPath(Path.Combine(provider.Root, remoteRelative ?? ""));
            if (!Store.IsUnder(remote, provider.Root)) throw new ArgumentException("The cloud folder must stay inside the selected drive.");
            RejectReparsePath(local);
            RejectReparsePath(remote);
            foreach (Provider drive in Store.DetectProviders())
                if (Store.IsUnder(local, drive.Root))
                    throw new ArgumentException("The local folder is inside a configured cloud drive: " + drive.Root
                        + (drive.Kind == "Folder"
                            ? ". If this is a local backup folder, remove its added drive tab and use Attach folder with your actual cloud drive."
                            : ". Choose a folder outside this cloud drive for local backups."));
            if (Store.IsUnder(local, remote) || Store.IsUnder(remote, local))
                throw new ArgumentException("Local and cloud folders must be separate and cannot contain each other.");
            foreach (LinkFolder existing in links)
            {
                string other = Store.ResolveRemote(existing, Store.DetectProviders());
                if (Store.IsUnder(local, existing.Local) || Store.IsUnder(existing.Local, local)
                    || (!string.IsNullOrEmpty(other) && (Store.IsUnder(remote, other) || Store.IsUnder(other, remote)))
                    || (!string.IsNullOrEmpty(other) && (Store.IsUnder(local, other) || Store.IsUnder(other, local)))
                    || Store.IsUnder(remote, existing.Local) || Store.IsUnder(existing.Local, remote))
                    throw new ArgumentException("This folder overlaps an existing attachment: " + existing.Local + ". Unlink it first or choose separate folders.");
            }
            return remote;
        }

        internal static void RejectReparsePath(string path)
        {
            string current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if ((Directory.Exists(current) || File.Exists(current)) && IsRedirect(current, File.GetAttributes(current)))
                    throw new ArgumentException("Choose a folder without symbolic links or junctions: " + current);
                current = Path.GetDirectoryName(current);
            }
        }

        public static LinkFolder Link(string local, string providerId, string remoteRelative)
        {
            using (LockState(120))
            {
                List<LinkFolder> links = Store.ReadLinks();
                string remote = ValidateLink(local, providerId, remoteRelative, links);
                local = Store.Norm(local);
                LinkFolder folder = new LinkFolder();
                folder.Id = Guid.NewGuid().ToString("n");
                folder.Local = local;
                folder.ProviderId = providerId;
                folder.RemoteRelative = Store.Relative(remote, Store.FindProvider(Store.DetectProviders(), providerId).Root) ?? "";
                folder.Added = Stamp(DateTime.UtcNow);
                Provider provider = Store.FindProvider(Store.DetectProviders(), providerId);
                IRemote cloud = RemoteFor(provider, folder.RemoteRelative, remote);
                Store.SaveCustomProvider(provider);
                if (!Directory.Exists(local)) Directory.CreateDirectory(local);
                Initialize(local, cloud, Store.StatePath(folder.Id));
                links.Add(folder);
                Store.SaveLinks(links);
                Store.Log("linked " + local + " <-> " + remote);
                return folder;
            }
        }

        public static void Unlink(string id)
        {
            using (LockState(60))
            {
                List<LinkFolder> links = Store.ReadLinks();
                List<LinkFolder> keep = new List<LinkFolder>();
                foreach (LinkFolder folder in links)
                {
                    if (!string.Equals(folder.Id, id, StringComparison.OrdinalIgnoreCase)) keep.Add(folder);
                }
                Store.SaveLinks(keep);
                string state = Store.StatePath(id);
                if (File.Exists(state)) File.Delete(state);
                Store.Log("unlinked " + id);
            }
        }

        public static LinkFolder FindLink(string path, List<LinkFolder> links)
        {
            LinkFolder best = null;
            int bestLen = -1;
            List<Provider> providers = Store.DetectProviders();
            foreach (LinkFolder folder in links)
            {
                if (Store.IsUnder(path, folder.Local) && Store.Norm(folder.Local).Length > bestLen)
                {
                    best = folder;
                    bestLen = Store.Norm(folder.Local).Length;
                }
                string remote = Store.ResolveRemote(folder, providers);
                if (!string.IsNullOrEmpty(remote) && Store.IsUnder(path, remote) && Store.Norm(remote).Length > bestLen)
                {
                    best = folder;
                    bestLen = Store.Norm(remote).Length;
                }
            }
            return best;
        }

        public class RowStatus
        {
            public string Text;
            public string Kind;
            public string LastSynced;
        }

        // Status shown while browsing compares sizes and times only. Reading a cloud file's contents
        // would make the cloud app download it to this PC.
        static bool SameShape(FileRecord local, FileRecord remote)
        {
            return local.Length == remote.Length && Math.Abs((local.MtimeUtc - remote.MtimeUtc).TotalSeconds) < 2;
        }

        // The cloud app may report a time rounded differently from the one recorded at upload.
        static bool NearSig(long length, DateTime mtime, string snapLength, string snapMtime)
        {
            if (string.IsNullOrEmpty(snapLength) || string.IsNullOrEmpty(snapMtime)) return false;
            long otherLength;
            if (!long.TryParse(snapLength, NumberStyles.Integer, CultureInfo.InvariantCulture, out otherLength) || length != otherLength) return false;
            DateTime other;
            if (!DateTime.TryParse(snapMtime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out other)) return false;
            return Math.Abs((mtime.ToUniversalTime() - other.ToUniversalTime()).TotalSeconds) < 2;
        }

        public static RowStatus Classify(FileRecord local, FileRecord remote, Snap snap)
        {
            RowStatus row = new RowStatus();
            row.Kind = "none";
            row.Text = "Not linked";
            row.LastSynced = "";
            if (local == null && remote == null) return row;
            if (snap != null) row.LastSynced = snap.Synced ?? "";
            if (local == null && remote != null) { row.Kind = "cloud"; row.Text = "Only in cloud"; row.LastSynced = ""; return row; }
            if (local != null && remote == null)
            {
                row.Kind = "local";
                row.Text = snap != null && (!string.IsNullOrEmpty(snap.Rl) || !string.IsNullOrEmpty(snap.Synced))
                    ? "Missing in the cloud" : (snap != null && snap.Pending ? "Waiting to upload" : "New on this PC");
                row.LastSynced = "";
                return row;
            }

            if (snap == null)
            {
                if (local != null && remote != null && SameShape(local, remote))
                {
                    row.Kind = "same";
                    row.Text = "Same size and date";
                    return row;
                }
                row.Kind = "cloud";
                row.Text = "Changed on both sides";
                return row;
            }
            if (snap.Pending && local != null && remote != null && SameShape(local, remote))
            {
                row.Kind = "same";
                row.Text = "In sync";
                return row;
            }
            if (snap.Pending)
            {
                row.Kind = "local";
                row.Text = "Waiting to upload";
                return row;
            }
            if (snap.Hold)
            {
                row.Kind = "cloud";
                row.Text = "Needs review";
                return row;
            }
            bool localSame = local != null && SameSig(local.Length, local.MtimeUtc, snap.Ll, snap.Lm);
            bool remoteSame = remote != null && NearSig(remote.Length, remote.MtimeUtc, snap.Rl, snap.Rm);
            if (localSame && remoteSame) { row.Kind = "same"; row.Text = string.IsNullOrEmpty(row.LastSynced) ? "Contents match" : "In sync"; return row; }
            if (!localSame && remoteSame) { row.Kind = "local"; row.Text = "Changed on this PC"; return row; }
            if (localSame && !remoteSame) { row.Kind = "cloud"; row.Text = "Changed in the cloud"; return row; }
            row.Kind = "cloud";
            row.Text = "Changed on both sides";
            return row;
        }

        public static string FormatWhen(string stamp)
        {
            if (string.IsNullOrEmpty(stamp)) return "";
            DateTime time;
            if (!DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time)) return stamp;
            return time.ToLocalTime().ToString("g");
        }

        public static string FormatSize(long length)
        {
            if (length < 1024) return length + " B";
            double value = length;
            string[] units = new string[] { "KB", "MB", "GB", "TB" };
            int unit = -1;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value = value / 1024;
                unit++;
            }
            return value.ToString("0.#") + " " + units[unit];
        }

        public static Overlap Score(string local, string remote)
        {
            List<FileRecord> localFiles = ListFiles(local);
            if (localFiles.Count == 0) return null;
            List<FileRecord> remoteFiles = ListFiles(remote);
            if (remoteFiles.Count == 0) return null;
            if (remoteFiles.Count > Math.Max((int)(localFiles.Count * 1.5), localFiles.Count + 10)) return null;
            Dictionary<string, long> remoteMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (FileRecord file in remoteFiles) remoteMap[file.Rel] = file.Length;
            int sample = localFiles.Count > 80 ? 80 : localFiles.Count;
            int hits = 0;
            for (int i = 0; i < sample; i++)
            {
                long length;
                if (remoteMap.TryGetValue(localFiles[i].Rel, out length) && length == localFiles[i].Length) hits++;
            }
            if (hits < 1) return null;
            double ratio = (double)hits / sample;
            if (ratio < 0.7) return null;
            Overlap overlap = new Overlap();
            overlap.Hits = hits;
            overlap.Ratio = ratio;
            return overlap;
        }

        public class Overlap
        {
            public int Hits;
            public double Ratio;
        }

        public static string FindRelative(string local, Provider provider)
        {
            if (provider == null || !provider.IsOnline) return null;
            string localFull = Store.Norm(local);
            string replica = Store.Norm(Store.ReplicaRoot(provider));
            if (Store.IsUnder(localFull, replica) && !string.Equals(localFull, replica, StringComparison.OrdinalIgnoreCase))
            {
                string rel = Store.Relative(localFull, replica);
                string remote = Path.Combine(provider.Root, rel ?? "");
                if (Directory.Exists(remote) && Score(localFull, remote) != null) return rel;
            }
            string name = Path.GetFileName(localFull);
            string bestRel = null;
            int bestHits = -1;
            int secondHits = -1;
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            Queue<string[]> queue = new Queue<string[]>();
            queue.Enqueue(new string[] { provider.Root, "", "0" });
            while (queue.Count > 0 && DateTime.UtcNow < deadline)
            {
                string[] item = queue.Dequeue();
                int depth = int.Parse(item[2], CultureInfo.InvariantCulture);
                if (depth > 4) continue;
                DirectoryInfo[] dirs;
                try { dirs = new DirectoryInfo(item[0]).GetDirectories(); }
                catch { continue; }
                foreach (DirectoryInfo dir in dirs)
                {
                    if (SkipName(dir.Name, true)) continue;
                    string rel = string.IsNullOrEmpty(item[1]) ? dir.Name : item[1] + "\\" + dir.Name;
                    if (string.Equals(dir.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        Overlap overlap = Score(localFull, dir.FullName);
                        if (overlap != null)
                        {
                            if (overlap.Hits > bestHits)
                            {
                                secondHits = bestHits;
                                bestHits = overlap.Hits;
                                bestRel = rel;
                            }
                            else if (overlap.Hits > secondHits)
                            {
                                secondHits = overlap.Hits;
                            }
                        }
                    }
                    queue.Enqueue(new string[] { dir.FullName, rel, (depth + 1).ToString(CultureInfo.InvariantCulture) });
                }
            }
            if (bestRel == null) return null;
            if (secondHits >= 0 && secondHits >= (bestHits * 0.6)) return null;
            return bestRel;
        }
    }
}
