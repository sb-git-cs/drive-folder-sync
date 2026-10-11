# Team handoff

## Objective
Version 1.5.1 fixes two bugs. A junction or symbolic link inside an attached folder aborted the initial backup; it is now skipped. File lists had a horizontal scrollbar; columns now fit the pane.

## Baseline
main at 0ed5167. Release v1.5.0 published.

## Assignments
| Agent | Assignment | Status |
| --- | --- | --- |
| Claude | Fix link handling and list column fitting for 1.5.1 | Complete; awaiting the owner's go-ahead to commit and release |

## Validation and delivery
Local build, `/selftest` and `/uitest` pass. Owner rule: sync never deletes local files, even when the cloud deletes them. New tests cover junction skipping in both directions and the absence of a horizontal list scrollbar at the default and minimum window sizes. See review.md.

## Next steps
- Commit to main to publish 1.5.1.
- Follow-ups not done:
  - viewing large pending folders compares file contents on every browse;
  - dark mode is unsupported;
  - the README doesn't document the auto-link watcher for Desktop/Documents/Downloads;
  - drag-and-drop copy (CopyInto) still refuses folders that contain a link.
