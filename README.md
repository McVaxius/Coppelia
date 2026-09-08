# HealBot

QST Companion can opt in to **HealRider** during its rotations. Select an unlocked passenger mount in QST's Coppelia Helper setup and enable HealRider on the Quester (default walking threshold: 50 yalms). Companion owns the shared party-inviter setting, travel exclusions, and Questionable pause/resume; HealBot owns approach, mount, exact boarding verification, flight, and dismount within five yalms. Missing ride support or eligibility leaves ordinary following available. HealRider uses separate v1 IPC endpoints and requires updated QST endpoints and HealBot; direct Newb pairing does not offer rides.

Set **Pickup wait (seconds)** in Companion (default 60, range 10–180) for one deadline shared by party formation, approach, and boarding. Invitations wait for the Helper's travel to finish and retry every five seconds for either inviter until exact two-member membership is confirmed. HealBot approaches within three yalms and lands before boarding readiness, even when already mounted far away. Companion then applies a temporary FrenRider pillion/close-follow profile on the Quester; **no FrenRider user configuration is required**, only an updated, enabled FrenRider plugin. Departure requires both Quester passenger confirmation and exact seat verification. Companion releases the temporary profile and transport-owned party on completion or cancellation, transfers the party during duty handoff, and supplies a fresh ordinary-follow snapshot after cleanup. Unrelated mounted state cannot hold failed party preparation open.

Stand-alone watched-target healing, healer-first JOAT, Fren-assisted powerleveling, and authenticated Helper/Newb pairing.

HealBot has four top-level operating roles: **Off**, **Stand-alone**, **Helper**, and **Newb**. Stand-alone has the smaller HealBot, Jacqueline of All Trades (JOAT), and PowerlevelBot behavior choice. Helper listens for one authenticated Newb and activates JOAT for remote healing and chase after a compatible status request. Newb connects asynchronously and performs no local healing or attacking. Use `/healbot`, `/hb`, or the retained `/copellia` alias to open.

## Quick Setup

Open Main and choose **Quick Setup**, or open Settings and select its permanent **Quick Setup** tab.

- Choose Stand-alone, Helper, or Newb; Stand-alone then chooses HealBot, JOAT, or PowerlevelBot.
- Changes remain in a draft until **Finish**. **Cancel** discards that draft.
- Finish requires either **Start this role now** or **Save setup and leave role Off**.
- If startup fails, the wizard stays incomplete and shows the same blocker used by normal role selection.
- A genuinely new configuration opens Quick Setup automatically. Existing configurations migrate without unsolicited onboarding.
- Closing an unfinished first-run setup leaves it incomplete, so it opens again on the next plugin load. Completed setup remains manually rerunnable.

## Stand-alone

Stand-alone performs no networking. Its HealBot and JOAT behaviors use the Watch window and existing per-healer action matrix; PowerlevelBot retains its BRD/MCH policy.

- Supports WHM, SCH, AST, and SGE.
- Evaluates up to 20 explicitly watched friendly targets, including players outside the party.
- Supports healing, raises, buffs, and pre-buffs according to the configured job rules.
- Can discover players, companion chocobos, NPC party members, and friendly battle NPCs through independent filters.
- Can optionally save explicitly watched targets. The saved-target scan range only allows a saved target to rejoin after it returns; it never discovers or selects a new target.
- Requires FrenRider, vnavmesh, and either BossMod Reborn (BMR) or VBM. JOAT additionally requires loaded, working Rotation Solver Reborn (RSR) for damage.
- HealBot uses optional RSR isolation when available. Coppelia restores the captured RSR session settings when it releases ownership.

Use the separate Watch window to add or remove targets. Unticking a target also removes its saved copy. Hold Ctrl while clearing the full set or removing an absent retained target.

## Helper and Newb direct pairing

Helper/Newb is one-to-one direct TCP pairing with no UDP discovery. Helper listens automatically when its port and shared secret are valid. Newb validates only local identity, IPv4 address, port, and shared secret synchronously; connection, authentication, assignment, and reconnect work run asynchronously.

- Select Helper on the healing client and Newb on the connecting client.
- In Newb Settings, enter the Helper PC's IPv4 address. Use `127.0.0.1` only when both game clients are on the same PC.
- Use port `47790` by default. Ports `1024` through `65535` are valid except reserved port `47789`.
- Use the same visible pair-specific shared secret on both clients; it must contain at least 16 characters. Settings generates exactly 32 random bytes as 64 hexadecimal characters and provides Copy.
- Editing a networking field commits when editing finishes; generation commits immediately. Each commit saves and restarts pairing once.
- The HealBot client needs its normal healing dependencies plus Lifestream and vnavmesh readiness for paired travel.

