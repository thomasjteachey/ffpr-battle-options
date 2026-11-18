# Battle Options for the FINAL FANTASY Pixel Remaster

Three battle settings in the game's own **Config** menu, for FINAL FANTASY VI Pixel Remaster
(Steam). An add-on: it changes no game data and installs beside the unmodded game or other mods
(Brave New World, Worlds Collide, HD-2D).

| Setting | Choices | What it does |
|---|---|---|
| **Battle Mode** | Wait / Active / **Pause** | Pause is Wait, and the enemies' ATB gauges also stop while one of your characters' turns is up (a command menu is waiting for you). Your other characters' gauges, actions already under way and status timers keep going. |
| **Cast Bars** | Off / On | While a character waits between a chosen command and its action (a spell's cast, an attack's wind-up, Jump's time in the air), their ATB gauge shows the wait left, draining. |
| **Enemy ATB** | Off / On | Each enemy's ATB gauge is shown under it. |

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
  Config's battle page; Pause holding an enemy's gauge while turns are up; a Fight's wait drained on
  the cast bar; saves untouched). Pause and the cast bars apply after Worlds Collide's SNES ATB rates
  and command waits. Built to sit beside Brave New World too (its own Wait Gauge: the Cast Bars
  setting decides whether it shows), not yet run together.
- **FINAL FANTASY IV and V PR**: the mod names only the engine code the Pixel Remasters share and
  finds each game's own Config and ATB classes by name, so it loads there, but it hasn't been tested
  in those games. FF V has no Cast Bars row (every command acts at once there); FF IV's cast bars
  measure the game's own countdown, since it has no separate wait-time table.
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
- `PauseAtb.cs`: an enemy's gauge increase (`ATBCalc`) is zero while the game's queue of characters
  waiting for a command (`BattleProgressATB.battleATBOrderUiStack`) isn't empty.
- `CastBars.cs`: the wait left on the character's chant (`BattleProgressATB.chantingInfo`) drawn on
  their ATB gauge (`BattlePlayerInfomationController.UpdateATB`).
- `EnemyAtb.cs`: each enemy's gauge (`gaugeStatusDictionary`) drawn under its sprite, projected by
  the camera that draws it.

No game files are in this repository.
