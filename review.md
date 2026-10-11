# Review — 1.5.2

## Bug fix in 1.5.2

- Dragging a folder that contained a link between the panes failed with "Copy cannot follow symbolic links or junctions". CopyInto now skips links, both in the source and at the destination, and returns the count, which the drop status reports. Covered by /selftest: a junction in a dropped folder is skipped while its siblings copy, and a drop onto a destination junction doesn't write into its target.

# Review — 1.5.1

## Bug fixes in 1.5.1

- A junction inside an attached folder aborted the initial backup and every sign-in refresh with "Sync cannot follow symbolic links or junctions". The owner hit this with D:\project-documents\…\prototype, a junction into a git repository. Sync now skips links on either side and drops the matching paths on the other side, so it neither follows a link nor writes through one. Folder mirroring skips them too. Attaching a folder whose path is itself a link is still rejected. Covered by /selftest: push skips the junction and backs up its siblings, and pull doesn't write into the junction target.
- File lists showed a horizontal scrollbar at the default window size, because the fixed columns needed about 630 px and each pane is about 455 px. Columns now shrink to their minimums and then hide Last synced and Date modified. Fitting is deferred until the list control has applied its new size; otherwise a stale scrollbar stayed after a resize. /uitest asserts there is no horizontal scrollbar at the default and minimum sizes.

# Review — 1.5.0

## Blocker — resolved in 1.5.0

- Pull permanently deleted unchanged local files that were missing from the cloud folder, including every file when a cloud client still loading at sign-in listed an empty folder. Per the owner's rule, sync now never deletes local files: cloud-deleted files stay on this PC, keep their snapshot and show as Missing in the cloud, and backup does not recreate them in the cloud. The only local-deletion path (SyncEngine.DeleteFile) was removed. Covered by /selftest (single deletion, emptied folder, deleted folder).
- Settings and manifest saves deleted the old file before moving the new one in, and any read error returned an empty list that the next save wrote back, which could erase every attachment. Saves now use File.Replace, interrupted saves are recovered from .tmp, corrupt files are copied to .corrupt, and a persistently locked file throws instead of reading as empty. Evidence: src/Store.cs ReadObject/WriteObject. Covered by /selftest.

## Should-fix — resolved in 1.5.0

