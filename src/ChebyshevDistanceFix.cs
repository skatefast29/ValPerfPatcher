using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Base game. Utils.ChebyshevDistance(Vector3, Vector3) takes the largest of three numbers with
    /// Mathf.Max(a, b, c), which is Unity's "params float[]" overload: every call creates a new 3-number
    /// array (about 50 bytes of garbage). The "is this point in the active area?" check uses it, and that
    /// check runs for every nearby object in the ownership pass every 2 seconds (~600 KB each time with
    /// ~12,000 objects), and for building pieces, rocks and stumps, spawn areas and object loading.
    /// This returns the same value without the array.
    /// </summary>
    internal static class ChebyshevDistanceFix
    {
        public static string Apply(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(Utils), nameof(Utils.ChebyshevDistance), new[] { typeof(Vector3), typeof(Vector3) });
            if (target == null || target.ReturnType != typeof(float))
                return "Utils.ChebyshevDistance has changed since this fix was written";
            // Only worth patching while the game still builds an array for Mathf.Max.
            if (!PatchProcessor.GetOriginalInstructions(target).Any(i => i.opcode == OpCodes.Newarr))
                return "Utils.ChebyshevDistance no longer allocates; nothing to fix";
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(ChebyshevDistanceFix), nameof(Prefix)) { priority = Priority.First });
            return null;
        }

        private static bool Prefix(Vector3 a, Vector3 b, ref float __result)
        {
            // In the order Mathf.Max(params float[]) compares them, so even a NaN gives the same answer.
            float max = Mathf.Abs(a.x - b.x), y = Mathf.Abs(a.y - b.y), z = Mathf.Abs(a.z - b.z);
            if (y > max) max = y;
            if (z > max) max = z;
            __result = max;
            return false;
        }
    }
}
