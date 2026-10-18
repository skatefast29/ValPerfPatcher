using HarmonyLib;

namespace ValPerfPatcher
{
    /// <summary>
    /// Seasonality transpiles Character.UpdateWalking so that, in winter with 'Water Freezes' on, any character whose
    /// ground is the frozen surface gets m_slipping = true (FrozenZones+Character_UpdateWalking_Patch.ForceSlipOnWater;
    /// the game's own rule never makes that surface slippery). In the game's slipping branch a moving character keeps
    /// accelerating up to m_slipperyMaxSpeed (14 m/s) with almost no control, the 20 m/s cap on push forces is lifted,
    /// and a character standing still is turned with Quaternion.LookRotation(zero), which logs "Look rotation viewing
    /// vector is zero" every physics step. For players that is the intended skating; creatures and enemies instead
    /// shoot across the ice and flood the log.
    /// This keeps Seasonality's slipping for players only: for every other character ForceSlipOnWater returns the
    /// game's own answer (its originalValue argument).
    /// </summary>
    internal static class SeasonalityIceGripFix
    {
        public static string Apply(Harmony harmony)
        {
            var patch = Plugin.ModType(SeasonalWaterCacheFix.SeasonalityGuid, "Seasonality.FrozenZones+Character_UpdateWalking_Patch");
            var force = patch == null ? null : AccessTools.DeclaredMethod(patch, "ForceSlipOnWater", new[] { typeof(Character), typeof(bool) });
            if (force == null || !force.IsStatic || force.ReturnType != typeof(bool))
                return "Seasonality's ice slipping has changed since this fix was written";
            harmony.Patch(force, postfix: new HarmonyMethod(typeof(SeasonalityIceGripFix), nameof(Postfix)));
            return null;
        }

        private static void Postfix(Character instance, bool originalValue, ref bool __result)
        {
            if (__result != originalValue && instance != null && !instance.IsPlayer())
                __result = originalValue;
        }
    }
}