- "Link folder for sync" from Explorer while the app was open only focused the window; the folder is now handed to the open window. Drive roots ("D:\") are parsed correctly.
- Closing the window during a sync ended the process mid-copy. It now offers to close when the sync finishes. Background completion callbacks no longer throw when the window is gone.
- Drag-and-drop silently overwrote existing files. It now asks first (UI test covers cancel).
- Uninstall from the app left the running exe behind, didn't stop the UI when run from Settings, and ignored active syncs. It now takes the sync lock, stops all copies, exits so the exe can be deleted, and registers/unregisters an Apps & Features entry.
- Add cloud drive accepted the Windows drive, overlapping drives and attached local folders, wrote config without the state lock, and made name-matched OneDrive/Dropbox folders unremovable. All fixed.
- "Attach & back up" rendered as "Attach _back up" (mnemonic).

## Validation — 1.5.0

- Build, /selftest and /uitest pass locally. The attach-dialog preview was inspected.
- Not exercised: real Windows shutdown, a live update from GitHub, or uninstalling via Settings → Apps.

# Prior review — 1.4.2

## Should-fix — resolved in 1.4.2

- Context actions depended on the focused list and current pair, omitting removal from virtual attachment lists or choosing the wrong attachment. List and tree context menus now resolve the source control and selected path, then capture the attachment ID for each action. Evidence: src/MainForm.cs:2174, src/MainForm.cs:2185 and src/MainForm.cs:2199.
- Removing a sync attachment was not disabled during active UI sync and unlink failures could escape the handler. Removal now has a busy guard, handles errors and identifies the entire attachment plus both paths in confirmation. Evidence: src/MainForm.cs:2411.

- The generic local-source rejection hid which configured cloud root was responsible; a mistakenly added local folder had no UI recovery. Validation now names the root, the dialog wraps the explanation, and unused manually added drive tabs can remove their registration without deleting files. Evidence: src/SyncEngine.cs:871, src/MainForm.cs:2768 and src/FolderPairs.cs:95. Linked drives are protected by a check under the sync mutex.

## Validation — 1.4.2

- Native UI regression passed: removal from the local overview with the opposite pane focused; cloud-folder removal; tree context action; confirmation cancellation; disabled action during sync; removal of only the selected pair; local/cloud files preserved.
- UI regression also reproduces an erroneous local-folder drive registration, verifies the root is visible in the error, protects a drive with attachments, cancels/removes an unused drive entry, and confirms that the local folder then validates for attachment. Native error-dialog preview inspected.
- Final build, full sync test and diff check are recorded in handoff.md. Tests operate on isolated folders.

## Prior review — 1.4.1

## Should-fix — resolved in 1.4.1

- A missing cloud copy could retain its synced timestamp or be classified In sync from stale local snapshots. Presence now takes precedence over pending/held state; absent counterparts clear the displayed sync timestamp. Evidence: src/SyncEngine.cs:977. Regression coverage includes previously synced, pending, held and local-only snapshots.
- A trashed/moved cloud root or subfolder retained a Synced badge; the attachment overview and compatibility views also reused historical folder sync times for missing items. Missing paths now display a warning without a synced timestamp. Evidence: src/MainForm.cs:1232, src/MainForm.cs:1436 and src/MainForm.cs:1548. Native UI tests move files, subfolders and an entire cloud root out of the paired paths, and verify local browsing leaves local data intact.
- The horizontal cloud-drive bar included Overview and Attached folders. Only provider tabs are now created. Evidence: src/FolderPairs.cs:52. The UI test checks that every tab identifies a detected provider.

## Validation — 1.4.1

- Final Windows/.NET Framework build, sync self-test, native UI self-test and diff checks passed locally.
- Native missing-folder preview inspected in dist/ui-check/missing-cloud.png.
- Trash is simulated by moving isolated cloud files/folders away from their paired paths. The app reads mounted provider folders; live cloud trash APIs and actual Windows shutdown were not exercised.

## Prior review — 1.4.0

No unresolved blocker or should-fix findings remain in the implemented change.

## Blocker — resolved

- Local deletion previously removed its cloud backup. Shutdown upload now retains cloud-only files; sign-in refresh restores them. Evidence: src/SyncEngine.cs:517. Retention, highlighting and restoration are covered by the sync self-test.
- Same-size files could be treated as synchronized during initial attachment, and simultaneous edits could overwrite a version. Initial comparison now verifies contents, and divergent versions remain held for review. Evidence: src/SyncEngine.cs:188 and src/SyncEngine.cs:471. Regression tests cover both cases.

## Should-fix — resolved

- A three-second signature tolerance skipped rapid same-size edits. Per-side snapshots now compare exact recorded times. Evidence: src/SyncEngine.cs:167. A one-second edit regression test verifies upload.
- An unavailable account blocked startup refresh of every drive. Startup now refreshes available pairs independently and retries failed pairs. Evidence: src/HostForm.cs:177.
- Reinstall could terminate a process during sync. Installation now acquires the shared sync mutex before stopping running copies; updates also defer during shutdown or a busy UI. Evidence: src/SetupForm.cs:18 and src/UpdateManager.cs:124.

## Validation

- Windows/.NET Framework build passed.
- /selftest passed: folder mappings, retention/restoration, initial conflicts, rapid edits, simultaneous edits, unavailable roots, denied scans, junction skipping, partial selections, updater versions/settings/checksums.
- /uitest passed: paired roots, cloud-only highlighting, horizontal tabs, folder selection, paired navigation and compact layout. Native window previews inspected in dist/ui-check.
- git diff --check passed.
- Tests use isolated folders. An actual Windows shutdown and a live installation of an online update were not performed; lifecycle sync operations and update validation were exercised independently.
