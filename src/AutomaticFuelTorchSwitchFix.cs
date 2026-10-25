using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ValPerfPatcher
{
    /// <summary>
    /// AutomaticFuel 1.5.8 added a key (StationToggleKey, B) that cycles the hovered torch or fire between ON, OFF and
    /// TIMER. Its torch path (TastyUtils.ToggleFireplaceMode) refuses unless this game already owns the fire's ZDO and
    /// shows "Cannot toggle (not owner)", so on a server only whoever happens to own that fire can switch it. Its smelter
    /// path (ToggleStationOnOff) claims ownership first. This does the same for fires: claim ownership just before the
    /// owner check, then the mod switches the fire as usual. Stands down if the mod no longer checks ownership there or
    /// already claims it.
    /// </summary>
    internal static class AutomaticFuelTorchSwitchFix
    {
        public static string Apply(Harmony harmony)
        {
            var type = Plugin.ModType(AutomaticFuelSmelterScanFix.TargetGuid, "AutomaticFuel.TastyUtils");
            var toggle = type == null ? null : AccessTools.DeclaredMethod(type, "ToggleFireplaceMode", new[] { typeof(Fireplace) });
            if (toggle == null || !toggle.IsStatic)
                return "AutomaticFuel's torch switch has changed since this fix was written";

            List<CodeInstruction> code;
            try { code = PatchProcessor.GetOriginalInstructions(toggle); }
            catch (Exception e) { return "could not read AutomaticFuel's torch switch (" + e.Message + ")"; }
            var isOwner = AccessTools.Method(typeof(ZNetView), nameof(ZNetView.IsOwner));
            var claim = AccessTools.Method(typeof(ZNetView), nameof(ZNetView.ClaimOwnership));
            bool checksOwner = false;
            foreach (var ci in code)
            {
                if (ci.Calls(claim))
                    return Plugin.AlreadyDone + "AutomaticFuel " + Plugin.VersionOf(AutomaticFuelSmelterScanFix.TargetGuid) + " itself (it takes ownership of the fire)";
                if (ci.Calls(isOwner)) checksOwner = true;
            }
            if (!checksOwner)
                return Plugin.AlreadyDone + "AutomaticFuel " + Plugin.VersionOf(AutomaticFuelSmelterScanFix.TargetGuid) + " itself (it no longer needs ownership to switch a fire)";

            harmony.Patch(toggle, prefix: new HarmonyMethod(typeof(AutomaticFuelTorchSwitchFix), nameof(Prefix)));
            return null;
        }

        private static void Prefix(Fireplace fireplace)
        {
            if (fireplace == null) return;
            var nview = fireplace.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid() && !nview.IsOwner())
                nview.ClaimOwnership();
        }
    }
}
