using System;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValPerfPatcher
{
    /// <summary>
    /// Runtime fixes for hot spots found with ValPerfMonitor. Each fix:
    ///  - only applies if the target mod is installed and the code still looks the way it was analysed
    ///    (a mod update that changes it makes the fix stand down instead of misbehaving);
    ///  - keeps the mod's behaviour identical, it only removes wasted work or garbage (the map icon order is the
    ///    one deliberate change);
    ///  - can be switched off on its own in the config.
    /// Nothing here edits other mods' files; it patches in memory at startup.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency(HearthBelowFix.TargetGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(AzuEpiBackpackFix.TargetGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(JotunnInputFix.TargetGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(DevcommandsPermissionFix.TargetGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(AdventureBackpacksProxyFix.TargetGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(CreatureLevelSectorTextFix.TargetGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(RiverheimLoadFix.TargetGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(ComfortTweaksConfigFix.TargetGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(ChebyshevDistanceFix.VcpGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(BetterMapLocationPinsFix.TargetGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(SeasonalWaterCacheFix.SeasonalityGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(AutomaticFuelSmelterScanFix.TargetGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(MultiUserChestBlockResetFix.TargetGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(QuickStackContainerListFix.TargetGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "valheimmods.ValPerfPatcher";
        public const string Name = "ValPerfPatcher";
        public const string Version = "1.0.18";

        private Harmony _harmony;

        private void Awake()
        {
            var harmony = _harmony = new Harmony(Guid);

            Apply("HearthBelow: skip idle zone sorting", HearthBelowFix.TargetGuid,
                "HearthBelow sorts all of its terrain zones every frame, even when none has work queued (~1 KB of garbage per frame). Skip the sort and the empty zone loop on those frames; its distant-terrain and visibility updates still run every frame.",
                () => HearthBelowFix.Apply(harmony));

            Apply("AzuExtendedPlayerInventory: cache backpack check", AzuEpiBackpackFix.TargetGuid,
                "AzuEPI's Adventure Backpacks compatibility asks 'is this item a backpack?' several times per frame, and each answer allocates (~2.5 KB of garbage per frame). Remember the answer per item type.",
                () => AzuEpiBackpackFix.Apply(harmony));

            Apply("Jotunn: reuse gamepad button names", JotunnInputFix.TargetGuid,
                "Jotunn builds the string \"Joy!\" + button name on every input check (~80 per frame, ~2.4 KB of garbage per frame). Reuse one cached string per button.",
                () => JotunnInputFix.Apply(harmony));

            Apply("HearthBelow: reuse dig preview fit", HearthBelowDigPreviewFix.TargetGuid,
                "While a digging tool is held, HearthBelow's dig preview fits its rings to the ground every frame (up to 240 raycasts). While the aim stays put, reuse the last fit for up to 0.2 s. The preview looks and works the same; its settings are untouched.",
                () => HearthBelowDigPreviewFix.Apply(harmony));

            Apply("Server Devcommands: reuse permission section name", DevcommandsPermissionFix.TargetGuid,
                "Every permission check (several per frame) lowercases the same section name into a new string. Lowercase it once and reuse it.",
                () => DevcommandsPermissionFix.Apply(harmony));

            Apply("Adventure Backpacks: backpack check without garbage", AdventureBackpacksProxyFix.TargetGuid,
                "Adventure Backpacks checks every object's name on every ownership check (hundreds per frame), making garbage each time. This remembers the answer per object instead.",
                () => AdventureBackpacksProxyFix.Apply(harmony));

            Apply("Adventure Backpacks: item backpack check without garbage", AdventureBackpacksProxyFix.TargetGuid,
                "Adventure Backpacks asks whether each equipped item is a backpack many times per frame, making about 2 KB of garbage per frame with a full set of gear. This remembers the answer per item type and skips the extra list (Adventure Backpacks 2.4.6 and later skip that list themselves).",
                () => AdventureBackpacksItemCheckFix.Apply(harmony));

            var sectorText = Config.Bind("Creature Level & Loot Control", "Minimap level refresh seconds", 0.25f,
                new ConfigDescription("How often the minimap's area level text is rebuilt (the mod does it every frame). 0 = every frame.", new AcceptableValueRange<float>(0f, 2f)));
            CreatureLevelSectorTextFix.IntervalSeconds = sectorText.Value;
            sectorText.SettingChanged += (_, __) => CreatureLevelSectorTextFix.IntervalSeconds = sectorText.Value;
            Apply("Creature Level & Loot Control: minimap level text a few times per second", CreatureLevelSectorTextFix.TargetGuid,
                "The mod rebuilds the minimap's area level text every frame, making garbage each time. This rebuilds it at most every 'Minimap level refresh seconds'.",
                () => CreatureLevelSectorTextFix.Apply(harmony));

            Apply("BetterMap: name trader pins only when the map updates them", BetterMapLocationPinsFix.TargetGuid,
                "BetterMap looks up every location on the map on every frame to name the trader pins, although the game only adds or removes those pins every 5 seconds (~0.1 ms and ~0.8 KB of garbage per frame). Do it only on the frames where the game updated them.",
                () => BetterMapLocationPinsFix.Apply(harmony));

            Apply("BetterMap: forget map boats when leaving a world", BetterMapLocationPinsFix.TargetGuid,
                "BetterMap keeps the boats and carts it shows on the map in a list that survives logging out. The game renumbers its objects for every world session, so after logging out and back in without restarting, BetterMap fails to find an old boat and logs an error on every frame. Empty that list when a world starts; BetterMap adds the boats again on its next update.",
                () => BetterMapVehicleResetFix.Apply(harmony));

            Apply("AutomaticFuel: count smelters without searching everything", AutomaticFuelSmelterScanFix.TargetGuid,
                "Every time a smelter, kiln or furnace is created (entering a base, loading an area, the hammer's placement preview) AutomaticFuel searches every object Unity has loaded to count the smelters, about 35 ms with a big mod list. Search once per world and keep that list up to date as smelters are created and removed. Same count. AutomaticFuel 1.5.7 and later no longer searches; with it, this one steps aside.",
                () => AutomaticFuelSmelterScanFix.Apply(harmony));

            Apply("AutomaticFuel: anyone can switch a torch", AutomaticFuelSmelterScanFix.TargetGuid,
                "AutomaticFuel's torch switch key only works for the player the game counts as the fire's owner; everyone else gets 'Cannot toggle (not owner)'. Take ownership of the fire first, as the mod already does for smelters.",
                () => AutomaticFuelTorchSwitchFix.Apply(harmony));

            Apply("Seasonality: helpers added once after a relog", SeasonalWaterCacheFix.SeasonalityGuid,
                "On every world load Seasonality adds another fall-colour helper to each tree prefab and another frozen-water helper to the area template, so after each logout and login every tree and water area carries one more (each tree repeats the same colour work, each water area gets another collider and material copy; about 12,700 more objects per relog). Add them only when the prefab has none yet, as on the first world load.",
                () => SeasonalityRandomColorsFix.Apply(harmony));

            Apply("Creature Level & Loot Control: item data added once after a relog", CreatureLevelSectorTextFix.TargetGuid,
                "On every world load the mod adds another copy of its original-item-values record to every item (about 12,500 more objects per relog); it only ever reads the first one. Add it only when the item has none yet, as on the first world load.",
                () => CreatureLevelItemDataFix.Apply(harmony));

            Apply("MultiUserChest: forget old inventories when a world starts", MultiUserChestBlockResetFix.TargetGuid,
                "MultiUserChest keeps a slot-block record for every inventory it has seen and never removes one, so the chests and player inventories of every earlier world session stay in memory. Empty that list when a world starts; the mod makes a fresh record whenever it needs one.",
                () => MultiUserChestBlockResetFix.Apply(harmony));

            Apply("Quick Stack: forget unloaded containers", QuickStackContainerListFix.TargetGuid,
                "Quick Stack keeps every container it has seen in a list and only removes one when it is broken, so containers in areas you left (and in earlier world sessions) stay in the list for good and every quick stack or restock walks past them. Remove the unloaded ones just before each search. Same containers found.",
                () => QuickStackContainerListFix.Apply(harmony));

            // Applied by EarlyPlugin before other mods start; bound here so the switches are in this config file.
            Report(EarlyPlugin.VanillaFix,
                "Startup creates many English translation objects, and each one re-reads all 13 of the game's translation files (every language). Keep the words each file gives per language after the first read and add those instead. Mods' own translation hooks still run.",
                EarlyPlugin.VanillaResult);

            Report(EarlyPlugin.LocalizerFix,
                "Mods that bundle LocalizationManager (Warfare, Monstrum, Wizardry, HearthBelow, AzuEPI, Comfort Tweaks and others) search the whole BepInEx folder and re-read their translation files every time any translation object is set up (some once per game translation file). Reuse each mod's already loaded text after the first time.",
                EarlyPlugin.LocalizerResult);

            Report(EarlyPlugin.BundleFix,
                "Many mods ship LZMA-compressed asset bundles that Unity unpacks completely every time they load (RtD Ocean, More World Locations, Bestiary, Valheim Armory, OdinShip: about 8 s at every startup; More World Locations' location files whenever those locations load). For any mod, the first load converts a copy to LZ4 in the background (in BepInEx\\cache\\ValPerfPatcher, a few hundred MB); later loads use that copy, which is much faster. Same assets. Copies unused for 30 days are deleted.",
                EarlyPlugin.BundleResult);

            Report(EarlyPlugin.ConfigFix,
                "BepInEx rewrites a mod's whole config file after every new setting it adds, so a mod adding hundreds of settings while it starts writes its file hundreds of times (RtD Ocean: about 8.5 s at every startup). Until the main menu has started, hold those saves and write each file once. Same files; saves while playing are not held.",
                EarlyPlugin.ConfigResult);

            Apply("Base game: distance check without garbage", null,
                "The game's 'is this near the player?' distance check builds a small throwaway array on every call (about 50 bytes), and it runs thousands of times: about 600 KB every 2 seconds in the object ownership pass alone, plus building pieces, rocks, spawners and object loading. Same result, no array.",
                () => ChebyshevDistanceFix.Apply(harmony));

            var interval = Config.Bind("Base game", "Static object ground check interval", 2f,
                new ConfigDescription("Valheim re-checks every rock, stump and similar object for ground underneath about every 0.3 s, forever. " +
                                      "After a check that leaves an object where it is, wait this many seconds (with a little jitter) before checking it again. " +
                                      "An object can take up to this long to start falling after the ground under it is dug away. 0 = game default.",
                                      new AcceptableValueRange<float>(0f, 10f)));
            StaticPhysicsThrottle.IntervalSeconds = interval.Value;
            interval.SettingChanged += (_, __) => StaticPhysicsThrottle.IntervalSeconds = interval.Value;
            Apply("Base game: free unused path-finding tiles sooner near the link limit", null,
                "Path-finding joins its area tiles with links, and Unity allows only 65,535. Travelling fast past coasts full of creatures (sailing, portals) can keep enough tiles loaded to reach that, after which every new link fails and the game logs an error each time. Once more than 45,000 links are in use, and until they are back under 40,000, tiles nobody has used for 10 s are dropped instead of after the game's 60 s. Tiles in use are never dropped; otherwise nothing changes. ValheimCommunityPatch 0.37 and later has the same fix ('Fix Pathfinding Link Limit'); while that is on, this one steps aside.",
                () => PathfindingLinkLimitFix.Apply(harmony, Logger));

            Apply("Base game: throttle static object ground checks", null,
                "Fewer ground checks for objects that are already resting on the ground; see 'Static object ground check interval' under Base game.",
                () => StaticPhysicsThrottle.Apply(harmony));

            Apply("Base game: no log flood when standing still on ice", null,
                "A character standing still on slippery ground is turned towards where it has been sliding. Right after moving straight up or down (a teleport into a new area, a fall) that slide can be exactly zero, and then the game writes 'Look rotation viewing vector is zero' to the log every physics step (about 50 a second) until it moves, and slowly turns it towards north. Common with Seasonality's frozen water, where everyone slips: the player standing still on the ice is enough. With no slide the character just keeps facing the way it does; anything else is unchanged.",
                () => SlideStandStillFix.Apply(harmony, Logger));

            Apply("Base game: character save without the map freeze", null,
                "Every character save packs the whole explored map one pixel at a time and compresses it, freezing the game for about a tenth of a second. A server makes every player save on its 30-minute world save, and each game also saves on its own 30-minute timer. For those two saves the map is copied in about a millisecond and packed on another CPU thread into exactly the same data, and the save finishes a moment later; if the map hasn't changed since the last save, the last data is used again. Logging out, quitting, sleeping and the save command save the game's way, as before. Only the character file is involved, never the world.",
                () => MapSaveFix.Apply(harmony, Logger));

            Apply("Base game: hide fish stuck in the air", null,
                "With other players around, fish can hang in the air above the water: a fish nobody was near stays where it was (often mid-jump), and when players come back it is handed to one of them, whose game may take a while to load it, while everyone else's game already shows it. Common when sailing together. On your game, a fish another player's game owns that has not moved for 3 seconds and is clearly above the water, not lying on anything, is not drawn until it moves again. Your own fish, moving or jumping fish, and fish lying on the shore or a deck always show. Only what you see changes.",
                () => StuckFishHideFix.Apply(harmony));
        }

        // After every plugin's Awake: fixes for code bundled into mods that may load after this one.
        private void Start()
        {
            Apply("CreatureManager mods: reuse spawn entries", null,
                "Mods that bundle CreatureManager (Bestiary, Air Animals, Land Animals and others) rebuild all their creature spawn entries from config every time an area loads (reflection per creature, about 0.35 ms per area with Bestiary). Build them once and reuse them until one of that mod's settings changes.",
                () => CreatureManagerSpawnFix.Apply(_harmony, Logger));

            Apply("Vegetation: no stacked copies in new areas", null,
                "Some vegetation asks for groups of plants but gives them no spread, so the extra plants of a group land exactly on the first one: invisible copies that cost as much as real ones and stay in the world (RtD Ocean's seaweed, ocean bushes, ocean rocks and land plants: about half of them). In newly generated areas, place one per group instead. Areas already generated are not changed.",
                () => StackedVegetationFix.Apply(_harmony, Logger));

            Apply("Riverheim: start building the world early", RiverheimLoadFix.TargetGuid,
                "Riverheim builds its whole world map (about 7 s) only after the game has paused and loaded the scene. Start the same build in the background as soon as a world is started from the menu, so it runs during that wait. When joining a server, start the world that server had last time as soon as Join is pressed; it is only used if the server sends the same world and settings. Same world.",
                () => RiverheimLoadFix.ApplyHeadStart(_harmony, Logger));

            Apply("Riverheim: biome map on all CPU cores", RiverheimLoadFix.TargetGuid,
                "Every world load the game looks up the biome and height of 4 million map points one by one (about 5 s with Riverheim). Riverheim's lookups are safe to run side by side, so split them across CPU cores. Same map. Only for Riverheim worlds, and only while no other mod hooks those lookups.",
                () => RiverheimLoadFix.ApplyParallelBiomes(_harmony, Logger));

            Apply("Comfort Tweaks: save its settings once", ComfortTweaksConfigFix.TargetGuid,
                "On the first world load Comfort Tweaks adds a setting for every comfort piece and fire (about 260), and BepInEx rewrites its whole config file after each one (about 1.5 s). Hold those saves and write the file once at the end. Same file.",
                () => ComfortTweaksConfigFix.Apply(_harmony));

            Apply("World: remove stacked mod plants", null,
                "Off by default: changes the world. Mod vegetation that asks for groups with no spread (RtD Ocean's seaweed and others) left invisible copies exactly on top of plants in areas generated before 'Vegetation: no stacked copies in new areas' (or generated on a server without it), about a third of the seaweed near coasts. When on, the extra copy on a spot is deleted from the world once your game owns that area, one plant stays. Never the game's own plants, pickable plants or anything built.",
                () => StackedVegetationCleanup.Apply(_harmony, Logger), defaultOn: false);

            Apply("Seasonality with ValheimCommunityPatch: water keeps moving after a season change", ChebyshevDistanceFix.VcpGuid,
                "ValheimCommunityPatch remembers each water area's surface look and only animates that. When Seasonality freezes or thaws the water it swaps that look, and the water you see stops moving while the waves go on: boats and fish seem to float above or sink into it until you relog. Let ValheimCommunityPatch pick up the new look right after Seasonality swaps it. ValheimCommunityPatch 0.37 and later does this itself (better: it also redoes the water's depth and wind setup); with it, this one steps aside.",
                () => SeasonalWaterCacheFix.Apply(_harmony));

            var leftAlone = Config.Bind("Seasonality", "Creatures left alone by the ice fix",
                "Neck, Seal, Seal_Pup, Neck_RtD, NeckSummon_RtD",
                "Creatures (prefab names, comma separated) that 'Seasonality: fish and sea creatures stay under the ice' never moves under the ice: amphibious ones that also live on shore, which may walk on the ice.");
            SeasonalityFishUnderIceFix.SetLeftAlone(leftAlone.Value);
            leftAlone.SettingChanged += (_, __) => SeasonalityFishUnderIceFix.SetLeftAlone(leftAlone.Value);
            Apply("Seasonality: fish and sea creatures stay under the ice", SeasonalWaterCacheFix.SeasonalityGuid,
                "In winter Seasonality freezes the water into a solid floor, but fish keep jumping and swimming just under the surface, and sea creatures (serpents, sharks, dolphins, whales, from the game or any mod) swim with their backs out of the water, so they end up on top of the ice. While the water is frozen, fish don't jump and swim at least a metre under the ice, sea creatures swim just deep enough to stay fully under it (fins included, with no ripples showing on the ice), and any found on the ice (except fish a player put down there) are put back under (where the water is deep enough). Tamed or ridden creatures are left alone, and so are Necks, seals and other shore creatures (the list 'Creatures left alone by the ice fix' under Seasonality). When the water thaws, everything swims as normal.",
                () => SeasonalityFishUnderIceFix.Apply(_harmony));

            Apply("Seasonality: icebergs do not fly around when they appear", SeasonalWaterCacheFix.SeasonalityGuid,
                "In winter Seasonality places icebergs and shore ice at the water line, which with 'Water Freezes' on is also its solid ice floor, so pieces with physics start inside that floor and get flung around as they appear. Let Seasonality's ice pieces pass through its ice floor, and push them gently out of anything else they appear inside. They still float, drift and can be rammed. They only exist in winter, so nothing else changes.",
                () => SeasonalityIceStillFix.Apply(_harmony));

            Apply("Seasonality: only players slide on the ice", SeasonalWaterCacheFix.SeasonalityGuid,
                "Seasonality makes every character on its frozen water slip, creatures included. Slipping creatures keep speeding up, can barely steer, and hits can send them far across the ice; one standing still also makes the game write a warning to the log every physics step. Keep the sliding for players (skating) and let creatures and enemies walk on the ice normally.",
                () => SeasonalityIceGripFix.Apply(_harmony));

            Apply("Seasonality: season change finishes", SeasonalWaterCacheFix.SeasonalityGuid,
                "Seasonality never takes broken shore-ice pieces off its list when they are mined or their area unloads, so the next season change fails on a piece that is gone and stops halfway: the world already looks like the new season, but the season name on the screen keeps the old one, and winter ice can keep being added to new areas. Remove the gone pieces from that list just before the season change goes through it. Seasonality 3.8.5 and later no longer fail on them, but the list still keeps growing; this keeps it short.",
                () => SeasonalitySeasonChangeFix.Apply(_harmony));

            Apply("Farming: no error for the planting preview", FarmingGhostPlantFix.TargetGuid,
                "Every seed you plant makes the game rebuild the see-through planting preview, and Farming's plant hook then tries to save your farming skill on that preview, which can't hold it: one error with a full stack trace in the log per seed (473 in one 2.5 h session), a small stutter each time. Skip Farming's hook for the preview only; real plants get your skill as before.",
                () => FarmingGhostPlantFix.Apply(_harmony));

            Apply("Map: other players on top", null,
                "The map draws icons in the order they were made, so other players' icons often end up hidden under other pins. Keep them on top. While TargetPortal's portal map is open, its portal icons stay above the players.",
                () => PlayerPinsOnTopFix.Apply(_harmony, Logger));
        }

        private void Report(string name, string description, string result)
        {
            Config.Bind("Fixes", name, true, description);
            if (result == null)
            {
                if (name == EarlyPlugin.LocalizerFix)
                {
                    TranslationLoadFix.PatchNewCopies();
                    Logger.LogInfo($"{name}: applied ({TranslationLoadFix.CopiesPatched} copies so far" +
                                   (TranslationLoadFix.CopiesSkipped > 0 ? $", {TranslationLoadFix.CopiesSkipped} look different and were left alone" : "") + ").");
                }
                else if (name == EarlyPlugin.BundleFix)
                    Logger.LogInfo($"{name}: applied ({BundleCacheFix.LoadedFromCopy} loaded from the fast copy so far" +
                                   (BundleCacheFix.Queued > 0 ? $", {BundleCacheFix.Queued} being converted for the next launch ({BundleCacheFix.SourceMegabytes:F0} MB)" : "") + ").");
                else Logger.LogInfo($"{name}: applied.");
            }
            else if (result == "off in config") Logger.LogInfo($"{name}: off in config.");
            else Logger.LogWarning($"{name}: not applied - {result}");
        }

        /// <summary>
        /// A type from an installed mod, looked up in that mod's own DLL. AccessTools.TypeByName goes through every
        /// type of every loaded assembly (0.1-0.3 s per call in a big profile); it stays as the fallback unless
        /// fallback is false (for a type that only some versions of the mod have).
        /// </summary>
        internal static Type ModType(string guid, string fullName, bool fallback = true)
        {
            if (Chainloader.PluginInfos.TryGetValue(guid, out var info) && info.Instance != null)
            {
                var type = info.Instance.GetType().Assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            return fallback ? AccessTools.TypeByName(fullName) : null;
        }

        // A static or instance field declared on the type, or null. Plain reflection: AccessTools logs a warning for
        // a field that is missing, and these checks expect some to be missing in other versions.
        internal static FieldInfo FieldOrNull(Type type, string name) =>
            type?.GetField(name, BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

        // An installed mod's version, for log lines.
        internal static string VersionOf(string guid) =>
            Chainloader.PluginInfos.TryGetValue(guid, out var info) ? info.Metadata.Version.ToString() : "?";

        // A fix returning this (plus the other mod's name) stands down quietly: that mod already does the same job.
        internal const string AlreadyDone = "already done by ";
        // A fix returning this (plus why) stands down quietly: a second mod it needs is not installed.
        internal const string NotNeeded = "not needed: ";

        private void Apply(string name, string targetGuid, string description, Func<string> apply, bool defaultOn = true)
        {
            ConfigEntry<bool> enabled;
            try
            {
                enabled = Config.Bind("Fixes", name, defaultOn, description);
            }
            catch (Exception e)
            {
                // One bad entry (e.g. a character BepInEx refuses in the name) must not stop the fixes after it.
                Logger.LogWarning($"{name}: not applied - its config entry could not be made ({e.Message})");
                return;
            }
            if (!enabled.Value)
            {
                Logger.LogInfo($"{name}: off in config.");
                return;
            }
            if (targetGuid != null && !Chainloader.PluginInfos.ContainsKey(targetGuid))
            {
                Logger.LogInfo($"{name}: {targetGuid} not installed, nothing to do.");
                return;
            }
            try
            {
                string problem = apply();
                if (problem == null) Logger.LogInfo($"{name}: applied.");
                else if (problem.StartsWith(AlreadyDone)) Logger.LogInfo($"{name}: {problem}, nothing to do.");
                else if (problem.StartsWith(NotNeeded)) Logger.LogInfo($"{name}: {problem.Substring(NotNeeded.Length)}, nothing to do.");
                else Logger.LogWarning($"{name}: not applied - {problem}");
            }
            catch (Exception e)
            {
                Logger.LogWarning($"{name}: not applied - {e.GetType().Name}: {e.Message}");
            }
        }
    }
}
