# Coppelia LCARS image prompts

## 01-main-regular-compact.png

```text
Create a Coppelia LCARS concept sheet showing regular and compact specimens of the same main window. Keep equal body readability and the same fixture; compact reduces gaps and reflows panels without shrinking all text. Dark near-black backgrounds and panel interiors; restrained orange/lilac/periwinkle LCARS elbows and segmented rails; blue control accents, dark outlined buttons. No white cards, gradients or glow. Native titlebars retain product actions and visible globe/language, monitor/Compact mode, droplet/colour, Krangle privacy and muted speaker shortcuts. Krangle obscures identifying text, never opens hidden settings. Anonymous fixture data only.

Keep the real dashboard structure, not invented top-level tabs. Header actions are Quick Setup, Watch, Settings, Mini, Status to chat and Ko-fi, with existing Krangle and DTR bar checkboxes. Status dashboard contains operating-role controls Off, Stand-alone, Helper, Newb, distinct from Stand-alone behavior HealBot, JOAT, PowerlevelBot. Use role Stand-alone and behavior HealBot selected in this design fixture, while Primary state reads a neutral blocked/idle condition. This does not claim performed actions. Do not replace roles with generic Start, Pause, Stop buttons.

Use the existing read-only summaries Primary state, Next action and Active / paired identity. Identity is None or anonymous Sample companion. Keep Dependency readiness and Watched targets as the lower panels, side by side when wide and stacked when narrow. The real Open watch window action remains. Readiness may show unavailable or unchecked sample states rather than a fabricated all-green operational pass. Show zero watched targets or a harmless anonymous row, no actual account identities.

Powerlevel job is a conditional combo only for PowerlevelBot; it is absent in this HealBot specimen. Do not invent combat rotation profiles, damage meters, dungeon queues or network pairing credentials on Main. Details retain the existing disclosure, native status text and scroll behavior. The global Compact mode shortcut changes spacing across the product; Mini remains a separate utility window. Source contract, not literal UI: Coppelia/Windows/MainWindow.cs:99 dashboard; :189 header; :252 read-only summaries; :337 behavior; :370 Powerlevel job gate. Preserve dark content and restrained semantic readiness colours inside LCARS rails.
```

## 02-settings-mini-and-auxiliary.png

```text
Create a Coppelia supporting-surface sheet with a full Settings owner and distinct Mini and Watch utility references. Labels outside the app separate these independent surfaces. Dark near-black backgrounds and panel interiors; restrained orange/lilac/periwinkle LCARS elbows and segmented rails; blue control accents, dark outlined buttons. No white cards, gradients or glow. Native titlebars retain product actions and visible globe/language, monitor/Compact mode, droplet/colour, Krangle privacy and muted speaker shortcuts. Krangle obscures identifying text, never opens hidden settings. Anonymous fixture data only.

Settings must show complete tabs Quick Setup, General, HealBot Actions, Window appearance, Requirements / Help. Select General with existing Krangle names, Show dependency toasts and Show DTR bar entry checkboxes, DTR mode combo and target-kind checkboxes Players, Companion chocobos, NPC party members, Friendly battle NPCs, Save heal targets. Do not add new target types. Include a separate Appearance crop with ordered Auto / Picker / LCARS radios and UI tones off sharing the native speaker. Existing language/compact/opacity preferences retain their scope; no volume slider.

The true Mini specimen uses role radios Off, Stand-alone, Helper, Newb, plus the existing behavior controls when appropriate. Use Off selected, so idle readiness is honest and no action is claimed. Its healing-context table headings remain Watched target, HP, LOS, Rescue with anonymous empty/idle values. Keep the small Krangle action. Do not scale down the main dashboard and call it Mini.

The Watch reference retains Quick Setup, Main, Settings, Mini, Refresh, Target current and Clear watched. Target-kind filters read Players, Chocobos, NPC Party, Battle NPC and Save heal targets. Use table headings Watch, Name, Type, Job, HP, Dist with one anonymous fixture row and an unchecked Watch box. Retained/offline rows use State instead of HP. Avoid player IDs and real network secrets.

Quick Setup remains the existing multi-step flow, beginning Set up Stand-alone / Set up Helper / Set up Newb; per-job HealBot action tables retain their original controls. Source evidence, not printed: ConfigWindow.cs:162, :256, :325, :690; MiniWindow.cs:224; WatchWindow.cs:226, :275, :413. No invented roles, hidden-setting Krangle or performed pairing.
```



