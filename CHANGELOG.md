# Changelog

## 1.7.0

High-speed direct Google Drive integration, dual-pane independent browsing, responsive checking experience, and live ETA tracking.

- **Dual-Pane Independent & Synced Browsing.** Browse local PC files and Google Drive cloud folders side-by-side or independently. Added a convenient **Synced browsing** toggle directly on the main screen to synchronize navigation between local folders and their cloud counterparts.
- **Lock-Free Asynchronous Navigation.** Folder navigation is completely non-blocking, eliminating UI hangs when switching folders or toggling synced browsing. State access and UI threads are isolated from long-running background mutexes.
- **High-Speed Direct Transfers.** Achieved near-native transfer speeds utilizing connection pooling (up to 64 concurrent sockets), 16 MB chunking, batch operations, and parallel worker threads for multi-file transfers.
- **Live Scanning Feedback & Real Item Counts.** Folder checking now provides continuous live updates. Local scans report live file and folder counts (`Scanning local files (X files, Y folders found)…`) rather than appearing frozen. Cloud directory discovery dynamically displays total items found.
- **Smooth Progress Bar & Live ETA during Checking.** The progress bar actively tracks percentage and time remaining during folder creation (`Preparing cloud folders (X of Y)`) and file comparisons (`Comparing files (X of Y)…`) before transfers begin.
- **Clear UI Status Feedback.** Dedicated status labels and pane headers show active operations (`Checking local files…`, `Checking Google Drive…`) so the user always knows what the engine is doing.
- **Robust OAuth Sign-In Lifecycle.** Google OAuth authentication is streamlined with automated loopback listener handling, clean window closure, and support for custom OAuth client credentials.

## 1.6.0

Google Drive backups now upload directly, and syncs show their progress.

- **Direct Google Drive upload.** Backups to Google Drive upload straight from the attached folder through the Google Drive API, and refreshes download straight back. Previously every file was copied into Google Drive for desktop's drive, which kept a second copy of the whole backup in its cache on drive C. Connect each account once in **… → Google Drive sign-in…** (see the README for the one-time Google Cloud setup). Uploads are resumable, retried after network errors, and checked against Google's checksum.
- **Progress, speed and time left.** The status bar shows the amount copied out of the total, the speed, the time left and the file count for backups, refreshes and drag-and-drop. The tray icon and the shutdown screen show the same progress for background syncs.
- **Stop.** A running sync can be stopped after the current file. Progress is saved as it goes, so the next run continues where it stopped.
- **Space check.** A backup that wouldn't fit in the Google Drive quota or on the target drive doesn't start, and says how much space it needs.
- **Long paths.** Files with paths longer than 260 characters no longer fail with "Illegal characters in path".
- **Safer copies.** Files are copied to a temporary file and moved into place when complete, so an interrupted copy never leaves a partial file.
- **Browsing doesn't download.** File statuses compare sizes and dates instead of reading cloud files, so opening a folder never makes a cloud app download its files. An unsynced pair with the same size and date shows **Same size and date**.
- **Backup on startup & shutdown reliability.** Backups now run automatically on system power-on / sign-in as well as at shutdown, ensuring local additions or edits are never stranded. If Windows terminates a shutdown backup early, the next startup detects the interruption and catches up immediately. Artificial shutdown delays were removed.
- **Continuous background backup.** Attached folders sync periodically while the PC is on so changes do not pile up for shutdown.
- **Sync & power status UI.** Attached folders show how many local files are waiting to back up; new **… → Sync and power backup status…** dialog displays live service health and power-lifecycle status; and **… → Back up to cloud automatically at startup** allows toggling startup uploads.
- Error summaries from **Sync all** now show the reason for the first folder that failed.

## 1.5.3

- Automatic updates no longer fail with "Update deferred … The operation has timed out" on networks where one of GitHub's download servers is unreachable. The updater now waits long enough for Windows to try the next server. Copies older than 1.5.3 may need this version installed manually once.
- A failed download now says that GitHub couldn't be reached instead of showing the raw network error.

## 1.5.2

- Dragging a folder that contains a symbolic link or junction between the panes now copies everything else instead of refusing. The link is skipped and nothing is copied through it. Nothing is written through a link that already exists at the destination either. The status bar shows how many linked items were skipped.

## 1.5.1

- A symbolic link or junction inside an attached folder no longer stops the backup with "Sync cannot follow symbolic links or junctions". The link is skipped and everything else is backed up. Nothing is copied into or through a link in either direction, and the sync summary shows how many linked folders were skipped.
- File lists no longer show a horizontal scrollbar. Columns shrink to fit the pane, and **Date modified** and **Last synced** are hidden when there isn't room for them. A column you resize keeps its width when space allows.

## 1.5.0

Safer syncing and a more consistent app.

