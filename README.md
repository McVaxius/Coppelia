# HealBot

Guided watched-target healing, healer-first JOAT support, Fren-assisted powerleveling, and paired Newb travel.

HealBot provides four mutually exclusive automation modes. HealBot runs configurable WHM, SCH, AST, or SGE healing for up to 20 watched friendly targets. Jacqueline of All Trades (JOAT) gives healing absolute priority, then uses the equipped healer's single-target filler only while healing is idle. PowerlevelBot retains its BRD/MCH instant-action policy. Newb sends one client's exact identity and travel state to one directly paired HealBot for remote healing and chase. Use `/healbot`, `/hb`, or the retained `/copellia` alias to open.

## Quick Setup

Open Main and choose **Quick Setup**, or open Settings and select its permanent **Quick Setup** tab.

- Choose HealBot, Jacqueline of All Trades (JOAT), PowerlevelBot, or Newb. The modes are mutually exclusive.
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
- Can optionally listen for one directly paired Newb while ordinary standalone healing continues normally.

Use the separate Watch window to add or remove targets. Unticking a target also removes its saved copy. Hold Ctrl while clearing the full set or removing an absent retained target.

## Newb direct pairing

Newb is a one-to-one, opt-in LAN mode. The Newb client connects directly to the configured HealBot IPv4 address; there is no UDP discovery. Both clients must run this compatible build and use the same TCP port and pair secret.

- Enable authenticated LAN pairing on both clients.
- On the Newb client, enter the HealBot PC's IPv4 address. Use `127.0.0.1` only when both game clients are on the same PC.
- Use port `47790` by default. Ports `1024` through `65535` are valid except reserved port `47789`.
- Use the same pair-specific secret on both clients; it must contain at least 16 characters. Mini and Settings can generate and copy one.
- Start HealBot mode and automation on the healing client first, then start Newb mode and automation on the Newb client.
- The HealBot client needs its normal healing dependencies plus Lifestream and vnavmesh readiness for paired travel.

Newb performs no local healing or attacking. After an exact-name/home-world assignment is acknowledged, it sends an immediate travel snapshot and then sends at most one every 750 ms after three yalms of movement or a mount, flight, world, territory, or accepted-teleport change. HealBot uses its existing exact ephemeral target, healing action engine, and owned travel route. QST and direct Newb assignments cannot be active at the same time. Disconnect, stop, mode change, configuration restart, or unload releases the session, paired target, and owned navigation; reconnect uses 1/2/5/10-second delays and a fresh assignment.

Pairing frames use HMAC-SHA256 authentication, two-minute freshness, five-minute replay rejection, and a 64-KiB newline-delimited limit. Traffic is authenticated but **not encrypted**: character names and coordinates remain visible to the local network. Use a trusted LAN and a pair-specific secret.

## Jacqueline of All Trades (JOAT)

JOAT shares HealBot's dependencies, watch list, per-healer action matrix, 900-ms decision cycle, and optional Rotation Solver Reborn isolation. Healing always wins: JOAT attacks only after the current healing decision queued nothing and found no blocked matching healing action.

- Requires an equipped WHM, SCH, AST, or SGE with that healer's action matrix enabled and at least one active watched target.
- Uses the existing FrenRider lease and restricted enemy selector, but ignores the BRD/MCH Powerlevel job selection.
- Considers only living, damaged, targetable enemies already targeting FrenRider's configured visible Fren or the local healer. Untouched enemies and enemies fighting anyone else are excluded.
- Uses only the highest currently available single-target filler spell for the equipped healer. DoTs, AoE, and oGCD attacks are excluded.
- Issues damage directly through HealBot's action executor while Rotation Solver Reborn remains isolated.
- Never auto-adds the Fren to Watch. When the Fren is the low-level character, select it explicitly so JOAT healing protects it.

### QST automatic helper mode

When a High-Level Helper selects the retained **Coppelia (JOAT)** provider in Questionable Companion, authenticated `Coppelia.QST` v3 status requests idempotently enable HealBot, select JOAT, and enable automation. One session-scoped assignment immediately displays the exact name/home-world Quester as selected but remote, then resolves only that identity into the native HealBot candidate when visible. It never saves the assignment or changes the configured watch list. The assembly, namespace, configuration/InternalName, repository, and `Coppelia.QST` IPC identity remain unchanged for compatibility.

