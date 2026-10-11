# Team handoff

## Objective
Version 1.5.2: dragging a folder that contains a link between the panes now skips the link instead of refusing the copy. 1.5.1 (link-safe sync, list columns that fit the pane) is released.

## Baseline
main at 11d8c5d. Release v1.5.1 published.

## Assignments
| Agent | Assignment | Status |
| --- | --- | --- |
| Claude | Skip links in drag-and-drop copies for 1.5.2 | Complete; committed to main for release |

## Validation and delivery
Local build, `/selftest` and `/uitest` pass. Owner rule: sync never deletes local files, even when the cloud deletes them. New tests cover junction skipping in sync and drag-and-drop copies and the absence of a horizontal list scrollbar at the default and minimum window sizes. See review.md.

## Next steps
- Confirm the 1.5.2 release workflow published the installer.
- Follow-ups not done:
  - viewing large pending folders compares file contents on every browse;
  - dark mode is unsupported;
  - the README doesn't document the auto-link watcher for Desktop/Documents/Downloads.
