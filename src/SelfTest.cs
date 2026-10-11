using System;
using System.Globalization;
using System.IO;

namespace DriveFolderSync
{
    public static class SelfTest
    {
        public static int Run(string resultPath)
        {
            string root = Path.Combine(Path.GetTempPath(), "dfs-test-" + Guid.NewGuid().ToString("n"));
            string log = Path.Combine(root, "test.log");
            Directory.CreateDirectory(root);
            Store.SetOverrides(root, log);
            try
            {
                string local = Path.Combine(root, "local");
                string remote = Path.Combine(root, "remote");
                string state = Path.Combine(root, "state.json");
                Directory.CreateDirectory(local);
                Directory.CreateDirectory(remote);
                DateTime stamp = DateTime.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
                string sameL = Path.Combine(local, "same.txt");
                string sameR = Path.Combine(remote, "same.txt");
                File.WriteAllText(sameL, "same");
                File.WriteAllText(sameR, "same");
                File.SetLastWriteTimeUtc(sameL, stamp);
                File.SetLastWriteTimeUtc(sameR, stamp);
                SyncEngine.Initialize(local, remote, state);
                SyncStats pull = SyncEngine.SyncFolder("Pull", local, remote, state, true);
                SyncStats push = SyncEngine.SyncFolder("Push", local, remote, state, true);
                if (pull.Down != 0 || push.Up != 0) throw new Exception("identical files were transferred");

                string only = Path.Combine(local, "only-local.txt");
                File.WriteAllText(only, "local-only");
                push = SyncEngine.SyncFolder("Push", local, remote, state, true);
                if (push.Up != 1) throw new Exception("expected 1 upload, got " + push.Up);
                if (File.ReadAllText(Path.Combine(remote, "only-local.txt")) != "local-only") throw new Exception("upload content mismatch");
                push = SyncEngine.SyncFolder("Push", local, remote, state, true);
                if (push.Up != 0) throw new Exception("second push repeated the upload");

                string cloud = Path.Combine(remote, "from-drive.txt");
                File.WriteAllText(cloud, "from-drive");
                pull = SyncEngine.SyncFolder("Pull", local, remote, state, true);
                if (pull.Down != 1) throw new Exception("expected 1 download, got " + pull.Down);
                if (File.ReadAllText(Path.Combine(local, "from-drive.txt")) != "from-drive") throw new Exception("download content mismatch");

                File.WriteAllText(only, "local-edited");
                File.SetLastWriteTimeUtc(only, DateTime.UtcNow);
                push = SyncEngine.SyncFolder("Push", local, remote, state, true);
                if (File.ReadAllText(Path.Combine(remote, "only-local.txt")) != "local-edited") throw new Exception("edit was not uploaded");

                File.WriteAllText(cloud, "drive-edited");
                File.SetLastWriteTimeUtc(cloud, DateTime.UtcNow.AddMinutes(1));
                pull = SyncEngine.SyncFolder("Pull", local, remote, state, true);
                if (File.ReadAllText(Path.Combine(local, "from-drive.txt")) != "drive-edited") throw new Exception("edit was not downloaded");

                File.Delete(only);
                push = SyncEngine.SyncFolder("Push", local, remote, state, true);
                if (File.Exists(Path.Combine(remote, "only-local.txt"))) throw new Exception("local delete was not applied on the cloud side");

                File.Delete(cloud);
                pull = SyncEngine.SyncFolder("Pull", local, remote, state, true);
                if (File.Exists(Path.Combine(local, "from-drive.txt"))) throw new Exception("cloud delete was not applied locally");

                string divL = Path.Combine(local, "diverged.txt");
                string divR = Path.Combine(remote, "diverged.txt");
                File.WriteAllText(divL, "aaa");
                File.WriteAllText(divR, "bbbb");
                SyncEngine.Initialize(local, remote, state);
                SyncEngine.SyncFolder("Push", local, remote, state, true);
                SyncEngine.SyncFolder("Pull", local, remote, state, true);
                if (File.ReadAllText(divL) != "aaa") throw new Exception("diverged local file was overwritten");
                if (File.ReadAllText(divR) != "bbbb") throw new Exception("diverged cloud file was overwritten");

                Directory.CreateDirectory(Path.Combine(local, "node_modules"));
                File.WriteAllText(Path.Combine(local, "node_modules", "secret.txt"), "nope");
                File.WriteAllText(Path.Combine(local, "~$open.docx"), "lock");
                SyncEngine.SyncFolder("Push", local, remote, state, true);
                if (File.Exists(Path.Combine(remote, "node_modules", "secret.txt"))) throw new Exception("node_modules was uploaded");
                if (File.Exists(Path.Combine(remote, "~$open.docx"))) throw new Exception("office lock file was uploaded");

                string pendingLocal = Path.Combine(local, "pending.txt");
                File.WriteAllText(pendingLocal, "pending-v1");
                SyncEngine.SyncFolder("Push", local, remote, state, false);
                string pendingRemote = Path.Combine(remote, "pending.txt");
                if (File.ReadAllText(pendingRemote) != "pending-v1") throw new Exception("pending upload did not copy");
                File.WriteAllText(pendingRemote, "old");
                File.SetLastWriteTimeUtc(pendingRemote, stamp);
                SyncEngine.SyncFolder("Pull", local, remote, state, false);
                if (File.ReadAllText(pendingLocal) != "pending-v1") throw new Exception("older cloud file overwrote a pending local file");

                Directory.Delete(remote, true);
                string before = File.ReadAllText(sameL);
                pull = SyncEngine.SyncFolder("Pull", local, remote, state, true);
                if (File.ReadAllText(sameL) != before) throw new Exception("missing cloud folder changed local files");
                if (pull.Errors < 1) throw new Exception("missing cloud folder did not report an error");

                FileRecord localRec = new FileRecord();
                localRec.Length = 4;
                localRec.MtimeUtc = DateTime.UtcNow;
                Snap snap = new Snap();
                snap.Ll = "4";
                snap.Lm = SyncEngine.Stamp(localRec.MtimeUtc);
                snap.Rl = "4";
                snap.Rm = SyncEngine.Stamp(localRec.MtimeUtc);
                SyncEngine.RowStatus changed = SyncEngine.Classify(localRec, null, snap);
                if (changed.Kind != "local") throw new Exception("local change was not marked green");
                FileRecord remoteRec = new FileRecord();
                remoteRec.Length = 9;
                remoteRec.MtimeUtc = DateTime.UtcNow;
                snap.Rl = "2";
                snap.Rm = SyncEngine.Stamp(DateTime.UtcNow.AddMinutes(-10));
                SyncEngine.RowStatus cloudRow = SyncEngine.Classify(localRec, remoteRec, snap);
                if (cloudRow.Kind != "cloud") throw new Exception("cloud change was not marked red");

                File.WriteAllText(resultPath, "self-test passed");
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(resultPath, "self-test failed: " + ex.Message);
                return 1;
            }
            finally
            {
                Store.SetOverrides(null, null);
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch { }
            }
        }
    }
}
