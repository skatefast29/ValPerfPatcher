using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// In winter Seasonality places copies of the game's ice ("SeasonalIce" from ice1, "SeasonalIceShore_1" from
    /// IceShore_1, and its broken pieces "Seasonal_IceShore_frac") as vegetation snapped to the water line
    /// (ZoneVegetation.m_snapToWater). With 'Water Freezes' on, that water line is also FrozenZones' solid surface
    /// collider (a MeshCollider on each area's WaterSurface), so a piece with physics starts inside a solid floor and
    /// the physics engine's push out of it flings the piece around as it spawns.
    /// This keeps the pieces' own physics (they still float, drift and can be rammed) and only stops that clash:
    ///  - each piece with a moving Rigidbody ignores collisions with every frozen surface collider
    ///    (Physics.IgnoreCollision, both ways: new pieces against loaded surfaces, new or re-frozen surfaces against
    ///    loaded pieces; the pair is re-applied after FreezeWater because re-enabling a collider can reset it);
    ///  - its Rigidbody.maxDepenetrationVelocity is lowered, so any other overlap it spawns in (another piece, the
    ///    shore) is resolved by a gentle push instead of a launch.
    /// The pieces are found by Seasonality's own SeasonalIce / SeasonalIceMineRock component (added only to them);
    /// the surfaces come from FrozenSurfaces. The pieces only exist in winter (Seasonality removes them in other
    /// seasons), so nothing else changes.
    /// </summary>
    internal static class SeasonalityIceStillFix
    {
        // Fastest speed (m/s) the physics engine may use to push a piece out of something it overlaps.
        private const float GentlePush = 1f;

        private static readonly List<Collider> IcePieces = new List<Collider>();
        private static AccessTools.FieldRef<object, MeshCollider> _surfaceCollider;

        public static string Apply(Harmony harmony)
        {
            var ice = Plugin.ModType(SeasonalWaterCacheFix.SeasonalityGuid, "Seasonality.SeasonalIce");
            var rock = Plugin.ModType(SeasonalWaterCacheFix.SeasonalityGuid, "Seasonality.SeasonalIceMineRock");
            var zones = Plugin.ModType(SeasonalWaterCacheFix.SeasonalityGuid, "Seasonality.FrozenZones");
            var iceAwake = ice == null ? null : AccessTools.DeclaredMethod(ice, "Awake", Type.EmptyTypes);
            var rockAwake = rock == null ? null : AccessTools.DeclaredMethod(rock, "Awake", Type.EmptyTypes);
            var freeze = zones == null ? null : AccessTools.DeclaredMethod(zones, "FreezeWater", Type.EmptyTypes);
            var surface = Plugin.FieldOrNull(zones, "m_surfaceCollider");
            if (iceAwake == null || rockAwake == null || freeze == null ||
                surface == null || surface.IsStatic || surface.FieldType != typeof(MeshCollider))
                return "Seasonality's ice has changed since this fix was written";
            var problem = FrozenSurfaces.Ensure(harmony);
            if (problem != null) return problem;

            _surfaceCollider = AccessTools.FieldRefAccess<object, MeshCollider>(surface);
            var piece = new HarmonyMethod(typeof(SeasonalityIceStillFix), nameof(PieceAwake));
            harmony.Patch(iceAwake, postfix: piece);
            harmony.Patch(rockAwake, postfix: piece);
            harmony.Patch(freeze, postfix: new HarmonyMethod(typeof(SeasonalityIceStillFix), nameof(Refrozen)));
            FrozenSurfaces.Added += SurfaceReady;
            return null;
        }

        private static void PieceAwake(Component __instance)
        {
            // Only pieces placed in the world; a prefab (no ZDO yet) never moves.
            var nview = __instance.GetComponent<ZNetView>();
            if (nview == null || nview.GetZDO() == null) return;

            bool moves = false;
            foreach (var body in __instance.GetComponentsInChildren<Rigidbody>())
            {
                if (body.isKinematic) continue;
                body.maxDepenetrationVelocity = GentlePush;
                moves = true;
            }
            if (!moves) return; // a piece without physics is never pushed

            IcePieces.RemoveAll(c => c == null);
            foreach (var col in __instance.GetComponentsInChildren<Collider>())
            {
                if (col.isTrigger) continue;
                IcePieces.Add(col);
                foreach (var s in FrozenSurfaces.All)
                    Ignore(col, s);
            }
        }

        private static void Refrozen(object __instance) => SurfaceReady(_surfaceCollider(__instance));

        private static void SurfaceReady(Collider s)
        {
            if (s == null) return;
            IcePieces.RemoveAll(c => c == null);
            foreach (var col in IcePieces)
                Ignore(col, s);
        }

        // Unity only accepts the pair while both colliders are switched on (a thawed surface's collider is off).
        private static void Ignore(Collider a, Collider b)
        {
            if (a != null && b != null && a.enabled && b.enabled && a.gameObject.activeInHierarchy && b.gameObject.activeInHierarchy)
                Physics.IgnoreCollision(a, b, true);
        }
    }
}
