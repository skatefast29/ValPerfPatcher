# Changelog

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