## 03-mini-regular-compact.png — dedicated Mini continuation, 2026-10-23

Built-in image generation; new image, no CLI or image-editing script. Both density examples now visibly include Operating role, Stand-alone behavior, JOAT attack mode, Companion summoning and all five stances, Healing context and Operational status.

Source evidence: `Coppelia/Windows/MiniWindow.cs:84` draws the Mini body; `:109` Companion; `:112` Operational status; `:162` Healing context; `:244` roles; `:273` standalone behavior; `:295` JOAT attack mode; `:328` companion controls.

```text
Use case: ui-mockup. Create a crisp straight-on landscape review sheet with two FULL existing Coppelia Mini desktop plugin windows, REGULAR left and COMPACT right, no game scene. This is source-faithful design artwork, not runtime evidence. Same readable body text size in both windows (18px equivalent), compact reduces gaps and chrome only. Choose generous 2400x1600 composition so all sections fit. Native titlebars "Coppelia Mini" with existing open-main, Settings, enabled power controls and optional appearance colour/globe/compact/opacity, privacy and MUTED speaker icons, native close. Black #07090D, warm orange #FFAA66 LCARS rounded elbows/segmented rails, lilac #CC99CC panel bars, periwinkle #9999FF, off-white text. Realistic flat dark outlined practical controls; no glow, gauges or decorative science-fiction numbers. Title rails compact enough to prioritize readable source content.
In BOTH windows display exactly the same complete coherent Stand-alone / JOAT fixture:
section "Operating role", four radio choices "Off", "Stand-alone" SELECTED, "Helper", "Newb". Adjacent real button "Krangle names: ON".
section "Stand-alone behavior", three radios "HealBot", "JOAT" SELECTED, "PowerlevelBot".
section "JOAT attack mode", two radios "DoTs only" SELECTED and "Full RSR rotation". Muted help "Both choices use RSR Manual targeting. Full RSR changes offensive actions and AoE only."
section "Companion", checked box "Summon companion chocobo", muted inventory "Gysahl Greens (NQ + HQ): 32". Five stance radios exactly "Free Stance" SELECTED, "Defender Stance", "Attacker Stance", "Healer Stance", "Follow". Reflow into readable rows; do not omit controls.
section "Healing context", actual compact table headers "Watched target", "HP", "LOS", "Rescue"; one fabricated row "Example target", "100%", "Visible", "Inactive". Keep table exact, no generic watched-target list.
section "Operational status" with two real label/value summaries "Primary state" / "Idle", "Next action" / "Waiting for eligible target".
These sections all appear in both density variants in this order, no tiny cropped controls, complete bottom frame. Captions REGULAR and COMPACT are outside. This Stand-alone fixture does not show Helper/Newb endpoint or remote-status fields because those are conditional. No extra Run switch, no invented action buttons, no progress percentages, no generic profile, no volume slider, no speech system, no real identities. Krangle means obfuscate identifying text. Distinguish actual status colours from decorative skin colours. This must visibly include Companion and Operational status, which earlier overview art omitted.
```

Observed limits: This is the Stand-alone/JOAT fixture. Helper/Newb endpoint and remote-status states, QST-owned disabled states and every original title action remain source-owned conditional behavior rather than simultaneous specimen fields. Title glyph spacing/pixel geometry is illustrative.
