# Team handoff

## Objective
Version 1.5.3 lets the updater outlast an unreachable GitHub download address. Released earlier: 1.5.2 (drag-and-drop skips links) and 1.5.1 (link-safe sync, list columns that fit the pane).

## Baseline
main at 2318070. Release v1.5.2 published.

## Assignments
| Agent | Assignment | Status |
| --- | --- | --- |
| Claude | Fix the update download timeout for 1.5.3 | Complete; committed to main for release |

## Validation and delivery
Local build, `/selftest` and `/uitest` pass. Owner rule: sync never deletes local files, even when the cloud deletes them. New tests cover junction skipping in sync and drag-and-drop copies and the absence of a horizontal list scrollbar at the default and minimum window sizes. See review.md.

## Next steps
- Installs older than 1.5.3 on affected networks need one manual install.
- Follow-ups not done:
  - viewing large pending folders compares file contents on every browse;
  - dark mode is unsupported;
  - the README doesn't document the auto-link watcher for Desktop/Documents/Downloads.
