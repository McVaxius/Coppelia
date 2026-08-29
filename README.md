# Coppelia

Guided watched-target healing, healer-first JOAT support, and Fren-assisted powerleveling.

Coppelia provides guided setup for three mutually exclusive automation modes. HealBot runs configurable WHM, SCH, AST, or SGE healing, raises, buffs, and pre-buffs for up to 20 watched friendly targets. Jacqueline of All Trades (JOAT) gives that healing absolute priority, then uses the equipped healer's highest available single-target filler spell only when the healing decision is idle and an eligible damaged enemy is already targeting FrenRider's configured visible Fren or the local healer. PowerlevelBot retains its BRD/MCH instant-action policy. Includes dependency/readiness checks, optional saved targets, and optional Rotation Solver Reborn isolation for HealBot and JOAT. Use `/healbot` to open.

## Quick Setup

Open Main and choose **Quick Setup**, or open Settings and select its permanent **Quick Setup** tab.

- Choose HealBot, Jacqueline of All Trades (JOAT), or PowerlevelBot. The modes are mutually exclusive.
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

## Jacqueline of All Trades (JOAT)

JOAT shares HealBot's dependencies, watch list, per-healer action matrix, 900-ms decision cycle, and optional Rotation Solver Reborn isolation. Healing always wins: JOAT attacks only after the current healing decision queued nothing and found no blocked matching healing action.

- Requires an equipped WHM, SCH, AST, or SGE with that healer's action matrix enabled and at least one active watched target.
- Uses the existing FrenRider lease and restricted enemy selector, but ignores the BRD/MCH Powerlevel job selection.
- Considers only living, damaged, targetable enemies already targeting FrenRider's configured visible Fren or the local healer. Untouched enemies and enemies fighting anyone else are excluded.
- Uses only the highest currently available single-target filler spell for the equipped healer. DoTs, AoE, and oGCD attacks are excluded.
- Issues damage directly through Coppelia's action executor while Rotation Solver Reborn remains isolated.
- Never auto-adds the Fren to Watch. When the Fren is the low-level character, select it explicitly so JOAT healing protects it.

### QST automatic helper mode

When a High-Level Helper selects **Coppelia (JOAT)** in Questionable Companion, authenticated `Coppelia.QST` v3 status requests idempotently enable Coppelia, select JOAT, and enable automation. One session-scoped assignment immediately displays the exact name/home-world Quester as selected but remote, then resolves only that identity into the native HealBot candidate when visible. It never saves the assignment or changes the configured watch list.

Outside duties, Coppelia follows without requiring a party: Lifestream visits the Quester's current world, uses the exact forwarded aetheryte when available or the nearest unlocked aetheryte in the newest reported territory, then owns one vnavmesh route at a time. Mounted follow latches on beyond 30 yalms and continues to 5; on-foot follow latches on beyond 20 yalms and continues to 10. Moving travel snapshots update the next destination without replacing an active or pending route. A route startup that never becomes active is rejected once and waits for a newer snapshot; an active route that completes can advance once to the newest accepted coordinates.

Native Mount Roulette mirrors a mounted Quester and still mounts for on-foot catch-up beyond 50 yalms. Every permitted mounted chase prefers flight with a five-yalm vnavmesh tolerance: an on-foot Quester is approached in flight to 20 yalms before landing, dismounting, and finishing on foot; a grounded mounted Quester is approached to 5 yalms before landing while remaining mounted; and a flying Quester is followed in the air to 5 yalms. Completed aether-current sets permit flight directly. Mountable ARR territories without a standard set probe one flying route; success proves flight for that territory, while a rejected or non-starting probe visibly falls back to ground until the territory or QST session resets. Coppelia stops only its owned active path for casts and actions, cancels an actually pending pathfind only on terminal session release, suspends travel inside duties, and clears travel once when QST releases the session.

The QST High-Level Helper setting **Summon companion chocobo** is on by default. QST must successfully apply that preference through the local-only companion IPC before Helper activation is ready. While owned and enabled, Coppelia checks at most every 15 seconds and uses Gysahl Greens only below 900 seconds of buddy time while logged in, on foot, outside combat/duties/sanctuaries, and free of casting or occupied states. It pauses only Coppelia-owned navigation for the item action and does not control companion stance. Deactivation, role/provider loss, plugin disable, and unload clear QST ownership.

HealBot exclusively owns prebuffing, healing, raising, action selection, and cast holds for the assigned Quester; JOAT attacks enemies targeting that exact visible Quester only after HealBot yields. Both remain held while the paired Helper is mounted or mounting, so after landing HealBot resumes first and keeps priority. Optional DAF duties require ADS and FrenRider. Quester-owned entry waits for stable duty entry before configuring FrenRider with the exact Quester and starting ADS. Coppelia-owned entry additionally requires DAD, keeps the helper as party leader, and asks DAD for a Regular + Unsynced run. Assignment release restores temporary FrenRider, target, and movement state while keeping QST-owned activation across connection churn; changing the Helper role/provider or unloading QST restores the prior Coppelia activation state.

## PowerlevelBot

PowerlevelBot requires an unlocked BRD or MCH that is already equipped. It never changes gearsets and does not use the HealBot watch list.

Activation requires compatible FrenRider Powerlevel IPC, FrenRider enabled, a configured and visible Fren, and no active companion chocobo. Target selection is intentionally narrow: only living, targetable, damaged combatant enemies already targeting FrenRider's configured Fren or the local player are eligible. Coppelia uses instant, hostile-only, single-target BRD/MCH actions and does not pull untouched enemies.

## Windows and commands

Coppelia retains three separate windows:

- **Main** is the status dashboard for mode, automation, readiness, runtime state, and mode-specific target information.
- **Settings** contains Quick Setup, General, HealBot Actions, and Requirements / Help tabs.
- **Watch** manages the shared HealBot/JOAT filters, persistence, retained targets, and the live eligible-target table.

Window positions are saved independently. `/healbot ws` resets all three positions and `/healbot j` moves Main to a random visible location.

Commands:

- `/healbot` or `/copellia` - open Main
- `/healbot config` - open Settings
- `/healbot watch` - open Watch
- `/healbot on` or `/healbot off` - control automation for the selected mode
- `/healbot heal` - select HealBot
- `/healbot powerlevel` or `/healbot pl` - select PowerlevelBot
- `/healbot joat` - select Jacqueline of All Trades (JOAT)
- `/healbot jot` - compatibility alias for `/healbot joat`
- `/healbot status` - print the selected mode status

## Build

The solution targets .NET 10, x64, and Dalamud API 15.

```powershell
dotnet test Coppelia.sln -c Debug --no-restore
dotnet build Coppelia.sln -c Release --no-restore
```

The Dalamud packager can emit a local `latest.zip` during a normal Release build. Building does not publish a release.
