# Drive Folder Sync

A File Explorer for folders you keep in sync with Google Drive, OneDrive, Dropbox, or any other cloud folder on this PC.

A green status changed on this PC since the last sync. A red status changed in the cloud, or both sides changed. Files with no changes show In sync. The Last synced column is the last time that file was recorded in sync.

Linked folders refresh when you sign in and upload when Windows shuts down. Nothing is copied until you link a folder.

## Install

Run `DriveFolderSyncSetup.exe`. It installs for the current user, starts at sign-in, and adds **Link folder for sync** to a folder's right-click menu. The installed app is in `%LOCALAPPDATA%\DriveFolderSync`.

Google Drive, OneDrive, and Dropbox folders that are already on the PC show up in the left pane. **Add drive** attaches any other folder, including a Dropbox folder that was not detected.

## Local replica

Select folders and files on a drive, then choose **Local replica**. Only those folders are created, and only those files are copied. A selected file keeps the same folder path as on the drive. Other folders and files stay in the cloud. The copy is under your `Work Drive` folder. Browsing either side shows one list: items that exist only in the cloud, only on this PC, or in both places.

## Link a folder

Open a folder in a drive and choose **Link folder**. The local copy uses that same replica structure. At shutdown, edits in the local copy are copied to the cloud folder. At sign-in, the local copy is refreshed from the cloud.

Personal and work Google Drive accounts both appear. A drive is included only after you link a folder from it.

## Getting around

Click a part of the address bar to jump to it, or click the empty space to type a path. Right-click a file or folder for Open, Show in File Explorer, Copy path, and the sync commands. Uninstall, the log folder, and About are under the **...** button.

| Keys | Action |
| --- | --- |
| Alt+Left, Backspace | Back |
| Alt+Right | Forward |
| Alt+Up | Parent folder |
| F5 | Refresh |
| Ctrl+F | Search this folder |
| Ctrl+L, Alt+D | Type a path |
| Ctrl+A | Select all |

## Build

Windows with .NET Framework 4.8 is enough. From this folder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

The installer is written to `dist\DriveFolderSyncSetup.exe`. `DriveFolderSyncSetup.exe /selftest` checks the sync rules without touching your drives.
