using System;
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
    ///  - keeps the mod's behaviour identical, it only removes wasted work or garbage;
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
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "valheimmods.ValPerfPatcher";
        public const string Name = "ValPerfPatcher";
        public const string Version = "1.0.3";

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

            // Applied by EarlyPlugin before other mods start; bound here so the switches are in this config file.
            Report(EarlyPlugin.VanillaFix,
                "Startup creates many English translation objects, and each one re-reads all 13 of the game's translation files (every language). Keep the words each file gives per language after the first read and add those instead. Mods' own translation hooks still run.",
                EarlyPlugin.VanillaResult);

            Report(EarlyPlugin.LocalizerFix,
                "Mods that bundle LocalizationManager (Warfare, Monstrum, Wizardry, HearthBelow, AzuEPI, Comfort Tweaks and others) search the whole BepInEx folder and re-read their translation files every time any translation object is set up (some once per game translation file). Reuse each mod's already loaded text after the first time.",
                EarlyPlugin.LocalizerResult);

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
            Apply("Base game: throttle static object ground checks", null,
                "Fewer ground checks for objects that are already resting on the ground; see 'Static object ground check interval' under Base game.",
                () => StaticPhysicsThrottle.Apply(harmony));
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
                "Riverheim builds its whole world map (about 7 s) only after the game has paused and loaded the scene. Start the same build in the background as soon as a world is started from the menu, so it runs during that wait. Same world; joining someone else's server is unchanged.",
                () => RiverheimLoadFix.ApplyHeadStart(_harmony, Logger));

            Apply("Riverheim: biome map on all CPU cores", RiverheimLoadFix.TargetGuid,
                "Every world load the game looks up the biome and height of 4 million map points one by one (about 5 s with Riverheim). Riverheim's lookups are safe to run side by side, so split them across CPU cores. Same map. Only for Riverheim worlds, and only while no other mod hooks those lookups.",
                () => RiverheimLoadFix.ApplyParallelBiomes(_harmony, Logger));

            Apply("Comfort Tweaks: save its settings once", ComfortTweaksConfigFix.TargetGuid,
                "On the first world load Comfort Tweaks adds a setting for every comfort piece and fire (about 260), and BepInEx rewrites its whole config file after each one (about 1.5 s). Hold those saves and write the file once at the end. Same file.",
                () => ComfortTweaksConfigFix.Apply(_harmony));
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
                else Logger.LogInfo($"{name}: applied.");
            }
            else if (result == "off in config") Logger.LogInfo($"{name}: off in config.");
            else Logger.LogWarning($"{name}: not applied - {result}");
        }

        // A fix returning this (plus the other mod's name) stands down quietly: that mod already does the same job.
        internal const string AlreadyDone = "already done by ";

        private void Apply(string name, string targetGuid, string description, Func<string> apply)
        {
            var enabled = Config.Bind("Fixes", name, true, description);
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
                else Logger.LogWarning($"{name}: not applied - {problem}");
            }
            catch (Exception e)
            {
                Logger.LogWarning($"{name}: not applied - {e.GetType().Name}: {e.Message}");
            }
        }
    }
}
