# Changelog

## 1.0.8

- Minor README fixes. No code changes since 1.0.7.

## 1.0.7

- Other players on top: no longer creates a little garbage on every map pin update while TargetPortal is installed.
- Other players on top: if it ever hits an error, it switches itself off with one warning in the log instead of interrupting the map's pin updates.

## 1.0.6

- New fix: other players' icons stay on top of the map. The map draws icons in the order they were made, so a player's icon was often hidden under pins made later (locations, mod pins, pins scrolling into view). Now other players are moved to the top on the big map and the minimap. While TargetPortal's portal map is open, its portal icons stay above the players so they can still be clicked. Switch: `Map: other players on top`.

## 1.0.5

- New fix: faster startup (and faster loading of some mod content in the world). Many mods ship LZMA-compressed asset bundles that Unity unpacks completely every time they load. Now, for any mod, the first load converts a copy to LZ4 in the background (in `BepInEx\cache\ValPerfPatcher`), and later loads use that copy. With RtD Ocean, More World Locations, Bestiary, Valheim Armory and OdinShip: loading mods 24.8 s -> 15.7 s at startup. More World Locations' ~180 location files are covered too. The first launch after installing (or after a mod updates, or a game update that changes Unity) is as slow as before; new copies are made automatically. Same assets; copies unused for 30 days are deleted. Switch: `Mod asset bundles: keep a fast-loading copy`.

## 1.0.4

- The changelog is now included in the package, so Thunderstore and Hexium show it. No code changes since 1.0.3.

## 1.0.3

- New fix for BetterMap: it named the trader pins by looking up every location on the map on every frame; now only when the game updates those pins (every 5 s). BetterMap went from 0.14 to 0.03 ms and from about 1 KB to 0.3 KB of garbage per frame.

## 1.0.2

- New icon. No code changes.

## 1.0.1

- Works alongside Valheim Community Patch 0.35 and later: VCP now has its own fix for the game's distance check, so this mod's version stands down when VCP is installed (one info line in the log). Without VCP nothing changes.

## 1.0.0

First public release. Each fix has its own config switch and a check that stands it down if the target code changed.

- **Less garbage while playing:** HearthBelow's idle zone sorting, AzuExtendedPlayerInventory's backpack check, Adventure Backpacks' ownership check, Creature Level & Loot Control's minimap level text, Jotunn's gamepad button names, Server Devcommands' permission checks, and the game's own distance check. Measured with 62 mods standing still: garbage from mods 12.2 -> 5.4 KB per frame, garbage collections 9 -> 7 per minute.
- **Less work while playing:** HearthBelow's dig preview reuses its ground fit while the aim stays put; rocks, stumps and similar objects resting on the ground check for ground every ~2 s instead of every ~0.3 s (setting `Static object ground check interval`).
- **Faster startup:** the game's translation files are read once per language, and mods that bundle LocalizationManager reuse their loaded text. Starts before every other mod, so the loads made while mods start are covered. 54.1 s -> 41.1 s to the main menu with 62 mods.
- **Faster world loading:** Riverheim starts building its world map as soon as a world is started; the game's biome map check runs on all CPU cores on Riverheim worlds; Comfort Tweaks writes its config file once instead of once per piece. 23.3 s -> 14.4 s until the player spawns.
- **Areas:** mods that bundle CreatureManager reuse their creature spawn entries instead of rebuilding them on every area load; mod vegetation with no spread no longer puts invisible stacked copies on the same spot in newly generated areas.
