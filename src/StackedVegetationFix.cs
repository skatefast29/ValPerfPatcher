using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;

namespace ValPerfPatcher
{
    /// <summary>
    /// Base game, with mod vegetation. When a new area is generated, ZoneSystem.PlaceVegetation places each
    /// vegetation entry in groups of m_groupSizeMin..m_groupSizeMax, the extra members at a random point within
    /// m_groupRadius of the first. An entry that asks for groups but leaves the radius at 0 (Jotunn's default)
    /// puts every extra member on exactly the same spot: an invisible copy that costs as much as a real one
    /// (networked object, collider, ground checks), saved in the world for good. RtD Ocean's seaweed, ocean bushes,
    /// ocean rocks and land plants all do this (groups of 1-2, radius 0).
    /// The game's own entries (from its location lists and alt biomes) are left alone: a few of them do this,
    /// probably on purpose (two turned copies make a fuller bush or cliff).
    /// This sets such mod entries to one per group before areas are generated, so new areas get as many plant spots
    /// as before without the stacked copies (the random sequence shifts, so the exact spots differ from what an
    /// unpatched game would pick). Areas generated earlier keep theirs.
    /// </summary>
    internal static class StackedVegetationFix
    {
        private static ManualLogSource _log;
        /// <summary>Entries this fix changed to one per group (their original settings stacked copies).</summary>
        internal static readonly HashSet<ZoneSystem.ZoneVegetation> Changed = new HashSet<ZoneSystem.ZoneVegetation>();
        private static ZoneSystem _checked;
        private static int _checkedCount = -1;

        public static string Apply(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            var place = AccessTools.Method(typeof(ZoneSystem), "PlaceVegetation");
            if (place == null || AccessTools.Field(typeof(ZoneSystem), nameof(ZoneSystem.m_vegetation)) == null ||
                AccessTools.Field(typeof(ZoneSystem.ZoneVegetation), nameof(ZoneSystem.ZoneVegetation.m_groupRadius)) == null)
                return "ZoneSystem's vegetation code has changed since this fix was written";
            harmony.Patch(place, prefix: new HarmonyMethod(typeof(StackedVegetationFix), nameof(Prefix)) { priority = Priority.First });
            return null;
        }

        // Runs before every area's vegetation is placed; does the check once per vegetation list.
        private static void Prefix(ZoneSystem __instance)
        {
            var list = __instance.m_vegetation;
            if (list == null || (__instance == _checked && list.Count == _checkedCount)) return;
            _checked = __instance;
            _checkedCount = list.Count;

            var game = GameVegetation();
            var names = new List<string>();
            foreach (var veg in list)
            {
                if (veg == null || veg.m_groupSizeMax <= 1 || veg.m_groupRadius > 0f || game.Contains(veg)) continue;
                Changed.Add(veg);
                veg.m_groupSizeMax = 1;
                if (veg.m_groupSizeMin > 1) veg.m_groupSizeMin = 1;
                names.Add(veg.m_prefab != null ? veg.m_prefab.name : veg.m_name);
            }
            if (names.Count > 0)
                _log.LogInfo($"Vegetation: {names.Count} mod entries placed groups on one spot; now one per group: {string.Join(", ", names)}");
        }

        /// <summary>The game's own vegetation entries, as ZoneSystem.SetupLocations collects them.</summary>
        internal static HashSet<ZoneSystem.ZoneVegetation> GameVegetation()
        {
            var set = new HashSet<ZoneSystem.ZoneVegetation>();
            foreach (var locations in LocationList.GetAllLocationLists())
                if (locations != null && locations.m_vegetation != null)
                    set.UnionWith(locations.m_vegetation);
            foreach (var alt in AltBiomeList.m_altBiomes)
                if (alt != null && alt.m_addVegetation != null)
                    set.UnionWith(alt.m_addVegetation);
            return set;
        }
    }
}
