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
                if (!File.Exists(Path.Combine(remote, "only-local.txt")) || push.Del != 0) throw new Exception("local deletion removed the cloud backup");
                FileRecord retained = new FileRecord();
                retained.Full = Path.Combine(remote, "only-local.txt");
                retained.Length = new FileInfo(retained.Full).Length;
                retained.MtimeUtc = File.GetLastWriteTimeUtc(retained.Full);
                SyncEngine.RowStatus retainedStatus = SyncEngine.Classify(null, retained, Store.ReadState(state)["only-local.txt"]);
                if (retainedStatus.Text != "Only in cloud" || retainedStatus.Kind != "cloud") throw new Exception("retained cloud file was not highlighted");
                pull = SyncEngine.SyncFolder("Pull", local, remote, state, true);
                if (File.ReadAllText(only) != "local-edited") throw new Exception("startup refresh did not restore the retained cloud file");

                File.Delete(cloud);
                FileRecord missingLocal = new FileRecord();
                missingLocal.Full = Path.Combine(local, "from-drive.txt");
                missingLocal.Length = new FileInfo(missingLocal.Full).Length;
                missingLocal.MtimeUtc = File.GetLastWriteTimeUtc(missingLocal.Full);
                Snap missingSnap = Store.ReadState(state)["from-drive.txt"];
                if (string.IsNullOrEmpty(missingSnap.Synced)) throw new Exception("missing cloud regression has no prior sync time");
                for (int variant = 0; variant < 4; variant++)
                {
                    missingSnap.Pending = variant == 1;
                    missingSnap.Hold = variant == 2;
                    if (variant == 3) { missingSnap.Rl = ""; missingSnap.Synced = ""; }
                    SyncEngine.RowStatus missingStatus = SyncEngine.Classify(missingLocal, null, missingSnap);
                    if (missingStatus.Kind == "same" || missingStatus.Text == "In sync" || missingStatus.LastSynced != "")
                        throw new Exception("missing cloud copy was shown as synced, variant " + variant);
                }
                if (SyncEngine.Classify(null, null, missingSnap).Kind == "same") throw new Exception("absent copies were shown as synced");
                pull = SyncEngine.SyncFolder("Pull", local, remote, state, true);
                if (!File.Exists(Path.Combine(local, "from-drive.txt")) || pull.Del != 0) throw new Exception("a cloud deletion removed the local file");
                if (SyncEngine.Classify(missingLocal, null, Store.ReadState(state)["from-drive.txt"]).Text != "Missing in the cloud")
                    throw new Exception("a file deleted in the cloud was not shown as missing in the cloud");
                push = SyncEngine.SyncFolder("Push", local, remote, state, true);
                if (File.Exists(cloud) || !File.Exists(Path.Combine(local, "from-drive.txt"))) throw new Exception("backup recreated a cloud-deleted file or removed the local copy");
                File.Delete(Path.Combine(local, "from-drive.txt"));

                string massLocal = Path.Combine(root, "mass-local");
                string massRemote = Path.Combine(root, "mass-remote");
                string massState = Path.Combine(root, "mass-state.json");
                Directory.CreateDirectory(massRemote);
                for (int i = 0; i < 12; i++) File.WriteAllText(Path.Combine(massRemote, "file" + i + ".txt"), "mass " + i);
                SyncEngine.SyncFolder("Pull", massLocal, massRemote, massState, true);
                File.Delete(Path.Combine(massRemote, "file3.txt"));
                pull = SyncEngine.SyncFolder("Pull", massLocal, massRemote, massState, true);
                if (Directory.GetFiles(massLocal).Length != 12 || pull.Del != 0) throw new Exception("a single cloud deletion removed a local file");
                foreach (string file in Directory.GetFiles(massRemote)) File.Delete(file);
                pull = SyncEngine.SyncFolder("Pull", massLocal, massRemote, massState, false);
                if (Directory.GetFiles(massLocal).Length != 12 || pull.Del != 0) throw new Exception("an emptied cloud folder removed local files");
                Directory.Delete(massRemote, true);
                SyncEngine.SyncFolder("Pull", massLocal, massRemote, massState, true);
                if (Directory.GetFiles(massLocal).Length != 12) throw new Exception("a deleted cloud folder removed local files");

                string manifest = Path.Combine(root, "atomic.json");
                Dictionary<string, object> saved = new Dictionary<string, object>();
                saved["value"] = "first";
                Store.WriteObject(manifest, saved);
                saved["value"] = "second";
                Store.WriteObject(manifest, saved);
                if (Store.GetString(Store.ReadObject(manifest), "value") != "second") throw new Exception("replacing a saved file lost its contents");
                File.Move(manifest, manifest + ".tmp");
                if (Store.GetString(Store.ReadObject(manifest), "value") != "second") throw new Exception("an interrupted save was not recovered");
                File.WriteAllText(manifest, "{ not json");
                if (Store.ReadObject(manifest) != null || !File.Exists(manifest + ".corrupt")) throw new Exception("a corrupt file was not set aside");

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

                string progressLocal = Path.Combine(root, "progress-local");
                string progressCloud = Path.Combine(root, "progress-cloud");
                Directory.CreateDirectory(Path.Combine(progressLocal, "sub"));
                File.WriteAllBytes(Path.Combine(progressLocal, "large.bin"), new byte[3 * 1024 * 1024 + 17]);
                File.WriteAllText(Path.Combine(progressLocal, "sub", "small.txt"), "small");
                string progressState = Path.Combine(root, "progress-state.json");
                SyncProgress measured = new SyncProgress("Push");
                SyncEngine.SyncFolder("Push", progressLocal, progressCloud, progressState, true, null, measured);
                long expected = 3 * 1024 * 1024 + 17 + 5;
                if (measured.TotalFiles != 2 || measured.DoneFiles != 2 || measured.TotalBytes != expected || measured.DoneBytes != expected || measured.Percent != 100)
                    throw new Exception("sync progress did not add up: " + measured.DoneFiles + "/" + measured.TotalFiles + " files, " + measured.DoneBytes + "/" + measured.TotalBytes + " bytes");
                if (new FileInfo(Path.Combine(progressCloud, "large.bin")).Length != 3 * 1024 * 1024 + 17) throw new Exception("progress copy changed the file size");
                SyncProgress idle = new SyncProgress("Push");
                SyncEngine.SyncFolder("Push", progressLocal, progressCloud, progressState, true, null, idle);
                if (idle.TotalFiles != 0 || idle.Percent != 100) throw new Exception("an up-to-date folder reported work to copy");
                if (SyncProgress.TimeLeft(30) != "about 30 s left" || SyncProgress.TimeLeft(150) != "about 3 min left" || SyncProgress.TimeLeft(4000) != "about 1 h 7 min left")
                    throw new Exception("time left was formatted incorrectly");

                string providerRoot = Path.Combine(root, "provider");
                Directory.CreateDirectory(providerRoot);
                Provider attachedProvider = new Provider();
                attachedProvider.Id = "test:provider";
                attachedProvider.Name = "Test Cloud";
                attachedProvider.Kind = "Folder";
                attachedProvider.Root = providerRoot;
                Store.SaveCustomProvider(attachedProvider);
                string arbitraryLocal = Path.Combine(root, "another-drive", "project-documents");
                Directory.CreateDirectory(Path.Combine(arbitraryLocal, "notes"));
                File.WriteAllText(Path.Combine(arbitraryLocal, "notes", "project.txt"), "project");
                Directory.CreateDirectory(Path.Combine(arbitraryLocal, "empty"));
                LinkFolder attachment = SyncEngine.Link(arbitraryLocal, attachedProvider.Id, @"backups\project-documents");
                string attachmentRemote = Store.ResolveRemote(attachment, Store.DetectProviders());
                if (Directory.Exists(attachmentRemote)) throw new Exception("linking copied or created a cloud destination before backup");
                SyncStats initialBackup = SyncEngine.SyncLink(attachment, "Push", true);
                if (initialBackup.Errors != 0 || File.ReadAllText(Path.Combine(attachmentRemote, "notes", "project.txt")) != "project")
                    throw new Exception("arbitrary local folder did not back up to the new destination");
                if (!Directory.Exists(Path.Combine(attachmentRemote, "empty"))) throw new Exception("empty local folder did not back up");
                if (Store.Norm(@"D:\") != @"D:\" || !Store.IsUnder(@"D:\project-documents", @"D:\")
                    || Store.Relative(@"D:\project-documents", @"D:\") != "project-documents")
                    throw new Exception("drive root paths were resolved incorrectly");
                Store.Place localPlace = Store.FindPlace(Path.Combine(arbitraryLocal, "notes"), Store.DetectProviders());
                Store.Place cloudPlace = Store.FindPlace(Path.Combine(attachmentRemote, "notes"), Store.DetectProviders());
                if (localPlace.Link.Id != attachment.Id || cloudPlace.ReplicaPath != localPlace.ReplicaPath
                    || localPlace.LinkRelative != "notes") throw new Exception("folder pair navigation used the default replica path");
                string anotherLocal = Path.Combine(root, "independent");
                Directory.CreateDirectory(anotherLocal);
                SyncEngine.Link(anotherLocal, attachedProvider.Id, "independent");
                bool rejected = false;
                try { SyncEngine.Link(arbitraryLocal, attachedProvider.Id, "different"); } catch (ArgumentException) { rejected = true; }
                if (!rejected) throw new Exception("duplicate local root was accepted");
                rejected = false;
                try { SyncEngine.Link(Path.Combine(root, "other"), attachedProvider.Id, @"backups\project-documents\nested"); } catch (ArgumentException) { rejected = true; }
                if (!rejected) throw new Exception("overlapping cloud destination was accepted");
                rejected = false;
                try { SyncEngine.Link(Path.Combine(root, "escape-local"), attachedProvider.Id, @"..\outside-provider"); } catch (ArgumentException) { rejected = true; }
                if (!rejected) throw new Exception("cloud path traversal was accepted");
                rejected = false;
                try { SyncEngine.Link(Path.Combine(providerRoot, "inside"), attachedProvider.Id, "elsewhere"); }
                catch (ArgumentException ex) { rejected = ex.Message.Contains(providerRoot); }
                if (!rejected) throw new Exception("a cloud folder was accepted as the local root");
                rejected = false;
                try { SyncEngine.Link("relative-folder", attachedProvider.Id, "relative-destination"); } catch (ArgumentException) { rejected = true; }
                if (!rejected) throw new Exception("a relative local path was accepted");

                string existingLocal = Path.Combine(root, "existing-local");
                string existingCloud = Path.Combine(providerRoot, "existing-cloud");
                Directory.CreateDirectory(existingLocal);
                Directory.CreateDirectory(existingCloud);
                string initialL = Path.Combine(existingLocal, "same-size.txt");
                string initialR = Path.Combine(existingCloud, "same-size.txt");
                File.WriteAllText(initialL, "aaaa");
                File.WriteAllText(initialR, "bbbb");
                File.SetLastWriteTimeUtc(initialL, stamp);
                File.SetLastWriteTimeUtc(initialR, stamp);
                LinkFolder existingAttachment = SyncEngine.Link(existingLocal, attachedProvider.Id, "existing-cloud");
                SyncEngine.SyncLink(existingAttachment, "Pull", true);
                SyncStats initialConflict = SyncEngine.SyncLink(existingAttachment, "Push", true);
                if (initialConflict.Conflict == 0 || File.ReadAllText(initialL) != "aaaa" || File.ReadAllText(initialR) != "bbbb")
                    throw new Exception("initial same-size conflict lost a file version");
                FileRecord first = new FileRecord(); first.Full = initialL; first.Length = 4; first.MtimeUtc = stamp;
                FileRecord second = new FileRecord(); second.Full = initialR; second.Length = 4; second.MtimeUtc = stamp;
                Dictionary<string, Snap> attachedState = Store.ReadState(Store.StatePath(existingAttachment.Id));
                if (!attachedState.ContainsKey("same-size.txt") || SyncEngine.Classify(first, second, attachedState["same-size.txt"]).Text != "Needs review")
                    throw new Exception("same-size differing contents were marked synced");
                using (FileStream locked = new FileStream(initialR, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    // Browsing must never open a cloud file: reading one makes the cloud app download it.
                    if (SyncEngine.Classify(first, second, null).Text != "Same size and date") throw new Exception("browsing compared file contents");
                }
                File.WriteAllText(initialR, "aaaa");
                if (SyncEngine.Classify(first, second, null).LastSynced != "") throw new Exception("a modification timestamp was reported as the sync time");
                string failedState = Path.Combine(root, "failed-initial.json");
                SyncEngine.SyncFolder("Pull", existingLocal, existingCloud, failedState, true);
                if (File.ReadAllText(initialL) != "aaaa") throw new Exception("refresh before baseline changed the local file");
                string missingRoot = Path.Combine(root, "not-mounted", "cloud");
                SyncStats unavailable = SyncEngine.SyncFolder("Pull", arbitraryLocal, missingRoot, Store.StatePath(attachment.Id), true);
                if (unavailable.Errors == 0 || !File.Exists(Path.Combine(arbitraryLocal, "notes", "project.txt")))
                    throw new Exception("unavailable root was treated as a deletion");
                string pairedLocalFile = Path.Combine(arbitraryLocal, "notes", "project.txt");
                string pairedCloudFile = Path.Combine(attachmentRemote, "notes", "project.txt");
                DateTime baselineTime = File.GetLastWriteTimeUtc(pairedLocalFile);
                File.WriteAllText(pairedLocalFile, "updated");
                File.SetLastWriteTimeUtc(pairedLocalFile, baselineTime.AddSeconds(1));
                SyncStats quickEdit = SyncEngine.SyncLink(attachment, "Push", true);
                if (quickEdit.Up != 1 || File.ReadAllText(pairedCloudFile) != "updated") throw new Exception("same-size edit within three seconds was skipped");
                File.WriteAllText(pairedLocalFile, "local-v");
                File.SetLastWriteTimeUtc(pairedLocalFile, baselineTime.AddSeconds(2));
                File.WriteAllText(pairedCloudFile, "cloud-v");
                File.SetLastWriteTimeUtc(pairedCloudFile, baselineTime.AddSeconds(3));
                SyncStats simultaneous = SyncEngine.SyncLink(attachment, "Push", true);
                if (simultaneous.Conflict != 1 || File.ReadAllText(pairedLocalFile) != "local-v" || File.ReadAllText(pairedCloudFile) != "cloud-v")
                    throw new Exception("simultaneous edits did not preserve both versions");
                Snap simultaneousState = Store.ReadState(Store.StatePath(attachment.Id))[@"notes\project.txt"];
                if (!simultaneousState.Hold) throw new Exception("a conflict was recorded as in sync");
                File.Delete(Path.Combine(arbitraryLocal, "notes", "project.txt"));
                SyncEngine.SyncLink(attachment, "Push", false);
                SyncEngine.SyncLink(attachment, "Pull", false);
                if (!File.Exists(Path.Combine(arbitraryLocal, "notes", "project.txt"))) throw new Exception("lifecycle backup and refresh did not restore cloud-only content");
                if (Store.ReadLinks().Find(delegate(LinkFolder f) { return f.Id == remembered.Id; }).Files.Count != 1)
                    throw new Exception("legacy partial selection was not preserved");
                if (!UpdateManager.IsNewer("v1.4.0", "1.3.1") || UpdateManager.IsNewer("v1.3.1", "1.4.0")
                    || UpdateManager.IsNewer("v1.4.0-beta", "1.3.1") || UpdateManager.IsNewer("garbage", "1.3.1"))
                    throw new Exception("update version selection was incorrect");
                string updateSample = Path.Combine(root, "update-sample.exe");
                File.WriteAllBytes(updateSample, new byte[] { 97, 98, 99 });
                UpdateManager.VerifyInstaller(updateSample, "sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
                rejected = false;
                try { UpdateManager.VerifyInstaller(updateSample, "sha256:" + new string('0', 64)); } catch (IOException) { rejected = true; }
                if (!rejected) throw new Exception("corrupt installer passed checksum verification");
                rejected = false;
                try { UpdateManager.VerifyInstaller(updateSample, ""); } catch (IOException) { rejected = true; }
                if (!rejected) throw new Exception("installer without a checksum was accepted");
                UpdateManager.SetAutomatic(false);
                if (UpdateManager.AutomaticEnabled) throw new Exception("auto-update setting was not saved");
                UpdateManager.SetAutomatic(true);
                if (!UpdateManager.AutomaticEnabled) throw new Exception("auto-update setting was not enabled");

                string junctionTarget = Path.Combine(root, "junction-target");
                string junction = Path.Combine(existingLocal, "redirected");
                Directory.CreateDirectory(junctionTarget);
                using (System.Diagnostics.Process command = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "cmd.exe", "/c mklink /J \"" + junction + "\" \"" + junctionTarget + "\"")
                    { UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden }))
                {
                    command.WaitForExit();
                    if (command.ExitCode != 0) throw new Exception("could not create the isolated junction test");
                }
                try
                {
                    if (!SyncEngine.IsRedirect(junction, File.GetAttributes(junction))) throw new Exception("junction tag was not recognized");
                    rejected = false;
                    try { SyncEngine.Link(junction, attachedProvider.Id, "junction-copy"); } catch (ArgumentException) { rejected = true; }
                    if (!rejected) throw new Exception("junction attachment was accepted");
                    File.WriteAllText(Path.Combine(junctionTarget, "outside.txt"), "outside");
                    File.WriteAllText(Path.Combine(existingLocal, "beside-link.txt"), "beside");
                    SyncStats linked = SyncEngine.SyncFolder("Push", existingLocal, existingCloud, failedState, true);
                    if (linked.Links != 1) throw new Exception("backup did not report the skipped junction");
                    if (!File.Exists(Path.Combine(existingCloud, "beside-link.txt"))) throw new Exception("a junction stopped the rest of the backup");
                    if (Directory.Exists(Path.Combine(existingCloud, "redirected"))) throw new Exception("backup followed a junction outside its paired folder");
                    Directory.CreateDirectory(Path.Combine(existingCloud, "redirected"));
                    File.WriteAllText(Path.Combine(existingCloud, "redirected", "into-link.txt"), "cloud");
                    linked = SyncEngine.SyncFolder("Pull", existingLocal, existingCloud, failedState, true);
                    if (linked.Links != 1 || File.Exists(Path.Combine(junctionTarget, "into-link.txt")))
                        throw new Exception("refresh wrote through a junction outside its paired folder");
                    string dropCopy = Path.Combine(root, "drop-with-link");
                    List<string> linkDirs = new List<string>();
                    List<string> linkFiles = new List<string>();
                    if (SyncEngine.CopyInto(existingLocal, dropCopy, "copied", linkDirs, linkFiles) != 1) throw new Exception("drop did not report the skipped junction");
                    if (!File.Exists(Path.Combine(dropCopy, "copied", "beside-link.txt"))) throw new Exception("a junction stopped the rest of the drop");
                    if (Directory.Exists(Path.Combine(dropCopy, "copied", "redirected"))) throw new Exception("drop followed a junction");
                    if (SyncEngine.CopyInto(Path.Combine(existingCloud, "redirected"), existingLocal, "redirected", linkDirs, linkFiles) != 1
                        || File.Exists(Path.Combine(junctionTarget, "into-link.txt")))
                        throw new Exception("drop wrote through a junction in the destination");
                    Directory.Delete(Path.Combine(existingCloud, "redirected"), true);
                    File.Delete(Path.Combine(existingCloud, "beside-link.txt"));
                }
                finally { Directory.Delete(junction); }

                string deniedFolder = Path.Combine(existingCloud, "inaccessible");
                Directory.CreateDirectory(deniedFolder);
                System.Security.AccessControl.DirectorySecurity originalAcl = Directory.GetAccessControl(deniedFolder);
                try
                {
                    System.Security.AccessControl.DirectorySecurity deniedAcl = Directory.GetAccessControl(deniedFolder);
                    deniedAcl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                        System.Security.Principal.WindowsIdentity.GetCurrent().User,
                        System.Security.AccessControl.FileSystemRights.ListDirectory,
                        System.Security.AccessControl.AccessControlType.Deny));
                    Directory.SetAccessControl(deniedFolder, deniedAcl);
                    rejected = false;
                    try { SyncEngine.SyncFolder("Pull", existingLocal, existingCloud, failedState, true); }
                    catch (IOException) { rejected = true; }
                    if (!rejected || !File.Exists(initialL)) throw new Exception("failed directory scan did not stop sync safely");
                }
                finally { Directory.SetAccessControl(deniedFolder, originalAcl); }

                if (!Store.BackupOnStartupEnabled) throw new Exception("startup backup was not enabled by default");
                Store.SetBackupOnStartup(false);
                if (Store.BackupOnStartupEnabled) throw new Exception("startup backup disable setting was not saved");
                Store.SetBackupOnStartup(true);
                if (!Store.BackupOnStartupEnabled) throw new Exception("startup backup enable setting was not restored");

                Store.RecordShutdownStart();
                if (!Store.WasLastShutdownInterrupted()) throw new Exception("incomplete shutdown was not detected as interrupted");
                Store.RecordShutdownComplete();
                if (Store.WasLastShutdownInterrupted()) throw new Exception("completed shutdown was detected as interrupted");
                Store.ClearShutdownMarker();
                if (Store.WasLastShutdownInterrupted()) throw new Exception("cleared shutdown marker was detected as interrupted");

                GoogleSelfTest.Run(Path.Combine(root, "google"));

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
