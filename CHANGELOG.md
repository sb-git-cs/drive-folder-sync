# Changelog

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
