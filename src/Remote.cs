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

        // Only the listed relative paths that exist.
        Dictionary<string, FileRecord> ListAllowed(List<string> rels);

        // Every folder under the root, as relative paths.
        List<string> Directories(Dictionary<string, bool> links);

        void EnsureDirectories(List<string> rels);

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
            return SyncEngine.Index(SyncEngine.ListFiles(root, links));
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
            foreach (string rel in rels) Directory.CreateDirectory(SyncEngine.LongPath(Path.Combine(root, rel)));
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
