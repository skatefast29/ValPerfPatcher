using System;
using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Base game. Pathfinding keeps a navigation tile per area and creature size, and joins neighbouring tiles with
    /// NavMeshLinks along their edges (about 90 per tile). A tile goes after nobody has asked for a path through it for
    /// m_tileTimeout (60 s in the game's Pathfinding object; the code default is 30). Moving fast through areas full of creatures (sailing past coasts, portals, many sea creatures
    /// needing their own tile sizes) can keep 700 or more tiles loaded, and Unity allows only 65,535 links: past that,
    /// every new link fails with "Failed to allocate NavMeshLink" (logged each time, thousands per minute) and creatures
    /// can't path between tiles.
    /// This looks at the number of links about once a second, from the game's own tile clean-up (TimeoutTiles). Only while
    /// it goes above 45,000 (and until it is back under 40,000) does the clean-up drop tiles unused for 10 s instead of the game's 60 s; otherwise the game's own value is
    /// put back. Tiles still in use are never dropped (any path request through a tile keeps it), so normal play is
    /// unchanged.
    /// </summary>
    internal static class PathfindingLinkLimitFix
    {
        // Not const so a test can lower it through the bridge (debug/set_value) to watch the switch happen.
        internal static int HighLinks = 45000;
        private const int Gap = 5000; // back to normal only below HighLinks - Gap, so it doesn't flip every second at the line
        private const float EarlyTimeout = 10f;

        private static ManualLogSource _log;
        private static FieldInfo _tilesField;
        private static Func<object, int> _tileLinks;
        private static Pathfinding _instance;
        private static IDictionary _tiles;
        private static float _normalTimeout, _nextCheck;
        private static bool _early, _told;

        public static string Apply(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            var timeout = AccessTools.DeclaredMethod(typeof(Pathfinding), "TimeoutTiles", Type.EmptyTypes);
            _tilesField = AccessTools.DeclaredField(typeof(Pathfinding), "m_tiles");
            var tileType = AccessTools.Inner(typeof(Pathfinding), "NavMeshTile");
            var links1 = tileType == null ? null : AccessTools.DeclaredField(tileType, "m_links1");
            var links2 = tileType == null ? null : AccessTools.DeclaredField(tileType, "m_links2");
            if (timeout == null || _tilesField == null || !typeof(IDictionary).IsAssignableFrom(_tilesField.FieldType) ||
                links1 == null || links2 == null || !typeof(ICollection).IsAssignableFrom(links1.FieldType) ||
                links2.FieldType != links1.FieldType ||
                AccessTools.DeclaredField(typeof(Pathfinding), nameof(Pathfinding.m_tileTimeout))?.FieldType != typeof(float))
                return "the game's path-finding tiles have changed since this fix was written";

            // (object tile) => tile.m_links1.Count + tile.m_links2.Count, for the game's private tile class.
            var count = AccessTools.PropertyGetter(links1.FieldType, "Count");
            var dm = new DynamicMethod("ValPerfPatcher_TileLinks", typeof(int), new[] { typeof(object) }, typeof(PathfindingLinkLimitFix), true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, tileType); il.Emit(OpCodes.Ldfld, links1); il.Emit(OpCodes.Callvirt, count);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, tileType); il.Emit(OpCodes.Ldfld, links2); il.Emit(OpCodes.Callvirt, count);
            il.Emit(OpCodes.Add); il.Emit(OpCodes.Ret);
            _tileLinks = (Func<object, int>)dm.CreateDelegate(typeof(Func<object, int>));

            harmony.Patch(timeout, prefix: new HarmonyMethod(typeof(PathfindingLinkLimitFix), nameof(Prefix)));
            return null;
        }

        private static void Prefix(Pathfinding __instance)
        {
            if (!ReferenceEquals(__instance, _instance))
            {
                _instance = __instance;
                _tiles = _tilesField.GetValue(__instance) as IDictionary;
                _normalTimeout = __instance.m_tileTimeout;
                _early = false;
                _nextCheck = 0f;
            }
            if (_tiles == null || Time.time < _nextCheck) return;
            _nextCheck = Time.time + 1f;

            int links = 0;
            foreach (var tile in _tiles.Values)
                links += _tileLinks(tile);

            bool early = _early ? links >= HighLinks - Gap : links >= HighLinks;
            if (early == _early) return;
            _early = early;
            __instance.m_tileTimeout = early ? Mathf.Min(_normalTimeout, EarlyTimeout) : _normalTimeout;
            if (early && !_told)
            {
                _told = true;
                _log.LogInfo($"Path-finding: {links} links in {_tiles.Count} tiles, near Unity's limit of 65,535; dropping tiles unused for {EarlyTimeout:0} s until it is back under {HighLinks - Gap}. (Shown once.)");
            }
        }
    }
}
