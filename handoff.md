# Team handoff

## Objective
Version 1.4.0: arbitrary local/cloud folder pairs, horizontal drive tabs, shutdown backup, sign-in refresh, cloud retention after local deletion, and automatic stable-release updates.

## Baseline
Updated clean main from 72c508a to origin/main e4a2129 before implementation.

## Assignments
| Agent | Assignment | Status |
| --- | --- | --- |
| Codex | Implement and review folder-pair UX, retention, lifecycle safety and updater; build/test and prepare release | Complete; publication uses the main-branch release workflow |

## Validation and delivery
Local build, sync self-test, native UI self-test and diff checks passed. See review.md for review evidence and test limits. CHANGELOG.md contains 1.4.0 release notes; src/Store.cs and assembly metadata carry 1.4.0. The GitHub build workflow repeats sync and UI tests before publishing the installer.
