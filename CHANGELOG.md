# Changelog

## 1.0.27

- New fix: mods with the same favouriting code (Quick Stack, AzuAutoStore, AzuExtendedPlayerInventory and others) share one set of favourites, so a star or unstar shows in all of them at once.
- Config save fix also holds saves while a world loads, for any mod (about 450 saves written as 5-7 files); replaces the Comfort Tweaks-only fix.
- Config save fix stops holding when the game connects to a server, so server settings never reach your files.
- Config save fix ends its startup hold when a world starts loading at the latest (dedicated servers), and writes a held file first when a mod re-reads it.
- Biome map on all CPU cores now works on every world, not only Riverheim worlds (normal world: 2.8 s -> 0.95 s per load).
- Inventory border fix works for every mod that draws on inventory slots, not only AzuAutoStore and Recycle_N_Reclaim (one setting instead of two).

## 1.0.26

- New fix: AzuAutoStore favourite borders and Recycle_N_Reclaim trash borders no longer make garbage every frame while the inventory is open.

## 1.0.25

- Character save fix: its map copies and buffers are made once when a world loads and reused, so a save asks for almost no new memory (a burst of new memory at the save made the garbage collector stop the game for 120-150 ms).

## 1.0.24

- Character save fix: checking the packed map no longer makes about 50 MB of garbage per save (now about 1 MB), which set off a 100-170 ms garbage collection during the save.
- Character save fix: steps aside if the game stops using GZip for map data.

## 1.0.23

- New fix: Creature Level & Loot Control keeps its three map copies (192 MB) only when its level circles are shown.
- New fix: AzuExtendedPlayerInventory refreshes its character preview only for your own gear, not every creature's.
- New fix: AzuExtendedPlayerInventory's vanity item lists no longer grow without end.
- MultiUserChest fix now also drops unused chest records while playing, not only when a world starts.

## 1.0.22

- New fix: Creature Level & Loot Control asks the server for the time at most every 10 seconds instead of up to 100 times a second.

## 1.0.21

- New fix: floating crates, logs and dropped items are sent to the server far less often (one player's upload near floating crates: 198 KB/s down to 34 KB/s).
- New fix: creatures' walk and turn speeds are sent less often.

## 1.0.20

- General snow accumulation fixes, configurable.

## 1.0.19

- New fix: Longship Upgrades ship check without garbage.

## 1.0.18

- New fix: fish no longer hang in the air over the water in multiplayer.
- New fix: AutomaticFuel's torch switch key works for every player, not only the fire's owner.
- Checked against AutomaticFuel 1.6.0, Valheim Community Patch 0.38.0 and other updates.

## 1.0.17

- Riverheim early world build now also works when joining a server (world build on the loading screen 6.8 s -> 0.6 s).
- Checked on a dedicated server: all fixes load there.
- AutomaticFuel 1.5.8: no more "has changed" warning (the fix still steps aside correctly).

## 1.0.16

- New fix: character saves no longer freeze the game while the map is packed (~135 ms -> ~30 ms).

## 1.0.15

- Checked against Seasonality 3.8.5, Adventure Backpacks 2.4.6 and other updates.
- Adventure Backpacks 2.4.6: part of the backpack check steps aside (the mod does it itself now).

## 1.0.14

- New fix: no log flood when standing still on ice.
- New fix: Farming no longer logs an error for every seed planted.
- One fix failing to set up no longer skips the fixes after it.
- Seasonality ice extras: fish stay under the ice, icebergs stay put, creatures don't slide, season changes finish.

## 1.0.13

- Fixes step aside when another mod already does the job (Valheim Community Patch 0.37, AutomaticFuel 1.5.7).

## 1.0.12

- New fix: path-finding no longer runs out of links when travelling fast past busy coasts.
- Memory no longer grows per relog from Seasonality, Creature Level & Loot Control, MultiUserChest and Quick Stack.

## 1.0.11

- New fix: AutomaticFuel no longer searches every object when a smelter appears (35 ms -> 0.015 ms).
- New fix: water keeps moving after a Seasonality season change with Valheim Community Patch.
- New fix: no BetterMap boat errors after a relog.
- Fixed warnings about ValPerfPatcher when HearthBelow isn't installed.

## 1.0.10

- Faster startup of the patcher itself.
- New fix: mod config files written once at startup (launch to main menu 44 s -> 28 s).
- New option (off by default): remove stacked mod plants in already-generated areas.
- New fix: Adventure Backpacks backpack check without garbage.

## 1.0.9

- README: fixes split into two tables.

## 1.0.8

- README fixes.

## 1.0.7

- Other players on top: less garbage with TargetPortal; switches itself off on an error.

## 1.0.6

- New fix: other players' icons stay on top of the map.

## 1.0.5

- New fix: fast-loading copies of mod asset bundles (loading mods 24.8 s -> 15.7 s).

## 1.0.4

- Changelog included in the package.

## 1.0.3

- New fix: BetterMap trader pins named only when they update.

## 1.0.2

- New icon.

## 1.0.1

- Steps aside for Valheim Community Patch's own distance-check fix.

## 1.0.0

First public release: less garbage and work while playing, faster startup (54 s -> 41 s), faster Riverheim world loading (23 s -> 14 s).
