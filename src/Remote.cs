using System;
using System.Collections.Generic;
using System.IO;

namespace DriveFolderSync
{
    // The cloud side of a folder pair. Sync decisions are the same for every remote; only listing,
    // copying and comparing differ between a mounted folder and the Google Drive API.
    public interface IRemote
    {
        string Name { get; }

        // Makes the remote root available. Returns false when it is missing and create is false.
        bool Prepare(bool create);

        // Every file under the root. Links and shortcuts are added to links and not followed.
        Dictionary<string, FileRecord> List(List<string> links);
        Dictionary<string, FileRecord> List(List<string> links, SyncProgress progress);

        // Only the listed relative paths that exist.
        Dictionary<string, FileRecord> ListAllowed(List<string> rels);

        // Every folder under the root, as relative paths.
        List<string> Directories(Dictionary<string, bool> links);

        void EnsureDirectories(List<string> rels);
        void EnsureDirectories(List<string> rels, SyncProgress progress);

        FileRecord Upload(string rel, FileRecord local, SyncProgress progress);

        void Download(FileRecord remote, string destination, SyncProgress progress);

        bool Same(FileRecord local, FileRecord remote);

        // Bytes that can still be written to the remote, or -1 when unknown.
        long FreeSpace();
    }

    public sealed class FileSystemRemote : IRemote
    {
        public const long Reserve = 2L * 1024 * 1024 * 1024;
        readonly string root;

        public FileSystemRemote(string root)
        {
            this.root = root;
        }

        public string Name { get { return root; } }
        public string Root { get { return root; } }

        public bool Prepare(bool create)
        {
            if (string.IsNullOrEmpty(root)) return false;
            if (!Directory.Exists(root))
            {
                if (!create) return false;
                Directory.CreateDirectory(root);
            }
            SyncEngine.RejectReparsePath(root);
            return true;
        }

        public Dictionary<string, FileRecord> List(List<string> links)
        {
            return List(links, null);
        }

        public Dictionary<string, FileRecord> List(List<string> links, SyncProgress progress)
        {
            return SyncEngine.Index(SyncEngine.ListFiles(root, links, progress, "Scanning cloud files"));
        }

        public Dictionary<string, FileRecord> ListAllowed(List<string> rels)
        {
            return SyncEngine.IndexAllowed(root, rels);
        }

        public List<string> Directories(Dictionary<string, bool> links)
        {
            return SyncEngine.ListDirectories(root, links);
        }

        public void EnsureDirectories(List<string> rels)
        {
            EnsureDirectories(rels, null);
        }

        public void EnsureDirectories(List<string> rels, SyncProgress progress)
        {
            if (rels == null) return;
            int total = rels.Count;
            DateTime start = DateTime.UtcNow;
            for (int i = 0; i < total; i++)
            {
                if (progress != null)
                {
                    if (progress.Canceled) break;
                    double etaSec = 0;
                    if (i > 0)
                    {
                        double elapsedSec = (DateTime.UtcNow - start).TotalSeconds;
                        etaSec = (elapsedSec / i) * (total - i);
                    }
                    progress.SetCheckStatus("Preparing cloud folders (" + (i + 1) + " of " + total + ")", etaSec > 0 ? (double?)etaSec : null, i + 1, total);
                }
                Directory.CreateDirectory(SyncEngine.LongPath(Path.Combine(root, rels[i])));
            }
        }

        public FileRecord Upload(string rel, FileRecord local, SyncProgress progress)
        {
            string dest = Path.Combine(root, rel);
            SyncEngine.CopyFile(local.Full, dest, progress);
            FileRecord copied = SyncEngine.FromDisk(dest);
            copied.Rel = rel;
            return copied;
        }

        public void Download(FileRecord remote, string destination, SyncProgress progress)
        {
            SyncEngine.CopyFile(remote.Full, destination, progress);
        }

        public bool Same(FileRecord local, FileRecord remote)
        {
            return SyncEngine.SameContents(local, remote);
        }

        public long FreeSpace()
        {
            try
            {
                string volume = Path.GetPathRoot(Path.GetFullPath(root));
                if (string.IsNullOrEmpty(volume) || volume.StartsWith(@"\\", StringComparison.Ordinal)) return -1;
                return Math.Max(0, new DriveInfo(volume).AvailableFreeSpace - Reserve);
            }
            catch (IOException) { return -1; }
            catch (ArgumentException) { return -1; }
        }
    }
}
