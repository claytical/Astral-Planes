# Steam Input setup

`in_game_actions.vdf` is the action manifest that `SteamInputManager` loads via
`SteamInput.SetInputActionManifestFilePath`. It has one action set, `Game`. Each action feeds one
control on the semantic `SteamInputGamepad`:

| Action | Unity control | Default binding (Xbox / PS) | Default binding (Switch) |
|---|---|---|---|
| Move | leftStick | Left stick | Left stick |
| Thrust | rightTrigger | RT / R2 | ZR |
| Choose | buttonSouth | A / Cross (bottom) | **A (right)** |
| Back | buttonEast | B / Circle (right) | **B (bottom)** |
| West | buttonWest | X / Square | Y |
| North | buttonNorth | Y / Triangle | X |
| LeftShoulder / RightShoulder | shoulders | LB/RB, L1/R1 | L/R |
| Menu | select | View / Share/Create | Minus |
| Start | start | Menu / Options | Plus |
| MenuUp/Down/Left/Right | dpad | D-pad | D-pad |

## Until default configs exist
Steam has nothing to bind, so `Choose` reports no origins. `SteamInputManager` then leaves Unity's
native gamepads enabled (fallback path), and nothing breaks. Steam takes over gameplay input
only once every connected controller has `Choose` bound.

## Authoring the default configs
1. Run the game through Steam (or the editor with `steam_appid.txt` and Steam running).
2. Open the controller configurator (Shift+Tab → Controller Layout, or Big Picture) for each
   controller type and bind the actions as in the table above.
3. Export each layout as a new config. Copy the `.vdf` into this folder (e.g.
   `controller_ps5.vdf`), then add a `configurations` block to `in_game_actions.vdf`. Paths are
   relative to the manifest:
   ```
   "configurations"
   {
       "controller_ps5"        { "0" { "path" "controller_ps5.vdf" } }
       "controller_switch_pro" { "0" { "path" "controller_switch_pro.vdf" } }
       ...
   }
   ```
   Types to cover: `controller_xbox360`, `controller_xboxone`, `controller_ps4`, `controller_ps5`,
   `controller_switch_pro`, `controller_switch_joycon_pair`, `controller_neptune` (Steam Deck),
   `controller_steamcontroller_gordon`, `controller_generic`.
4. On the Steamworks partner site (Steam Input): choose the in-game-actions / Steam Input API
   option, opt in every controller type, and publish the same configs as official defaults.
