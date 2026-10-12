# Changelog

## 1.0.3

- New fix for BetterMap: it named the trader pins by looking up every location on the map on every frame; now only when the game updates those pins (every 5 s). BetterMap went from 0.14 to 0.03 ms and from about 1 KB to 0.3 KB of garbage per frame.

## 1.0.1

- Works alongside Valheim Community Patch 0.35 and later: VCP now has its own fix for the game's distance check, so this mod's version stands down when VCP is installed (one info line in the log). Without VCP nothing changes.

## 1.0.0

First public release. Each fix has its own config switch and a check that stands it down if the target code changed.

- **Less garbage while playing:** HearthBelow's idle zone sorting, AzuExtendedPlayerInventory's backpack check, Adventure Backpacks' ownership check, Creature Level & Loot Control's minimap level text, Jotunn's gamepad button names, Server Devcommands' permission checks, and the game's own distance check. Measured with 62 mods standing still: garbage from mods 12.2 -> 5.4 KB per frame, garbage collections 9 -> 7 per minute.
- **Less work while playing:** HearthBelow's dig preview reuses its ground fit while the aim stays put; rocks, stumps and similar objects resting on the ground check for ground every ~2 s instead of every ~0.3 s (setting `Static object ground check interval`).
- **Faster startup:** the game's translation files are read once per language, and mods that bundle LocalizationManager reuse their loaded text. Starts before every other mod, so the loads made while mods start are covered. 54.1 s -> 41.1 s to the main menu with 62 mods.
- **Faster world loading:** Riverheim starts building its world map as soon as a world is started; the game's biome map check runs on all CPU cores on Riverheim worlds; Comfort Tweaks writes its config file once instead of once per piece. 23.3 s -> 14.4 s until the player spawns.
- **Areas:** mods that bundle CreatureManager reuse their creature spawn entries instead of rebuilding them on every area load; mod vegetation with no spread no longer puts invisible stacked copies on the same spot in newly generated areas.
