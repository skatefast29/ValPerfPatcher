using HarmonyLib;

namespace ValPerfPatcher
{
    /// <summary>
    /// Smoothbrain Farming's Plant.Awake postfix (Farming.Farming+SaveSkillLevel.Postfix) stores the planter's skill on
    /// the new plant's ZDO while Player.PlacePiece runs (its PlayerIsPlantingPlants flag). Planting uses up a seed, the
    /// inventory change makes the game rebuild the placement ghost (Player.OnInventoryChanged ->
    /// UpdateAvailablePiecesList -> SetupPlacementGhost), and that ghost's Plant wakes while the flag is still set. The
    /// ghost has no ZDO, so __instance.m_nview.GetZDO().Set(...) throws a NullReferenceException: once per seed planted
    /// (473 times in a 2.5 h session), each one a thrown exception plus a logged stack trace, during planting.
    /// Its other branch only reads the ZDO too, so for an object without a ZDO the postfix does nothing useful. This
    /// skips it for such objects (placement ghosts); real plants, which always have a ZDO, are unchanged.
    /// </summary>
    internal static class FarmingGhostPlantFix
    {
        public const string TargetGuid = "org.bepinex.plugins.farming";

        private static AccessTools.FieldRef<Plant, ZNetView> _nview;

        public static string Apply(Harmony harmony)
        {
            var type = Plugin.ModType(TargetGuid, "Farming.Farming+SaveSkillLevel");
            var postfix = type == null ? null : AccessTools.DeclaredMethod(type, "Postfix", new[] { typeof(Plant) });
            if (postfix == null || !postfix.IsStatic)
                return "Farming's plant skill hook has changed since this fix was written";
            if (AccessTools.DeclaredField(typeof(Plant), "m_nview")?.FieldType != typeof(ZNetView))
                return "the game's plant code has changed since this fix was written";
            _nview = AccessTools.FieldRefAccess<Plant, ZNetView>("m_nview");
            harmony.Patch(postfix, prefix: new HarmonyMethod(typeof(FarmingGhostPlantFix), nameof(Prefix)));
            return null;
        }

        private static bool Prefix(Plant __instance)
        {
            var nview = __instance == null ? null : _nview(__instance);
            return nview != null && nview.GetZDO() != null;
        }
    }
}
