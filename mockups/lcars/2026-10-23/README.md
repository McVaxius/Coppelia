# Coppelia LCARS concepts

Approved LCARS design concepts. Implementation is selected for the repositories in Z:\myrepos.txt, with the rollout tracked in Z:\!todo\2026-10-22GOATFUCK0002.md. These images are design references, not runtime or test evidence.

- [Main: regular and compact](01-main-regular-compact.png)
- [Settings, Mini and supporting surfaces](02-settings-mini-and-auxiliary.png)
- [Dedicated Mini: regular and compact](03-mini-regular-compact.png)

The second sheet depicts selected existing supporting surfaces. Other source windows retain their established layout and actions under the proposed shared appearance; this is not a claim that every scroll position or conditional state has a separate image. Browser companions retain browser controls. Main/Mini windows are distinct from density variants. Generated text and examples are design material, not runtime evidence.

## Source scope

Source commit: `b5a59e3e930473e2d5a3664a38eef8f45afa7c84`. Existing dirty paths were inspected by count (0); none are included or changed by this mockup task.

## Source-verified control contract

- `Coppelia/Windows/MainWindow.cs:99` has Status dashboard, Dependency readiness and Watched targets panels, not a new tab system. `:189` defines Quick Setup, Watch, Settings, Mini, Status to chat, Ko-fi.
- `MainWindow.cs:205` onwards keeps native operating-role and behavior controls; Stand-alone behavior uses HealBot, JOAT, PowerlevelBot (`:337`); Powerlevel job appears only in PowerlevelBot (`:370`). The status cards Primary state, Next action and Active / paired identity are read-only.
- `ConfigWindow.cs:162` defines Quick Setup, General, HealBot Actions, Window appearance, Requirements / Help. Setup role actions are Set up Stand-alone / Set up Helper / Set up Newb (`:256`), followed by behavior choice and target-kind checkboxes (`:325`). Do not conflate roles and behaviors.
- `MiniWindow.cs:224` uses Off, Stand-alone, Helper, Newb role radios. Its Healing context columns are Watched target / HP / LOS / Rescue (with its native assignment/role-dependent first heading). `WatchWindow.cs:226` defines Refresh, Target current, Clear watched; `:275` defines Players / Chocobos / NPC Party / Battle NPC filters; tables retain Watch / Name / Type / Job / HP or State / Dist.

Selected supporting surfaces are complete Settings navigation with General/Appearance crops, the true Mini window and Watch. Quick Setup steps and per-job HealBot action editors retain their source flow and conditional controls; network details use placeholders and no real address/secret. The main Powerlevel job selector is omitted in the HealBot fixture because its gate is false.

## Existing UI source inventory

- `Coppelia/Windows/ConfigWindow.cs`
- `Coppelia/Windows/MainWindow.cs`
- `Coppelia/Windows/MiniWindow.cs`
- `Coppelia/Windows/WatchWindow.cs`

## Shared appearance contract

Auto and Picker retain their order and values; LCARS is the third option. Preserve native IDs, original actions, saved scope and semantic status colours. The proposed LCARS UI tones default off; the speaker and Settings preference are the same value, with no added volume or pack selector. Krangle obfuscates identifying text. Actual adoption waits for the FrenRider pilot.

Exact generation prompts are in [PROMPTS.md](PROMPTS.md).







## Dedicated Mini review — 2026-10-23

[03-mini-regular-compact.png](03-mini-regular-compact.png) is the dedicated, source-checked Mini sheet generated with the built-in image tool. Both density examples now visibly include Operating role, Stand-alone behavior, JOAT attack mode, Companion summoning and all five stances, Healing context and Operational status.

Source evidence: `Coppelia/Windows/MiniWindow.cs:84` draws the Mini body; `:109` Companion; `:112` Operational status; `:162` Healing context; `:244` roles; `:273` standalone behavior; `:295` JOAT attack mode; `:328` companion controls.

Raster limits: This is the Stand-alone/JOAT fixture. Helper/Newb endpoint and remote-status states, QST-owned disabled states and every original title action remain source-owned conditional behavior rather than simultaneous specimen fields. Title glyph spacing/pixel geometry is illustrative. The regular/compact comparison changes spacing; implementation must retain the same readable body typography. This artwork is not runtime or game/GPU evidence.
