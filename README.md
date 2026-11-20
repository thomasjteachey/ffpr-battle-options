# Battle Options for the FINAL FANTASY Pixel Remaster

Battle settings in the game's own **Config** menu, for FINAL FANTASY IV, V and VI Pixel Remaster
(Steam). An add-on: it changes no game data and installs beside the unmodded game or other mods.

| Setting | Choices | What it does |
|---|---|---|
| **Battle Mode** | Wait / Active / **Pause** | Pause is Wait, and all of the battle's time also stops while you choose a command and while an action plays out: every ATB gauge (your party's and the enemies'), every chosen action's cast time and every status timer (Regen, Poison, Stop, Doom...). |
| **Cast Bars** | Off / On | While a character waits between a chosen command and its action (a spell's cast, an attack's wind-up, Jump's time in the air), their ATB gauge shows the wait left, draining. |
| **Enemy ATB** | Off / On | Each enemy's ATB gauge is shown under it, behind the battle menus. A defeated enemy's gauge stays until its death animation starts; one that escapes loses it at once. |

The Cast Bars and Enemy ATB rows sit under the battle settings (after Battle Messages). The choices
are kept in `BepInEx/config/prmod.battleoptions.cfg`; under Pause the game's own config keeps Wait,
so removing the mod leaves the game on Wait.

## Install

1. BepInEx 6 for Unity IL2CPP games, x64, in the game folder (a bleeding-edge build from
   builds.bepinex.dev; made with 6.0.0-be.697). Start the game once so BepInEx sets itself up.
2. Put the `BattleOptions` folder (with `BattleOptions.PR.dll`) in `BepInEx/plugins`.

Steam Deck / Linux (Proton): BepInEx only starts with this in the game's Steam launch options:

    WINEDLLOVERRIDES="winhttp=n,b" %command%

Remove it by deleting `BepInEx/plugins/BattleOptions`.

## Compatibility

- **FINAL FANTASY VI PR**: checked headless with Worlds Collide installed (the rows in the in-game
  Config's battle page; Pause holding gauges while turns are up; a Fight's wait drained on
  the cast bar; saves untouched). Pause and the cast bars apply after Worlds Collide's SNES ATB rates
  and command waits; Worlds Collide 0.6.4's status clock waits with Pause. Built to sit beside Brave New World too (its own Wait Gauge: the Cast Bars
  setting decides whether it shows), not yet run together.
- **FINAL FANTASY IV PR**: tested in the in-game Config and battle. Pause, Cast Bars and Enemy ATB
  load; enemy gauges track live ATB and disappear with the enemy. FF IV's cast bars measure the
  game's own countdown, since it has no separate wait-time table.
- **FINAL FANTASY V PR**: tested in the in-game Config and battle. Pause held enemy ATB, cast time
  and status timers while a command was open, and Enemy ATB tracked the enemy and disappeared with
  it. FF V intentionally has no Cast Bars row because its commands act immediately.
- FINAL FANTASY I-III PR have no ATB: the mod stays off there.

## Build

    cd plugin/BattleOptionsPlugin
    dotnet build -c Release -p:GameDir="<the game's folder>"

The game folder needs BepInEx with its generated `BepInEx/interop` (the game's code as .NET
assemblies: start the game once with BepInEx). `plugin/PrKit` is the modding kit the plugin compiles
in (safe Harmony hooks, switchable features, headless test runs); `PrKit.props` explains the setup.

## How it works

- `ConfigMenu.cs`: the Config menu's rows are `ConfigCommandController`s whose `ConfigCommandsData`
  names their setting. Battle Mode gets a third choice; Cast Bars and Enemy ATB are copies of an
  On/Off row with setting types of their own, handled by hooks on the game's row set-up, Left/Right
  and help-line methods.
- `PauseAtb.cs`: time is held while the game's queue of characters waiting for a command
  (`BattleProgressATB.battleATBOrderUiStack`) isn't empty or an action is being prepared, played or
  finished (`BattleActExection.GetStatingAct()`): every gauge increase (`ATBCalc`) is zero, the cast
  countdown (`UpdateChantTime`) and the status timers (`BattleConditionController.Update`, the part
  of the ATB update the game's own Wait skips in its menus) are skipped. A mod with a battle clock
  of its own can wait too: the held frame is in AppDomain data `pr.battle.timeHeldFrame` (Worlds
  Collide's SNES status clock reads it).
- `CastBars.cs`: the wait left on the character's chant (`BattleProgressATB.chantingInfo`) drawn on
  their ATB gauge (`BattlePlayerInfomationController.UpdateATB`).
- `EnemyAtb.cs`: each enemy's gauge (`gaugeStatusDictionary`) under its sprite, projected by the
  camera that draws it. The gauges are images in the battle UI's own canvas, just before the battle
  menus (`BattleUIManager.keyMenuParent`), so every menu window covers them. A defeated enemy keeps
  its gauge until its animator enters `DownStart` (the death animation).

No game files are in this repository.
