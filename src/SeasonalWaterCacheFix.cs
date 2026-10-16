using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Seasonality with ValheimCommunityPatch. VCP's WaterVolumeMaterialCachePatch remembers each water area's surface
    /// material (WaterVolume.m_waterSurface.material) the first time WaterVolume.UpdateMaterials runs and from then on
    /// only sets the wave time on that remembered material. Seasonality's FrozenZones swaps the surface material when
    /// water freezes or thaws (FreezeWater / ThawWater set m_surfaceRenderer.material). After a swap VCP keeps animating
    /// the old material, so the water you see stops moving while the game's wave physics go on: boats and fish look as
    /// if they float above or sink into the water, with steps between water areas, until those areas reload.
    /// This drops that water area from VCP's cache right after Seasonality swaps its material, so VCP picks up the
    /// material now on the surface at the next update, as the game itself would (it reads .material every frame).
    /// </summary>
    internal static class SeasonalWaterCacheFix
    {
        public const string SeasonalityGuid = "RustyMods.Seasonality";

        private static IDictionary _cache;
        private static AccessTools.FieldRef<object, WaterVolume> _volume;

        public static string Apply(Harmony harmony)
        {
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(SeasonalityGuid))
                return Plugin.NotNeeded + "Seasonality is not installed";
            var zones = Plugin.ModType(SeasonalityGuid, "Seasonality.FrozenZones");
            var thaw = zones == null ? null : AccessTools.DeclaredMethod(zones, "ThawWater", Type.EmptyTypes);
            var freeze = zones == null ? null : AccessTools.DeclaredMethod(zones, "FreezeWater", Type.EmptyTypes);
            var volume = zones == null ? null : AccessTools.DeclaredField(zones, "m_waterVolume");
            if (thaw == null || freeze == null || volume == null || volume.IsStatic || volume.FieldType != typeof(WaterVolume))
                return "Seasonality's frozen water has changed since this fix was written";

            var patch = Plugin.ModType(ChebyshevDistanceFix.VcpGuid, "ValheimCommunityPatch.Patches.Performance.WaterVolumeMaterialCachePatch");
            var cache = patch == null ? null : AccessTools.DeclaredField(patch, "Cache");
            if (cache == null || !cache.IsStatic || cache.FieldType != typeof(Dictionary<WaterVolume, Material>) ||
                !(cache.GetValue(null) is IDictionary dict))
                return "ValheimCommunityPatch's water material cache has changed since this fix was written";

            _cache = dict;
            _volume = AccessTools.FieldRefAccess<object, WaterVolume>(volume);
            var postfix = new HarmonyMethod(typeof(SeasonalWaterCacheFix), nameof(Postfix));
            harmony.Patch(thaw, postfix: postfix);
            harmony.Patch(freeze, postfix: postfix);
            return null;
        }

        private static void Postfix(object __instance)
        {
            var volume = _volume(__instance);
            if (volume != null) _cache.Remove(volume);
        }
    }
}
