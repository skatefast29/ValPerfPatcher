using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace ValPerfPatcher
{
    /// <summary>
    /// Seasonality's SeasonalIceMineRock (on the broken pieces of its winter shore ice) adds itself to the static
    /// m_instances list in Awake but has no OnDestroy, so a piece that is mined away or unloaded with its area stays
    /// in the list. On the next season change SeasonalityPlugin.OnSeasonChange calls SeasonalIceMineRock.UpdateAll,
    /// which calls CheckSeason on every entry; for a gone piece m_nview.Destroy() throws a NullReferenceException
    /// (seen in a live log). That aborts OnSeasonChange halfway: the textures, terrain and moss have already changed,
    /// but SeasonalIce.UpdateZoneVeg (the winter ice stays in the list of things new areas grow) and
    /// SeasonSE.UpdateStatus (the season name on the HUD) never run.
    /// This drops the gone pieces from the list just before UpdateAll walks it.
    /// </summary>
    internal static class SeasonalitySeasonChangeFix
    {
        private static IList _instances;
        private static AccessTools.FieldRef<object, ZNetView> _nview;

        public static string Apply(Harmony harmony)
        {
            var rock = Plugin.ModType(SeasonalWaterCacheFix.SeasonalityGuid, "Seasonality.SeasonalIceMineRock");
            var updateAll = rock == null ? null : AccessTools.DeclaredMethod(rock, "UpdateAll", Type.EmptyTypes);
            var instances = Plugin.FieldOrNull(rock, "m_instances");
            var nview = Plugin.FieldOrNull(rock, "m_nview");
            if (updateAll == null || instances == null || !instances.IsStatic || !(instances.GetValue(null) is IList list) ||
                nview == null || nview.IsStatic || nview.FieldType != typeof(ZNetView))
                return "Seasonality's shore ice pieces have changed since this fix was written";
            // A later Seasonality that removes pieces itself (an OnDestroy) needs none of this.
            if (rock.GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) != null)
                return Plugin.AlreadyDone + "Seasonality " + Plugin.VersionOf(SeasonalWaterCacheFix.SeasonalityGuid) +
                       " (its shore ice pieces now leave the list themselves)";

            _instances = list;
            _nview = AccessTools.FieldRefAccess<object, ZNetView>(nview);
            harmony.Patch(updateAll, prefix: new HarmonyMethod(typeof(SeasonalitySeasonChangeFix), nameof(Prefix)));
            return null;
        }

        private static void Prefix()
        {
            for (int i = _instances.Count - 1; i >= 0; i--)
            {
                var piece = _instances[i] as UnityEngine.Object;
                if (piece == null || _nview(piece) == null)
                    _instances.RemoveAt(i);
            }
        }
    }
}
