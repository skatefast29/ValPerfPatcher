using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// BetterMap names the trader pins on the map (Haldor, Hildir, the Bog Witch) with a postfix on
    /// Minimap.UpdateLocationPins. The game runs that method every frame but only does its work every 5 seconds
    /// (it counts m_updateLocationsTimer down and resets it to 5 when it adds or removes location pins). BetterMap's
    /// postfix runs every frame regardless and asks ZoneSystem for every location icon each time (about 0.11 ms and
    /// 0.8 KB of garbage per frame). Location pins only change inside that 5-second update, so this lets the postfix
    /// run only on the frames where the update ran (or when the timer can't be read). Same pins, same names.
    /// </summary>
    internal static class BetterMapLocationPinsFix
    {
        public const string TargetGuid = "xtav1m.BetterMap";
        private const float GameInterval = 5f; // Minimap.UpdateLocationPins: m_updateLocationsTimer = 5f

        private static AccessTools.FieldRef<Minimap, float> _timer;

        public static string Apply(Harmony harmony)
        {
            var patches = Plugin.ModType(TargetGuid, "BetterMap.Scripts.Pins.NamedPinPatches");
            var postfix = patches == null ? null : AccessTools.DeclaredMethod(patches, "Minimap_UpdateLocationPins_Postfix", System.Type.EmptyTypes);
            var named = Plugin.ModType(TargetGuid, "BetterMap.Scripts.Pins.NamedPins");
            var timer = AccessTools.DeclaredField(typeof(Minimap), "m_updateLocationsTimer");
            var update = AccessTools.DeclaredMethod(typeof(Minimap), "UpdateLocationPins", new[] { typeof(float) });
            if (postfix == null || !postfix.IsStatic || postfix.ReturnType != typeof(void) ||
                named == null || AccessTools.DeclaredMethod(named, "NameLocationPins", System.Type.EmptyTypes) == null)
                return "BetterMap's trader pin naming has changed since this fix was written";
            if (timer == null || timer.FieldType != typeof(float) || update == null)
                return "the game's location pin update has changed since this fix was written";
            if (!HasTimerReset(update))
                return "the game's location pin update no longer resets its timer to 5 seconds";
            _timer = AccessTools.FieldRefAccess<Minimap, float>(timer);
            harmony.Patch(postfix, prefix: new HarmonyMethod(typeof(BetterMapLocationPinsFix), nameof(Prefix)));
            return null;
        }

        // The game's method must still contain "m_updateLocationsTimer = 5f" for the frame test below to be right.
        private static bool HasTimerReset(System.Reflection.MethodInfo update)
        {
            List<CodeInstruction> code;
            try { code = PatchProcessor.GetOriginalInstructions(update); }
            catch { return false; }
            for (int i = 0; i + 1 < code.Count; i++)
                if (code[i].opcode == System.Reflection.Emit.OpCodes.Ldc_R4 && code[i].operand is float f && f == GameInterval &&
                    code[i + 1].opcode == System.Reflection.Emit.OpCodes.Stfld &&
                    code[i + 1].operand is System.Reflection.FieldInfo fi && fi.Name == "m_updateLocationsTimer")
                    return true;
            return false;
        }

        // Run BetterMap's postfix only on frames where the game just did its location update (timer was reset to 5).
        private static bool Prefix()
        {
            var map = Minimap.instance;
            if (map == null) return true;
            return _timer(map) >= GameInterval;
        }
    }
}
