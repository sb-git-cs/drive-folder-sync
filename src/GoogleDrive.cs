using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace DriveFolderSync
{
    // Google sign-in for installed apps: the browser returns to a loopback port, and the refresh
    // token is kept encrypted for the current Windows user.
    public static class GoogleAuth
    {
        public const string Scope = "https://www.googleapis.com/auth/drive";
        public static string AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
        public static string TokenUrl = "https://oauth2.googleapis.com/token";
        public static string ApiBase = "https://www.googleapis.com";
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DriveFolderSync.google");
        static readonly object Gate = new object();
        static readonly Dictionary<string, KeyValuePair<string, DateTime>> Tokens = new Dictionary<string, KeyValuePair<string, DateTime>>(StringComparer.OrdinalIgnoreCase);

        internal static void SetTestToken(string email, string token)
        {
            lock (Gate)
            {
                if (token == null) Tokens.Remove(email);
                else Tokens[email] = new KeyValuePair<string, DateTime>(token, DateTime.UtcNow.AddHours(1));
            }
        }
        static volatile TcpListener activeSignInListener;
        static volatile bool signInCanceled;

        public static void CancelSignIn()
        {
            signInCanceled = true;
            try
            {
                TcpListener l = activeSignInListener;
                if (l != null) l.Stop();
            }
            catch { }
        }

        public static string ClientId { get { return Store.GetString(Store.ReadConfig(), "googleClientId"); } }
        public static string ClientSecret { get { return Store.GetString(Store.ReadConfig(), "googleClientSecret"); } }
        public static bool Configured { get { return !string.IsNullOrEmpty(ClientId) && !string.IsNullOrEmpty(ClientSecret); } }

        public static void SaveClient(string id, string secret)
        {
            Store.SetGoogleClient(id, secret);
        }

        // The account behind a Google Drive tab, from its provider id "google:<email>".
        public static string EmailOf(Provider provider)
        {
            if (provider == null || provider.Kind != "GoogleDrive" || string.IsNullOrEmpty(provider.Id)) return null;
            if (provider.Id.StartsWith("google:", StringComparison.OrdinalIgnoreCase)) return provider.Id.Substring(7);
            if (!string.IsNullOrEmpty(provider.Root) && provider.Root.StartsWith("gdrive:", StringComparison.OrdinalIgnoreCase))
            {
                string r = provider.Root.Substring(7);
                int slash = r.IndexOf('\\');
                return slash < 0 ? r : r.Substring(0, slash);
            }
            return null;
        }

        static string TokenPath(string email)
        {
            return Path.Combine(Store.Root, "google", Store.SafeName(email.ToLowerInvariant()) + ".token");
        }

        public static bool IsSignedIn(string email)
        {
            return !string.IsNullOrEmpty(email) && File.Exists(TokenPath(email));
        }

        public static List<string> GetAccounts()
        {
            List<string> accounts = new List<string>();
            Dictionary<string, object> cfg = Store.ReadConfig();
            foreach (string acc in Store.GetStringList(cfg, "googleAccounts"))
            {
                if (IsSignedIn(acc) && !accounts.Contains(acc)) accounts.Add(acc);
            }
            string dir = Path.Combine(Store.Root, "google");
            if (Directory.Exists(dir))
            {
                foreach (string file in Directory.GetFiles(dir, "*.token"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (!string.IsNullOrEmpty(name) && !accounts.Contains(name)) accounts.Add(name);
                }
            }
            return accounts;
        }

        public static void SignOut(string email)
        {
            if (string.IsNullOrEmpty(email)) return;
            lock (Gate) Tokens.Remove(email);
            string path = TokenPath(email);
            if (File.Exists(path)) File.Delete(path);
            try
            {
                Dictionary<string, object> cfg = Store.ReadConfig();
                List<string> accs = Store.GetStringList(cfg, "googleAccounts");
                accs.RemoveAll(delegate(string a) { return string.Equals(a, email, StringComparison.OrdinalIgnoreCase); });
                cfg["googleAccounts"] = accs;
                Store.WriteObject(Store.ConfigPath, cfg);
            }
            catch { }
            Store.Log("signed out of Google Drive for " + email);
        }

        static void SaveRefresh(string email, string refresh)
        {
            string path = TokenPath(email);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            byte[] sealedToken = ProtectedData.Protect(Encoding.UTF8.GetBytes(refresh), Entropy, DataProtectionScope.CurrentUser);
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, sealedToken);
            if (File.Exists(path)) File.Replace(temp, path, null, true);
            else File.Move(temp, path);
            try
            {
                Dictionary<string, object> cfg = Store.ReadConfig();
                List<string> accs = Store.GetStringList(cfg, "googleAccounts");
                bool found = false;
                foreach (string a in accs) if (string.Equals(a, email, StringComparison.OrdinalIgnoreCase)) found = true;
                if (!found)
                {
                    accs.Add(email.ToLowerInvariant());
                    cfg["googleAccounts"] = accs;
                    Store.WriteObject(Store.ConfigPath, cfg);
                }
            }
            catch { }
        }

        static string LoadRefresh(string email)
        {
            string path = TokenPath(email);
            if (!File.Exists(path)) return null;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser)); }
            catch (CryptographicException) { return null; }
        }

        public static string AccessToken(string email, bool force)
        {
            lock (Gate)
            {
                KeyValuePair<string, DateTime> cached;
                if (!force && Tokens.TryGetValue(email, out cached) && cached.Value > DateTime.UtcNow.AddSeconds(60)) return cached.Key;
                string refresh = LoadRefresh(email);
                if (string.IsNullOrEmpty(refresh))
                    throw new IOException("Sign in to Google Drive for " + email + " first: choose … → Google Drive sign-in.");
                Dictionary<string, string> form = new Dictionary<string, string>();
                form["client_id"] = ClientId;
                form["client_secret"] = ClientSecret;
                form["refresh_token"] = refresh;
                form["grant_type"] = "refresh_token";
                Dictionary<string, object> reply = PostForm(TokenUrl, form);
                if (reply.ContainsKey("error"))
                {
                    string error = Store.GetString(reply, "error");
                    if (error == "invalid_grant")
                        throw new IOException("The Google Drive sign-in for " + email + " expired or was revoked. Sign in again: … → Google Drive sign-in.");
                    throw new IOException("Google sign-in failed: " + error + " " + Store.GetString(reply, "error_description"));
                }
                string access = Store.GetString(reply, "access_token");
                int seconds = Store.GetInt(reply, "expires_in");
                Tokens[email] = new KeyValuePair<string, DateTime>(access, DateTime.UtcNow.AddSeconds(seconds > 0 ? seconds : 3600));
                return access;
            }
        }

        public static GoogleDriveClient Client(string email)
        {
            return new GoogleDriveClient(ApiBase, delegate(bool force) { return AccessToken(email, force); });
        }

        static Dictionary<string, object> PostForm(string url, Dictionary<string, string> form)
        {
            List<string> pairs = new List<string>();
            foreach (KeyValuePair<string, string> pair in form)
                pairs.Add(Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value ?? ""));
            byte[] body = Encoding.UTF8.GetBytes(string.Join("&", pairs.ToArray()));
            HttpWebRequest request = GoogleDriveClient.NewRequest("POST", url);
            request.ContentType = "application/x-www-form-urlencoded";
            request.ContentLength = body.Length;
            using (Stream output = request.GetRequestStream()) output.Write(body, 0, body.Length);
            string text;
            try
            {
                using (WebResponse response = request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream())) text = reader.ReadToEnd();
            }
            catch (WebException ex)
            {
                if (ex.Response == null) throw;
                using (StreamReader reader = new StreamReader(ex.Response.GetResponseStream())) text = reader.ReadToEnd();
            }
            return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text) ?? new Dictionary<string, object>();
        }

        static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        // Opens the browser for consent and waits for Google to return. Returns the signed-in email.
        public static string SignIn(string loginHint, int timeoutSeconds)
        {
            if (!Configured) throw new IOException("Enter the Google sign-in client ID and secret first.");
            signInCanceled = false;
            byte[] random = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(random);
            string verifier = Base64Url(random);
            string challenge;
            using (SHA256 sha = SHA256.Create()) challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            string state = Guid.NewGuid().ToString("n");
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            activeSignInListener = listener;
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                string redirect = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/";
                string url = AuthUrl + "?client_id=" + Uri.EscapeDataString(ClientId)
                    + "&redirect_uri=" + Uri.EscapeDataString(redirect)
                    + "&response_type=code&scope=" + Uri.EscapeDataString(Scope)
                    + "&code_challenge=" + challenge + "&code_challenge_method=S256"
                    + "&access_type=offline&prompt=consent&state=" + state
                    + (string.IsNullOrEmpty(loginHint) ? "" : "&login_hint=" + Uri.EscapeDataString(loginHint));
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                Dictionary<string, string> query = WaitForRedirect(listener, DateTime.UtcNow.AddSeconds(timeoutSeconds));
                if (signInCanceled) throw new OperationCanceledException("Google sign-in was canceled.");
                if (query.ContainsKey("error"))
                    throw new IOException(query["error"] == "access_denied" ? "Google sign-in was canceled." : "Google sign-in failed: " + query["error"]);
                if (!query.ContainsKey("state") || query["state"] != state) throw new IOException("Google sign-in returned an unexpected response.");
                Dictionary<string, string> form = new Dictionary<string, string>();
                form["code"] = query.ContainsKey("code") ? query["code"] : "";
                form["client_id"] = ClientId;
                form["client_secret"] = ClientSecret;
                form["redirect_uri"] = redirect;
                form["grant_type"] = "authorization_code";
                form["code_verifier"] = verifier;
                Dictionary<string, object> reply = PostForm(TokenUrl, form);
                if (signInCanceled) throw new OperationCanceledException("Google sign-in was canceled.");
                string refresh = Store.GetString(reply, "refresh_token");
                string access = Store.GetString(reply, "access_token");
                if (string.IsNullOrEmpty(refresh) || string.IsNullOrEmpty(access))
                    throw new IOException("Google sign-in failed: " + Store.GetString(reply, "error") + " " + Store.GetString(reply, "error_description"));
                GoogleDriveClient probe = new GoogleDriveClient(ApiBase, delegate(bool force) { return access; });
                string email = probe.AccountEmail();
                if (string.IsNullOrEmpty(email)) throw new IOException("Google did not report which account signed in.");
                SaveRefresh(email, refresh);
                lock (Gate) Tokens[email] = new KeyValuePair<string, DateTime>(access, DateTime.UtcNow.AddSeconds(Math.Max(60, Store.GetInt(reply, "expires_in"))));
                Store.Log("signed in to Google Drive as " + email);
                return email;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                if (signInCanceled) throw new OperationCanceledException("Google sign-in was canceled.");
                throw;
            }
            finally
            {
                activeSignInListener = null;
                try { listener.Stop(); } catch { }
            }
        }

        internal static Dictionary<string, string> WaitForRedirectForTest(TcpListener listener, DateTime deadline)
        {
            activeSignInListener = listener;
            signInCanceled = false;
            try
            {
                return WaitForRedirect(listener, deadline);
            }
            finally
            {
                activeSignInListener = null;
            }
        }

        static Dictionary<string, string> WaitForRedirect(TcpListener listener, DateTime deadline)
        {
            while (true)
            {
                if (signInCanceled) throw new OperationCanceledException("Google sign-in was canceled.");
                TimeSpan left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) throw new IOException("Google sign-in timed out. Try again.");
                IAsyncResult pending;
                try { pending = listener.BeginAcceptTcpClient(null, null); }
                catch (Exception)
                {
                    if (signInCanceled) throw new OperationCanceledException("Google sign-in was canceled.");
                    throw;
                }
                while (!pending.AsyncWaitHandle.WaitOne(150))
                {
                    if (signInCanceled)
                    {
                        try { listener.Stop(); } catch { }
                        throw new OperationCanceledException("Google sign-in was canceled.");
                    }
                    if (DateTime.UtcNow >= deadline) throw new IOException("Google sign-in timed out. Try again.");
                }
                if (signInCanceled) throw new OperationCanceledException("Google sign-in was canceled.");
                TcpClient client;
                try { client = listener.EndAcceptTcpClient(pending); }
                catch (Exception)
                {
                    if (signInCanceled) throw new OperationCanceledException("Google sign-in was canceled.");
                    throw;
                }
                using (client)
                {
                    NetworkStream stream = client.GetStream();
                    stream.ReadTimeout = 10000;
                    string head = ReadHead(stream);
                    string target = "";
                    string[] parts = head.Split(' ');
                    if (parts.Length >= 2) target = parts[1];
                    Dictionary<string, string> query = ParseQuery(target.Contains("?") ? target.Substring(target.IndexOf('?') + 1) : "");
                    bool done = query.ContainsKey("code") || query.ContainsKey("error");
                    string page = done
                        ? "<!doctype html><meta charset=utf-8><title>Drive Folder Sync</title><body style=\"font-family:Segoe UI,sans-serif;padding:40px\"><h2>Drive Folder Sync</h2><p>" + (query.ContainsKey("code") ? "Signed in. You can close this tab and return to the app." : "Sign-in was not completed. You can close this tab.") + "</p></body>"
                        : "Not found";
                    byte[] body = Encoding.UTF8.GetBytes(page);
                    byte[] header = Encoding.ASCII.GetBytes((done ? "HTTP/1.1 200 OK" : "HTTP/1.1 404 Not Found")
                        + "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                    try
                    {
                        stream.Write(header, 0, header.Length);
                        stream.Write(body, 0, body.Length);
                    }
                    catch (IOException) { }
                    if (done) return query;
                }
            }
        }

        internal static string ReadHead(Stream stream)
        {
            StringBuilder text = new StringBuilder();
            int value;
            while (text.Length < 65536 && (value = stream.ReadByte()) >= 0)
            {
                text.Append((char)value);
                if (text.Length >= 4 && text[text.Length - 1] == '\n' && text[text.Length - 2] == '\r' && text[text.Length - 3] == '\n' && text[text.Length - 4] == '\r') break;
            }
            return text.ToString();
        }

        internal static Dictionary<string, string> ParseQuery(string query)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string pair in query.Split('&'))
            {
                if (pair.Length == 0) continue;
                int equals = pair.IndexOf('=');
                string key = equals < 0 ? pair : pair.Substring(0, equals);
                string value = equals < 0 ? "" : pair.Substring(equals + 1);
                map[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
            }
            return map;
        }
    }

    public sealed class HttpReply
    {
        public int Status;
        public string Body;
        public WebHeaderCollection Headers;
    }

    // Google Drive REST v3 calls used by the backup. Transient errors are retried with back-off.
    public sealed class GoogleDriveClient
    {
        public const string FolderType = "application/vnd.google-apps.folder";
        public const string ShortcutType = "application/vnd.google-apps.shortcut";
        const string FileFields = "id,name,mimeType,size,modifiedTime,md5Checksum,parents";
        public static int SimpleLimit = 5 * 1024 * 1024;
        public static int ChunkSize = 16 * 1024 * 1024;
        public static int RetryDelayMs = 1000;
        readonly string apiBase;
        readonly Func<bool, string> token;

        public GoogleDriveClient(string apiBase, Func<bool, string> token)
        {
            this.apiBase = apiBase.TrimEnd('/');
            this.token = token;
        }

        internal static HttpWebRequest NewRequest(string method, string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 64);
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.UserAgent = "DriveFolderSync/" + Store.Version;
            request.Timeout = 90000;
            request.ReadWriteTimeout = 60000;
            request.AllowAutoRedirect = false;
            request.KeepAlive = true;
            request.Proxy = null;
            request.ServicePoint.Expect100Continue = false;
            request.ServicePoint.UseNagleAlgorithm = false;
            request.ServicePoint.ConnectionLimit = 64;
            return request;
        }

        static bool Transient(HttpReply reply)
        {
            if (reply.Status == 429 || reply.Status == 408 || reply.Status >= 500) return true;
            return reply.Status == 403 && reply.Body != null
                && (reply.Body.Contains("rateLimitExceeded") || reply.Body.Contains("userRateLimitExceeded"));
        }

        static void Pause(int attempt)
        {
            int delay = RetryDelayMs * (1 << Math.Min(attempt - 1, 5));
            Thread.Sleep(delay + new Random().Next(0, Math.Max(1, RetryDelayMs / 2)));
        }

        HttpReply SendOnce(string method, string url, string contentType, byte[] body, Dictionary<string, string> headers, bool authorize)
        {
            HttpWebRequest request = NewRequest(method, url);
            if (authorize) request.Headers["Authorization"] = "Bearer " + token(false);
            if (headers != null) foreach (KeyValuePair<string, string> header in headers) request.Headers[header.Key] = header.Value;
            if (body != null)
            {
                if (contentType != null) request.ContentType = contentType;
                request.ContentLength = body.Length;
                using (Stream output = request.GetRequestStream()) output.Write(body, 0, body.Length);
            }
            return Read(request);
        }

        static HttpReply Read(HttpWebRequest request)
        {
            HttpWebResponse response;
            try { response = (HttpWebResponse)request.GetResponse(); }
            catch (WebException ex)
            {
                response = ex.Response as HttpWebResponse;
                if (response == null) throw;
            }
            using (response)
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                HttpReply reply = new HttpReply();
                reply.Status = (int)response.StatusCode;
                reply.Headers = response.Headers;
                reply.Body = reader.ReadToEnd();
                return reply;
            }
        }

        // Sends a request, refreshing the token once on 401 and retrying transient failures.
        HttpReply Send(string method, string url, string contentType, byte[] body, Dictionary<string, string> headers)
        {
            bool refreshed = false;
            for (int attempt = 1; ; attempt++)
            {
                HttpReply reply = null;
                Exception failure = null;
                try { reply = SendOnce(method, url, contentType, body, headers, true); }
                catch (WebException ex) { failure = ex; }
                catch (IOException ex) { failure = ex; }
                if (reply != null && reply.Status == 401 && !refreshed)
                {
                    token(true);
                    refreshed = true;
                    continue;
                }
                if (reply != null && !Transient(reply)) return reply;
                if (attempt >= 6)
                {
                    if (reply != null) return reply;
                    throw new IOException("Couldn't reach Google Drive: " + failure.Message, failure);
                }
                Pause(attempt);
            }
        }

        static Dictionary<string, object> Json(HttpReply reply)
        {
            Dictionary<string, object> data = null;
            try { data = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(reply.Body ?? ""); }
            catch (ArgumentException) { }
            if (reply.Status >= 200 && reply.Status < 300 && data != null) return data;
            string message = "";
            if (data != null && data.ContainsKey("error"))
            {
                Dictionary<string, object> error = data["error"] as Dictionary<string, object>;
                message = error != null ? Store.GetString(error, "message") : Store.GetString(data, "error");
            }
            throw new IOException("Google Drive returned " + reply.Status + (string.IsNullOrEmpty(message) ? "" : ": " + message));
        }

        static string Serialize(object value)
        {
            return new JavaScriptSerializer().Serialize(value);
        }

        public static string Quote(string value)
        {
            return "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
        }

        public static string Rfc3339(DateTime time)
        {
            return time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        public static DateTime ParseTime(string value)
        {
            DateTime time;
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out time)) return time;
            return DateTime.MinValue;
        }

        public static FileRecord Record(Dictionary<string, object> item)
        {
            FileRecord record = new FileRecord();
            record.Id = Store.GetString(item, "id");
            record.Rel = Store.GetString(item, "name");
            record.Md5 = Store.GetString(item, "md5Checksum");
            long size;
            long.TryParse(Store.GetString(item, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
            record.Length = size;
            record.MtimeUtc = ParseTime(Store.GetString(item, "modifiedTime"));
            return record;
        }

        public string AccountEmail()
        {
            Dictionary<string, object> about = Json(Send("GET", apiBase + "/drive/v3/about?fields=user(emailAddress)", null, null, null));
            Dictionary<string, object> user = about.ContainsKey("user") ? about["user"] as Dictionary<string, object> : null;
            return user == null ? null : Store.GetString(user, "emailAddress");
        }

        // Remaining quota in bytes, or -1 for an unlimited account.
        public long FreeQuota()
        {
            Dictionary<string, object> about = Json(Send("GET", apiBase + "/drive/v3/about?fields=storageQuota(limit,usage)", null, null, null));
            Dictionary<string, object> quota = about.ContainsKey("storageQuota") ? about["storageQuota"] as Dictionary<string, object> : null;
            if (quota == null) return -1;
            long limit, usage;
            if (!long.TryParse(Store.GetString(quota, "limit"), NumberStyles.Integer, CultureInfo.InvariantCulture, out limit)) return -1;
            long.TryParse(Store.GetString(quota, "usage"), NumberStyles.Integer, CultureInfo.InvariantCulture, out usage);
            return Math.Max(0, limit - usage);
        }

        public bool GetQuotaInfo(out long used, out long limit)
        {
            used = 0;
            limit = -1;
            try
            {
                Dictionary<string, object> about = Json(Send("GET", apiBase + "/drive/v3/about?fields=storageQuota(limit,usage)", null, null, null));
                Dictionary<string, object> quota = about.ContainsKey("storageQuota") ? about["storageQuota"] as Dictionary<string, object> : null;
                if (quota == null) return false;
                long.TryParse(Store.GetString(quota, "limit"), NumberStyles.Integer, CultureInfo.InvariantCulture, out limit);
                long.TryParse(Store.GetString(quota, "usage"), NumberStyles.Integer, CultureInfo.InvariantCulture, out used);
                return true;
            }
            catch { return false; }
        }

        public void DeleteFile(string fileId)
        {
            if (string.IsNullOrEmpty(fileId)) return;
            Send("DELETE", apiBase + "/drive/v3/files/" + Uri.EscapeDataString(fileId), null, null, null);
        }

        public string ResolvePathFolderId(string rootFolderId, string relativePath, bool create)
        {
            if (string.IsNullOrEmpty(relativePath)) return rootFolderId;
            string currentId = rootFolderId;
            foreach (string part in relativePath.Split('\\'))
            {
                if (string.IsNullOrEmpty(part)) continue;
                string next = FindFolder(currentId, part);
                if (next == null)
                {
                    if (!create) return null;
                    next = CreateFolder(currentId, part);
                }
                currentId = next;
            }
            return currentId;
        }

        public int UploadDirectoryDirect(string localDir, string parentId, SyncProgress progress)
        {
            string dirName = Path.GetFileName(localDir.TrimEnd('\\'));
            string targetFolderId = FindFolder(parentId, dirName);
            if (targetFolderId == null) targetFolderId = CreateFolder(parentId, dirName);
            int count = 0;
            string[] files = Directory.GetFiles(SyncEngine.LongPath(localDir));
            foreach (string file in files)
            {
                if (progress != null && progress.Canceled) throw new OperationCanceledException();
                string fileName = Path.GetFileName(file);
                if (SyncEngine.SkipName(fileName, false)) continue;
                FileRecord existing = FindFile(targetFolderId, fileName);
                DateTime mtime = File.GetLastWriteTimeUtc(SyncEngine.LongPath(file));
                Upload(file, fileName, targetFolderId, existing == null ? null : existing.Id, mtime, progress);
                count++;
            }
            string[] subdirs = Directory.GetDirectories(SyncEngine.LongPath(localDir));
            foreach (string subdir in subdirs)
            {
                if (progress != null && progress.Canceled) throw new OperationCanceledException();
                string subName = Path.GetFileName(subdir);
                if (SyncEngine.SkipName(subName, true) || SyncEngine.IsRedirect(subdir, File.GetAttributes(subdir))) continue;
                count += UploadDirectoryDirect(subdir, targetFolderId, progress);
            }
            return count;
        }

        public int DownloadDirectoryDirect(string folderId, string localDir, SyncProgress progress)
        {
            Directory.CreateDirectory(SyncEngine.LongPath(localDir));
            int count = 0;
            List<Dictionary<string, object>> children = Query(Quote(folderId) + " in parents and trashed = false");
            foreach (Dictionary<string, object> item in children)
            {
                if (progress != null && progress.Canceled) throw new OperationCanceledException();
                string name = Store.GetString(item, "name");
                string mime = Store.GetString(item, "mimeType");
                bool isDir = mime == FolderType;
                if (SyncEngine.SkipName(name, isDir)) continue;
                if (!isDir && (mime.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal) || mime == ShortcutType)) continue;
                if (isDir)
                {
                    string subDir = Path.Combine(localDir, name);
                    count += DownloadDirectoryDirect(Store.GetString(item, "id"), subDir, progress);
                }
                else
                {
                    FileRecord record = Record(item);
                    string dest = Path.Combine(localDir, name);
                    Download(record, dest, progress);
                    count++;
                }
            }
            return count;
        }

        public string RootId()
        {
            return Store.GetString(Json(Send("GET", apiBase + "/drive/v3/files/root?fields=id", null, null, null)), "id");
        }

        public List<Dictionary<string, object>> Query(string q)
        {
            List<Dictionary<string, object>> items = new List<Dictionary<string, object>>();
            string page = null;
            do
            {
                string url = apiBase + "/drive/v3/files?q=" + Uri.EscapeDataString(q)
                    + "&fields=" + Uri.EscapeDataString("nextPageToken,files(" + FileFields + ")")
                    + "&pageSize=1000&spaces=drive"
                    + (page == null ? "" : "&pageToken=" + Uri.EscapeDataString(page));
                Dictionary<string, object> result = Json(Send("GET", url, null, null, null));
                foreach (Dictionary<string, object> item in Store.GetObjectList(result, "files")) items.Add(item);
                page = Store.GetString(result, "nextPageToken");
                if (page == "") page = null;
            }
            while (page != null);
            return items;
        }

        public string FindFolder(string parentId, string name)
        {
            List<Dictionary<string, object>> found = Query(Quote(parentId) + " in parents and name = " + Quote(name)
                + " and mimeType = " + Quote(FolderType) + " and trashed = false");
            return found.Count == 0 ? null : Store.GetString(found[0], "id");
        }

        public FileRecord FindFile(string parentId, string name)
        {
            FileRecord best = null;
            foreach (Dictionary<string, object> item in Query(Quote(parentId) + " in parents and name = " + Quote(name) + " and trashed = false"))
            {
                if (Store.GetString(item, "mimeType").StartsWith("application/vnd.google-apps.", StringComparison.Ordinal)) continue;
                FileRecord record = Record(item);
                if (best == null || record.MtimeUtc > best.MtimeUtc) best = record;
            }
            return best;
        }

        public string CreateFolder(string parentId, string name)
        {
            Dictionary<string, object> meta = new Dictionary<string, object>();
            meta["name"] = name;
            meta["mimeType"] = FolderType;
            meta["parents"] = new string[] { parentId };
            return Store.GetString(Json(Send("POST", apiBase + "/drive/v3/files?fields=id", "application/json; charset=UTF-8",
                Encoding.UTF8.GetBytes(Serialize(meta)), null)), "id");
        }

        public static string Md5Of(string path)
        {
            using (MD5 md5 = MD5.Create())
            using (FileStream input = new FileStream(SyncEngine.LongPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20))
                return BitConverter.ToString(md5.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        }

        // Creates a file (fileId null) or replaces the contents of an existing one, keeping its history.
        public FileRecord Upload(string path, string name, string parentId, string fileId, DateTime mtimeUtc, SyncProgress progress)
        {
            return Upload(path, name, parentId, fileId, mtimeUtc, progress, name);
        }

        public FileRecord Upload(string path, string name, string parentId, string fileId, DateTime mtimeUtc, SyncProgress progress, string rel)
        {
            long size = new FileInfo(SyncEngine.LongPath(path)).Length;
            string md5 = Md5Of(path);
            Dictionary<string, object> meta = new Dictionary<string, object>();
            meta["modifiedTime"] = Rfc3339(mtimeUtc);
            bool isNew = string.IsNullOrEmpty(fileId);
            if (isNew)
            {
                meta["name"] = name;
                meta["parents"] = new string[] { parentId };
            }
            string url = apiBase + "/upload/drive/v3/files" + (isNew ? "" : "/" + fileId);
            string method = isNew ? "POST" : "PATCH";
            string fields = "&fields=" + Uri.EscapeDataString(FileFields);
            Dictionary<string, object> result = size <= SimpleLimit
                ? UploadSmall(method, url + "?uploadType=multipart" + fields, Serialize(meta), path, size, progress, rel)
                : UploadResumable(method, url + "?uploadType=resumable" + fields, Serialize(meta), path, size, progress, rel);
            FileRecord record = Record(result);
            if (string.IsNullOrEmpty(record.Rel)) record.Rel = string.IsNullOrEmpty(rel) ? name : rel;
            if (record.Length != size) throw new IOException("Google Drive stored " + record.Length + " bytes instead of " + size + " for " + name);
            if (!string.IsNullOrEmpty(record.Md5) && !string.Equals(record.Md5, md5, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The uploaded copy of " + name + " didn't match its checksum.");
            return record;
        }

        Dictionary<string, object> UploadSmall(string method, string url, string meta, string path, long size, SyncProgress progress, string rel)
        {
            byte[] data = new byte[size];
            using (FileStream input = new FileStream(SyncEngine.LongPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                ReadFully(input, data, (int)size);
            string boundary = "dfs" + Guid.NewGuid().ToString("n");
            MemoryStream body = new MemoryStream();
            byte[] head = Encoding.UTF8.GetBytes("--" + boundary + "\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n" + meta
                + "\r\n--" + boundary + "\r\nContent-Type: application/octet-stream\r\n\r\n");
            byte[] tail = Encoding.UTF8.GetBytes("\r\n--" + boundary + "--");
            body.Write(head, 0, head.Length);
            body.Write(data, 0, data.Length);
            body.Write(tail, 0, tail.Length);
            Dictionary<string, object> result = Json(Send(method, url, "multipart/related; boundary=" + boundary, body.ToArray(), null));
            if (progress != null) progress.Copied(rel, size);
            return result;
        }

        static void ReadFully(Stream input, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int got = input.Read(buffer, read, count - read);
                if (got <= 0) throw new IOException("The file changed while it was being uploaded.");
                read += got;
            }
        }

        Dictionary<string, object> UploadResumable(string method, string url, string meta, string path, long size, SyncProgress progress, string rel)
        {
            Dictionary<string, string> startHeaders = new Dictionary<string, string>();
            startHeaders["X-Upload-Content-Type"] = "application/octet-stream";
            startHeaders["X-Upload-Content-Length"] = size.ToString(CultureInfo.InvariantCulture);
            HttpReply start = Send(method, url, "application/json; charset=UTF-8", Encoding.UTF8.GetBytes(meta), startHeaders);
            if (start.Status != 200) Json(start);
            string session = start.Headers["Location"];
            if (string.IsNullOrEmpty(session)) throw new IOException("Google Drive did not start the upload.");
            using (FileStream input = new FileStream(SyncEngine.LongPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20))
            {
                long offset = 0;
                int failures = 0;
                byte[] buffer = new byte[ChunkSize];
                while (true)
                {
                    if (progress != null && progress.Canceled) throw new OperationCanceledException();
                    int count = (int)Math.Min(ChunkSize, size - offset);
                    input.Position = offset;
                    ReadFully(input, buffer, count);
                    HttpReply reply = null;
                    try { reply = SendChunk(session, buffer, count, offset, size, progress, rel); }
                    catch (WebException) { }
                    catch (IOException) { }
                    if (reply != null && (reply.Status == 200 || reply.Status == 201)) return Json(reply);
                    if (reply != null && reply.Status == 308)
                    {
                        offset = NextOffset(reply);
                        failures = 0;
                        continue;
                    }
                    if (reply != null && reply.Status == 404) throw new IOException("The upload session expired; the file will be sent again next time.");
                    if (reply != null && !Transient(reply)) Json(reply);
                    if (++failures > 6) throw new IOException("Couldn't finish uploading to Google Drive after several attempts.");
                    Pause(failures);
                    HttpReply status = null;
                    try
                    {
                        Dictionary<string, string> headers = new Dictionary<string, string>();
                        headers["Content-Range"] = "bytes */" + size.ToString(CultureInfo.InvariantCulture);
                        status = SendOnce("PUT", session, null, new byte[0], headers, false);
                    }
                    catch (WebException) { }
                    catch (IOException) { }
                    if (status != null && (status.Status == 200 || status.Status == 201)) return Json(status);
                    if (status != null && status.Status == 308) offset = NextOffset(status);
                }
            }
        }

        HttpReply SendChunk(string session, byte[] buffer, int count, long offset, long size, SyncProgress progress, string rel)
        {
            HttpWebRequest request = NewRequest("PUT", session);
            request.ContentLength = count;
            request.Headers["Content-Range"] = count == 0
                ? "bytes */" + size.ToString(CultureInfo.InvariantCulture)
                : "bytes " + offset.ToString(CultureInfo.InvariantCulture) + "-" + (offset + count - 1).ToString(CultureInfo.InvariantCulture) + "/" + size.ToString(CultureInfo.InvariantCulture);
            using (Stream output = request.GetRequestStream())
            {
                int written = 0;
                while (written < count)
                {
                    int slice = Math.Min(256 * 1024, count - written);
                    output.Write(buffer, written, slice);
                    written += slice;
                    if (progress != null) progress.Copied(rel, offset + written);
                }
            }
            return Read(request);
        }

        static long NextOffset(HttpReply reply)
        {
            string range = reply.Headers["Range"];
            if (string.IsNullOrEmpty(range)) return 0;
            int dash = range.LastIndexOf('-');
            long last;
            if (dash < 0 || !long.TryParse(range.Substring(dash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out last)) return 0;
            return last + 1;
        }

        // Downloads into a temporary file next to the destination and moves it into place once the
        // size and checksum match, so an interrupted download never leaves a partial file.
        public void Download(FileRecord remote, string destination, SyncProgress progress)
        {
            string parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(SyncEngine.LongPath(parent));
            string partial = destination + ".dfs-partial.tmp";
            bool refreshed = false;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    HttpWebRequest request = NewRequest("GET", apiBase + "/drive/v3/files/" + Uri.EscapeDataString(remote.Id) + "?alt=media");
                    request.AllowAutoRedirect = true;
                    request.Headers["Authorization"] = "Bearer " + token(false);
                    HttpWebResponse response;
                    try { response = (HttpWebResponse)request.GetResponse(); }
                    catch (WebException ex)
                    {
                        HttpWebResponse failed = ex.Response as HttpWebResponse;
                        if (failed == null) throw;
                        HttpReply reply = new HttpReply();
                        reply.Status = (int)failed.StatusCode;
                        using (StreamReader reader = new StreamReader(failed.GetResponseStream())) reply.Body = reader.ReadToEnd();
                        failed.Close();
                        if (reply.Status == 401 && !refreshed) { token(true); refreshed = true; attempt--; continue; }
                        if (!Transient(reply)) Json(reply);
                        throw new IOException("Google Drive returned " + reply.Status);
                    }
                    string md5;
                    long written = 0;
                    using (response)
                    using (Stream input = response.GetResponseStream())
                    using (FileStream output = new FileStream(SyncEngine.LongPath(partial), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    using (MD5 hash = MD5.Create())
                    {
                        byte[] buffer = new byte[1 << 20];
                        int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (progress != null && progress.Canceled) throw new OperationCanceledException();
                            output.Write(buffer, 0, count);
                            hash.TransformBlock(buffer, 0, count, null, 0);
                            written += count;
                            if (progress != null) progress.Copied(remote.Rel, written);
                        }
                        hash.TransformFinalBlock(new byte[0], 0, 0);
                        md5 = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
                    }
                    if (written != remote.Length) throw new IOException("The download of " + Path.GetFileName(destination) + " was incomplete.");
                    if (!string.IsNullOrEmpty(remote.Md5) && !string.Equals(md5, remote.Md5, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The download of " + Path.GetFileName(destination) + " didn't match its checksum.");
                    if (remote.MtimeUtc > DateTime.MinValue) File.SetLastWriteTimeUtc(SyncEngine.LongPath(partial), remote.MtimeUtc);
                    SyncEngine.MoveIntoPlace(partial, destination);
                    return;
                }
                catch (Exception ex)
                {
                    try { if (File.Exists(SyncEngine.LongPath(partial))) File.Delete(SyncEngine.LongPath(partial)); }
                    catch (IOException) { }
                    if (!(ex is IOException || ex is WebException)) throw;
                    if (attempt >= 4 || ex.Message.Contains("checksum") || ex.Message.StartsWith("Google Drive returned 4", StringComparison.Ordinal)) throw;
                    Pause(attempt);
                }
            }
        }
    }

    // A folder in My Drive reached through the API, so files move between this PC and Google
    // Drive directly instead of through Google Drive for desktop's cache.
    public sealed class GoogleRemote : IRemote
    {
        readonly GoogleDriveClient client;
        readonly string email;
        readonly string relative;
        readonly object remoteGate = new object();
        readonly Dictionary<string, string> folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, FileRecord> files = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
        bool listed;

        public GoogleRemote(GoogleDriveClient client, string email, string relative)
        {
            this.client = client;
            this.email = email ?? "";
            this.relative = (relative ?? "").Trim('\\');
        }

        public string Name { get { return "Google Drive (" + email + ")\\My Drive" + (relative.Length == 0 ? "" : "\\" + relative); } }

        public bool Prepare(bool create)
        {
            string id = client.RootId();
            if (relative.Length > 0)
            {
                foreach (string part in relative.Split('\\'))
                {
                    if (part.Length == 0) continue;
                    string next = client.FindFolder(id, part);
                    if (next == null)
                    {
                        if (!create) return false;
                        next = client.CreateFolder(id, part);
                    }
                    id = next;
                }
            }
            lock (remoteGate)
            {
                folders.Clear();
                files.Clear();
                folders[""] = id;
                listed = false;
            }
            return true;
        }

        static bool WindowsName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal)) return false;
            return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        public Dictionary<string, FileRecord> List(List<string> links)
        {
            return List(links, null);
        }

        public Dictionary<string, FileRecord> List(List<string> links, SyncProgress progress)
        {
            string rootId;
            lock (remoteGate)
            {
                files.Clear();
                rootId = folders[""];
                folders.Clear();
                folders[""] = rootId;
            }
            Dictionary<string, string> relOf = new Dictionary<string, string>(StringComparer.Ordinal);
            relOf[rootId] = "";
            List<string> queue = new List<string>();
            queue.Add(rootId);
            int skipped = 0;
            while (queue.Count > 0)
            {
                if (progress != null && progress.Canceled) break;
                if (progress != null)
                {
                    int foundCount;
                    int folderCount;
                    lock (remoteGate)
                    {
                        foundCount = files.Count;
                        folderCount = Math.Max(0, folders.Count - 1);
                    }
                    progress.SetCheckStatus(SyncEngine.FormatScanStatus("Scanning Google Drive", foundCount, folderCount));
                }
                // One query covers many folders, so a large tree needs few requests.
                List<string> batch = queue.GetRange(0, Math.Min(30, queue.Count));
                queue.RemoveRange(0, batch.Count);
                List<string> clauses = new List<string>();
                foreach (string id in batch) clauses.Add(GoogleDriveClient.Quote(id) + " in parents");
                foreach (Dictionary<string, object> item in client.Query("(" + string.Join(" or ", clauses.ToArray()) + ") and trashed = false"))
                {
                    string parentRel = null;
                    foreach (object parent in Store.AsEnumerable(item.ContainsKey("parents") ? item["parents"] : null))
                    {
                        string key = parent as string;
                        if (key != null && relOf.ContainsKey(key)) { parentRel = relOf[key]; break; }
                    }
                    if (parentRel == null) continue;
                    string name = Store.GetString(item, "name");
                    string mime = Store.GetString(item, "mimeType");
                    bool isFolder = mime == GoogleDriveClient.FolderType;
                    if (!WindowsName(name)) { skipped++; continue; }
                    string rel = parentRel.Length == 0 ? name : parentRel + "\\" + name;
                    if (mime == GoogleDriveClient.ShortcutType)
                    {
                        if (links != null) links.Add(rel);
                        continue;
                    }
                    if (SyncEngine.SkipName(name, isFolder)) continue;
                    if (isFolder)
                    {
                        string id = Store.GetString(item, "id");
                        lock (remoteGate)
                        {
                            if (folders.ContainsKey(rel)) Store.Log("Google Drive has more than one folder named " + rel + "; their contents are combined");
                            else folders[rel] = id;
                        }
                        relOf[id] = rel;
                        queue.Add(id);
                        continue;
                    }
                    if (mime.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal)) { skipped++; continue; }
                    FileRecord record = GoogleDriveClient.Record(item);
                    record.Rel = rel;
                    lock (remoteGate)
                    {
                        FileRecord existing;
                        if (files.TryGetValue(rel, out existing))
                        {
                            Store.Log("Google Drive has more than one file named " + rel + "; using the newest");
                            if (existing.MtimeUtc >= record.MtimeUtc) continue;
                        }
                        files[rel] = record;
                    }
                }
            }
            if (skipped > 0) Store.Log("skipped " + skipped + " Google Docs files or names Windows can't use in " + Name);
            if (progress != null)
            {
                int foundCount;
                int folderCount;
                lock (remoteGate)
                {
                    foundCount = files.Count;
                    folderCount = Math.Max(0, folders.Count - 1);
                }
                progress.SetCheckStatus(SyncEngine.FormatScanStatus("Scanning Google Drive", foundCount, folderCount));
            }
            lock (remoteGate)
            {
                listed = true;
                return new Dictionary<string, FileRecord>(files, StringComparer.OrdinalIgnoreCase);
            }
        }

        string FolderId(string rel, bool create)
        {
            rel = (rel ?? "").Trim('\\');
            string id;
            lock (remoteGate)
            {
                if (folders.TryGetValue(rel, out id)) return id;
            }
            int slash = rel.LastIndexOf('\\');
            string parentRel = slash < 0 ? "" : rel.Substring(0, slash);
            string name = slash < 0 ? rel : rel.Substring(slash + 1);
            string parentId = FolderId(parentRel, create);
            if (parentId == null) return null;
            lock (remoteGate)
            {
                if (folders.TryGetValue(rel, out id)) return id;
                if (!listed) id = client.FindFolder(parentId, name);
                if (id == null && create) id = client.CreateFolder(parentId, name);
                if (id != null) folders[rel] = id;
                return id;
            }
        }

        public Dictionary<string, FileRecord> ListAllowed(List<string> rels)
        {
            Dictionary<string, FileRecord> map = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
            if (rels == null) return map;
            foreach (string rel in rels)
            {
                if (string.IsNullOrEmpty(rel) || SyncEngine.SkipName(Path.GetFileName(rel), false)) continue;
                string parentId = FolderId(Path.GetDirectoryName(rel), false);
                if (parentId == null) continue;
                FileRecord record = client.FindFile(parentId, Path.GetFileName(rel));
                if (record == null) continue;
                record.Rel = rel;
                lock (remoteGate) { files[rel] = record; }
                map[rel] = record;
            }
            return map;
        }

        public List<string> Directories(Dictionary<string, bool> links)
        {
            if (!listed) List(null);
            List<string> dirs = new List<string>();
            lock (remoteGate)
            {
                foreach (string rel in folders.Keys)
                {
                    if (rel.Length == 0) continue;
                    bool linked = false;
                    if (links != null) foreach (string link in links.Keys) if (string.Equals(rel, link, StringComparison.OrdinalIgnoreCase) || rel.StartsWith(link + "\\", StringComparison.OrdinalIgnoreCase)) linked = true;
                    if (!linked) dirs.Add(rel);
                }
            }
            return dirs;
        }

        public void EnsureDirectories(List<string> rels)
        {
            EnsureDirectories(rels, null);
        }

        public void EnsureDirectories(List<string> rels, SyncProgress progress)
        {
            List<string> missing = new List<string>();
            lock (remoteGate)
            {
                foreach (string rel in rels)
                {
                    if (string.IsNullOrEmpty(rel)) continue;
                    string clean = rel.Trim('\\');
                    if (clean.Length > 0 && !folders.ContainsKey(clean)) missing.Add(clean);
                }
            }
            missing.Sort(delegate(string a, string b) { return a.Length.CompareTo(b.Length); });
            int total = missing.Count;
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
                FolderId(missing[i], true);
            }
        }

        public FileRecord Upload(string rel, FileRecord local, SyncProgress progress)
        {
            string parentId = FolderId(Path.GetDirectoryName(rel), true);
            FileRecord existing;
            lock (remoteGate) { files.TryGetValue(rel, out existing); }
            FileRecord record = client.Upload(local.Full, Path.GetFileName(rel), parentId, existing == null ? null : existing.Id, local.MtimeUtc, progress, rel);
            record.Rel = rel;
            lock (remoteGate) { files[rel] = record; }
            return record;
        }

        public void Download(FileRecord remote, string destination, SyncProgress progress)
        {
            client.Download(remote, destination, progress);
        }

        public bool Same(FileRecord local, FileRecord remote)
        {
            if (local == null || remote == null || local.Length != remote.Length) return false;
            if (string.IsNullOrEmpty(remote.Md5)) return Math.Abs((local.MtimeUtc - remote.MtimeUtc).TotalSeconds) < 1;
            return string.Equals(GoogleDriveClient.Md5Of(local.Full), remote.Md5, StringComparison.OrdinalIgnoreCase);
        }

        public long FreeSpace()
        {
            return client.FreeQuota();
        }
    }
}
