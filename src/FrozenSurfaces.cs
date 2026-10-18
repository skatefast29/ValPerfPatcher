using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// The solid ice colliders Seasonality's FrozenZones adds to each loaded area's water surface (its
    /// m_surfaceCollider, a MeshCollider on "WaterSurface"), tracked by reference from FrozenZones.Awake to
    /// FrozenZones.OnDestroy. Shared by the Seasonality winter fixes so they can ask "is this collider the ice?"
    /// without reading object names (which makes a new string each time).
    /// </summary>
    internal static class FrozenSurfaces
    {
        private static readonly HashSet<Collider> Set = new HashSet<Collider>();
        private static readonly List<Collider> List = new List<Collider>();
        private static readonly Dictionary<WaterVolume, Collider> ByVolume = new Dictionary<WaterVolume, Collider>();
        private static AccessTools.FieldRef<object, MeshCollider> _surface;
        private static AccessTools.FieldRef<object, WaterVolume> _volume;
        private static string _problem;
        private static bool _patched;

        /// <summary>Raised when an area's ice collider is first seen.</summary>
        public static event Action<Collider> Added;

        /// <summary>The ice colliders of the loaded areas (on while frozen, off while thawed).</summary>
        public static List<Collider> All => List;

        public static bool Contains(Collider c) => !ReferenceEquals(c, null) && Set.Contains(c);

        /// <summary>True when this water area has Seasonality's ice and it is frozen now (its collider is on).</summary>
        public static bool IsFrozen(WaterVolume volume) =>
            !ReferenceEquals(volume, null) && ByVolume.TryGetValue(volume, out var c) && c != null && c.enabled;

        /// <summary>Hooks FrozenZones once; returns null, or why it can't (then the caller stands down).</summary>
        public static string Ensure(Harmony harmony)
        {
            if (_patched || _problem != null) return _problem;
            var zones = Plugin.ModType(SeasonalWaterCacheFix.SeasonalityGuid, "Seasonality.FrozenZones");
            var awake = zones == null ? null : AccessTools.DeclaredMethod(zones, "Awake", Type.EmptyTypes);
            var destroy = zones == null ? null : AccessTools.DeclaredMethod(zones, "OnDestroy", Type.EmptyTypes);
            var surface = Plugin.FieldOrNull(zones, "m_surfaceCollider");
            var volume = Plugin.FieldOrNull(zones, "m_waterVolume");
            if (awake == null || destroy == null || surface == null || surface.IsStatic || surface.FieldType != typeof(MeshCollider) ||
                volume == null || volume.IsStatic || volume.FieldType != typeof(WaterVolume))
                return _problem = "Seasonality's frozen water has changed since this fix was written";

            _surface = AccessTools.FieldRefAccess<object, MeshCollider>(surface);
            _volume = AccessTools.FieldRefAccess<object, WaterVolume>(volume);
            harmony.Patch(awake, postfix: new HarmonyMethod(typeof(FrozenSurfaces), nameof(AwakePostfix)));
            harmony.Patch(destroy, prefix: new HarmonyMethod(typeof(FrozenSurfaces), nameof(DestroyPrefix)));
            _patched = true;
            return null;
        }

        private static void AwakePostfix(object __instance)
        {
            var c = _surface(__instance);
            if (c == null || !Set.Add(c)) return;
            List.Add(c);
            var v = _volume(__instance);
            if (v != null) ByVolume[v] = c;
            Added?.Invoke(c);
        }

        private static void DestroyPrefix(object __instance)
        {
            var c = _surface(__instance);
            if (ReferenceEquals(c, null)) return;
            if (Set.Remove(c)) List.Remove(c);
            var v = _volume(__instance);
            if (!ReferenceEquals(v, null)) ByVolume.Remove(v);
        }
    }
}