Outside duties, HealBot follows without requiring a party: Lifestream visits the Quester's current world, uses the exact forwarded aetheryte when available or the nearest unlocked aetheryte in the newest reported territory, then owns one vnavmesh route at a time. Mounted follow latches on beyond 30 yalms and continues to 5; on-foot follow latches on beyond 20 yalms and continues to 10. Moving travel snapshots update the next destination without replacing an active or pending route. A route startup that never becomes active is rejected once and waits for a newer snapshot; an active route that completes can advance once to the newest accepted coordinates.

Native Mount Roulette mirrors a mounted Quester and still mounts for on-foot catch-up beyond 50 yalms. Every permitted mounted chase prefers flight with a five-yalm vnavmesh tolerance: an on-foot Quester is approached in flight to 20 yalms before landing, dismounting, and finishing on foot; a grounded mounted Quester is approached to 5 yalms before landing while remaining mounted; and a flying Quester is followed in the air to 5 yalms. Completed aether-current sets permit flight directly. Mountable ARR territories without a standard set probe one flying route; success proves flight for that territory, while a rejected or non-starting probe visibly falls back to ground until the territory or session resets. HealBot stops only its owned active path for casts and actions, cancels an actually pending pathfind only on terminal session release, suspends travel inside duties, and clears travel once when the session releases.

The QST High-Level Helper setting **Summon companion chocobo** is on by default. QST must successfully apply that preference through the local-only companion IPC before Helper activation is ready. While owned and enabled, HealBot checks at most every 15 seconds and uses Gysahl Greens only below 900 seconds of buddy time while logged in, on foot, outside combat/duties/sanctuaries, and free of casting or occupied states. It pauses only HealBot-owned navigation for the item action and does not control companion stance. Deactivation, role/provider loss, plugin disable, and unload clear QST ownership.

HealBot exclusively owns prebuffing, healing, raising, action selection, and cast holds for the assigned Quester; JOAT attacks enemies targeting that exact visible Quester only after HealBot yields. Both remain held while the paired Helper is mounted or mounting, so after landing HealBot resumes first and keeps priority. Optional DAF duties require ADS and FrenRider. Quester-owned entry waits for stable duty entry before configuring FrenRider with the exact Quester and starting ADS. The retained `Coppelia`-owned entry additionally requires DAD, keeps the helper as party leader, and asks DAD for a Regular + Unsynced run. Assignment release restores temporary FrenRider, target, and movement state while keeping QST-owned activation across connection churn; changing the Helper role/provider or unloading QST restores the prior HealBot activation state.

## PowerlevelBot

PowerlevelBot requires an unlocked BRD or MCH that is already equipped. It never changes gearsets and does not use the HealBot watch list.

Activation requires compatible FrenRider Powerlevel IPC, FrenRider enabled, a configured and visible Fren, and no active companion chocobo. Target selection is intentionally narrow: only living, targetable, damaged combatant enemies already targeting FrenRider's configured Fren or the local player are eligible. HealBot uses instant, hostile-only, single-target BRD/MCH actions and does not pull untouched enemies.

## Windows and commands

HealBot provides four windows:

- **Main** is the status dashboard for mode, automation, readiness, runtime state, and mode-specific target information.
- **Settings** contains Quick Setup, General, HealBot Actions, and Requirements / Help tabs.
- **Watch** manages the shared HealBot/JOAT filters, persistence, retained targets, and the live eligible-target table.
- **Mini** provides all four mode choices, Start/Stop, role-aware pairing settings, secret generation/copy, save/restart, and live connection/healing/chase status.

Main, Settings, and Watch positions are saved independently. `/healbot ws` resets those positions and `/healbot j` moves Main to a random visible location.

Commands:

- `/healbot`, `/hb`, or `/copellia` - open Main
- `/healbot mini`, `/hb mini`, or `/copellia mini` - open Mini
- `/healbot config` - open Settings
- `/healbot watch` - open Watch
- `/healbot on` or `/healbot off` - control automation for the selected mode
- `/healbot heal` - select HealBot
- `/healbot powerlevel` or `/healbot pl` - select PowerlevelBot
- `/healbot joat` - select Jacqueline of All Trades (JOAT)
- `/healbot jot` - compatibility alias for `/healbot joat`
- `/healbot newb` - select Newb
- `/healbot status` - print the selected mode status

## Build

The solution targets .NET 10, x64, and Dalamud API 15.

```powershell
dotnet test Coppelia.sln -c Debug --no-restore
dotnet build Coppelia.sln -c Release --no-restore
```

The Dalamud packager can emit a local `latest.zip` during a normal Release build. Building does not publish a release.
