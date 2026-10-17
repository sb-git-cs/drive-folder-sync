using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace DriveFolderSync
{
    // An in-memory stand-in for the parts of the Google Drive API the backup uses, so /selftest can
    // check uploads, downloads, retries and checksums without a Google account.
    public sealed class FakeDrive : IDisposable
    {
        public sealed class Item
        {
            public string Id;
            public string Name;
            public string MimeType;
            public string Parent;
            public byte[] Data;
            public DateTime Modified;
        }

        sealed class Session
        {
            public string FileId;
            public Dictionary<string, object> Meta;
            public long Total;
            public MemoryStream Received = new MemoryStream();
        }

        public const string RootId = "root-folder";
        public readonly Dictionary<string, Item> Items = new Dictionary<string, Item>();
        readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        readonly TcpListener listener;
        readonly Thread worker;
        readonly object gate = new object();
        int nextId;
        volatile bool stopping;
        public int PageSize = 3;
        public long QuotaLimit = -1;
        public int FailListOnce;
        public int FailChunkOnce;
        public bool CorruptChecksum;
        public int Requests;

        public FakeDrive()
        {
            Items[RootId] = new Item { Id = RootId, Name = "My Drive", MimeType = GoogleDriveClient.FolderType, Modified = DateTime.UtcNow };
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            worker = new Thread(Serve);
            worker.IsBackground = true;
            worker.Start();
        }

        public string BaseUrl { get { return "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture); } }

        public void Dispose()
        {
            stopping = true;
            listener.Stop();
        }

        public Item Add(string parent, string name, string mime, byte[] data, DateTime modified)
        {
            lock (gate)
            {
                Item item = new Item { Id = "id" + (++nextId).ToString(CultureInfo.InvariantCulture), Name = name, MimeType = mime, Parent = parent, Data = data, Modified = modified };
                Items[item.Id] = item;
                return item;
            }
        }

        public Item Find(string path)
        {
            lock (gate)
            {
                string parent = RootId;
                Item found = null;
                foreach (string part in path.Split('\\'))
                {
                    found = null;
                    foreach (Item item in Items.Values) if (item.Parent == parent && item.Name == part) found = item;
                    if (found == null) return null;
                    parent = found.Id;
                }
                return found;
            }
        }

        public int Count(string parent, string name)
        {
            lock (gate)
            {
                int count = 0;
                foreach (Item item in Items.Values) if (item.Parent == parent && item.Name == name) count++;
                return count;
            }
        }

        static string Md5(byte[] data)
        {
            using (MD5 md5 = MD5.Create()) return BitConverter.ToString(md5.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        Dictionary<string, object> Resource(Item item)
        {
            Dictionary<string, object> map = new Dictionary<string, object>();
            map["id"] = item.Id;
            map["name"] = item.Name;
            map["mimeType"] = item.MimeType;
            map["parents"] = new string[] { item.Parent };
            map["modifiedTime"] = GoogleDriveClient.Rfc3339(item.Modified);
            if (item.Data != null && !item.MimeType.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal))
            {
                map["size"] = item.Data.Length.ToString(CultureInfo.InvariantCulture);
                map["md5Checksum"] = CorruptChecksum ? new string('0', 32) : Md5(item.Data);
            }
            return map;
        }

        void Serve()
        {
            while (!stopping)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (SocketException) { return; }
                catch (ObjectDisposedException) { return; }
                ThreadPool.QueueUserWorkItem(delegate
                {
                    using (client)
                    {
                        try { Handle(client.GetStream()); }
                        catch (IOException) { }
                        catch (SocketException) { }
                    }
                });
            }
        }

        void Handle(NetworkStream stream)
        {
            stream.ReadTimeout = 10000;
            string head = GoogleAuth.ReadHead(stream);
            string[] lines = head.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return;
            string method = first[0];
            string target = first[1];
            Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0) headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }
            byte[] body = new byte[0];
            string lengthText;
            if (headers.TryGetValue("Content-Length", out lengthText))
            {
                body = new byte[int.Parse(lengthText, CultureInfo.InvariantCulture)];
                int read = 0;
                while (read < body.Length)
                {
                    int got = stream.Read(body, read, body.Length - read);
                    if (got <= 0) break;
                    read += got;
                }
            }
            string path = target.Contains("?") ? target.Substring(0, target.IndexOf('?')) : target;
            Dictionary<string, string> query = GoogleAuth.ParseQuery(target.Contains("?") ? target.Substring(target.IndexOf('?') + 1) : "");
            int status = 200;
            Dictionary<string, string> extra = new Dictionary<string, string>();
            byte[] reply;
            lock (gate)
            {
                Requests++;
                reply = Route(method, path, query, headers, body, ref status, extra);
            }
            StringBuilder text = new StringBuilder();
            text.Append("HTTP/1.1 " + status + " X\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: " + reply.Length + "\r\n");
            foreach (KeyValuePair<string, string> pair in extra) text.Append(pair.Key + ": " + pair.Value + "\r\n");
            text.Append("\r\n");
            byte[] start = Encoding.ASCII.GetBytes(text.ToString());
            stream.Write(start, 0, start.Length);
            stream.Write(reply, 0, reply.Length);
        }

        static byte[] Json(object value)
        {
            return Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(value));
        }

        static byte[] Error(int code, string message, ref int status)
        {
            status = code;
            Dictionary<string, object> error = new Dictionary<string, object>();
            error["code"] = code;
            error["message"] = message;
            Dictionary<string, object> wrapper = new Dictionary<string, object>();
            wrapper["error"] = error;
            return Json(wrapper);
        }

        byte[] Route(string method, string path, Dictionary<string, string> query, Dictionary<string, string> headers, byte[] body, ref int status, Dictionary<string, string> extra)
        {
            if (method == "GET" && path == "/drive/v3/about")
            {
                Dictionary<string, object> about = new Dictionary<string, object>();
                about["user"] = new Dictionary<string, object> { { "emailAddress", "test@example.com" } };
                Dictionary<string, object> quota = new Dictionary<string, object>();
                long used = 0;
                foreach (Item item in Items.Values) if (item.Data != null) used += item.Data.Length;
                if (QuotaLimit >= 0) quota["limit"] = QuotaLimit.ToString(CultureInfo.InvariantCulture);
                quota["usage"] = used.ToString(CultureInfo.InvariantCulture);
                about["storageQuota"] = quota;
                return Json(about);
            }
            if (method == "GET" && path == "/drive/v3/files/root") return Json(new Dictionary<string, object> { { "id", RootId } });
            if (method == "GET" && path == "/drive/v3/files")
            {
                if (FailListOnce > 0) { FailListOnce--; return Error(503, "Backend Error", ref status); }
                return List(query);
            }
            if (method == "POST" && path == "/drive/v3/files")
            {
                Dictionary<string, object> meta = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(body));
                Item folder = Add(FirstParent(meta), Store.GetString(meta, "name"), Store.GetString(meta, "mimeType"), null, DateTime.UtcNow);
                return Json(new Dictionary<string, object> { { "id", folder.Id } });
            }
            if (method == "GET" && path.StartsWith("/drive/v3/files/", StringComparison.Ordinal) && query.ContainsKey("alt"))
            {
                Item item;
                if (!Items.TryGetValue(path.Substring("/drive/v3/files/".Length), out item)) return Error(404, "File not found", ref status);
                return item.Data;
            }
            if (method == "DELETE" && path.StartsWith("/drive/v3/files/", StringComparison.Ordinal))
            {
                string id = path.Substring("/drive/v3/files/".Length);
                Items.Remove(id);
                status = 204;
                return new byte[0];
            }
            if ((method == "POST" || method == "PATCH") && path.StartsWith("/upload/drive/v3/files", StringComparison.Ordinal))
            {
                string fileId = path.Length > "/upload/drive/v3/files/".Length ? path.Substring("/upload/drive/v3/files/".Length) : null;
                if (query["uploadType"] == "multipart") return Multipart(fileId, headers, body);
                Session session = new Session();
                session.FileId = fileId;
                session.Meta = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(body));
                session.Total = long.Parse(headers["X-Upload-Content-Length"], CultureInfo.InvariantCulture);
                string sid = "s" + (++nextId).ToString(CultureInfo.InvariantCulture);
                sessions[sid] = session;
                extra["Location"] = BaseUrl + "/upload/session/" + sid;
                return new byte[0];
            }
            if (method == "PUT" && path.StartsWith("/upload/session/", StringComparison.Ordinal))
            {
                Session session;
                if (!sessions.TryGetValue(path.Substring("/upload/session/".Length), out session)) return Error(404, "No session", ref status);
                string range = headers["Content-Range"];
                if (!range.StartsWith("bytes */", StringComparison.Ordinal))
                {
                    if (FailChunkOnce > 0) { FailChunkOnce--; return Error(503, "Backend Error", ref status); }
                    long from = long.Parse(range.Substring(6, range.IndexOf('-') - 6), CultureInfo.InvariantCulture);
                    if (from > session.Received.Length) return Error(400, "Gap in upload", ref status);
                    session.Received.SetLength(from);
                    session.Received.Position = from;
                    session.Received.Write(body, 0, body.Length);
                }
                if (session.Received.Length < session.Total)
                {
                    status = 308;
                    if (session.Received.Length > 0) extra["Range"] = "bytes=0-" + (session.Received.Length - 1).ToString(CultureInfo.InvariantCulture);
                    return new byte[0];
                }
                return Json(Resource(Save(session.FileId, session.Meta, session.Received.ToArray())));
            }
            return Error(404, "Unknown request " + method + " " + path, ref status);
        }

        static string FirstParent(Dictionary<string, object> meta)
        {
            foreach (object parent in Store.AsEnumerable(meta.ContainsKey("parents") ? meta["parents"] : null)) return parent as string;
            return null;
        }

        Item Save(string fileId, Dictionary<string, object> meta, byte[] data)
        {
            DateTime modified = meta.ContainsKey("modifiedTime") ? GoogleDriveClient.ParseTime(Store.GetString(meta, "modifiedTime")) : DateTime.UtcNow;
            Item item;
            if (fileId != null && Items.TryGetValue(fileId, out item))
            {
                item.Data = data;
                item.Modified = modified;
                return item;
            }
            return Add(FirstParent(meta), Store.GetString(meta, "name"), "application/octet-stream", data, modified);
        }

        byte[] Multipart(string fileId, Dictionary<string, string> headers, byte[] body)
        {
            string type = headers["Content-Type"];
            string boundary = "--" + type.Substring(type.IndexOf("boundary=", StringComparison.Ordinal) + 9);
            string text = Encoding.GetEncoding(28591).GetString(body);
            int metaStart = text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
            int metaEnd = text.IndexOf("\r\n" + boundary, metaStart, StringComparison.Ordinal);
            string meta = metaEnd > metaStart ? Encoding.UTF8.GetString(body, metaStart, metaEnd - metaStart) : "";
            int dataStart = text.IndexOf("\r\n\r\n", metaEnd, StringComparison.Ordinal) + 4;
            int dataEnd = text.LastIndexOf("\r\n" + boundary + "--", StringComparison.Ordinal);
            byte[] data = new byte[dataEnd - dataStart];
            Array.Copy(body, dataStart, data, 0, data.Length);
            return Json(Resource(Save(fileId, new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(meta), data)));
        }

        static string Unquote(string value)
        {
            return value.Replace("\\'", "'").Replace("\\\\", "\\");
        }

        byte[] List(Dictionary<string, string> query)
        {
            string q = query["q"];
            List<string> parents = new List<string>();
            foreach (Match match in Regex.Matches(q, @"'([^'\\]*)' in parents")) parents.Add(match.Groups[1].Value);
            Match name = Regex.Match(q, @"name = '((?:\\.|[^'\\])*)'");
            bool foldersOnly = q.Contains("mimeType = '" + GoogleDriveClient.FolderType + "'");
            List<Item> matches = new List<Item>();
            foreach (Item item in Items.Values)
            {
                if (item.Id == RootId || !parents.Contains(item.Parent)) continue;
                if (name.Success && item.Name != Unquote(name.Groups[1].Value)) continue;
                if (foldersOnly && item.MimeType != GoogleDriveClient.FolderType) continue;
                matches.Add(item);
            }
            matches.Sort(delegate(Item a, Item b) { return string.CompareOrdinal(a.Id, b.Id); });
            int offset = query.ContainsKey("pageToken") ? int.Parse(query["pageToken"], CultureInfo.InvariantCulture) : 0;
            List<object> page = new List<object>();
            for (int i = offset; i < matches.Count && i < offset + PageSize; i++) page.Add(Resource(matches[i]));
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["files"] = page;
            if (offset + PageSize < matches.Count) result["nextPageToken"] = (offset + PageSize).ToString(CultureInfo.InvariantCulture);
            return Json(result);
        }
    }

    public static class GoogleSelfTest
    {
        static void Check(bool condition, string failure)
        {
            if (condition) return;
            string detail = "";
            try
            {
                foreach (string line in File.ReadAllLines(Store.LogFile))
                    if (line.Contains("failed") || line.Contains("error")) detail = line;
            }
            catch (IOException) { }
            throw new Exception(failure + (detail.Length == 0 ? "" : " (last log failure: " + detail + ")"));
        }

        static byte[] Bytes(int length, int seed)
        {
            byte[] data = new byte[length];
            new Random(seed).NextBytes(data);
            return data;
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        public static void Run(string root)
        {
            int simple = GoogleDriveClient.SimpleLimit;
            int chunk = GoogleDriveClient.ChunkSize;
            int delay = GoogleDriveClient.RetryDelayMs;
            GoogleDriveClient.SimpleLimit = 64 * 1024;
            GoogleDriveClient.ChunkSize = 256 * 1024;
            GoogleDriveClient.RetryDelayMs = 5;
            try
            {
                using (FakeDrive drive = new FakeDrive())
                    Exercise(drive, root);
            }
            finally
            {
                GoogleDriveClient.SimpleLimit = simple;
                GoogleDriveClient.ChunkSize = chunk;
                GoogleDriveClient.RetryDelayMs = delay;
            }
        }

        static void Exercise(FakeDrive drive, string root)
        {
            Check(GoogleAuth.ParseQuery("code=4%2F0A&state=x+y")["code"] == "4/0A", "sign-in reply was not decoded");
            int refreshes = 0;
            GoogleDriveClient client = new GoogleDriveClient(drive.BaseUrl, delegate(bool force) { if (force) refreshes++; return "test-token"; });
            Check(client.AccountEmail() == "test@example.com", "account email was not read");

            string local = Path.Combine(root, "local");
            Directory.CreateDirectory(Path.Combine(local, "sub", "deep"));
            byte[] small = Encoding.UTF8.GetBytes("small file");
            byte[] big = Bytes(700 * 1024 + 13, 1);
            File.WriteAllBytes(Path.Combine(local, "a.txt"), small);
            File.WriteAllBytes(Path.Combine(local, "sub", "big.bin"), big);
            File.WriteAllText(Path.Combine(local, "sub", "deep", "c.txt"), "deep");
            File.WriteAllText(Path.Combine(local, "Bob's notes.txt"), "quote");
            File.WriteAllBytes(Path.Combine(local, "empty.txt"), new byte[0]);
            // A path longer than 260 characters must upload and download like any other.
            string longDir = local;
            while (longDir.Length < 280) longDir = Path.Combine(longDir, "a-long-folder-name-for-testing");
            Directory.CreateDirectory(SyncEngine.LongPath(longDir));
            string longFile = Path.Combine(longDir, "long.txt");
            File.WriteAllText(SyncEngine.LongPath(longFile), "long path");
            string state = Path.Combine(root, "state.json");

            GoogleRemote remote = new GoogleRemote(client, "test@example.com", @"Backups\project");
            drive.FailListOnce = 1;
            drive.FailChunkOnce = 1;
            SyncProgress progress = new SyncProgress("Push");
            SyncStats pushed = SyncEngine.SyncFolder("Push", local, remote, state, true, null, progress);
            Check(pushed.Up == 6 && pushed.Errors == 0, "first Google backup uploaded " + pushed.Up + " files with " + pushed.Errors + " errors");
            Check(progress.DoneFiles == 6 && progress.DoneBytes == progress.TotalBytes, "Google upload progress did not add up");
            FakeDrive.Item uploaded = drive.Find(@"Backups\project\sub\big.bin");
            Check(uploaded != null && Same(uploaded.Data, big), "resumable upload did not store the file exactly");
            Check(drive.Find(@"Backups\project\Bob's notes.txt") != null, "a name with an apostrophe was not uploaded");
            Check(drive.Find(@"Backups\project\empty.txt") != null && drive.Find(@"Backups\project\empty.txt").Data.Length == 0, "an empty file was not uploaded");
            Check(Math.Abs((uploaded.Modified - new FileInfo(Path.Combine(local, "sub", "big.bin")).LastWriteTimeUtc).TotalMilliseconds) < 1, "upload did not keep the modified time");
            Check(drive.FailListOnce == 0 && drive.FailChunkOnce == 0, "the injected server errors were not retried");
            string longRel = longFile.Substring(local.Length + 1);
            Check(drive.Find(@"Backups\project\" + longRel) != null, "a long path was not uploaded");

            SyncStats again = SyncEngine.SyncFolder("Push", local, new GoogleRemote(client, "test@example.com", @"Backups\project"), state, true, null, null);
            Check(again.Up == 0 && again.Errors == 0, "an unchanged folder uploaded again");

            File.WriteAllText(Path.Combine(local, "a.txt"), "small file, edited");
            File.SetLastWriteTimeUtc(Path.Combine(local, "a.txt"), DateTime.UtcNow.AddMinutes(1));
            SyncStats edited = SyncEngine.SyncFolder("Push", local, new GoogleRemote(client, "test@example.com", @"Backups\project"), state, true, null, null);
            FakeDrive.Item project = drive.Find(@"Backups\project");
            Check(edited.Up == 1 && drive.Count(project.Id, "a.txt") == 1, "an edited file was not replaced in place");
            Check(Encoding.UTF8.GetString(drive.Find(@"Backups\project\a.txt").Data) == "small file, edited", "the edit did not reach the cloud");

            FakeDrive.Item deep = drive.Find(@"Backups\project\sub\deep\c.txt");
            deep.Data = Encoding.UTF8.GetBytes("changed in the cloud");
            deep.Modified = DateTime.UtcNow.AddMinutes(2);
            drive.Add(project.Id, "new.txt", "application/octet-stream", Bytes(300 * 1024, 2), DateTime.UtcNow.AddMinutes(-5));
            drive.Add(project.Id, "Plan", "application/vnd.google-apps.document", null, DateTime.UtcNow);
            drive.Add(project.Id, "Shortcut", GoogleDriveClient.ShortcutType, null, DateTime.UtcNow);
            FakeDrive.Item gone = drive.Find(@"Backups\project\Bob's notes.txt");
            drive.Items.Remove(gone.Id);
            SyncProgress pulling = new SyncProgress("Pull");
            SyncStats pulled = SyncEngine.SyncFolder("Pull", local, new GoogleRemote(client, "test@example.com", @"Backups\project"), state, true, null, pulling);
            Check(pulled.Down == 2 && pulled.Errors == 0, "refresh downloaded " + pulled.Down + " files with " + pulled.Errors + " errors");
            Check(File.ReadAllText(Path.Combine(local, "sub", "deep", "c.txt")) == "changed in the cloud", "a cloud change was not downloaded");
            Check(Math.Abs((File.GetLastWriteTimeUtc(Path.Combine(local, "sub", "deep", "c.txt")) - deep.Modified).TotalMilliseconds) < 1, "download did not keep the cloud time");
            Check(Same(File.ReadAllBytes(Path.Combine(local, "new.txt")), drive.Find(@"Backups\project\new.txt").Data), "a new cloud file was not downloaded exactly");
            Check(File.Exists(Path.Combine(local, "Bob's notes.txt")), "a file deleted in the cloud was deleted on this PC");
            Check(!File.Exists(Path.Combine(local, "Plan")) && !Directory.Exists(Path.Combine(local, "Shortcut")) && pulled.Links == 1, "Google Docs files or shortcuts were downloaded");
            Check(Directory.GetFiles(local, "*.dfs-partial.tmp", SearchOption.AllDirectories).Length == 0, "a temporary download file was left behind");

            File.WriteAllText(Path.Combine(local, "checked.txt"), "checksum");
            drive.CorruptChecksum = true;
            SyncStats corrupt = SyncEngine.SyncFolder("Push", local, new GoogleRemote(client, "test@example.com", @"Backups\project"), state, true, null, null);
            drive.CorruptChecksum = false;
            Check(corrupt.Errors == 1 && corrupt.Up == 0, "an upload with a wrong checksum was accepted");

            File.WriteAllText(Path.Combine(local, "quota.txt"), "needs space");
            drive.QuotaLimit = 1;
            bool full = false;
            try { SyncEngine.SyncFolder("Push", local, new GoogleRemote(client, "test@example.com", @"Backups\project"), state, true, null, null); }
            catch (IOException ex) { full = ex.Message.StartsWith("Not enough space", StringComparison.Ordinal); }
            drive.QuotaLimit = -1;
            Check(full, "a backup larger than the remaining quota started");

            SyncProgress stopped = new SyncProgress("Push");
            stopped.Cancel();
            SyncStats canceled = SyncEngine.SyncFolder("Push", local, new GoogleRemote(client, "test@example.com", @"Backups\project"), state, true, null, stopped);
            Check(canceled.Canceled && canceled.Up == 0 && canceled.Errors == 0, "a stopped backup still uploaded");

            string other = Path.Combine(root, "initial");
            Directory.CreateDirectory(other);
            File.WriteAllBytes(Path.Combine(other, "a.txt"), Encoding.UTF8.GetBytes("small file, edited"));
            File.WriteAllText(Path.Combine(other, "new.txt"), "different");
            string initialState = Path.Combine(root, "initial.json");
            SyncEngine.Initialize(other, new GoogleRemote(client, "test@example.com", @"Backups\project"), initialState);
            Dictionary<string, Snap> initial = Store.ReadState(initialState);
            Check(initial.ContainsKey("a.txt") && !initial["a.txt"].Hold, "matching contents were flagged for review");
            Check(initial.ContainsKey("new.txt") && initial["new.txt"].Hold, "different contents were not flagged for review");
            string dropped = Path.Combine(root, "drop");
            Directory.CreateDirectory(Path.Combine(dropped, "inner"));
            File.WriteAllText(Path.Combine(dropped, "inner", "x.txt"), "dropped");
            List<string> movedDirs = new List<string>();
            List<string> movedFiles = new List<string>();
            List<KeyValuePair<string, string>> items = new List<KeyValuePair<string, string>>();
            items.Add(new KeyValuePair<string, string>(dropped, "dropped"));
            SyncEngine.TransferThroughRemote(new GoogleRemote(client, "test@example.com", @"Backups\project"), local, false, items, movedDirs, movedFiles, null);
            Check(drive.Find(@"Backups\project\dropped\inner\x.txt") != null, "a folder dropped on the cloud pane was not uploaded");
            items.Clear();
            items.Add(new KeyValuePair<string, string>("dropped", "restored"));
            SyncEngine.TransferThroughRemote(new GoogleRemote(client, "test@example.com", @"Backups\project"), local, true, items, movedDirs, movedFiles, null);
            Check(File.ReadAllText(Path.Combine(local, "restored", "inner", "x.txt")) == "dropped", "a folder dropped on the local pane was not downloaded");
            Check(refreshes == 0, "the access token was refreshed without a 401");

            long quotaUsed, quotaLimit;
            bool gotQuota = client.GetQuotaInfo(out quotaUsed, out quotaLimit);
            Check(gotQuota && quotaUsed >= 0, "direct quota info failed");

            string directUploadDir = Path.Combine(root, "direct-upload");
            Directory.CreateDirectory(Path.Combine(directUploadDir, "subfolder"));
            File.WriteAllText(Path.Combine(directUploadDir, "hello.txt"), "direct hello");
            File.WriteAllText(Path.Combine(directUploadDir, "subfolder", "world.txt"), "direct world");

            string directTargetFolderId = client.ResolvePathFolderId(client.RootId(), @"DirectUploadRoot\SubTarget", true);
            Check(!string.IsNullOrEmpty(directTargetFolderId), "direct folder resolution failed");

            int uploadedCount = client.UploadDirectoryDirect(directUploadDir, directTargetFolderId, null);
            Check(uploadedCount == 2, "expected 2 files uploaded directly, got " + uploadedCount);

            string directDownloadDir = Path.Combine(root, "direct-download");
            int downloadedCount = client.DownloadDirectoryDirect(directTargetFolderId, directDownloadDir, null);
            Check(downloadedCount == 2, "expected 2 files downloaded directly, got " + downloadedCount);
            Check(File.ReadAllText(Path.Combine(directDownloadDir, "direct-upload", "hello.txt")) == "direct hello", "direct download content mismatch");
            Check(File.ReadAllText(Path.Combine(directDownloadDir, "direct-upload", "subfolder", "world.txt")) == "direct world", "direct download subfolder content mismatch");

            client.DeleteFile(directTargetFolderId);
            Check(!drive.Items.ContainsKey(directTargetFolderId), "direct delete failed to remove folder");

            // Test 1: Upload a single file directly to Google Drive via API (client.Upload)
            string singleUploadFile = Path.Combine(root, "test-upload-file.txt");
            string testContent = "Testing upload of a single file to Google Drive directly via API.";
            File.WriteAllText(singleUploadFile, testContent);
            FileRecord uploadedRecord = client.Upload(singleUploadFile, "test-upload-file.txt", client.RootId(), null, File.GetLastWriteTimeUtc(singleUploadFile), null);
            Check(uploadedRecord != null && !string.IsNullOrEmpty(uploadedRecord.Id), "client.Upload returned invalid file record");
            FakeDrive.Item driveItem = drive.Find("test-upload-file.txt");
            Check(driveItem != null, "uploaded file was not found in Google Drive");
            Check(Encoding.UTF8.GetString(driveItem.Data) == testContent, "uploaded file data mismatch in Google Drive");
            Check(driveItem.Id == uploadedRecord.Id, "uploaded file id mismatch");
            FileRecord foundRec = client.FindFile(client.RootId(), "test-upload-file.txt");
            Check(foundRec != null && foundRec.Id == uploadedRecord.Id, "client.FindFile did not find uploaded file");

            // Test 2: In-place update / replace of the uploaded file
            string updatedContent = "Updated content for uploaded Google Drive file.";
            File.WriteAllText(singleUploadFile, updatedContent);
            FileRecord replacedRecord = client.Upload(singleUploadFile, "test-upload-file.txt", client.RootId(), uploadedRecord.Id, File.GetLastWriteTimeUtc(singleUploadFile), null);
            Check(replacedRecord != null && replacedRecord.Id == uploadedRecord.Id, "client.Upload update did not preserve file id");
            driveItem = drive.Find("test-upload-file.txt");
            Check(Encoding.UTF8.GetString(driveItem.Data) == updatedContent, "updated file content mismatch in Google Drive");

            // Test 3: Download the uploaded file back from Google Drive
            string downloadedFile = Path.Combine(root, "test-download-file.txt");
            client.Download(replacedRecord, downloadedFile, null);
            Check(File.Exists(downloadedFile), "downloaded file does not exist locally");
            Check(File.ReadAllText(downloadedFile) == updatedContent, "downloaded file content does not match uploaded content");

            // Test 4: Delete the uploaded file from Google Drive
            client.DeleteFile(uploadedRecord.Id);
            Check(drive.Find("test-upload-file.txt") == null, "deleted file remained in Google Drive");

            // Test 5: Single file upload through SyncEngine.TransferThroughRemote
            string singleDropFile = Path.Combine(root, "single-drop.txt");
            File.WriteAllText(singleDropFile, "dropped single file content");
            items.Clear();
            items.Add(new KeyValuePair<string, string>(singleDropFile, "single-drop.txt"));
            SyncEngine.TransferThroughRemote(new GoogleRemote(client, "test@example.com", @"Backups\project"), local, false, items, movedDirs, movedFiles, null);
            FakeDrive.Item singleDropUploaded = drive.Find(@"Backups\project\single-drop.txt");
            Check(singleDropUploaded != null && Encoding.UTF8.GetString(singleDropUploaded.Data) == "dropped single file content", "a single file dropped on the cloud pane was not uploaded");

            // Test 6: Upload through GoogleAuth.Client
            string origApi = GoogleAuth.ApiBase;
            GoogleAuth.ApiBase = drive.BaseUrl;
            GoogleAuth.SetTestToken("auth-test@example.com", "fake-token");
            try
            {
                GoogleDriveClient authClient = GoogleAuth.Client("auth-test@example.com");
                string authUploadFile = Path.Combine(root, "auth-upload.txt");
                File.WriteAllText(authUploadFile, "auth client uploaded file");
                FileRecord authRec = authClient.Upload(authUploadFile, "auth-upload.txt", client.RootId(), null, File.GetLastWriteTimeUtc(authUploadFile), null);
                Check(authRec != null, "GoogleAuth.Client upload returned null");
                FakeDrive.Item authItem = drive.Find("auth-upload.txt");
                Check(authItem != null && Encoding.UTF8.GetString(authItem.Data) == "auth client uploaded file", "GoogleAuth.Client upload content mismatch");
                authClient.DeleteFile(authRec.Id);
            }
            finally
            {
                GoogleAuth.ApiBase = origApi;
                GoogleAuth.SetTestToken("auth-test@example.com", null);
            }

            // Test Google sign-in listener cancellation
            TcpListener testListener = new TcpListener(IPAddress.Loopback, 0);
            testListener.Start();
            bool canceledThrown = false;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(50);
                GoogleAuth.CancelSignIn();
            });
            try
            {
                GoogleAuth.WaitForRedirectForTest(testListener, DateTime.UtcNow.AddSeconds(10));
            }
            catch (OperationCanceledException)
            {
                canceledThrown = true;
            }
            finally
            {
                try { testListener.Stop(); } catch { }
            }
            sw.Stop();
            Check(canceledThrown, "Google sign-in cancellation did not throw OperationCanceledException");
            Check(sw.ElapsedMilliseconds < 2000, "Google sign-in cancellation took too long: " + sw.ElapsedMilliseconds + "ms");
            GoogleAuth.CancelSignIn();

            // Test 7: SyncProgress elapsed time and ETA formatting
            Check(SyncProgress.FormatElapsed(TimeSpan.FromSeconds(0)) == "0:00", "0s elapsed format mismatch");
            Check(SyncProgress.FormatElapsed(TimeSpan.FromSeconds(5)) == "0:05", "5s elapsed format mismatch");
            Check(SyncProgress.FormatElapsed(TimeSpan.FromSeconds(75)) == "1:15", "75s elapsed format mismatch");
            Check(SyncProgress.FormatElapsed(TimeSpan.FromSeconds(3665)) == "1:01:05", "3665s elapsed format mismatch");

            SyncProgress testProg = new SyncProgress("Push");
            testProg.StartFolder(@"D:\test-folder", 1, 1);
            string initialDesc = testProg.Describe();
            Check(initialDesc.Contains("Checking test-folder") && initialDesc.Contains("elapsed"), "initial check describe missing check title or elapsed");

            testProg.SetCheckStatus("Scanning Google Drive…");
            string scanDesc = testProg.Describe();
            Check(scanDesc.Contains("Checking test-folder (Scanning Google Drive…)") && scanDesc.Contains("elapsed"), "scan describe missing status or elapsed");

            testProg.SetCheckStatus("Preparing cloud folders (3 of 10)", 15);
            string prepDesc = testProg.Describe();
            Check(prepDesc.Contains("Preparing cloud folders (3 of 10)") && prepDesc.Contains("ETA") && prepDesc.Contains("left") && prepDesc.Contains("elapsed"), "prep describe missing ETA or elapsed: " + prepDesc);
            string prepBrief = testProg.Brief();
            Check(prepBrief.Contains("ETA") && prepBrief.Contains("left"), "prep brief missing ETA or left: " + prepBrief);

            testProg.Plan(10, 100L * 1024 * 1024);
            testProg.BeginFile("data.bin");
            testProg.Copied(10L * 1024 * 1024);
            Thread.Sleep(1100);
            testProg.Copied(30L * 1024 * 1024);
            string plannedDesc = testProg.Describe();
            Check(plannedDesc.Contains("30 MB of 100 MB") && plannedDesc.Contains("MB/s") && plannedDesc.Contains("ETA") && plannedDesc.Contains("left") && plannedDesc.Contains("elapsed") && plannedDesc.Contains("file 1 of 10"), "planned describe missing fields: " + plannedDesc);
            string plannedBrief = testProg.Brief();
            Check(plannedBrief.Contains("30%") && plannedBrief.Contains("left"), "planned brief missing percent or left: " + plannedBrief);

            // Test 8: EnsureDirectories optimization and progress reporting
            GoogleRemote optRemote = new GoogleRemote(client, "test@example.com", @"Backups\opt-test");
            optRemote.Prepare(true);
            optRemote.List(null);
            SyncProgress ensureProg = new SyncProgress("Push");
            ensureProg.StartFolder(@"D:\opt-test", 1, 1);
            List<string> missingDirs = new List<string> { @"sub1", @"sub1\nested1", @"sub2" };
            optRemote.EnsureDirectories(missingDirs, ensureProg);
            Check(drive.Find(@"Backups\opt-test\sub1\nested1") != null, "nested cloud directory was not created");
            Check(ensureProg.CheckStatus.Contains("Preparing cloud folders"), "EnsureDirectories did not update progress status: " + ensureProg.CheckStatus);

            // Re-running EnsureDirectories should find all directories already cached and not re-create them
            int countBefore = drive.Items.Count;
            optRemote.EnsureDirectories(missingDirs, null);
            Check(drive.Items.Count == countBefore, "EnsureDirectories re-created already existing directories");

            // Test 9: Concurrent multi-file upload and download via SyncFolder
            string concurrentLocal = Path.Combine(root, "concurrent-local");
            Directory.CreateDirectory(concurrentLocal);
            for (int i = 0; i < 8; i++)
            {
                File.WriteAllText(Path.Combine(concurrentLocal, "file" + i + ".txt"), "Content of concurrent file " + i);
            }
            GoogleRemote concRemote = new GoogleRemote(client, "test@example.com", @"Backups\concurrent-test");
            string concState = Path.Combine(root, "conc-state.json");
            SyncProgress concProg = new SyncProgress("Push");
            concProg.StartFolder(concurrentLocal, 1, 1);
            SyncStats pushStats = SyncEngine.SyncFolder("Push", concurrentLocal, concRemote, concState, true, null, concProg);
            Check(pushStats.Up == 8, "concurrent push did not upload all 8 files, uploaded: " + pushStats.Up);
            Check(pushStats.Errors == 0, "concurrent push reported errors: " + pushStats.Errors);
            for (int i = 0; i < 8; i++)
            {
                FakeDrive.Item item = drive.Find(@"Backups\concurrent-test\file" + i + ".txt");
                Check(item != null && Encoding.UTF8.GetString(item.Data) == "Content of concurrent file " + i, "concurrent cloud file " + i + " mismatch");
            }

            // Test concurrent pull to a fresh local directory
            string concDownloadLocal = Path.Combine(root, "concurrent-download");
            string concPullState = Path.Combine(root, "conc-pull-state.json");
            SyncProgress pullProg = new SyncProgress("Pull");
            pullProg.StartFolder(concDownloadLocal, 1, 1);
            SyncStats pullStats = SyncEngine.SyncFolder("Pull", concDownloadLocal, concRemote, concPullState, true, null, pullProg);
            Check(pullStats.Down == 8, "concurrent pull did not download all 8 files, downloaded: " + pullStats.Down);
            Check(pullStats.Errors == 0, "concurrent pull reported errors: " + pullStats.Errors);
            for (int i = 0; i < 8; i++)
            {
                string localPath = Path.Combine(concDownloadLocal, "file" + i + ".txt");
                Check(File.Exists(localPath), "concurrent downloaded file " + i + " missing on disk");
                Check(File.ReadAllText(localPath) == "Content of concurrent file " + i, "concurrent downloaded file " + i + " content mismatch");
            }
        }
    }
}
