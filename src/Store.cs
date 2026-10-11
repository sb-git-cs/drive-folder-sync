using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace DriveFolderSync
{
    public class Provider
    {
        public string Id;
        public string Kind;
        public string Name;
        public string Root;
        public string Detail;
        public bool Custom;

        public bool IsOnline
        {
            get { return !string.IsNullOrEmpty(Root) && Directory.Exists(Root); }
        }

        public string Title
        {
            get
            {
                string text = Name;
                if (!string.IsNullOrEmpty(Detail)) text = text + "  ·  " + Detail;
                if (!IsOnline) text = text + "  (not connected)";
                return text;
            }
        }
    }

    public class LinkFolder
    {
        public string Id;
        public string Local;
        public string ProviderId;
        public string RemoteRelative;
        public string Added;
        public string LastSync;
        public int LastErrors;
        public bool Partial;
        public List<string> Files;
        public List<string> Dirs;
    }

    public class FileRecord
    {
        public string Rel;
        public string Full;
        public long Length;
        public DateTime MtimeUtc;
    }

    public class Snap
    {
        public string Ll;
        public string Lm;
        public string Rl;
        public string Rm;
        public bool Pending;
        public bool Hold;
        public string Synced;
    }

    public class SyncStats
    {
        public int Up;
        public int Down;
        public int Del;
        public int Conflict;
        public int Errors;
    }

    public class Store
    {
        public const string Version = "1.5.0";
        static readonly object LogGate = new object();
        static string rootOverride;
        static string logOverride;

        public static string DefaultRoot
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveFolderSync"); }
        }

        public static string Root
        {
            get { return string.IsNullOrEmpty(rootOverride) ? DefaultRoot : rootOverride; }
        }

        public static string ConfigPath { get { return Path.Combine(Root, "config.json"); } }
        public static string ManifestPath { get { return Path.Combine(Root, "manifest.json"); } }
        public static string StateDir { get { return Path.Combine(Root, "state"); } }
        public static string LogFile { get { return string.IsNullOrEmpty(logOverride) ? Path.Combine(Root, "sync.log") : logOverride; } }
        public static string PidFile { get { return Path.Combine(Root, "host.pid"); } }
        public static string InstallExe { get { return Path.Combine(DefaultRoot, "DriveFolderSync.exe"); } }

        public static void SetOverrides(string root, string log)
        {
            rootOverride = root;
            logOverride = log;
        }

        public static void EnsureDirs()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(StateDir);
        }

        public static bool IsInstalledCopy()
        {
            string current = Path.GetFullPath(System.Reflection.Assembly.GetExecutingAssembly().Location).TrimEnd('\\');
            string expected = Path.GetFullPath(InstallExe).TrimEnd('\\');
            return string.Equals(current, expected, StringComparison.OrdinalIgnoreCase);
        }

        public static void Log(string message)
        {
            try
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message;
                lock (LogGate)
                {
                    string path = LogFile;
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    if (File.Exists(path) && new FileInfo(path).Length > 2L * 1024 * 1024)
                    {
                        string old = path + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch
            {
            }
        }

        public static string LocalRoot()
        {
            Dictionary<string, object> cfg = ReadConfig();
            string root = GetString(cfg, "localRoot");
            if (string.IsNullOrEmpty(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Work Drive");
            return root;
        }

        public static string SafeName(string value)
        {
            if (string.IsNullOrEmpty(value)) return "drive";
            foreach (char bad in Path.GetInvalidFileNameChars()) value = value.Replace(bad, '-');
            value = value.Trim();
            return string.IsNullOrEmpty(value) ? "drive" : value;
        }

        public static string ReplicaLeaf(Provider provider)
        {
            string raw = provider == null ? "" : (provider.Detail ?? "");
            bool path = raw.IndexOf('\\') >= 0 || (raw.Length >= 2 && raw[1] == ':');
            if (string.IsNullOrEmpty(raw) || path)
                raw = provider == null ? "" : (provider.Name ?? "");
            int space = raw.IndexOf(' ');
            if (space > 0) raw = raw.Substring(0, space);
            return SafeName(raw);
        }

        public static string ReplicaRoot(Provider provider)
        {
            return Path.Combine(LocalRoot(), ReplicaLeaf(provider));
        }

        public static string ReplicaPath(Provider provider, string relative)
        {
            string root = ReplicaRoot(provider);
            if (string.IsNullOrEmpty(relative)) return root;
            return Path.Combine(root, relative);
        }

        public class Place
        {
            public Provider Provider;
            public string Relative;
            public bool ViewingReplica;
            public string CloudPath;
            public string ReplicaPath;
            public LinkFolder Link;
            public string LinkRelative;
            public string LocalRoot;
            public string CloudRoot;
        }

        public static Place FindPlace(string path, List<Provider> providers)
        {
            if (string.IsNullOrEmpty(path) || providers == null) return null;
            Place linked = FindLinkedPlace(path, providers, ReadLinks());
            if (linked != null) return linked;
            foreach (Provider provider in providers)
            {
                if (provider == null || string.IsNullOrEmpty(provider.Root)) continue;
                if (!IsUnder(path, provider.Root)) continue;
                return MakePlace(provider, Relative(path, provider.Root) ?? "", false);
            }
            foreach (Provider provider in providers)
            {
                if (provider == null) continue;
                string replica = ReplicaRoot(provider);
                if (!IsUnder(path, replica)) continue;
                return MakePlace(provider, Relative(path, replica) ?? "", true);
            }
            return null;
        }

        public static Place FindLinkedPlace(string path, List<Provider> providers, List<LinkFolder> links)
        {
            Place best = null;
            int depth = -1;
            foreach (LinkFolder link in links)
            {
                Provider provider = FindProvider(providers, link.ProviderId);
                string cloud = ResolveRemote(link, providers);
                if (provider == null || string.IsNullOrEmpty(cloud)) continue;
                bool local = IsUnder(path, link.Local);
                string anchor = local ? link.Local : cloud;
                if (!IsUnder(path, anchor) || Norm(anchor).Length <= depth) continue;
                string rel = Relative(path, anchor) ?? "";
                best = new Place();
                best.Provider = provider;
                best.Link = link;
                best.LinkRelative = rel;
                best.LocalRoot = link.Local;
                best.CloudRoot = cloud;
                best.ViewingReplica = local;
                best.CloudPath = Path.Combine(cloud, rel);
                best.ReplicaPath = Path.Combine(link.Local, rel);
                best.Relative = Relative(best.CloudPath, provider.Root) ?? "";
                depth = Norm(anchor).Length;
            }
            return best;
        }

        static Place MakePlace(Provider provider, string relative, bool viewingReplica)
        {
            Place place = new Place();
            place.Provider = provider;
            place.Relative = relative ?? "";
            place.ViewingReplica = viewingReplica;
            place.CloudPath = string.IsNullOrEmpty(place.Relative) ? provider.Root : Path.Combine(provider.Root, place.Relative);
            place.ReplicaPath = ReplicaPath(provider, place.Relative);
            place.LinkRelative = place.Relative;
            place.LocalRoot = ReplicaRoot(provider);
            place.CloudRoot = provider.Root;
            return place;
        }

        public static List<string> WatchRoots()
        {
            Dictionary<string, object> cfg = ReadConfig();
            List<string> list = GetStringList(cfg, "watchRoots");
            if (list.Count == 0)
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                list.Add(Path.Combine(profile, "Desktop"));
                list.Add(Path.Combine(profile, "Documents"));
                list.Add(Path.Combine(profile, "Downloads"));
                list.Add(LocalRoot());
            }
            return list;
        }

        public static List<string> ExcludePaths()
        {
            Dictionary<string, object> cfg = ReadConfig();
            List<string> list = GetStringList(cfg, "excludePaths");
            if (list.Count == 0)
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                list.Add(Path.Combine(profile, "Desktop", "git"));
                list.Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
                list.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            }
            return list;
        }

        public static Dictionary<string, object> ReadConfig()
        {
            Dictionary<string, object> cfg = ReadObject(ConfigPath);
            if (cfg == null) cfg = new Dictionary<string, object>();
            return cfg;
        }

        public static void SaveCustomProvider(Provider provider)
        {
            Dictionary<string, object> cfg = ReadConfig();
            if (!cfg.ContainsKey("localRoot") || string.IsNullOrEmpty(GetString(cfg, "localRoot")))
                cfg["localRoot"] = LocalRoot();
            List<Dictionary<string, object>> custom = GetObjectList(cfg, "customProviders");
            bool found = false;
            for (int i = 0; i < custom.Count; i++)
            {
                if (string.Equals(GetString(custom[i], "id"), provider.Id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Norm(GetString(custom[i], "root")), Norm(provider.Root), StringComparison.OrdinalIgnoreCase))
                {
                    custom[i] = ProviderToMap(provider);
                    found = true;
                    break;
                }
            }
            if (!found) custom.Add(ProviderToMap(provider));
            cfg["customProviders"] = custom;
            WriteObject(ConfigPath, cfg);
        }

        public static void RemoveCustomProvider(string id)
        {
            Dictionary<string, object> cfg = ReadConfig();
            List<Dictionary<string, object>> custom = GetObjectList(cfg, "customProviders");
            List<Dictionary<string, object>> keep = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> item in custom)
            {
                if (!string.Equals(GetString(item, "id"), id, StringComparison.OrdinalIgnoreCase))
                    keep.Add(item);
            }
            cfg["customProviders"] = keep;
            WriteObject(ConfigPath, cfg);
        }

        static Dictionary<string, object> ProviderToMap(Provider provider)
        {
            Dictionary<string, object> map = new Dictionary<string, object>();
            map["id"] = provider.Id;
            map["kind"] = provider.Kind;
            map["name"] = provider.Name;
            map["root"] = provider.Root;
            map["detail"] = provider.Detail ?? "";
            return map;
        }

        public static List<Provider> DetectProviders()
        {
            List<Provider> list = new List<Provider>();
            DetectGoogle(list);
            DetectOneDrive(list);
            DetectDropbox(list);
            Dictionary<string, object> cfg = ReadConfig();
            foreach (Dictionary<string, object> item in GetObjectList(cfg, "customProviders"))
            {
                Provider provider = new Provider();
                provider.Id = GetString(item, "id");
                provider.Kind = GetString(item, "kind");
                provider.Name = GetString(item, "name");
                provider.Root = GetString(item, "root");
                provider.Detail = GetString(item, "detail");
                provider.Custom = true;
                if (string.IsNullOrEmpty(provider.Id) || string.IsNullOrEmpty(provider.Root)) continue;
                if (string.IsNullOrEmpty(provider.Kind)) provider.Kind = "Folder";
                if (string.IsNullOrEmpty(provider.Name)) provider.Name = new DirectoryInfo(provider.Root).Name;
                if (!ContainsRoot(list, provider.Root) && FindProvider(list, provider.Id) == null) list.Add(provider);
            }
            return list;
        }

        static void DetectGoogle(List<Provider> list)
        {
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch { return; }
            foreach (DriveInfo drive in drives)
            {
                string label = "";
                try
                {
                    if (!drive.IsReady) continue;
                    label = drive.VolumeLabel ?? "";
                }
                catch { continue; }
                int at = label.IndexOf('@');
                if (at < 0) continue;
                string detail = label.Trim();
                int space = detail.IndexOf(' ');
                if (space > 0) detail = detail.Substring(0, space).Trim();
                string root = Path.Combine(drive.RootDirectory.FullName, "My Drive");
                if (!Directory.Exists(root)) root = drive.RootDirectory.FullName;
                Provider provider = new Provider();
                provider.Id = "google:" + detail.ToLowerInvariant();
                provider.Kind = "GoogleDrive";
                provider.Name = "Google Drive";
                provider.Root = root;
                provider.Detail = detail + " (" + drive.Name.TrimEnd('\\') + ")";
                if (!ContainsRoot(list, root)) list.Add(provider);
            }
        }

        static void DetectOneDrive(List<Provider> list)
        {
            AddOneDrivePath(list, Environment.GetEnvironmentVariable("OneDrive"), "");
            AddOneDrivePath(list, Environment.GetEnvironmentVariable("OneDriveConsumer"), "");
            AddOneDrivePath(list, Environment.GetEnvironmentVariable("OneDriveCommercial"), "");
            try
            {
                string keyPath = @"Software\Microsoft\OneDrive\Accounts";
                using (var accounts = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath))
                {
                    if (accounts == null) return;
                    foreach (string name in accounts.GetSubKeyNames())
                    {
                        using (var account = accounts.OpenSubKey(name))
                        {
                            if (account == null) continue;
                            string folder = account.GetValue("UserFolder") as string;
                            string email = account.GetValue("UserEmail") as string;
                            AddOneDrivePath(list, folder, email ?? "");
                        }
                    }
                }
            }
            catch { }
        }

        static void AddOneDrivePath(List<Provider> list, string path, string email)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            if (ContainsRoot(list, path)) return;
            Provider provider = new Provider();
            provider.Kind = "OneDrive";
            provider.Name = "OneDrive";
            provider.Root = Path.GetFullPath(path).TrimEnd('\\');
            provider.Detail = string.IsNullOrEmpty(email) ? provider.Root : email;
            provider.Id = "onedrive:" + (string.IsNullOrEmpty(email) ? provider.Root : email).ToLowerInvariant();
            list.Add(provider);
        }

        static void DetectDropbox(List<Provider> list)
        {
            string[] infoFiles = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dropbox", "info.json"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dropbox", "info.json")
            };
            foreach (string file in infoFiles)
            {
                if (!File.Exists(file)) continue;
                string text;
                try { text = File.ReadAllText(file); }
                catch { continue; }
                int search = 0;
                while (search < text.Length)
                {
                    int marker = text.IndexOf("\"path\"", search, StringComparison.OrdinalIgnoreCase);
                    if (marker < 0) break;
                    int colon = text.IndexOf(':', marker);
                    int quote = colon < 0 ? -1 : text.IndexOf('"', colon + 1);
                    int end = quote < 0 ? -1 : text.IndexOf('"', quote + 1);
                    if (quote < 0 || end < 0) break;
                    string path = text.Substring(quote + 1, end - quote - 1).Replace("\\\\", "\\").Replace("\\/", "/");
                    AddDropbox(list, path);
                    search = end + 1;
                }
            }
            string[] hostFiles = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dropbox", "host.db"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dropbox", "host.db")
            };
            foreach (string file in hostFiles)
            {
                if (!File.Exists(file)) continue;
                try
                {
                    string[] lines = File.ReadAllLines(file);
                    if (lines.Length >= 2)
                    {
                        byte[] bytes = Convert.FromBase64String(lines[1].Trim());
                        AddDropbox(list, Encoding.UTF8.GetString(bytes));
                    }
                }
                catch { }
            }
            AddDropbox(list, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Dropbox"));
        }

        static void AddDropbox(List<Provider> list, string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            path = Path.GetFullPath(path).TrimEnd('\\');
            if (ContainsRoot(list, path)) return;
            Provider provider = new Provider();
            provider.Id = "dropbox:" + path.ToLowerInvariant();
            provider.Kind = "Dropbox";
            provider.Name = "Dropbox";
            provider.Root = path;
            provider.Detail = path;
            list.Add(provider);
        }

        static bool ContainsRoot(List<Provider> list, string root)
        {
            string norm = Norm(root);
            foreach (Provider provider in list)
            {
                if (string.Equals(Norm(provider.Root), norm, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static Provider FindProvider(List<Provider> providers, string id)
        {
            if (providers == null || string.IsNullOrEmpty(id)) return null;
            foreach (Provider provider in providers)
            {
                if (string.Equals(provider.Id, id, StringComparison.OrdinalIgnoreCase)) return provider;
            }
            return null;
        }

        public static string ResolveRemote(LinkFolder folder, List<Provider> providers)
        {
            if (folder == null) return null;
            Provider provider = FindProvider(providers, folder.ProviderId);
            if (provider == null || string.IsNullOrEmpty(provider.Root)) return null;
            if (string.IsNullOrEmpty(folder.RemoteRelative)) return provider.Root;
            string remote = Path.GetFullPath(Path.Combine(provider.Root, folder.RemoteRelative));
            return IsUnder(remote, provider.Root) ? remote : null;
        }

        public static List<LinkFolder> ReadLinks()
        {
            EnsureDirs();
            MigrateLegacy();
            List<LinkFolder> list = new List<LinkFolder>();
            Dictionary<string, object> root = ReadObject(ManifestPath);
            if (root == null) return list;
            foreach (Dictionary<string, object> item in GetObjectList(root, "folders"))
            {
                LinkFolder folder = new LinkFolder();
                folder.Id = GetString(item, "id");
                folder.Local = GetString(item, "local");
                folder.ProviderId = GetString(item, "providerId");
                folder.RemoteRelative = GetString(item, "remoteRelative");
                folder.Added = GetString(item, "added");
                folder.LastSync = GetString(item, "lastSync");
                folder.LastErrors = GetInt(item, "lastErrors");
                folder.Partial = GetBool(item, "partial");
                folder.Files = GetStringList(item, "files");
                folder.Dirs = GetStringList(item, "dirs");
                if (string.IsNullOrEmpty(folder.Id) || string.IsNullOrEmpty(folder.Local)) continue;
                list.Add(folder);
            }
            return list;
        }

        public static void SaveLinks(List<LinkFolder> links)
        {
            EnsureDirs();
            List<Dictionary<string, object>> folders = new List<Dictionary<string, object>>();
            foreach (LinkFolder folder in links)
            {
                Dictionary<string, object> item = new Dictionary<string, object>();
                item["id"] = folder.Id;
                item["local"] = folder.Local;
                item["providerId"] = folder.ProviderId ?? "";
                item["remoteRelative"] = folder.RemoteRelative ?? "";
                item["added"] = folder.Added ?? "";
                item["lastSync"] = folder.LastSync ?? "";
                item["lastErrors"] = folder.LastErrors;
                item["partial"] = folder.Partial;
                item["files"] = folder.Files ?? new List<string>();
                item["dirs"] = folder.Dirs ?? new List<string>();
                folders.Add(item);
            }
            Dictionary<string, object> root = new Dictionary<string, object>();
            root["folders"] = folders;
            WriteObject(ManifestPath, root);
        }

        public static string StatePath(string id)
        {
            return Path.Combine(StateDir, id + ".json");
        }

        public static Dictionary<string, Snap> ReadState(string path)
        {
            Dictionary<string, Snap> map = new Dictionary<string, Snap>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> root = ReadObject(path);
            if (root == null) return map;
            foreach (Dictionary<string, object> item in GetObjectList(root, "files"))
            {
                string rel = GetString(item, "rel");
                if (string.IsNullOrEmpty(rel)) continue;
                Snap snap = new Snap();
                snap.Ll = NullIfEmpty(GetString(item, "ll"));
                snap.Lm = NullIfEmpty(GetString(item, "lm"));
                snap.Rl = NullIfEmpty(GetString(item, "rl"));
                snap.Rm = NullIfEmpty(GetString(item, "rm"));
                snap.Pending = GetBool(item, "pending");
                snap.Hold = GetBool(item, "hold");
                snap.Synced = GetString(item, "synced");
                map[rel] = snap;
            }
            return map;
        }

        public static void SaveState(string path, Dictionary<string, Snap> map)
        {
            List<string> keys = new List<string>();
            foreach (string key in map.Keys) keys.Add(key);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> files = new List<Dictionary<string, object>>();
            foreach (string key in keys)
            {
                Snap snap = map[key];
                Dictionary<string, object> item = new Dictionary<string, object>();
                item["rel"] = key;
                item["ll"] = snap.Ll;
                item["lm"] = snap.Lm;
                item["rl"] = snap.Rl;
                item["rm"] = snap.Rm;
                item["pending"] = snap.Pending;
                item["hold"] = snap.Hold;
                item["synced"] = snap.Synced ?? "";
                files.Add(item);
            }
            Dictionary<string, object> root = new Dictionary<string, object>();
            root["files"] = files;
            WriteObject(path, root);
        }

        static void MigrateLegacy()
        {
            if (File.Exists(ManifestPath)) return;
            string oldRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkDriveSync");
            string oldManifest = Path.Combine(oldRoot, "manifest.json");
            if (!File.Exists(oldManifest)) return;
            string email = "";
            Dictionary<string, object> oldCfg = ReadObject(Path.Combine(oldRoot, "config.json"));
            if (oldCfg != null) email = GetString(oldCfg, "accountEmail");
            string providerId = string.IsNullOrEmpty(email) ? "" : "google:" + email.ToLowerInvariant();
            Dictionary<string, object> old = ReadObject(oldManifest);
            if (old == null) return;
            List<Dictionary<string, object>> folders = GetObjectList(old, "folders");
            foreach (Dictionary<string, object> item in folders)
            {
                if (!item.ContainsKey("providerId") || string.IsNullOrEmpty(GetString(item, "providerId")))
                    item["providerId"] = providerId;
            }
            WriteObject(ManifestPath, old);
            string oldState = Path.Combine(oldRoot, "state");
            if (!Directory.Exists(oldState)) return;
            Directory.CreateDirectory(StateDir);
            foreach (string file in Directory.GetFiles(oldState, "*.json"))
            {
                string dest = Path.Combine(StateDir, Path.GetFileName(file));
                if (!File.Exists(dest)) File.Copy(file, dest);
            }
            Log("migrated the previous work Drive folder list");
        }

        public static bool Excluded(string full)
        {
            if (string.IsNullOrEmpty(full)) return true;
            string norm = Norm(full);
            foreach (string prefix in ExcludePaths())
            {
                if (string.IsNullOrEmpty(prefix)) continue;
                string root = Norm(prefix);
                if (string.Equals(norm, root, StringComparison.OrdinalIgnoreCase)) return true;
                if (norm.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static string Norm(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try
            {
                string full = Path.GetFullPath(path);
                string root = Path.GetPathRoot(full);
                return string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ? full : full.TrimEnd('\\');
            }
            catch { return path.TrimEnd('\\'); }
        }

        public static bool IsUnder(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            string left = Norm(path);
            string right = Norm(root);
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;
            return left.StartsWith(right.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        public static string Relative(string path, string root)
        {
            string left = Norm(path);
            string right = Norm(root);
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return "";
            string prefix = right.TrimEnd('\\') + "\\";
            if (!left.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            return left.Substring(prefix.Length);
        }

        static string NullIfEmpty(string value)
        {
            return string.IsNullOrEmpty(value) ? null : value;
        }

        public static string GetString(Dictionary<string, object> map, string key)
        {
            if (map == null || !map.ContainsKey(key) || map[key] == null) return "";
            return Convert.ToString(map[key]);
        }

        public static int GetInt(Dictionary<string, object> map, string key)
        {
            if (map == null || !map.ContainsKey(key) || map[key] == null) return 0;
            try { return Convert.ToInt32(map[key]); }
            catch { return 0; }
        }

        public static bool GetBool(Dictionary<string, object> map, string key)
        {
            if (map == null || !map.ContainsKey(key) || map[key] == null) return false;
            object value = map[key];
            if (value is bool) return (bool)value;
            bool parsed;
            if (bool.TryParse(Convert.ToString(value), out parsed)) return parsed;
            return false;
        }

        public static List<string> GetStringList(Dictionary<string, object> map, string key)
        {
            List<string> list = new List<string>();
            if (map == null || !map.ContainsKey(key) || map[key] == null) return list;
            foreach (object item in AsEnumerable(map[key]))
            {
                if (item != null) list.Add(Convert.ToString(item));
            }
            return list;
        }

        public static List<Dictionary<string, object>> GetObjectList(Dictionary<string, object> map, string key)
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            if (map == null || !map.ContainsKey(key) || map[key] == null) return list;
            foreach (object item in AsEnumerable(map[key]))
            {
                Dictionary<string, object> child = item as Dictionary<string, object>;
                if (child != null) list.Add(child);
            }
            return list;
        }

        static IEnumerable AsEnumerable(object value)
        {
            IEnumerable enumerable = value as IEnumerable;
            if (enumerable == null || value is string) return new object[0];
            return enumerable;
        }

        // Missing or corrupt files read as null; a corrupt file is kept beside it for recovery.
        // A file that stays locked throws, so callers never save an empty list over real data.
        public static Dictionary<string, object> ReadObject(string path)
        {
            string tmp = path + ".tmp";
            if (!File.Exists(path) && File.Exists(tmp))
            {
                try { File.Move(tmp, path); }
                catch { }
            }
            if (!File.Exists(path)) return null;
            string json = null;
            for (int attempt = 1; json == null; attempt++)
            {
                try { json = File.ReadAllText(path, Encoding.UTF8); }
                catch (IOException ex)
                {
                    if (attempt >= 5) throw new IOException("Could not read " + path + ". " + ex.Message, ex);
                    System.Threading.Thread.Sleep(200);
                }
            }
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = int.MaxValue;
                return serializer.Deserialize<Dictionary<string, object>>(json);
            }
            catch (Exception ex)
            {
                Log("could not read " + path + ": " + ex.Message);
                try { File.Copy(path, path + ".corrupt", true); }
                catch { }
                return null;
            }
        }

        public static void WriteObject(string path, object value)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            string json = serializer.Serialize(value);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (!File.Exists(path))
            {
                File.Move(tmp, path);
                return;
            }
            try { File.Replace(tmp, path, null, true); }
            catch (IOException)
            {
                File.Delete(path);
                File.Move(tmp, path);
            }
        }
    }
}
