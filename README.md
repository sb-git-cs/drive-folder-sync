<img src="docs/icon.png" width="72" alt="Drive Folder Sync icon">

# Drive Folder Sync

Drive Folder Sync is a File Explorer for the folders you keep on Google Drive, OneDrive, Dropbox, or any other cloud folder on this PC. The drive stays on the left. Its local replica stays on the right.

![The drive on the left and the local replica on the right](docs/screenshots/side-by-side.png)

Drag a file onto the other pane and that file is copied onto the same path. Drag a folder and the folder is copied with the files inside it. A green status means the file changed on this PC. A red status means it changed in the cloud, or both sides changed. The Last synced column is the last time that file was recorded in sync.

Signing in refreshes the linked selection from the cloud. Shutting down uploads that selection back. Until you drag an item, choose **Local replica**, or link a folder, nothing is copied.

## Install

Download [DriveFolderSyncSetup.exe](https://github.com/sb-git-cs/drive-folder-sync/releases/latest) and run it. The install is for the current user. The app starts at sign-in, and a folder's right-click menu gains **Link folder for sync**. The program is stored in `%LOCALAPPDATA%\DriveFolderSync`. If a copy is already installed, choose Reinstall to update it.

![Installer, listing the drives found on this PC](docs/screenshots/setup.png)

Google Drive, OneDrive, and Dropbox folders already on the PC are listed. **Add drive** attaches any other folder.

## The two panes

Open a drive from This PC. Both panes follow the same folder, so a drop lands on the matching path.

![This PC, with each drive beside its local replica](docs/screenshots/overview.png)

The local copy lives under your `Work Drive` folder, in the same relative folders as the drive.

- **Local replica** copies only the selection. A selected folder is created as a directory. A selected file is copied, and the parent folders are created so the path matches.
- **Link folder** includes that selection in the sign-in refresh and the shutdown upload.
- A drive can be browsed as soon as it is on this PC. It joins sync after you link a folder or drop files into the replica.

## Getting around

Click a part of the address bar to open it, or click the empty space to type a path. Right-click a file or folder for Open, Show in File Explorer, Copy path, and the sync commands. Uninstall, the log folder, and About are under the **...** button.

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

Windows with .NET Framework 4.8 is enough.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

The installer is written to `dist\DriveFolderSyncSetup.exe`. `DriveFolderSyncSetup.exe /selftest` checks the sync rules without touching your drives.

## License

MIT. See [LICENSE](LICENSE).