An authenticated compatible v2 status request idempotently activates the Helper's plugin and JOAT provider even when local JOAT was not already running. Disconnect releases the Newb assignment, exact target, and owned navigation while keeping Helper activation ready for reconnection. Leaving Helper or unloading restores the pre-Helper plugin, Stand-alone behavior, and automation state. QST and direct Newb have separate activation and assignment ownership and cannot restore or release each other's state.

After an exact-name/home-world assignment is acknowledged, Newb sends an immediate travel snapshot and then sends at most one every 750 ms after three yalms of movement or a mount, flight, world, territory, or accepted-teleport change. Reconnect uses 1/2/5/10-second delays and a fresh assignment.

Protocol v2 reports provider, JOAT, and travel readiness separately. Older peers receive an explicit incompatibility blocker before assignment or Helper activation. Pairing frames retain the authenticated HMAC-SHA256 envelope, two-minute freshness, five-minute replay rejection, correlated acknowledgements, and a 64-KiB newline-delimited limit. Traffic is authenticated but **not encrypted**: character names and coordinates remain visible to the local network. Use a trusted LAN and a pair-specific secret.

## Jacqueline of All Trades (JOAT)

JOAT shares HealBot's dependencies, watch list, per-healer action matrix, and 900-ms decision cycle. Loaded, working Rotation Solver Reborn is required and exclusively executes JOAT damage. Healing always wins: before a matching Watch heal, buff, or raise rule executes, Coppelia switches RSR off; it stays off while healing is queued, blocked, or casting and returns to Auto only after the next genuinely idle healing decision and an eligible enemy selection.

- Requires an equipped WHM, SCH, AST, or SGE with that healer's action matrix enabled and at least one active watched target.
- Uses the existing FrenRider lease and restricted enemy selector, but ignores the BRD/MCH Powerlevel job selection.
- Considers only living, damaged, targetable enemies already targeting FrenRider's configured visible Fren or the local healer. Untouched enemies and enemies fighting anyone else are excluded.
- Defaults to **DoTs only**, which temporarily enables WHM Aero/Aero II/Dia, SCH Bio/Bio II/Biolysis, AST Combust variants, or SGE Eukrasian Dosis variants while disabling the equipped healer's other RSR offensive actions.
- **Full RSR rotation** reuses the offensive-action states, AoE mode, and hostile-target mode captured when Coppelia took ownership. Switching modes or releasing ownership restores captured values instead of enabling every action.
- Issues no JOAT damage through Coppelia's direct action executor; RSR Auto owns the attack after Coppelia selects the eligible enemy.
- Never auto-adds the Fren to Watch. When the Fren is the low-level character, select it explicitly so JOAT healing protects it.

### QST-owned Helper provider

When a High-Level Helper selects the retained **Coppelia (JOAT)** provider in Questionable Companion, authenticated `Coppelia.QST` v3 status requests idempotently enable HealBot, select JOAT, and enable automation. One session-scoped assignment immediately displays the exact name/home-world Quester as selected but remote, then resolves only that identity into the native HealBot candidate when visible. It never saves the assignment or changes the configured watch list. The assembly, namespace, configuration/InternalName, repository, and `Coppelia.QST` IPC identity remain unchanged for compatibility.

Outside duties, HealBot follows without requiring a party: Lifestream visits the Quester's current world, uses the exact forwarded aetheryte when available or the nearest unlocked aetheryte in the newest reported territory, then owns one vnavmesh route at a time. Mounted follow latches on beyond 30 yalms and continues to 5; on-foot follow latches on beyond 20 yalms and continues to 10. Moving travel snapshots update the next destination without replacing an active or pending route until it has been trying for at least 10 seconds and the newest coordinates have moved more than 5 yalms; HealBot then replaces only its owned route. A route startup that never becomes active is rejected once and waits for a newer snapshot; an active route that completes can advance once to the newest accepted coordinates.

