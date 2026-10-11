<img src="docs/icon.png" width="64" alt="Drive Folder Sync icon">

# Drive Folder Sync

Drive Folder Sync is a File Explorer for the folders you keep on Google Drive, OneDrive, Dropbox, or any other cloud folder on this PC. The drive stays on the left. Its local replica stays on the right.

![The drive on the left and the local replica on the right](docs/screenshots/side-by-side.png)

Drag a file onto the other pane and that file is copied onto the same path. Drag a folder and the folder is copied with the files inside it. Green statuses are about this PC: the item changed here or is only here. Red statuses are about the cloud: the item changed there, is only there, or changed on both sides.

Signing in refreshes linked folders from the cloud. Shutting down uploads them back. Nothing is copied until you drag an item, choose **Local replica**, or link a folder. The one exception is a new folder in Desktop, Documents, Downloads, or Work Drive whose name and files match a folder on a drive. The app links that folder itself and shows a notification.

## Install

Download [DriveFolderSyncSetup.exe](https://github.com/sb-git-cs/drive-folder-sync/releases/latest) and run it. The install is for the current user. The app starts at sign-in, and a folder's right-click menu gains **Link folder for sync** (on Windows 11, under **Show more options**). The program is stored in `%LOCALAPPDATA%\DriveFolderSync`. If a copy is already installed, choose Reinstall to update it.

<img src="docs/screenshots/setup.png" width="520" alt="Installer, listing the drives found on this PC">

The installer lists the Google Drive, OneDrive, and Dropbox folders already on this PC. To use any other cloud folder, choose **Add drive** in the app.

## The two panes

Open a drive from This PC. Both panes follow the same folder, so a drop lands on the matching path.

![This PC, with each drive beside its local replica](docs/screenshots/overview.png)

The local copy lives in `%USERPROFILE%\Work Drive`, in one folder per drive (for example `Work Drive\alex@example.com`), with the same relative folders as the drive.

- **Local replica** copies only the selection. A selected folder is created as a directory. A selected file is copied, and the parent folders are created so the path matches.
- **Link folder** keeps a whole folder in sync. It is refreshed right away, again at each sign-in, and uploaded at shutdown.
- A drive can be browsed as soon as it is on this PC. It joins sync after you link a folder, choose **Local replica**, or drag items between the panes.

Sync all refreshes or uploads every linked folder at once.

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

64-bit Windows with .NET Framework 4.8 is enough.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

The installer is written to `dist\DriveFolderSyncSetup.exe`. `dist\DriveFolderSyncSetup.exe /selftest` checks the sync rules without touching your drives. The result goes to `%TEMP%\drivesync-selftest.txt`, or to a path given after `/selftest`, and the exit code is 0 when it passes.

GitHub Actions builds and self-tests pull requests and pushes to `main` and `release-*`. When `main` has a version in `src\Store.cs` with no release yet, it publishes that release with the installer attached. The notes come from that version's section of [CHANGELOG.md](CHANGELOG.md).

## License

MIT. See [LICENSE](LICENSE).
