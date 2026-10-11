# Team handoff

## Objective
Version 1.4.1: show only cloud drives on the horizontal bar and prevent missing or trashed cloud items from appearing synced. Existing sync and deletion behavior remains as released in 1.4.0.

## Baseline
Started from clean main/origin/main 3661c35 (released v1.4.0).

## Assignments
| Agent | Assignment | Status |
| --- | --- | --- |
| Codex | Fix missing-cloud statuses and cloud tabs; review, build/test and publish patch release | Complete; validated locally, publication uses the main-branch release workflow |

## Validation and delivery
Final local build, sync self-test, native UI self-test and diff checks passed. Sync and native UI regressions include moving cloud files, subfolders and an entire attached root out of their paired paths. Detection uses mounted provider folders; live cloud APIs are not queried. See review.md for findings and validation limits. The main-branch build workflow runs both test suites before publishing the installer.
