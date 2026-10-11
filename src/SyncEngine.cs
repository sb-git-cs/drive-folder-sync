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
                    if (IsRedirect(child.FullName, child.Attributes)) throw new IOException("Sync cannot follow symbolic links or junctions: " + child.FullName);
                    if (isDir)
                    {
                        stack.Push(child.FullName);
                        continue;
                    }
                    FileInfo info = child as FileInfo;
                    if (info == null) continue;
                    string rel = info.FullName;
                    if (rel.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) rel = @"\\" + rel.Substring(8);
                    else if (rel.StartsWith(@"\\?\", StringComparison.Ordinal)) rel = rel.Substring(4);
                    if (rel.Length < rootFull.Length) continue;
                    rel = rel.Substring(rootFull.Length).TrimStart('\\');
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
            Dictionary<string, FileRecord> localFiles = Index(ListFiles(local));
            Dictionary<string, FileRecord> remoteFiles = Index(ListFiles(remote));
            Dictionary<string, Snap> map = new Dictionary<string, Snap>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, bool> rels = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in localFiles.Keys) rels[key] = true;
            foreach (string key in remoteFiles.Keys) rels[key] = true;
            foreach (string rel in rels.Keys)
            {
                FileRecord left = localFiles.ContainsKey(rel) ? localFiles[rel] : null;
                FileRecord right = remoteFiles.ContainsKey(rel) ? remoteFiles[rel] : null;
                if (left == null || right == null) continue;
                bool hold = !SameContents(left, right);
                map[rel] = NewEntry(left, right, false, hold);
            }
            Store.SaveState(statePath, map);
        }

        public static void CopyFile(string source, string destination)
        {
            string parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                Directory.CreateDirectory(LongPath(parent));
            File.Copy(LongPath(source), LongPath(destination), true);
        }

        public static void DeleteFile(string target)
        {
            string path = LongPath(target);
            if (File.Exists(path)) File.Delete(path);
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

        static FileRecord FromDisk(string path)
        {
            FileInfo info = new FileInfo(LongPath(path));
            FileRecord record = new FileRecord();
            record.Full = path;
            record.Length = info.Length;
            record.MtimeUtc = info.LastWriteTimeUtc;
            return record;
        }

        public static int MirrorDirectories(string cloud, string local)
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
                    if (IsRedirect(dir.FullName, dir.Attributes)) throw new IOException("Sync cannot follow symbolic links or junctions: " + dir.FullName);
                    string child = string.IsNullOrEmpty(rel) ? dir.Name : rel + "\\" + dir.Name;
                    stack.Push(child);
                }
            }
            Store.Log("mirrored folders into " + local + " (" + made + " new)");
            return made;
        }

        public static void CopyInto(string source, string destRoot, string relative, List<string> dirs, List<string> files)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(destRoot) || string.IsNullOrEmpty(relative)) return;
            if (dirs == null) dirs = new List<string>();
            if (files == null) files = new List<string>();
            Directory.CreateDirectory(destRoot);
            string dest = Path.GetFullPath(Path.Combine(destRoot, relative));
            if (!Store.IsUnder(dest, destRoot)) throw new IOException("The copy must stay inside the selected folder.");
            RejectReparsePath(source);
            RejectReparsePath(dest);
            if (File.Exists(source))
            {
                if (string.Equals(Store.Norm(source), Store.Norm(dest), StringComparison.OrdinalIgnoreCase)) return;
                CopyFile(source, dest);
                files.Add(relative);
                return;
            }
            if (!Directory.Exists(source)) return;
            if (Store.IsUnder(dest, source)) return;
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
                    if (IsRedirect(child.FullName, child.Attributes)) throw new IOException("Copy cannot follow symbolic links or junctions: " + child.FullName);
                    string childSub = string.IsNullOrEmpty(sub) ? child.Name : sub + "\\" + child.Name;
                    if (isDir) pending.Push(childSub);
                    else
                    {
                        string childRel = relative + "\\" + childSub;
                        CopyFile(child.FullName, Path.Combine(destRoot, childRel));
                        files.Add(childRel);
                    }
                }
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
            SyncStats stats = new SyncStats();
            if (direction == "Pull" && (string.IsNullOrEmpty(remote) || !Directory.Exists(remote)))
            {
                Store.Log("skip pull, cloud folder missing: " + remote);
                stats.Errors++;
                return stats;
            }
            if (direction == "Push" && !Directory.Exists(local ?? ""))
            {
                Store.Log("skip push, local folder missing: " + local);
                stats.Errors++;
                return stats;
            }
            if (!Directory.Exists(local)) Directory.CreateDirectory(local);
            RejectReparsePath(local);
            RejectReparsePath(remote);
            if (onlyFiles == null)
            {
                if (!Directory.Exists(remote)) Directory.CreateDirectory(remote);
            }
            else if (!Directory.Exists(remote))
            {
                Store.Log("skip, cloud folder missing: " + remote);
                stats.Errors++;
                return stats;
            }

            Dictionary<string, FileRecord> localFiles;
            Dictionary<string, FileRecord> remoteFiles;
            if (onlyFiles != null)
            {
                localFiles = IndexAllowed(local, onlyFiles);
                remoteFiles = IndexAllowed(remote, onlyFiles);
            }
            else
            {
                localFiles = Index(ListFiles(local));
                remoteFiles = Index(ListFiles(remote));
            }
            Dictionary<string, Snap> state = Store.ReadState(statePath);
            if (onlyFiles != null) DropKeys(state, AllowSet(onlyFiles));
            else if (direction == "Push") MirrorDirectories(local, remote);
            else MirrorDirectories(remote, local);

            if (direction == "Push")
            {
                List<string> localKeys = new List<string>(localFiles.Keys);
                foreach (string rel in localKeys)
                {
                    FileRecord localFile = localFiles[rel];
                    FileRecord remoteFile = remoteFiles.ContainsKey(rel) ? remoteFiles[rel] : null;
                    Snap snap = state.ContainsKey(rel) ? state[rel] : null;
                    if (snap != null && snap.Hold && remoteFile != null && SameContents(localFile, remoteFile))
                    {
                        state[rel] = SyncedEntry(localFile, remoteFile);
                        continue;
                    }
                    bool localSame = snap != null && SameSig(localFile.Length, localFile.MtimeUtc, snap.Ll, snap.Lm);
                    bool pending = snap != null && snap.Pending;
                    if (pending && remoteFile != null && SameContents(localFile, remoteFile))
                    {
                        state[rel] = SyncedEntry(localFile, remoteFile);
                        continue;
                    }
                    if (snap == null && remoteFile != null)
                    {
                        bool same = SameContents(localFile, remoteFile);
                        state[rel] = same ? SyncedEntry(localFile, remoteFile) : NewEntry(localFile, remoteFile, false, true);
                        if (!same) stats.Conflict++;
                        continue;
                    }
                    if (localSame && !pending)
                    {
                        if (snap.Hold) stats.Conflict++;
                        else if (remoteFile != null && string.IsNullOrEmpty(snap.Synced) && SameContents(localFile, remoteFile))
                            state[rel] = SyncedEntry(localFile, remoteFile);
                        continue;
                    }
                    bool remoteMoved = snap != null && remoteFile != null && !SameSig(remoteFile.Length, remoteFile.MtimeUtc, snap.Rl, snap.Rm);
                    if (remoteMoved && !pending)
                    {
                        string conflictRel = ConflictRel(rel, "local");
                        try
                        {
                            CopyFile(localFile.Full, Path.Combine(remote, conflictRel));
                            state[rel] = NewEntry(localFile, remoteFile, false, true);
                            stats.Conflict++;
                            Store.Log("conflict kept newer cloud file, saved local copy: " + conflictRel);
                        }
                        catch (Exception ex)
                        {
                            stats.Errors++;
                            Store.Log("conflict copy failed " + rel + ": " + ex.Message);
                        }
                        continue;
                    }
                    try
                    {
                        string dest = Path.Combine(remote, rel);
                        CopyFile(localFile.Full, dest);
                        FileRecord remoteNow = FromDisk(dest);
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
                    }
                    catch (Exception ex)
                    {
                        stats.Errors++;
                        Store.Log("upload failed " + rel + ": " + ex.Message);
                    }
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
                    if (snap != null && snap.Hold && localFile != null && SameContents(localFile, remoteFile))
                    {
                        state[rel] = SyncedEntry(localFile, remoteFile);
                        continue;
                    }
                    if (snap != null && snap.Pending && localFile != null)
                    {
                        bool driveUnchanged = !string.IsNullOrEmpty(snap.Rl) && SameSig(remoteFile.Length, remoteFile.MtimeUtc, snap.Rl, snap.Rm);
                        if (SameContents(localFile, remoteFile))
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
                            try
                            {
                                string conflictRel = ConflictRel(rel, "drive");
                                CopyFile(remoteFile.Full, Path.Combine(local, conflictRel));
                                state[rel] = NewEntry(localFile, remoteFile, true, false);
                                stats.Conflict++;
                                Store.Log("saved cloud conflict " + conflictRel);
                            }
                            catch (Exception ex)
                            {
                                stats.Errors++;
                                Store.Log("conflict save failed " + rel + ": " + ex.Message);
                            }
                        }
                        continue;
                    }
                    if (snap == null && localFile != null)
                    {
                        bool same = SameContents(localFile, remoteFile);
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
                        try
                        {
                            string conflictRel = ConflictRel(rel, "drive");
                            CopyFile(remoteFile.Full, Path.Combine(local, conflictRel));
                            state[rel] = NewEntry(localFile, remoteFile, false, true);
                            stats.Conflict++;
                            Store.Log("kept local edits, saved cloud copy " + conflictRel);
                        }
                        catch (Exception ex)
                        {
                            stats.Errors++;
                            Store.Log("conflict save failed " + rel + ": " + ex.Message);
                        }
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
                    try
                    {
                        string dest = Path.Combine(local, rel);
                        string volume = Path.GetPathRoot(dest);
                        DriveInfo disk = new DriveInfo(volume);
                        if ((disk.AvailableFreeSpace - remoteFile.Length) < FreeReserve)
                        {
                            stats.Errors++;
                            Store.Log("skipped download, low disk space: " + rel);
                            continue;
                        }
                        CopyFile(remoteFile.Full, dest);
                        FileRecord localNow = FromDisk(dest);
                        state[rel] = SyncedEntry(localNow, remoteFile);
                        stats.Down++;
                        Store.Log("downloaded " + rel);
                    }
                    catch (Exception ex)
                    {
                        stats.Errors++;
                        Store.Log("download failed " + rel + ": " + ex.Message);
                    }
                }
                List<string> stateKeys = new List<string>(state.Keys);
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
                    bool localSame = SameSig(localFile.Length, localFile.MtimeUtc, snap.Ll, snap.Lm);
                    if (!localSame)
                    {
                        Snap entry = NewEntry(localFile, null, true, false);
                        entry.Rl = snap.Rl;
                        entry.Rm = snap.Rm;
                        state[rel] = entry;
                        continue;
                    }
                    try
                    {
                        RejectReparsePath(Path.GetDirectoryName(Path.Combine(remote, rel)));
                        DeleteFile(localFile.Full);
                        state.Remove(rel);
                        stats.Del++;
                        Store.Log("deleted local " + rel);
                    }
                    catch (Exception ex)
                    {
                        stats.Errors++;
                        Store.Log("local delete failed " + rel + ": " + ex.Message);
                    }
                }
            }

            Store.SaveState(statePath, state);
            return stats;
        }

        public static SyncStats SyncLink(LinkFolder folder, string direction, bool cloudReadbackIsTruth)
        {
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
            SyncStats stats = SyncFolder(direction, folder.Local, remote, Store.StatePath(folder.Id), cloudReadbackIsTruth, onlyFiles);
            if (stats.Errors == 0 && stats.Conflict == 0) folder.LastSync = Stamp(DateTime.UtcNow);
            folder.LastErrors = stats.Errors;
            Store.Log(direction + " " + folder.Local + ": uploaded=" + stats.Up + " downloaded=" + stats.Down
                + " deleted=" + stats.Del + " conflicts=" + stats.Conflict + " errors=" + stats.Errors);
            return stats;
        }

        public static SyncStats SyncAll(string direction)
        {
            SyncStats total = new SyncStats();
            using (LockState(120))
            {
                List<LinkFolder> links = Store.ReadLinks();
                foreach (LinkFolder folder in links)
                {
                    try
                    {
                        SyncStats stats = SyncLink(folder, direction, false);
                        total.Up += stats.Up;
                        total.Down += stats.Down;
                        total.Del += stats.Del;
                        total.Conflict += stats.Conflict;
                        total.Errors += stats.Errors;
                    }
                    catch (Exception ex)
                    {
                        total.Errors++;
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

        static Dictionary<string, FileRecord> IndexAllowed(string root, List<string> onlyFiles)
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
                string remoteRoot = Store.ResolveRemote(folder, Store.DetectProviders());
                if (files != null)
                {
                    foreach (string rel in files)
                    {
                        if (string.IsNullOrEmpty(rel)) continue;
                        string localFile = Path.Combine(localRoot, rel);
                        string remoteFile = string.IsNullOrEmpty(remoteRoot) ? null : Path.Combine(remoteRoot, rel);
                        FileRecord left = File.Exists(localFile) ? FromDisk(localFile) : null;
                        FileRecord right = !string.IsNullOrEmpty(remoteFile) && File.Exists(remoteFile) ? FromDisk(remoteFile) : null;
                        if (left == null && right == null) continue;
                        state[rel] = NewEntry(left, right, false, left != null && right != null && !SameContents(left, right));
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
            if (Store.IsUnder(local, remote) || Store.IsUnder(remote, local))
                throw new ArgumentException("Local and cloud folders must be separate and cannot contain each other.");
            foreach (Provider drive in Store.DetectProviders())
                if (Store.IsUnder(local, drive.Root)) throw new ArgumentException("Choose a local folder outside your cloud drives.");
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

        static void RejectReparsePath(string path)
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
                Store.SaveCustomProvider(Store.FindProvider(Store.DetectProviders(), providerId));
                if (!Directory.Exists(local)) Directory.CreateDirectory(local);
                if (Directory.Exists(remote)) Initialize(local, remote, Store.StatePath(folder.Id));
                else Store.SaveState(Store.StatePath(folder.Id), new Dictionary<string, Snap>(StringComparer.OrdinalIgnoreCase));
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

        public static RowStatus Classify(FileRecord local, FileRecord remote, Snap snap)
        {
            RowStatus row = new RowStatus();
            row.Kind = "none";
            row.Text = "Not linked";
            row.LastSynced = "";
            if (snap == null && local == null && remote == null) return row;
            if (snap != null) row.LastSynced = snap.Synced ?? "";
            if (local == null && remote != null) { row.Kind = "cloud"; row.Text = "Only in cloud"; row.LastSynced = ""; return row; }

            if (snap == null)
            {
                if (local != null && remote == null) { row.Kind = "local"; row.Text = "New on this PC"; return row; }
                if (local == null && remote != null) { row.Kind = "cloud"; row.Text = "New in the cloud"; return row; }
                if (local != null && remote != null && SameContents(local, remote))
                {
                    row.Kind = "same";
                    row.Text = "Contents match";
                    return row;
                }
                row.Kind = "cloud";
                row.Text = "Changed on both sides";
                return row;
            }
            if (snap.Pending && local != null && remote != null && SameContents(local, remote))
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
            bool remoteSame = remote != null && SameSig(remote.Length, remote.MtimeUtc, snap.Rl, snap.Rm);
            bool localMissing = local == null && !string.IsNullOrEmpty(snap.Ll);
            bool remoteMissing = remote == null && !string.IsNullOrEmpty(snap.Rl);
            if (localMissing && remoteMissing) { row.Kind = "same"; row.Text = "In sync"; return row; }
            if (localMissing) { row.Kind = "cloud"; row.Text = "Missing on this PC"; return row; }
            if (remoteMissing) { row.Kind = "local"; row.Text = "Missing in the cloud"; return row; }
            if (local != null && remote == null) { row.Kind = "local"; row.Text = localSame ? "In sync" : "Changed on this PC"; if (localSame) row.Kind = "same"; return row; }
            if (local == null && remote != null) { row.Kind = "cloud"; row.Text = remoteSame ? "In sync" : "Changed in the cloud"; if (remoteSame) row.Kind = "same"; return row; }
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
