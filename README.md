<img src="docs/icon.png" width="64" alt="Drive Folder Sync icon">

# Drive Folder Sync

Back up folders from any local drive to Google Drive, OneDrive, Dropbox, or another cloud folder on this PC. Select a cloud drive from the horizontal tabs, browse its data on the left, and choose an attached local folder on the right.

## Install

Download [DriveFolderSyncSetup.exe](https://github.com/sb-git-cs/drive-folder-sync/releases/latest) and run it. Installation is for the current user. The app starts at sign-in and adds **Attach to Drive Folder Sync** to the folder right-click menu (under **Show more options** on Windows 11). Existing installations can use **Reinstall**. The installed program is in `%LOCALAPPDATA%\DriveFolderSync`. Remove it from **Settings → Apps → Installed apps** or from **… → Uninstall** in the app; your files and settings are kept.

The cloud provider’s desktop app must be installed and its folder or mounted drive available. Detected Google Drive, OneDrive and Dropbox accounts appear as tabs. **Add cloud drive** registers the folder used by another cloud desktop client. Choose **Attach folder** for a local folder you want to back up. A local backup folder added as a drive will be rejected as a source; right-click its unused drive tab and choose **Remove added cloud drive** to correct the saved entry. Files are kept.

## Attach any local folder

For example, pair **D:\project-documents** with **Google Drive\project-documents**:

1. Select the cloud drive tab and browse to the desired backup location.
2. Choose **Attach folder** and select `D:\project-documents`.
3. Keep the suggested cloud folder name, enter a different name, or browse to an existing cloud folder.
4. Choose **Attach & back up**. A new destination is created and the initial backup starts.

The local folder stays in its original location. Each drive can have several folder pairs; select one from the picker above the local pane. The panes follow the same relative path inside that pair. **Back up to cloud**, **Refresh from cloud**, and drag-and-drop target the selected pair. Dropping onto a name that already exists asks before replacing it. Differing files in an existing destination are retained and marked for review during initial attachment.

**Local replica** remains available for selecting individual cloud files and directories into the existing `Work Drive` structure. Existing attachments and partial selections continue to work. A selected directory creates its structure; a selected file copies that file. Dropping a directory between paired panes copies its contents.

## Automatic backup and refresh

- **At Windows sign-in:** available attached folders refresh from the cloud. Disconnected drives retry for up to eight minutes without blocking other drives.
- **At shutdown or sign-out:** local changes back up to the cloud folder.
- **After local deletion:** the cloud backup stays. The cloud pane highlights it as **Only in cloud**. The next sign-in refresh downloads it again.
- **After cloud trash or a move:** once the cloud desktop client removes the original path, browsing or pressing **F5** shows **Missing in the cloud** without a synced timestamp. Restore the folder in the cloud and refresh to see it again.
- **After cloud deletion:** nothing is deleted on this PC. The local copy stays and is shown as **Missing in the cloud**. Drive Folder Sync never deletes local files; delete them yourself if you no longer need them.
- **When both sides change:** conflict handling preserves versions. Unreadable scans stop sync rather than treating files as deleted.

Green statuses indicate new or changed local files. Red statuses indicate cloud changes, cloud-only files, or conflicts. **Last synced** records successful sync activity rather than the file’s modification date. Status describes the mounted provider folder; the provider’s desktop app handles online upload. Keep that app running for cloud transfer.

**Sync all** backs up or refreshes every attachment. Right-click an attached local or cloud folder and choose **Remove synced folder**. The confirmation shows both paths; removing the attachment stops its automatic backup and refresh and keeps files on both sides. **Stop syncing this folder** in the **…** menu performs the same action. **… → View sync log** shows what each sync did, including errors.

## Automatic updates

Version 1.4.0 and later check this repository’s latest stable GitHub release at each installed-app startup and Windows sign-in. Automatic download and installation are enabled by default. The installer must match the release’s SHA-256 checksum, and installation waits for active sync to finish.

Use **… → Install updates automatically at startup** to change the setting, or **Check and install updates** to request a check. Network failures, incomplete releases and busy sync sessions defer the update while the current app remains usable. Existing attachments and files survive reinstalling.

## Getting around

Click an address-bar segment to navigate, or type a path. Right-click items for Open, Show in File Explorer, Copy path and sync actions.

| Keys | Action |
| --- | --- |
| Alt+Left, Backspace | Back |
| Alt+Right | Forward |
| Alt+Up | Parent folder |
| F5 | Refresh the displayed status |
| Ctrl+F | Search this folder |
| Ctrl+L, Alt+D | Type a path |
| Ctrl+A | Select all |

## Build and verify

Windows with .NET Framework 4.8 is sufficient:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
.\dist\DriveFolderSyncSetup.exe /selftest
.\dist\DriveFolderSyncSetup.exe /uitest .\dist\ui-check
```

The installer is written to `dist\DriveFolderSyncSetup.exe`. `/selftest` checks sync, mapping and updater rules in isolated temporary folders; its exit code is 0 on success. Pass a result-file path after `/selftest` to choose its output location. `/uitest` exercises the native UI and renders previews in the given output directory without touching your linked folders.

GitHub Actions builds and tests pull requests and pushes to `main` and `release-*`. A new version on `main` is published with its installer after checks pass. Release notes come from [CHANGELOG.md](CHANGELOG.md).

## License

MIT. See [LICENSE](LICENSE).
