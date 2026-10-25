using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ValPerfPatcher
{
    /// <summary>
    /// Longship Upgrades 1.0.24 caps a longship with its mast removed at slow speed from a Ship.UpdateControlls prefix
    /// (Ship_UpdateControlls_SpeedLimitWithoutMast). That runs on every ship, every physics step, and its first check,
    /// IsControlledComponent, reads the root object's name and strips "(Clone)" (two new strings per ship per step).
    /// This replaces that prefix with the same logic, but tells a longship by its ZDO prefab hash (the mod's own
    /// LongshipCustomizableParts.prefabInt, "VikingShip"). Stands down if the prefix no longer looks as analysed.
    /// </summary>
    internal static class LongshipUpgradesShipCheckFix
    {
        public const string TargetGuid = "shudnal.LongshipUpgrades";

        private static readonly AccessTools.FieldRef<Ship, ZNetView> ShipNview = AccessTools.FieldRefAccess<Ship, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<Ship, Ship.Speed> ShipSpeed = AccessTools.FieldRefAccess<Ship, Ship.Speed>("m_speed");
        private static int _prefab, _mastRemoved;

        public static string Apply(Harmony harmony)
        {
            var plugin = Plugin.ModType(TargetGuid, "LongshipUpgrades.LongshipUpgrades");
            var patch = Plugin.ModType(TargetGuid, "LongshipUpgrades.LongshipUpgrades+Ship_UpdateControlls_SpeedLimitWithoutMast");
            var parts = Plugin.ModType(TargetGuid, "LongshipUpgrades.LongshipCustomizableParts");
            var prefix = patch == null ? null : AccessTools.DeclaredMethod(patch, "Prefix", new[] { typeof(Ship) });
            var isControlled = plugin == null ? null : AccessTools.DeclaredMethod(plugin, "IsControlledComponent");
            var prefabField = Plugin.FieldOrNull(parts, "prefabInt");
            var mastField = Plugin.FieldOrNull(parts, "s_mastRemoved");
            if (prefix == null || !prefix.IsStatic || isControlled == null || prefabField == null || mastField == null ||
                !prefabField.IsStatic || !mastField.IsStatic || prefabField.FieldType != typeof(int) || mastField.FieldType != typeof(int))
                return "Longship Upgrades' mast speed limit has changed since this fix was written";

            List<CodeInstruction> code;
            try { code = PatchProcessor.GetOriginalInstructions(prefix); }
            catch (Exception e) { return "could not read Longship Upgrades' mast speed limit (" + e.Message + ")"; }
            var speed = AccessTools.Field(typeof(Ship), "m_speed");
            var isSailUp = AccessTools.Method(typeof(Ship), nameof(Ship.IsSailUp));
            bool callsName = false, readsMast = false, setsSpeed = false, sailUp = false;
            foreach (var ci in code)
            {
                if (ci.Calls(isControlled)) callsName = true;
                if (ci.LoadsField(mastField)) readsMast = true;
                if (ci.StoresField(speed)) setsSpeed = true;
                if (ci.Calls(isSailUp)) sailUp = true;
            }
            if (!callsName)
                return Plugin.AlreadyDone + "Longship Upgrades " + Plugin.VersionOf(TargetGuid) + " itself (it no longer checks the ship's name there)";
            if (!readsMast || !setsSpeed || !sailUp)
                return "Longship Upgrades' mast speed limit has changed since this fix was written";

            _prefab = (int)prefabField.GetValue(null);
            _mastRemoved = (int)mastField.GetValue(null);
            harmony.Patch(prefix, prefix: new HarmonyMethod(typeof(LongshipUpgradesShipCheckFix), nameof(Prefix)));
            return null;
        }

        // Same checks as the mod's prefix, with the prefab hash in place of the name. The mod's parameter is itself
        // named __instance, which Harmony would read as the (static) target's instance, so it is taken by position.
        private static bool Prefix(Ship __0)
        {
            if (__0 == null) return false;
            var nview = ShipNview(__0);
            if (nview == null || !nview.IsValid()) return false;
            var zdo = nview.GetZDO();
            if (zdo.GetPrefab() == _prefab && __0.IsSailUp() && zdo.GetBool(_mastRemoved))
                ShipSpeed(__0) = Ship.Speed.Slow;
            return false;
        }
    }
}
