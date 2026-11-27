# ValPerfPatcher

Fixes for performance hot spots in popular mods and in Valheim itself, found with [ValPerfMonitor](https://github.com/skatefast29/ValPerfMonitor). It patches code in memory at startup and never edits other mods' files. A few fixes are for glitches and errors, not speed; the tables mark them.

## Install
- **Mod manager (Gale, r2modman):** install as usual, or *Import → local mod* with the release zip.
- **Manual:** copy `plugins/ValPerfPatcher.dll` to `BepInEx/plugins/ValPerfPatcher/` and `patchers/ValPerfPatcher.Preloader.dll` to `BepInEx/patchers/ValPerfPatcher/`.
- Needs BepInExPack_Valheim 5.4.23 or newer. None of the target mods is required: a fix for a mod you don't have does nothing.
- **On your PC:** all the in-game and startup fixes.
- **On a server or when hosting:** the world-save fixes, the missing-mod data fix and the early world build. Install it there with both folders.
- It adds no network messages, and players without it can join as normal.

## Valheim 1.0.30 renamed `SoftReference.Name`
Mods built before 1.0.30 still ask for `Name` and fail (`MissingMethodException ... get_Name()`). BetterMap 1.1.0 then breaks location pins and, on a server, players' joins. Worldhalla 1.0.1's location preview fails. Jotunn 2.30.2 lost every location and dungeon room added through it (Jotunn 2.30.3 fixes this itself). The preloader (`BepInEx/patchers`) adds `Name` back before any mod loads, and does nothing once a mod or the game no longer needs it. The plugin logs an error if the preloader is missing.

## Fixes
Biggest gain first in each table. "Not speed" = a glitch or error fix.

### Valheim, BepInEx and code shared by many mods
| Fix | Problem | Change |
|---|---|---|
| Write each config file once at startup and world load | BepInEx rewrites a mod's whole config after every new setting (RtD Ocean: 8.5 s) | Saves are held while mods start and while a world loads, then each file is written once. Mods loading 26.3 s -> 13.3 s. Never holds a value sent by a server. |
| Fast-loading copy of asset bundles | Unity unpacks LZMA bundles fully on every load (RtD Ocean, More World Locations, Bestiary and others: ~8 s per startup) | First load makes an LZ4 copy in `BepInEx/cache/ValPerfPatcher`; later loads use it. 24.8 s -> 15.7 s loading mods. Copies unused for 30 days are deleted. |
| Read the translation files once | Every mod translation object re-reads all 13 game translation files (~9 s at startup with 62 mods) | Words are kept after the first read. Runs before other mods ("ValPerfPatcher (early start)"). |
| Reuse loaded mod translations | Mods bundling LocalizationManager re-read their files every time any translation object is set up | Each copy reuses what it already loaded. |
| Biome map on all CPU cores | Each world load looks up 4 million map points one at a time (2.8 s; ~5 s on Riverheim) | Same lookups split across cores, same map. 2.8 s -> 0.95 s; Riverheim 5.1 s -> 0.5 s. |
| Free path-finding tiles sooner near the link limit | Fast travel past busy coasts hits Unity's 65,535 link limit, then errors on every new link | Above 45,000 links, tiles unused for 10 s are dropped instead of after 60 s. Steps aside for Valheim Community Patch 0.37+. |
| Inventory borders without garbage | Mods drawing on inventory slots search every slot every frame (AzuAutoStore ~35 KB, Recycle_N_Reclaim ~13 KB per frame) | The found slot child is remembered; search only for new slots. |
| Reuse gamepad button names (Jotunn) | `"Joy!" + name` builds a new string ~80 times per frame | One cached string per button. |
| Distance check without garbage | The game's "near the player?" check allocates an array per call (~640 KB every 2 s) | Same result without the array. Steps aside for Valheim Community Patch 0.35+. |
| Throttle static ground checks | Every rock and stump raycasts for ground every 0.3 s (~85 per frame) | After an unchanged check, wait ~2 s (`Static object ground check interval`). |
| Floating objects sent less often | Floating crates, logs and items are re-sent 20-26 times a second each | Sent again only after moving 5 cm, turning 2° or changing speed, and at most 5 times a second on waves (`Floating object updates per second`). Upload near crates: 198 -> 34 KB/s. |
| Creature walk and turn speeds sent less often | Tiny speed wobbles re-send a creature ~10 times a second | Re-sent only after a change of 0.1. |
| No stacked copies in new areas | Some mod vegetation (RtD Ocean) puts several plants on the same spot | One plant per group in newly generated areas. |
| Remove stacked mod plants (off by default) | Areas generated earlier keep their stacked copies | Deletes the extra copies in areas you own (`World: remove stacked mod plants`). Changes the world. |
| Reuse creature spawn entries | Mods bundling CreatureManager (Bestiary, Air Animals, Land Animals) rebuild their spawns on every area load | Built once; rebuilt when one of that mod's settings changes. |
| Character save without the map freeze | Each 30-minute character save packs the map on the game thread (~105 ms) | Packed on another thread into the same data (~30 ms left). Logout, quit and sleep save as before. |
| World save without the full copy | Before each world save the game copies every object (250-680 ms on a big server) | Copies only the objects being written. Same files, ~10 ms. Steps aside for SteadyFrame 1.3.4+. |
| Keep data of missing mods | A missing or broken location mod makes the game delete its locations and objects for good | Kept until the mod is back. Turn off for one session to clean up after a mod you removed on purpose. |
| No log flood standing still on ice | Unity logs "Look rotation viewing vector is zero" ~50 times a second | Character keeps its facing until it slides. |
| Hide sea creatures stuck above water | Multiplayer: creatures left mid-jump hang above the water | Not drawn until they move. Not speed. |
| Hide fish stuck in the air | Same for fish | Not drawn until they move. Not speed. |
| No breeding error for untameable creatures | Creatures that breed but can't be tamed (Bestiary's fox) error every 30 s | Skipped; only tamed creatures breed anyway. |
| Snow cap on objects | Snow on stations and furniture grows into tall pillars (worse with Seasonality) | Capped at 10% height (settings under `Snow`); building parts unchanged. Not speed. |
| Shared favourites | Quick Stack, AzuAutoStore, AzuExtendedPlayerInventory each keep their own favourites copy | All use one set, so a star shows everywhere at once. Not speed. |
| Other players on top of the map | Player icons get hidden under pins made later | Moved to the top (portal icons stay above while TargetPortal's map is open). Not speed. |

### Single mods
| Fix | Mod | Problem | Change |
|---|---|---|---|
| Start building the world early | Riverheim through Worldhalla | World build (~5.6 s) starts only after the scene load | Starts the moment a world is chosen or you press Join. Used only if the server sends the same world. |
| Smelter count without a full search | AutomaticFuel up to 1.5.6 | Searches all loaded objects whenever a smelter appears (~35 ms) | One search per world, then a kept list. AutomaticFuel 1.5.7+ fixes it itself. |
| Anyone can switch a torch | AutomaticFuel 1.5.8-1.6.2 | Only the fire's owner can toggle it | Takes ownership first. AutomaticFuel 1.6.3+ fixes it itself. Not speed. |
| Forget map boats after a relog | BetterMap | Old boat list errors every frame after a relog | List emptied when a world starts. |
| Trader pin names only when pins change | BetterMap | Looks up every location every frame | Runs only when the game updates pins. |
| Ship check without garbage | Longship Upgrades | Two new strings per ship per physics step | Uses the ship's type number. |
| Helpers added once after a relog | Seasonality | Adds another tree and water helper on every world load (~12,700 objects per relog) | Added only once. |
| Season change finishes | Seasonality | Mined shore ice left on its list made the season change stop halfway | Gone pieces removed first. Not speed. |
| Winter snow on every unroofed piece | Seasonality | Floor under a piece counted as roof, so snow appeared by chance | Only what's above counts, checked every few seconds. Not speed. |
| Fish and sea creatures stay under the ice | Seasonality | Fish and sea creatures end up on top of frozen water | Kept under the ice; shore creatures (list in config) left alone. Not speed. |
| Icebergs don't fly around | Seasonality | Ice pieces spawn inside the ice floor and get flung | They pass through the ice floor. Not speed. |
| Only players slide on ice | Seasonality | Creatures slip, speed up and spam the log | Creatures walk normally; players still skate. Not speed. |
| Water keeps moving after a season change | Seasonality with Valheim Community Patch | Frozen/thawed water stops animating until relog | Steps aside for Valheim Community Patch 0.37+. Not speed. |
| No error for the planting preview | Farming | One error per seed planted | Preview skipped. |
| Item data added once after a relog | Creature Level & Loot Control | ~12,500 more objects per relog | Added only once. |
| Map copies only when level circles are shown | Creature Level & Loot Control | Keeps 192 MB of map copies even with circles off | Made only while circles are shown. |
| Server time asked less often | Creature Level & Loot Control | Asks the server up to ~100 times a second | At most every 10 s; clients only. |
| Minimap level text a few times a second | Creature Level & Loot Control | Rebuilt every frame | Every 0.25 s (`Minimap level refresh seconds`). |
| Forget old inventories | MultiUserChest | Keeps every chest it touched in memory | Emptied per world, trimmed while playing. |
| Vanity lists kept from growing | AzuExtendedPlayerInventory | Grows without end while farming (+42,800 objects in 15 min) | Emptied past 2,048 entries. |
| Character preview only for your own gear | AzuExtendedPlayerInventory | Re-renders when any creature equips something | Only for your character. |
| Cache backpack check | AzuExtendedPlayerInventory | "Is this a backpack?" allocates several times per frame | Answer kept per item type. |
| Forget unloaded containers | Quick Stack - Store - Sort - Trash - Restock | Keeps unloaded containers forever | Removed before each search. |
| Item backpack check without garbage | Adventure Backpacks | ~2 KB per frame at a busy base | Answer kept per item type. |
| Backpack check without garbage | Adventure Backpacks | Reads every object's name on hundreds of checks per frame | Name read once per object. |
| Skip idle zone sorting | HearthBelow | Sorts all zones every frame with nothing to do | Skipped on idle frames. |
| Reuse dig preview fit | HearthBelow | Up to 240 raycasts per frame while digging | Reused while the aim stays put (up to 0.2 s). |
| Reuse permission section name | Server Devcommands | New lowercase string on every permission check | Made once. |

## Measured
62 mods, same spot, all fixes on vs off (ValPerfMonitor):

| | Off | On |
|---|---|---|
| Mod garbage per frame | 12.2 KB | 5.4 KB |
| Garbage collections per minute | 9 | 7 |
| CPU in mods per frame | 0.71 ms | 0.55-0.62 ms |
| Launch to main menu | 54.1 s | 41.1 s |
| Riverheim world load to spawn | 23.3 s | 14.4 s |

## Safety
Every fix applies only when its target is installed, checks the target code still looks as expected (otherwise it stands down with a log line), steps aside when another mod already does the job, and can be turned off in `BepInEx/config/valheimmods.ValPerfPatcher.cfg`. The startup log lists each fix as applied, skipped, or why it stood down.

To check the gain yourself: run ValPerfMonitor's suite (Shift+F11) at one spot with the fixes on and again with them off, then compare in `BepInEx/ValPerfMonitor/compare.html`.

## Build from source
Needs the .NET SDK, Valheim, and a BepInEx profile with HearthBelow installed (one fix compiles against it).

```
dotnet build -c Release                 # bin/Release/net48/ValPerfPatcher.dll (+ the preloader)
dotnet build -c Release -p:Deploy=true  # also copies both into the profile
python package.py                       # Thunderstore zip in dist/
```
Defaults: Steam install and a Gale profile named "Default". Override with `-p:ValheimDir=...` and `-p:GaleProfile=...` (the folder containing `BepInEx`).

Each fix is one file in `src/` with an `Apply` method that returns `null` when applied or a reason when it stood down, registered in `Plugin.cs` with its own config switch.

## License
MIT, see [LICENSE](LICENSE).
