# P14 Coppelia Guided Setup, UI, and Description Checklist

- Project: Coppelia (DevHub P14)
- DevHub workflow: W43
- Scope: I10, I12, and I34
- Version: 1.0.0.8 (no version bump)
- Stage: implementation and build/test only
- Baseline: clean `main` at `7f914fc674e458611d36e8ddd41682bb7da36e60`
- Started: 2026-07-25

## Boundaries

- No packaging, release, GitHub push, live Aethertek publication, machine-wide action, or live-game test.
- Preserve runtime action rules, `/healbot` and `/copellia` commands, FrenRider IPC contracts, saved targets, filters, and all three saved window positions.
- Retain Main, Settings, and Watch as separate windows.
- Keep one canonical README, changelog, checklist, and current-day XA pointer.

## Acceptance

- [x] Configuration schema is v6 with `SetupWizardCompleted`.
- [x] New configurations remain incomplete and auto-open Quick Setup.
- [x] v5-and-earlier configurations migrate as completed without unsolicited onboarding.
- [x] Wizard settings remain draft-only until Finish; Cancel discards the draft.
- [x] HealBot setup explains behavior, shows FrenRider/vnavmesh/BMR-or-VBM/healer readiness, configures filters/persistence/range, and opens Watch directly.
- [x] PowerlevelBot setup chooses BRD or MCH and shows job, FrenRider IPC/configuration/visibility, and companion readiness.
- [x] Finish requires enable-now or leave-off; failed activation retains incomplete state and displays the existing blocker.
- [x] Main presents mode, automation, runtime status, readiness, and mode-specific target information.
- [x] Settings contains Quick Setup, General, HealBot Actions, and Requirements / Help tabs with the existing action matrix preserved.
- [x] Watch retains add/remove, Ctrl-clear, persistence, filtering, and placement behavior while presenting clearer status, controls, filters, and tables.
- [x] Both manifests and the in-plugin summary use the supplied punchline and description.
- [x] Canonical README and Unreleased changelog entry are present.
- [x] Local Aethertek canonical page, redirect, both index cards, and dynamic feed lookup validated.
- [x] Complete Debug tests pass with zero failures.
- [x] Release build passes without packaging.
- [x] Final metadata/site validator and full diff review pass.

## Evidence and checkpoints

- 2026-07-25 baseline: `dotnet test Coppelia.sln -c Debug --nologo` passed 27/27 before edits.
- 2026-07-25 final Debug suite: `dotnet test Coppelia.sln -c Debug --no-restore --nologo --disable-build-servers` ran from 13:49:56Z to 13:51:48Z and passed 31/31 with zero failures and zero skipped tests.
- 2026-07-25 package-free Release build: `dotnet build Coppelia.sln -c Release --no-restore --nologo --disable-build-servers -p:PackagerTargetFile=Z:\temp\NoDalamudPackaging.targets` ran from 14:04:33Z to 14:05:21Z and completed with zero warnings, zero errors, and no `latest.zip`.
- 2026-07-25 source inspection: 17/17 onboarding, wizard, readiness, window-organization, preserved-behavior, and migration checks passed from 14:08:22Z to 14:08:25Z.
- 2026-07-25 metadata/site inspection: 19/19 manifest, description, canonical-page, redirect, index-card, feed-lookup, and public-URL checks passed from 14:10:11Z to 14:10:16Z.
- 2026-07-25 final scope review: the exact expected source set, unchanged lockfile and version, clean diff, package-free output, backups, and local-only website files passed after documentation synchronization.
- Live-game wizard interaction and runtime activation are not tested in this authorized pass.

## Recovery

Existing production files were copied once before editing with timestamp `2026-07-25_09-21-27`; the later test-project backup is `2026-07-25_09-30-40`. Backups are stored in the nearest `backups` folder and excluded from source control and builds.

## Next action

Review the local changes in game when separately authorized; no implementation, packaging, publishing, pushing, or release work remains in this pass.