- Drive Folder Sync no longer deletes any file on this PC. A file deleted or trashed in the cloud stays on this PC and is shown as **Missing in the cloud**. Previously, refresh deleted unchanged local copies, and an empty listing from a cloud app that was still loading could remove many files at once.
- Settings and the attached-folder list are saved atomically. An interrupted save is recovered, and a corrupt file is set aside instead of silently replacing your attachments.
- Choosing **Attach to Drive Folder Sync** in File Explorer while the app is open now opens the attach dialog. Drive roots are handled correctly.
- Closing the window during a sync waits for the copy to finish instead of interrupting it.
- Dragging onto a file or folder that already exists asks before replacing it.
- Drive Folder Sync is listed in **Settings → Apps** and can be uninstalled there. Uninstalling from the app now closes it so its program file is removed, and it waits for an active sync.
- **Add cloud drive** rejects the Windows drive, existing cloud drives and attached local folders. Any drive you added can be removed from its tab when it has no attachments.
- Consistent wording: folders are *attached* and *backed up* throughout the app, installer and tray. **… → View sync log** opens the log directly, and the attach dialog says whether the cloud folder exists or will be created.
- Fixed the **Attach & back up** button label, which rendered as "Attach _back up".

## 1.4.2

- Right-click an attached local or cloud folder and choose **Remove synced folder** to stop its automatic backup and refresh. Local and cloud files are kept.
- Removal is available in folder lists, the attached-folder overview and folder trees. The confirmation identifies the entire attachment and both paths.
- Context-menu sync actions use the right-clicked folder instead of another selected or active pair. Removal is disabled during sync.
- Attachment errors identify the configured cloud root that contains the local source and wrap long explanations. **Add cloud drive** is distinguished from **Attach folder**.
- Unused local folders mistakenly added as cloud drives can have their saved registrations removed by right-clicking their drive tabs. Files are kept, and entries with attachments must be unlinked first.
- Native UI tests cover cancellation, correct attachment targeting, busy-state disabling, preserving both copies after removal, and recovery from a local folder mistakenly added as a cloud drive.

## 1.4.1

- The horizontal bar contains only cloud drives; Overview and Attached folders remain accessible through navigation.
- Files and folders missing from their paired cloud path, including items moved to trash, no longer display a synced timestamp or an In sync status. Missing cloud roots and subfolders show a warning instead of a stale Synced badge.
- The cloud pane explains when a folder is unavailable instead of presenting it as empty. Detection uses the provider's mounted folder after its desktop client reflects the change.
- Regression tests cover removed cloud files, subfolders, entire attached folders, stale pending states, attachment overview and cloud-only tabs.

## 1.4.0

Attach local folders from any drive to existing or new cloud destinations. Horizontal drive tabs and a folder-pair selector make the cloud and local paths explicit.

- Attaching starts an initial backup. Linked folders upload at shutdown/sign-out and refresh at Windows sign-in.
- Local deletion retains the cloud backup, highlighted as **Only in cloud** until sign-in refresh restores it. Cloud-to-local deletion rules remain unchanged.
- Multiple independent folder pairs are supported per drive. Existing replicas and partial selections remain compatible.
- First-sync comparisons verify contents, preserving same-size differing files. Unreadable scans stop safely, and sync times reflect successful activity.
- Automatic startup updates download stable GitHub releases, verify SHA-256 checksums, and wait for active sync before installation. The setting can be changed from the **…** menu.
- Sync and native UI tests cover folder mappings, retention/restoration, failure cases and updater validation.

Run the installer and choose **Reinstall** to upgrade from an older version. Automatic updates apply after version 1.4.0 is installed.

## 1.3.1

The installer is now built and self-tested on GitHub Actions. The readme and screenshots are updated. Syncing is unchanged.

## 1.3.0

The drive and its local replica now sit side by side. Drag a file or a folder from either pane and it is copied onto the same path.

- The cloud drive stays on the left and the local replica stays on the right.
- Dropping a file copies that file. Dropping a folder copies the folder and the files inside it.
- Local replica still copies only what you select. Parent folders are created so the path matches the drive.
- Signing in refreshes the linked selection. Shutting down uploads it.

The installer is for the current user. If Drive Folder Sync is already installed, run the installer and choose Reinstall.

## 1.2.0

Explorer window with an address bar, keyboard navigation, and a right-click menu. The installer and app icon are updated. Local replica still copies only the folders and files you select.

## 1.1.1

Local replica copies only the folders and files you select. A selected file keeps the same folder path as on the drive. Other folders and files stay in the cloud, including on sign-in refresh and shutdown upload.

## 1.1.0

Local replica keeps the same folders as the cloud drive.

Open a drive folder and choose Local replica. The copy is created under Work Drive with the same structure. Browsing either side shows one list.

## 1.0.0

Installer for Drive Folder Sync.

Run DriveFolderSyncSetup.exe. It installs for the current Windows user, starts at sign-in, and adds a folder right-click command.

Green rows changed on this PC. Red rows changed in the cloud.
