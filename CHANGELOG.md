# Changelog

## 1.3.1

Installers are now built by GitHub Actions, and every build runs the sync self-test before it is published.

- The readme has a new layout with install steps, a feature overview, and keyboard shortcuts.
- The screenshots are sharper and show every sync status.
- No changes to how syncing works.

The installer is for the current user. If Drive Folder Sync is already installed, run the installer and choose Reinstall.

## 1.3.0

The drive and its local replica now sit side by side. Drag a file or a folder from either pane and it is copied onto the same path.

- The cloud drive stays on the left and the local replica stays on the right.
- Dropping a file copies that file. Dropping a folder copies the folder and the files inside it.
- Local replica still copies only what you select. Parent folders are created so the path matches the drive.
- Signing in refreshes the linked selection. Shutting down uploads it.

## 1.2.0

Explorer window with an address bar, keyboard navigation, and a right-click menu. The installer and app icon are updated. Local replica still copies only the folders and files you select.

## 1.1.1

Local replica copies only the folders and files you select. A selected file keeps the same folder path as on the drive. Other folders and files stay in the cloud, including on sign-in refresh and shutdown upload.

## 1.1.0

Local replica keeps the same folders as the cloud drive. Open a drive folder and choose Local replica. The copy is created under Work Drive with the same structure.

## 1.0.0

First release. The installer sets up Drive Folder Sync for the current Windows user, starts it at sign-in, and adds a folder right-click command.
