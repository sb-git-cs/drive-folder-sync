# Review — 1.4.1

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
- /selftest passed: folder mappings, retention/restoration, initial conflicts, rapid edits, simultaneous edits, unavailable roots, denied scans, junction rejection, partial selections, updater versions/settings/checksums.
- /uitest passed: paired roots, cloud-only highlighting, horizontal tabs, folder selection, paired navigation and compact layout. Native window previews inspected in dist/ui-check.
- git diff --check passed.
- Tests use isolated folders. An actual Windows shutdown and a live installation of an online update were not performed; lifecycle sync operations and update validation were exercised independently.
