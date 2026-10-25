using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    internal enum SnowCapScope { Objects, ListedPieces, AllPieces }

    /// <summary>
    /// Base game snow on building pieces (WearNTear.m_snow meshes, grown by the "_SnowLevel" material value). The game
    /// only builds it up in the Deep North; Seasonality's 'Add Snow' pushes every unroofed piece in other biomes to full
    /// in winter, and on small objects (beehives, smelters, kilns) the snow mesh turns into a tall pillar at full.
    /// WearNTear.UpdateSnowVisual shows the mesh above m_snowBuildup 0.25 and sets the level from 0.25..1 -> 0..1. For
    /// the capped pieces (objects plus the listed pieces, only the listed ones, or every piece, by Scope) this lowers
    /// m_snowBuildup for that call only (restored right after), so the snow drawn on this game is at most CapPercent of
    /// full. The saved snow amount, snow walking and Seasonality's own buildup are untouched; only what this game draws
    /// changes. Deep North pieces are left alone unless IncludeDeepNorth.
    /// </summary>
    internal static class SnowCapFix
    {
        // 0..100: share of full snow height drawn. 0 hides the snow on capped pieces.
        internal static float CapPercent = 10f;
        internal static bool IncludeDeepNorth;

        private static SnowCapScope _scope = SnowCapScope.Objects;
        internal static SnowCapScope Scope
        {
            get => _scope;
            set { _scope = value; _listVersion++; }
        }

        private static HashSet<string> _pieces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Bumped when the list or the scope changes, so each piece works out again whether it is capped.
        private static int _listVersion = 1;

        private sealed class Info
        {
            public Heightmap.Biome Biome;
            public int ListVersion;
            public bool Listed;
        }
        private static readonly ConditionalWeakTable<WearNTear, Info> Infos = new ConditionalWeakTable<WearNTear, Info>();

        private static AccessTools.FieldRef<WearNTear, Heightmap.Biome> _biome;

        public static string Apply(Harmony harmony)
        {
            var visual = AccessTools.DeclaredMethod(typeof(WearNTear), "UpdateSnowVisual", Type.EmptyTypes);
            var buildup = AccessTools.DeclaredField(typeof(WearNTear), "m_snowBuildup");
            var biome = AccessTools.DeclaredField(typeof(WearNTear), "m_biome");
            if (visual == null || visual.IsStatic || buildup == null || buildup.FieldType != typeof(float) ||
                biome == null || biome.FieldType != typeof(Heightmap.Biome))
                return "the game's building snow code has changed since this fix was written";
            _biome = AccessTools.FieldRefAccess<WearNTear, Heightmap.Biome>(biome);
            harmony.Patch(visual,
                prefix: new HarmonyMethod(typeof(SnowCapFix), nameof(Prefix)),
                finalizer: new HarmonyMethod(typeof(SnowCapFix), nameof(Finalizer)));
            return null;
        }

        // Prefab names, comma separated.
        internal static void SetPieces(string list)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in (list ?? "").Split(','))
                if (name.Trim().Length > 0) set.Add(name.Trim());
            _pieces = set;
            _listVersion++;
        }

        // Redraw loaded pieces after a setting change (non-owners otherwise only redraw when the piece reloads).
        internal static void Refresh()
        {
            foreach (var wnt in WearNTear.GetAllInstances())
                if (wnt != null && wnt.m_snow != null) wnt.UpdateSnowVisual();
        }

        private static void Prefix(WearNTear __instance, out float __state)
        {
            __state = -1f;
            if (__instance.m_snow == null || CapPercent >= 100f) return;
            float max = 0.25f + 0.75f * Mathf.Clamp01(CapPercent / 100f);
            if (__instance.m_snowBuildup <= max) return;
            var info = Infos.GetOrCreateValue(__instance);
            if (!Listed(__instance, info)) return;
            if (!IncludeDeepNorth && BiomeOf(__instance, info) == Heightmap.Biome.DeepNorth) return;
            __state = __instance.m_snowBuildup;
            __instance.m_snowBuildup = max;
        }

        // A finalizer, so the real amount is restored even if the game's drawing throws.
        private static void Finalizer(WearNTear __instance, float __state)
        {
            if (__state >= 0f) __instance.m_snowBuildup = __state;
        }

        private static bool Listed(WearNTear wnt, Info info)
        {
            if (_scope == SnowCapScope.AllPieces) return true;
            if (info.ListVersion != _listVersion)
            {
                info.Listed = _pieces.Contains(Utils.GetPrefabName(wnt.gameObject)) ||
                              _scope == SnowCapScope.Objects && !IsBuildingPart(wnt);
                info.ListVersion = _listVersion;
            }
            return info.Listed;
        }

        // Only the game's own pieces have snow meshes (Valheim 1.0.17). Objects: the Crafting and Furniture tabs, anything
        // with a comfort group (beds, chairs, tables), containers, the map table, and resource stacks and piles. Everything
        // else is a building part: the building tabs, and the walls, roofs, beams, stakes, fences and adornments that the
        // Deep North and Misc tabs also hold.
        private static bool IsBuildingPart(WearNTear wnt)
        {
            var piece = wnt.GetComponent<Piece>();
            if (piece == null) return true;
            if (piece.m_category == Piece.PieceCategory.Crafting || piece.m_category == Piece.PieceCategory.Furniture ||
                piece.m_comfortGroup != Piece.ComfortGroup.None)
                return false;
            if (wnt.GetComponent<Container>() != null || wnt.GetComponent<MapTable>() != null) return false;
            var name = Utils.GetPrefabName(wnt.gameObject);
            return name.IndexOf("_stack", StringComparison.OrdinalIgnoreCase) < 0 &&
                   name.IndexOf("_pile", StringComparison.OrdinalIgnoreCase) < 0;
        }

        // The piece's own biome once the game has looked it up (owners only); else found once and remembered.
        private static Heightmap.Biome BiomeOf(WearNTear wnt, Info info)
        {
            var own = _biome(wnt);
            if (own != Heightmap.Biome.None) return own;
            if (info.Biome == Heightmap.Biome.None) info.Biome = Heightmap.FindBiome(wnt.transform.position);
            return info.Biome;
        }
    }
}
