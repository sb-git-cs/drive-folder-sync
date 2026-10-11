# Team handoff

## Objective
Version 1.5.0: audit fixes for sync safety, workflow bugs and consistent UI wording. Committed to main; the main-branch workflow publishes the release.

## Baseline
Started from clean main/origin/main 82c4782 (released v1.4.2). Remote branch `claude/admiring-clarke-1b18bc` has one unmerged README/screenshot commit (80e7c82) and was left untouched.

## Assignments
| Agent | Assignment | Status |
| --- | --- | --- |
| Claude | Audit, fix, test and document 1.5.0 | Complete; committed to main for release |

## Validation and delivery
Local build, `/selftest` and `/uitest` pass. Owner rule: sync never deletes local files, even when the cloud deletes them. New regression tests cover single cloud deletion, emptied and deleted cloud folders, atomic save/recovery/corrupt-file handling, the drop replace prompt and the attach-dialog hint. See review.md.

## Next steps
- Confirm the 1.5.0 release workflow published the installer.
- Follow-ups not done: list columns overflow into a horizontal scrollbar at the default window size; viewing large pending folders compares file contents on every browse; dark mode is unsupported; the auto-link watcher for Desktop/Documents/Downloads is undocumented in the README.
