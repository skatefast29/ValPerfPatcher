using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// AzuAutoStore (favourite borders) and Recycle_N_Reclaim (trash borders) draw a border picture on every slot of
    /// your inventory and, every frame the inventory is open, look it up again with the game's Utils.FindChild. That
    /// search builds a new Stack and reads each child's name (a new string per child), and AzuAutoStore runs it twice
    /// per slot: about 35 KB and 13 KB of garbage per frame, the most any mod made in a live play session, and the
    /// collections it set off froze the game for about 200 ms each. This transpiler swaps those FindChild calls for a
    /// lookup that remembers which child was found under each slot and checks it is still there. When it is gone (slot
    /// rebuilt) or was never found, the game's own search runs as before. Same borders, same results.
    /// </summary>
    internal static class InventoryBorderFindFix
    {
        public const string AutoStoreGuid = "Azumatt.AzuAutoStore";
        public const string RecycleGuid = "Azumatt.Recycle_N_Reclaim";
        private const int MaxEntries = 1024;
        private static readonly Dictionary<(int, string), Transform> Found = new Dictionary<(int, string), Transform>();
        private static int _replaced;

        public static string ApplyAutoStore(Harmony harmony) =>
            Apply(harmony, AutoStoreGuid, "AzuAutoStore.Patches.Favoriting.BorderRenderer", "UpdateGui", 4, "AzuAutoStore");

        public static string ApplyRecycle(Harmony harmony) =>
            Apply(harmony, RecycleGuid, "Recycle_N_Reclaim.GamePatches.MarkAsTrash.InventoryGridUpdateGuiPatch", "Postfix", 1, "Recycle_N_Reclaim");

        // expected = how many FindChild calls the method had when this fix was written.
        private static string Apply(Harmony harmony, string guid, string typeName, string methodName, int expected, string modName)
        {
            var type = Plugin.ModType(guid, typeName);
            var method = type == null ? null : AccessTools.DeclaredMethod(type, methodName);
            var changed = modName + "'s inventory border code has changed since this fix was written";
            if (method == null || FindChild == null || Count(method) != expected)
                return changed;
            _replaced = 0;
            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(InventoryBorderFindFix), nameof(Transpiler)));
            return _replaced == expected ? null : changed;
        }

        private static readonly MethodInfo FindChild = AccessTools.Method(typeof(Utils), nameof(Utils.FindChild),
            new[] { typeof(Transform), typeof(string), typeof(Utils.IterativeSearchType) });

        private static int Count(MethodBase method)
        {
            int n = 0;
            foreach (var code in PatchProcessor.GetOriginalInstructions(method))
                if (code.opcode == OpCodes.Call && Equals(code.operand, FindChild)) n++;
            return n;
        }

        public static Transform FindChildCached(Transform parent, string name, Utils.IterativeSearchType searchType)
        {
            if (parent == null) return Utils.FindChild(parent, name, searchType);
            var key = (parent.GetInstanceID(), name);
            if (Found.TryGetValue(key, out var child) && child != null && child.parent == parent)
                return child;
            child = Utils.FindChild(parent, name, searchType);
            if (child != null)
            {
                // Slots destroyed when the inventory changes size leave dead entries; this is only a cache.
                if (Found.Count >= MaxEntries) Found.Clear();
                Found[key] = child;
            }
            return child;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var cached = AccessTools.Method(typeof(InventoryBorderFindFix), nameof(FindChildCached));
            foreach (var code in instructions)
            {
                if (code.opcode == OpCodes.Call && Equals(code.operand, FindChild))
                {
                    code.operand = cached;
                    _replaced++;
                }
                yield return code;
            }
        }
    }
}
