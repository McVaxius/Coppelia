# Coppelia UI/UX Recommendations

**Review date:** 2026-08-18  
**Scope:** UI code review only; no runtime behaviour or implementation changes are included in this document.

## Product goal

Choose HealBot or PowerlevelBot, meet its dependencies, select the relevant targets, and understand the last automation decision.

## Reviewed surfaces

- `Coppelia/Windows/MainWindow.cs`
- `Coppelia/Windows/ConfigWindow.cs`
- `Coppelia/Windows/WatchWindow.cs`

## What is already working

- Quick Setup makes the two mutually exclusive modes explicit.
- The main window surfaces runtime status, last action/rule, readiness, dependencies, and watched-target counts.
- The Watch window supports filtering, categories, live/retained targets, safe clearing, and saved targets.

## Prioritized recommendations

| Priority | Recommendation | Rationale and completion signal |
| --- | --- | --- |
| P0 | Treat mode choice as a workspace switch. | Use two descriptive mode cards and change the entire dashboard context after selection; do not keep HealBot watch concepts prominent while PowerlevelBot is active. |
| P0 | Put the mode-specific primary task first. | HealBot should lead with watched targets and healing readiness. PowerlevelBot should lead with Fren, current job, enemy-policy readiness, and active target. |
| P0 | Turn readiness lines into corrective actions. | Missing FrenRider, wrong job, unavailable healer profile, or unmet dependencies should link to Settings, Watch, refresh, or the relevant plugin action. |
| P1 | Clarify live versus retained/saved targets. | Use distinct section labels and badges, show why an absent target is retained, and provide a clear remove-versus-stop-watching distinction. |
| P1 | Support bulk target selection. | Add Select visible, Clear filtered, selected count, and a review of saved-target impact for up to 20 targets. |
| P1 | Add a final setup test. | After Quick Setup, validate the selected mode and show a non-destructive test/readiness result before enabling automation. |
| P2 | Keep technical requirements secondary. | Present a short user-facing dependency summary first and move IPC/rule detail to Requirements or diagnostics. |

## Suggested information hierarchy

1. Selected mode and automation state
2. Mode-specific readiness
3. Primary targets/action
4. Last decision
5. Settings/help

## Validation checklist

- A new user can identify the primary action and current blocker within five seconds.
- Every disabled control has a nearby plain-language reason and, when possible, a direct corrective action.
- Healthy, warning, error, running, and disabled states remain distinguishable without colour.
- The UI remains usable at narrow window widths and common Dalamud UI scales without clipped labels or unreachable controls.
- Destructive, global, or high-impact actions identify their scope and require confirmation or provide a safe undo.
- Empty, loading, stale-data, success, partial-success, and failure states each provide an appropriate next action.
- Settings clearly identify whether they apply globally, per account, per character, per preset, or only for the current session.
- Advanced diagnostics are still reachable but do not compete with the everyday workflow.

## Recommended implementation order

1. Implement P0 items and validate the primary workflow plus blocker recovery.
2. Implement P1 information-architecture and configuration improvements.
3. Apply P2 polish, then test at multiple UI scales with both fresh and mature configurations.
