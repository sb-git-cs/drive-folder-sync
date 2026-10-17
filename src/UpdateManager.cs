using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;

namespace DriveFolderSync
{
    public static class UpdateManager
    {
        const string Repository = "https://github.com/sb-git-cs/drive-folder-sync";
        const string Latest = "https://api.github.com/repos/sb-git-cs/drive-folder-sync/releases/latest";

        public static bool AutomaticEnabled
        {
            get
            {
                Dictionary<string, object> config = Store.ReadConfig();
                return !config.ContainsKey("autoUpdate") || Store.GetBool(config, "autoUpdate");
            }
        }

        public static void SetAutomatic(bool enabled)
        {
            lock (Store.ConfigGate)
            {
                Dictionary<string, object> config = Store.ReadConfig();
                config["autoUpdate"] = enabled;
                Store.WriteObject(Store.ConfigPath, config);
            }
        }

        public static bool IsNewer(string tag, string current)
        {
            Version candidate;
            Version installed;
            return Version.TryParse((tag ?? "").TrimStart('v'), out candidate)
                && Version.TryParse(current, out installed) && candidate > installed;
        }

        public static void VerifyInstaller(string path, string digest)
        {
            if (string.IsNullOrEmpty(digest) || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71)
                throw new IOException("The release has no SHA-256 checksum.");
            string actual;
            using (SHA256 sha = SHA256.Create())
            using (FileStream input = File.OpenRead(path))
                actual = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
            if (!string.Equals(actual, digest.Substring(7), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The update checksum did not match. Your current installation was kept.");
        }

        static HttpWebRequest Request(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "DriveFolderSync/" + Store.Version;
            request.Accept = "application/vnd.github+json";
            // GitHub hosts resolve to several addresses. When the first one is unreachable, Windows
            // takes about 21 seconds to give up on it before the next one is tried.
            request.Timeout = 90000;
            request.ReadWriteTimeout = 15000;
            return request;
        }

        public static bool CheckAndInstall(Action<string> report, Func<bool> mayInstall)
        {
            using (Mutex mutex = new Mutex(false, @"Local\DriveFolderSyncUpdates"))
            {
                bool owned;
                try { owned = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) return false;
                try
                {
                    Dictionary<string, object> release;
                    using (WebResponse response = Request(Latest).GetResponse())
                    using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                        release = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                    if (Store.GetBool(release, "draft") || Store.GetBool(release, "prerelease")
                        || !IsNewer(Store.GetString(release, "tag_name"), Store.Version))
                    {
                        report("Drive Folder Sync is up to date");
                        return false;
                    }
                    string url = "";
                    string digest = "";
                    foreach (Dictionary<string, object> asset in Store.GetObjectList(release, "assets"))
                    {
                        if (Store.GetString(asset, "name") != "DriveFolderSyncSetup.exe") continue;
                        url = Store.GetString(asset, "browser_download_url");
                        digest = Store.GetString(asset, "digest");
                    }
                    if (!url.StartsWith(Repository + "/releases/download/", StringComparison.Ordinal)
                        || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71)
                        throw new IOException("The release has no verified installer. Try again after it finishes publishing.");
                    report("Downloading Drive Folder Sync " + Store.GetString(release, "tag_name") + "…");
                    string directory = Path.Combine(Store.Root, "updates");
                    Directory.CreateDirectory(directory);
                    string installer = Path.Combine(directory, "DriveFolderSyncUpdate.exe");
                    string pending = installer + ".download";
                    try
                    {
                        using (WebResponse response = Request(url).GetResponse())
                        using (Stream input = response.GetResponseStream())
                        using (FileStream output = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            byte[] buffer = new byte[65536];
                            int count;
                            long total = 0;
                            DateTime deadline = DateTime.UtcNow.AddMinutes(3);
                            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                total += count;
                                if (total > 100L * 1024 * 1024 || DateTime.UtcNow > deadline)
                                    throw new IOException("The update download exceeded its size or time limit.");
                                output.Write(buffer, 0, count);
                            }
                        }
                        VerifyInstaller(pending, digest);
                        using (SyncEngine.LockState(0))
                        {
                            if (!mayInstall()) { report("Update deferred until the next restart"); return false; }
                            if (File.Exists(installer)) File.Delete(installer);
                            File.Move(pending, installer);
                            Store.Log("installing verified update " + Store.GetString(release, "tag_name"));
                            Process.Start(new ProcessStartInfo(installer, "/install /quiet /update")
                            {
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                WindowStyle = ProcessWindowStyle.Hidden
                            });
                        }
                        report("Installing update; the app will restart…");
                        return true;
                    }
                    finally
                    {
                        try { if (File.Exists(pending)) File.Delete(pending); }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    Store.Log("update deferred: " + ex.Message);
                    if (ex is WebException)
                        report("Couldn't reach GitHub to download the update. The current app remains available; try again later.");
                    else report("Update deferred; the current app remains available. " + ex.Message);
                    return false;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
