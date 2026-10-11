using System;
using System.Collections.Generic;
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

                Provider sample = new Provider();
                sample.Name = "Google Drive";
                sample.Kind = "GoogleDrive";
                sample.Detail = "work@example.com (H:)";
                sample.Root = @"H:\My Drive";
                if (Store.ReplicaLeaf(sample) != "work@example.com") throw new Exception("replica folder name was not the cloud account");
                string replica = Store.ReplicaPath(sample, @"docs\a");
                if (!replica.EndsWith(@"work@example.com\docs\a", StringComparison.OrdinalIgnoreCase)) throw new Exception("replica path did not keep the cloud structure");
                string cloudDir = Path.Combine(root, "cloud-tree");
                string localDir = Path.Combine(root, "local-tree");
                Directory.CreateDirectory(Path.Combine(cloudDir, "project", "notes"));
                int made = SyncEngine.MirrorDirectories(cloudDir, localDir);
                if (!Directory.Exists(Path.Combine(localDir, "project", "notes"))) throw new Exception("local replica did not keep the cloud folders");
                if (made < 1) throw new Exception("mirror did not create folders");

                string selCloud = Path.Combine(root, "sel-cloud");
                string selLocal = Path.Combine(root, "sel-local");
                Directory.CreateDirectory(Path.Combine(selCloud, "a"));
                Directory.CreateDirectory(Path.Combine(selCloud, "b"));
                File.WriteAllText(Path.Combine(selCloud, "a", "one.txt"), "one");
                File.WriteAllText(Path.Combine(selCloud, "a", "two.txt"), "two");
                File.WriteAllText(Path.Combine(selCloud, "b", "skip.txt"), "skip");
                List<string> selDirs = new List<string>();
                selDirs.Add("b");
                List<string> selFiles = new List<string>();
                selFiles.Add(@"a\one.txt");
                SyncEngine.MaterializeSelection(selCloud, selLocal, selDirs, selFiles);
                if (File.ReadAllText(Path.Combine(selLocal, "a", "one.txt")) != "one") throw new Exception("selected file was not copied");
                if (File.Exists(Path.Combine(selLocal, "a", "two.txt"))) throw new Exception("unselected file in the same folder was copied");
                if (!Directory.Exists(Path.Combine(selLocal, "b"))) throw new Exception("selected folder was not created");
                if (File.Exists(Path.Combine(selLocal, "b", "skip.txt"))) throw new Exception("files inside a selected folder were copied");

                string pullLocal = Path.Combine(root, "pull-local");
                string pullRemote = Path.Combine(root, "pull-remote");
                Directory.CreateDirectory(pullRemote);
                File.WriteAllText(Path.Combine(pullRemote, "keep.txt"), "keep");
                File.WriteAllText(Path.Combine(pullRemote, "other.txt"), "other");
                string pullState = Path.Combine(root, "pull-state.json");
                List<string> allow = new List<string>();
                allow.Add("keep.txt");
                SyncStats limited = SyncEngine.SyncFolder("Pull", pullLocal, pullRemote, pullState, true, allow);
                if (limited.Down != 1) throw new Exception("allow list downloaded " + limited.Down + " files");
                if (File.ReadAllText(Path.Combine(pullLocal, "keep.txt")) != "keep") throw new Exception("allowed file was not downloaded");
                if (File.Exists(Path.Combine(pullLocal, "other.txt"))) throw new Exception("file outside the allow list was downloaded");
                File.WriteAllText(Path.Combine(pullLocal, "extra.txt"), "extra");
                SyncEngine.SyncFolder("Push", pullLocal, pullRemote, pullState, true, allow);
                if (File.Exists(Path.Combine(pullRemote, "extra.txt"))) throw new Exception("file outside the allow list was uploaded");
                List<string> none = new List<string>();
                string emptyLocal = Path.Combine(root, "empty-local");
                SyncStats emptyPull = SyncEngine.SyncFolder("Pull", emptyLocal, pullRemote, Path.Combine(root, "empty-state.json"), true, none);
                if (emptyPull.Down != 0) throw new Exception("empty allow list downloaded files");
                if (File.Exists(Path.Combine(emptyLocal, "keep.txt")) || File.Exists(Path.Combine(emptyLocal, "other.txt")))
                    throw new Exception("empty allow list created files");

                LinkFolder remembered = SyncEngine.RememberSelection("missing-provider", selLocal, selDirs, selFiles);
                if (remembered == null || !remembered.Partial) throw new Exception("selection was not stored as a partial replica");
                if (remembered.Files.Count != 1 || remembered.Dirs.Count != 1) throw new Exception("selection did not keep the chosen file and folder");
                SyncEngine.RememberSelection("missing-provider", selLocal, selDirs, selFiles);
                int partials = 0;
                foreach (LinkFolder link in Store.ReadLinks())
                {
                    if (link.Partial) partials++;
                }
                if (partials != 1) throw new Exception("selecting the same items created another replica link");

                string dropCloud = Path.Combine(root, "drop-cloud");
                string dropLocal = Path.Combine(root, "drop-local");
                Directory.CreateDirectory(Path.Combine(dropCloud, "a"));
                Directory.CreateDirectory(Path.Combine(dropCloud, "b"));
                File.WriteAllText(Path.Combine(dropCloud, "a", "one.txt"), "one");
                File.WriteAllText(Path.Combine(dropCloud, "a", "two.txt"), "two");
                File.WriteAllText(Path.Combine(dropCloud, "b", "skip.txt"), "skip");
                List<string> dropDirs = new List<string>();
                List<string> dropFiles = new List<string>();
                SyncEngine.CopyInto(Path.Combine(dropCloud, "a", "one.txt"), dropLocal, @"a\one.txt", dropDirs, dropFiles);
                if (File.ReadAllText(Path.Combine(dropLocal, "a", "one.txt")) != "one") throw new Exception("dropped file did not keep its folder path");
                if (File.Exists(Path.Combine(dropLocal, "a", "two.txt"))) throw new Exception("a file that was not dropped was copied");
                dropDirs.Clear();
                SyncEngine.CopyInto(Path.Combine(dropCloud, "b"), dropLocal, "b", dropDirs, dropFiles);
                if (File.ReadAllText(Path.Combine(dropLocal, "b", "skip.txt")) != "skip") throw new Exception("dropped folder did not keep its files");
                if (dropFiles.Count < 2) throw new Exception("dropped folder did not record its files");

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
