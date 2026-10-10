# ValPerfPatcher

Runtime fixes for performance hot spots found with [ValPerfMonitor](https://github.com/skatefast29/ValPerfMonitor). It patches other mods in memory at startup. It never edits their files, and each mod keeps working the same way, minus the wasted work.

## Fixes
| Fix | Target mod | Problem | Change |
|---|---|---|---|
| Skip idle zone sorting | HearthBelow 0.6.2 | Every frame it sorts all terrain zones with a new closure, even when no zone has work (~1 KB of garbage per frame) | On frames with no queued zone work, run only the distant-terrain and visibility updates. Frames with work run the original unchanged. |
| Cache backpack check | AzuExtendedPlayerInventory 2.6.1 | Its Adventure Backpacks compatibility asks "is this a backpack?" several times per frame, and each answer allocates (~2.5 KB per frame) | Remember the answer per item type, only once in a world |
| Backpack check without garbage | Adventure Backpacks 2.2.9 | Its patches on the game's ownership check and network calls read every object's name (`gameObject.name`, a new string each time) to spot its hidden backpack object: about 56 bytes on each of hundreds of checks per frame | The name is read once per object and the answer remembered |
| Minimap level text a few times per second | Creature Level & Loot Control 5.0.6 | Rebuilds the minimap's area level text every frame (a new dictionary, text replace, number text and a copy of the level thresholds, about 0.6 KB per frame) | Rebuilt at most every 0.25 s (setting `Minimap level refresh seconds`); the first update after the minimap opens runs at once. The shown level can lag by up to that long. |
| Reuse creature spawn entries | Mods bundling CreatureManager (Bestiary 1.2.1, Air Animals, Land Animals) | Every time an area loads, the mod takes its creatures out of the shared spawn list and builds them again from its config, with reflection per creature (about 0.35 ms per area with Bestiary) | Built once, then reused while they are still in the list and none of that mod's settings has changed. A setting change (also one synced from a server) rebuilds them at the next area, as before. |
| No stacked copies in new areas | Mod vegetation (RtD Ocean 2.2.51) | Some mod vegetation asks for groups of plants with no spread, so the extra plants of a group land exactly on the first: invisible copies that cost as much as real ones and stay in the world. RtD Ocean's seaweed, sponges, coral stones and crops do this (about a third of its seaweed near the coast) | In newly generated areas, one plant per group. The game's own vegetation and areas already generated are not changed. |
| Reuse gamepad button names | Jotunn 2.30.2 | `"Joy!" + name` builds a new string on about 80 input checks per frame (~2.4 KB per frame) | One cached string per button |
| Reuse dig preview fit | HearthBelow 0.6.2 | While a digging tool is held, the dig preview fits its rings to the ground every frame: up to 240 raycasts (about 0.2 ms per frame) | While the aim stays put (within 1 cm), reuse the last fit for up to 0.2 s. The preview and its settings are unchanged. |
| Reuse permission section name | Server Devcommands 1.115 | Every permission check (several per frame) lowercases the same section name into a new string | Lowercase it once and reuse it |
| Start building the world early | Riverheim 1.2.1 | Riverheim builds its whole world map (about 7 s on 32 threads) only after the game's 1.5 s start pause and the scene load | The same build starts in the background the moment a world is started from the menu; Riverheim takes its results when it gets there. Only when hosting your own world. |
| Biome map on all CPU cores | Valheim itself, with Riverheim 1.2.1 | Every world load the game looks up the biome and height of 4 million map points one at a time (about 5 s with Riverheim) | The same lookups split across CPU cores; each point is still filled once, so the map is the same. Only on Riverheim worlds, and only while no other mod hooks those lookups. |
| Save its settings once | Comfort Tweaks 3.3.11 | On the first world load it adds a setting for every comfort piece and fire (about 260), and BepInEx rewrites the whole 52 KB config file after each one (about 1.5 s) | Saves are held while it sets them up, then the file is written once. Same file. |
| Throttle static object ground checks | Valheim itself | Every rock, stump and similar object casts a ray to check for ground under it about every 0.3 s, forever (about 85 per frame) | After a check that leaves the object where it is, check again after about 2 s (setting `Static object ground check interval`, 0 = game default). An object can take up to that long to start falling after the ground under it is dug away. Works the same with Valheim Community Patch. |
| Distance check without garbage | Valheim itself | The "is this near the player?" check builds a throwaway 3-number array on every call (about 50 bytes, `Mathf.Max` with three values). The object ownership pass runs it for every nearby object every 2 seconds (~640 KB each time), and building pieces, rocks, spawners and object loading use it too | Same result computed without the array. Measured: that pass went from 643 KB to under 5 KB, about one garbage collection per minute fewer standing still. Valheim Community Patch 0.35 and later has the same fix; with it installed this one stands down. |
| Read the translation files once | Valheim itself | Mods make their own English translation objects, and each one re-reads all 13 of the game's translation files (every language column, character by character): about 0.4 s each time, 9 s at startup with 62 mods | The words each file gives per language are kept after the first read and added instead. Mods' own translation hooks still run, so the words are the same. Starts before every other mod (a second small plugin, "ValPerfPatcher (early start)"), so the loads made while mods start are covered. |
| Reuse loaded mod translations | Mods bundling LocalizationManager (AAABuildMenu, Bestiary, GapFiller, Seasonality, HearthBelow, Comfort Tweaks, Sailing, the Azu mods and others) | Every time any translation object is set up, each copy searches the whole BepInEx folder and re-reads its translation files; some do it once per game translation file | After its first load per language, each copy adds its already loaded words. |

**Measured for the HearthBelow zone sorting, AzuExtendedPlayerInventory and Jotunn fixes** (ValPerfMonitor, same spot, with and without): the garbage those three mods create dropped from 5.4 to 1.2 KB per frame, and garbage collections from 12 to 9 per minute. The gain is less garbage collection (fewer small stutters), not CPU time: the HearthBelow fix's CPU saving was within run-to-run noise.

**Measured for the in-game fixes** (62 mods, standing still in the Meadows, same spot, all fixes on vs all off):

| | Off | On |
|---|---|---|
| Garbage created by mods, per frame | 12.2 KB | 5.4 KB |
| Garbage collections per minute | 9 | 7 |
| CPU time in mods, per frame | 0.71 ms | 0.55-0.62 ms |

**Startup and world loading** (same 62 mods): 54.1 s to the main menu with the translation fixes off, 41.1 s with them on. World loading on a Riverheim world: 23.3 s -> 14.4 s until the player spawns.

## Safety
Every fix:
- only applies when its target mod is installed (the base-game fix always applies);
- checks that the target code still looks the way it was analysed, and stands down with a log warning if a mod update changed it;
- keeps the target mod's behaviour the same;
- can be turned off on its own in `BepInEx/config/valheimmods.ValPerfPatcher.cfg`.

The startup log says which fixes were applied, for example `HearthBelow: skip idle zone sorting: applied.`

## Requirements
- Valheim 1.0 with BepInExPack_Valheim 5.4.23 or newer.
- Only needed where you want the speed-up. Servers and other players don't need it, and it sends nothing over the network. None of the target mods is required: fixes for mods that aren't installed do nothing.

## Installation
**Gale / r2modman:** *Import → local mod* and pick the release zip. **Manual:** copy `ValPerfPatcher.dll` to `BepInEx/plugins/ValPerfPatcher/`.

## Checking that it helps
Install ValPerfMonitor in a test profile and run its full suite (Shift+F11) twice at the same spot: once with this mod's fixes on, once with them all set to `false` in the config. Compare the runs in `BepInEx/ValPerfMonitor/compare.html` (Allocations and GC per minute show the difference).

## Build from source
Needs the .NET SDK, Valheim, and a BepInEx profile that has **HearthBelow** installed (the HearthBelow fix compiles against its DLL; at runtime it's only touched when HearthBelow is present).

```
dotnet build -c Release
dotnet build -c Release -p:Deploy=true
python package.py
```
- The first builds `bin/Release/net48/ValPerfPatcher.dll`.
- `Deploy=true` also copies it into the profile's `BepInEx/plugins/ValPerfPatcher/`.
- `package.py` (Python 3) makes the Thunderstore-format zip in `dist/`.

Paths default to a Steam install and a Gale profile named "Default". Override them: `dotnet build -c Release -p:ValheimDir="D:\Games\Valheim" -p:GaleProfile="C:\path\to\profile"` (the folder that contains `BepInEx`). HearthBelow is expected at `BepInEx/plugins/Azumatt-HearthBelow/HearthBelow.dll`. It targets .NET Framework 4.8 because HearthBelow does.

## Adding a fix
Each fix is one file in `src/` with an `Apply(Harmony)` method that returns `null` when applied or a reason when it stood down, registered in `Plugin.cs` with its own config switch. Before patching, verify every member it touches exists with the expected type; if not, return a reason instead of patching.

## License
MIT, see [LICENSE](LICENSE).
