# Review — 1.4.0

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
