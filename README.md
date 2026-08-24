# Coppelia

Guided watched-target healing, healer-first JOT support, and Fren-assisted powerleveling.

Coppelia provides guided setup for three mutually exclusive automation modes. HealBot runs configurable WHM, SCH, AST, or SGE healing, raises, buffs, and pre-buffs for up to 20 watched friendly targets. Jacqueline of All Trades (JOT) gives that healing absolute priority, then uses the equipped healer's highest available single-target filler spell only when the healing decision is idle and an eligible damaged enemy is already targeting FrenRider's configured visible Fren or the local healer. PowerlevelBot retains its BRD/MCH instant-action policy. Includes dependency/readiness checks, optional saved targets, and optional Rotation Solver Reborn isolation for HealBot and JOT. Use `/healbot` to open.

## Quick Setup

Open Main and choose **Quick Setup**, or open Settings and select its permanent **Quick Setup** tab.

- Choose HealBot, Jacqueline of All Trades (JOT), or PowerlevelBot. The modes are mutually exclusive.
- Changes remain in a draft until **Finish**. **Cancel** discards that draft.
- Finish requires either **Enable this mode now** or **Save setup and leave automation off**.
- If activation fails, the wizard stays incomplete and shows the same blocker used by normal activation.
- A genuinely new configuration opens Quick Setup automatically. Existing configurations migrate without unsolicited onboarding.
- Closing an unfinished first-run setup leaves it incomplete, so it opens again on the next plugin load. Completed setup remains manually rerunnable.

## HealBot

HealBot uses the Watch window and the existing per-healer action matrix.

- Supports WHM, SCH, AST, and SGE.
- Evaluates up to 20 explicitly watched friendly targets, including players outside the party.
- Supports healing, raises, buffs, and pre-buffs according to the configured job rules.
- Can discover players, companion chocobos, NPC party members, and friendly battle NPCs through independent filters.
- Can optionally save explicitly watched targets. The saved-target scan range only allows a saved target to rejoin after it returns; it never discovers or selects a new target.
- Requires FrenRider, vnavmesh, and either BossMod Reborn (BMR) or VBM.
- Uses optional Rotation Solver Reborn isolation and restores the session snapshot when available.

Use the separate Watch window to add or remove targets. Unticking a target also removes its saved copy. Hold Ctrl while clearing the full set or removing an absent retained target.

## Jacqueline of All Trades (JOT)

JOT shares HealBot's dependencies, watch list, per-healer action matrix, 900-ms decision cycle, and optional Rotation Solver Reborn isolation. Healing always wins: JOT attacks only after the current healing decision queued nothing and found no blocked matching healing action.

- Requires an equipped WHM, SCH, AST, or SGE with that healer's action matrix enabled and at least one active watched target.
- Uses the existing FrenRider lease and restricted enemy selector, but ignores the BRD/MCH Powerlevel job selection.
- Considers only living, damaged, targetable enemies already targeting FrenRider's configured visible Fren or the local healer. Untouched enemies and enemies fighting anyone else are excluded.
- Uses only the highest currently available single-target filler spell for the equipped healer. DoTs, AoE, and oGCD attacks are excluded.
- Issues damage directly through Coppelia's action executor while Rotation Solver Reborn remains isolated.
- Never auto-adds the Fren to Watch. When the Fren is the low-level character, select it explicitly so JOT healing protects it.

## PowerlevelBot

PowerlevelBot requires an unlocked BRD or MCH that is already equipped. It never changes gearsets and does not use the HealBot watch list.

Activation requires compatible FrenRider Powerlevel IPC, FrenRider enabled, a configured and visible Fren, and no active companion chocobo. Target selection is intentionally narrow: only living, targetable, damaged combatant enemies already targeting FrenRider's configured Fren or the local player are eligible. Coppelia uses instant, hostile-only, single-target BRD/MCH actions and does not pull untouched enemies.

## Windows and commands

Coppelia retains three separate windows:

- **Main** is the status dashboard for mode, automation, readiness, runtime state, and mode-specific target information.
- **Settings** contains Quick Setup, General, HealBot Actions, and Requirements / Help tabs.
- **Watch** manages the shared HealBot/JOT filters, persistence, retained targets, and the live eligible-target table.

Window positions are saved independently. `/healbot ws` resets all three positions and `/healbot j` moves Main to a random visible location.

Commands:

- `/healbot` or `/copellia` - open Main
- `/healbot config` - open Settings
- `/healbot watch` - open Watch
- `/healbot on` or `/healbot off` - control automation for the selected mode
- `/healbot heal` - select HealBot
- `/healbot powerlevel` or `/healbot pl` - select PowerlevelBot
- `/healbot jot` - select Jacqueline of All Trades (JOT)
- `/healbot status` - print the selected mode status

## Build

The solution targets .NET 10, x64, and Dalamud API 15.

```powershell
dotnet test Coppelia.sln -c Debug --no-restore
dotnet build Coppelia.sln -c Release --no-restore
```

The Dalamud packager can emit a local `latest.zip` during a normal Release build. Building does not publish a release.