Native Mount Roulette mirrors a mounted Quester and still mounts for on-foot catch-up beyond 50 yalms. Every permitted mounted chase prefers flight with a five-yalm vnavmesh tolerance: an on-foot Quester is approached in flight to 20 yalms before landing, dismounting, and finishing on foot; a grounded mounted Quester is approached to 5 yalms before landing while remaining mounted; and a flying Quester is followed in the air to 5 yalms. Completed aether-current sets permit flight directly. Mountable ARR territories without a standard set probe one flying route; success proves flight for that territory, while a rejected or non-starting probe visibly falls back to ground until the territory or session resets. HealBot stops only its owned active path for casts, actions, and stale-destination refreshes; it cancels an actually pending pathfind only for a stale-destination refresh or terminal session release, suspends travel inside duties, and clears travel once when the session releases.

The QST High-Level Helper setting **Summon companion chocobo** is on by default. QST must successfully apply that preference and its default-DoTs-only JOAT attack preference through separate local-only IPC endpoints before Helper activation is ready. While companion summoning is owned and enabled, HealBot checks at most every 15 seconds and uses Gysahl Greens only below 900 seconds of buddy time while logged in, on foot, outside combat/duties/sanctuaries, and free of casting or occupied states. It pauses only HealBot-owned navigation for the item action and does not control companion stance. QST's attack-mode ownership is temporary; assignment release, deactivation, role/provider loss, and unload restore Coppelia's saved local radio choice.

HealBot exclusively owns prebuffing, healing, raising, action selection, and cast holds for the assigned Quester; JOAT attacks enemies targeting that exact visible Quester only after HealBot yields. Both remain held while the paired Helper is mounted or mounting, so after landing HealBot resumes first and keeps priority. Optional DAF duties require ADS and FrenRider. Quester-owned entry waits for stable duty entry before configuring FrenRider with the exact Quester and starting ADS. The retained `Coppelia`-owned entry additionally requires DAD, keeps the helper as party leader, and asks DAD for a Regular + Unsynced run. Assignment release restores temporary FrenRider, target, and movement state while keeping QST-owned activation across connection churn; changing the Helper role/provider or unloading QST restores the prior HealBot activation state.

## PowerlevelBot

PowerlevelBot requires an unlocked BRD or MCH that is already equipped. It never changes gearsets and does not use the HealBot watch list.

Activation requires compatible FrenRider Powerlevel IPC, FrenRider enabled, a configured and visible Fren, and no active companion chocobo. Target selection is intentionally narrow: only living, targetable, damaged combatant enemies already targeting FrenRider's configured Fren or the local player are eligible. HealBot uses instant, hostile-only, single-target BRD/MCH actions and does not pull untouched enemies.

## Windows and commands

HealBot provides four windows:

- **Main** begins with Off, Stand-alone, Helper, and Newb, then shows one primary state, one next action, and the active or paired identity.
- **Settings** contains Quick Setup, General, role-specific networking, JOAT DoTs-only/full-RSR radios, HealBot Actions, and Requirements / Help tabs. QST temporarily disables the local attack radios while it owns the mode; Helper owns port/shared-secret fields, and Newb additionally owns the Helper IPv4 address.
- **Watch** manages the shared HealBot/JOAT filters, persistence, retained targets, and the live eligible-target table.
- **Mini** provides the four operational role controls, Stand-alone behavior selection, JOAT attack-mode radios when applicable, live status, and a Settings button; it does not edit networking fields. QST ownership disables the radios and shows the effective paired mode.

Main, Settings, and Watch positions are saved independently. `/healbot ws` resets those positions and `/healbot j` moves Main to a random visible location.

Commands:

- `/healbot`, `/hb`, or `/copellia` - open Main
- `/healbot mini`, `/hb mini`, or `/copellia mini` - open Mini
- `/healbot config` - open Settings
- `/healbot watch` - open Watch
- `/healbot off` - select Off
- `/healbot on` - resume the saved non-Off role
- `/healbot standalone`, `/healbot helper`, or `/healbot newb` - select that operating role
- `/healbot heal` - select Stand-alone + HealBot
- `/healbot powerlevel` or `/healbot pl` - select Stand-alone + PowerlevelBot
- `/healbot joat` - select Stand-alone + Jacqueline of All Trades (JOAT)
- `/healbot jot` - compatibility alias for `/healbot joat`
- `/healbot status` - print role, primary state, next action, and active/paired identity

## Build

The solution targets .NET 10, x64, and Dalamud API 15.

```powershell
dotnet test Coppelia.sln -c Debug --no-restore
dotnet build Coppelia.sln -c Release --no-restore
```

The Dalamud packager can emit a local `latest.zip` during a normal Release build. Building does not publish a release.
