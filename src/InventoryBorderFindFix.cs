using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Mods that draw something on inventory slots (AzuAutoStore 3.1.7's favourite borders, Recycle_N_Reclaim 1.4.5's
    /// trash borders) hook InventoryGrid.UpdateGui and, every frame the inventory is open, look their picture up again
    /// on every slot with the game's Utils.FindChild. That search builds a new Stack and reads each child's name (a new
    /// string per child): about 35 KB and 13 KB of garbage per frame for those two, the most any mod made in a live
    /// play session, and the collections it set off froze the game for about 200 ms each.
    /// Once every mod has started, this goes through every mod's patch on InventoryGrid.UpdateGui and UpdateInventory
    /// (and the methods of the patch's own class it calls) and swaps their FindChild calls for a lookup that remembers
    /// which child was found under each slot and checks it is still under it. When it is gone (slot rebuilt) or was
    /// never found, the game's own search runs as before. Same results.
    /// </summary>
    internal static class InventoryBorderFindFix
    {
        private const int MaxEntries = 1024;
        private static readonly Dictionary<(int, string), Transform> Found = new Dictionary<(int, string), Transform>();

        private static readonly MethodInfo FindChild = AccessTools.Method(typeof(Utils), nameof(Utils.FindChild),
            new[] { typeof(Transform), typeof(string), typeof(Utils.IterativeSearchType) });

        public static string Apply(Harmony harmony, ManualLogSource log)
        {
            if (FindChild == null) return "the game's child search has changed since this fix was written";
            var hooked = new[] { "UpdateGui", "UpdateInventory" }
                .Select(n => AccessTools.DeclaredMethod(typeof(InventoryGrid), n)).Where(m => m != null).ToList();
            if (hooked.Count == 0) return "the game's inventory grid has changed since this fix was written";

            // Every mod's patch method on those, then the methods of its own class it calls (helpers).
            var targets = new HashSet<MethodInfo>();
            foreach (var method in hooked)
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                foreach (var p in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers))
                    if (p.owner != harmony.Id && p.PatchMethod != null)
                        targets.Add(p.PatchMethod);
            }
            foreach (var m in targets.ToList())
                foreach (var called in Calls(m))
                    if (called is MethodInfo helper && helper.DeclaringType == m.DeclaringType && helper.GetMethodBody() != null)
                        targets.Add(helper);

            var mods = new SortedSet<string>();
            int swapped = 0;
            foreach (var m in targets)
            {
                int n = Calls(m).Count(c => Equals(c, FindChild));
                if (n == 0) continue;
                try
                {
                    harmony.Patch(m, transpiler: new HarmonyMethod(typeof(InventoryBorderFindFix), nameof(Transpiler)));
                    swapped += n;
                    mods.Add(Plugin.ModName(m.DeclaringType?.Assembly));
                }
                catch (Exception e)
                {
                    log.LogWarning($"Inventory borders: left {m.DeclaringType?.FullName}.{m.Name} alone ({e.GetType().Name}: {e.Message}).");
                }
            }
            if (swapped == 0) return Plugin.NotNeeded + "no installed mod looks up slot children this way";
            log.LogInfo($"Inventory borders: {swapped} slot lookups made reusable in {string.Join(", ", mods)}.");
            return null;
        }

        // Methods called by this one, or nothing if its code can't be read.
        private static IEnumerable<MethodBase> Calls(MethodBase method)
        {
            List<CodeInstruction> code;
            try { code = PatchProcessor.GetOriginalInstructions(method); }
            catch { yield break; }
            foreach (var ci in code)
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodBase called)
                    yield return called;
        }

        public static Transform FindChildCached(Transform parent, string name, Utils.IterativeSearchType searchType)
        {
            if (parent == null) return Utils.FindChild(parent, name, searchType);
            var key = (parent.GetInstanceID(), name);
            if (Found.TryGetValue(key, out var child) && child != null && child != parent && child.IsChildOf(parent))
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
                    code.operand = cached;
                yield return code;
            }
        }
    }
}
