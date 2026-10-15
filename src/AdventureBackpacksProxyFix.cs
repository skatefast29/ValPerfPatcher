using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Adventure Backpacks patches ZNetView.IsOwner, both ZNetView.InvokeRPC overloads and ZNetView.Awake to
    /// recognise its hidden backpack object, by checking gameObject.name.StartsWith("AB_BackpackProxy") on
    /// every call. Unity builds a new string each time a name is read, so every ownership check in the game
    /// (hundreds per frame) made about 56 bytes of garbage. This transpiler swaps that name check for a lookup:
    /// each object's name is read once and the answer remembered by its instance id. Same answer, same
    /// behaviour (the backpack object gets its name when it is created, before its ZNetView is added).
    /// </summary>
    internal static class AdventureBackpacksProxyFix
    {
        public const string TargetGuid = "vapok.mods.adventurebackpacks";
        private const string Outer = "AdventureBackpacks.Patches.ContainerPatches";
        private const string ProxyName = "AB_BackpackProxy";
        private static readonly string[] Patches = { "ZNetViewIsOwnerPatch", "ZNetViewInvokeRpcPatch", "ZNetViewInvokeRpcWithTargetPatch", "ZNetViewAwakePatch" };
        private static readonly Dictionary<int, bool> Cache = new Dictionary<int, bool>();
        private static int _replaced;

        public static string Apply(Harmony harmony)
        {
            var outer = Plugin.ModType(TargetGuid, Outer);
            if (outer == null)
                return "Adventure Backpacks' container patches were not found";
            int patched = 0, expected = 0;
            foreach (var name in Patches)
            {
                var inner = AccessTools.Inner(outer, name);
                var prefix = inner == null ? null : AccessTools.Method(inner, "Prefix");
                if (prefix == null || prefix.ReturnType != typeof(bool)) continue;
                expected++;
                int before = _replaced;
                harmony.Patch(prefix, transpiler: new HarmonyMethod(typeof(AdventureBackpacksProxyFix), nameof(Transpiler)));
                if (_replaced == before + 1) patched++;
                else harmony.Unpatch(prefix, HarmonyPatchType.Transpiler, harmony.Id); // not as analysed: leave it alone
            }
            if (patched == 0)
                return "Adventure Backpacks' backpack check has changed since this fix was written";
            harmony.Patch(AccessTools.Method(typeof(Game), "Shutdown"),
                postfix: new HarmonyMethod(typeof(AdventureBackpacksProxyFix), nameof(Clear)));
            return patched == expected ? null : $"only {patched} of {expected} checks matched; the rest are left as they were";
        }

        /// <summary>gameObject.name.StartsWith("AB_BackpackProxy"), read once per object.</summary>
        public static bool IsProxy(GameObject go)
        {
            int id = go.GetInstanceID();
            if (!Cache.TryGetValue(id, out bool proxy))
            {
                if (Cache.Count > 200_000) Cache.Clear(); // ids aren't reused; just keep the table from growing forever
                proxy = go.name.StartsWith(ProxyName, StringComparison.Ordinal);
                Cache[id] = proxy;
            }
            return proxy;
        }

        private static void Clear() => Cache.Clear();

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getName = AccessTools.PropertyGetter(typeof(UnityEngine.Object), nameof(UnityEngine.Object.name));
            var startsWith = AccessTools.Method(typeof(string), nameof(string.StartsWith), new[] { typeof(string) });
            var isProxy = AccessTools.Method(typeof(AdventureBackpacksProxyFix), nameof(IsProxy));
            var code = new List<CodeInstruction>(instructions);
            int found = -1, count = 0;
            for (int i = 0; i + 2 < code.Count; i++)
            {
                // (GameObject on the stack) callvirt Object.get_name ; ldstr "AB_BackpackProxy" ; callvirt string.StartsWith(string)
                if ((code[i].opcode == OpCodes.Callvirt || code[i].opcode == OpCodes.Call) && Equals(code[i].operand, getName) &&
                    code[i + 1].opcode == OpCodes.Ldstr && (string)code[i + 1].operand == ProxyName &&
                    (code[i + 2].opcode == OpCodes.Callvirt || code[i + 2].opcode == OpCodes.Call) && Equals(code[i + 2].operand, startsWith))
                {
                    found = i;
                    count++;
                }
            }
            if (count != 1 || code[found + 1].labels.Count > 0 || code[found + 2].labels.Count > 0)
                return code; // not exactly one check as analysed: unchanged
            // Keep the first instruction object (and any labels on it), just change it.
            code[found].opcode = OpCodes.Call;
            code[found].operand = isProxy;
            code.RemoveRange(found + 1, 2);
            _replaced++;
            return code;
        }
    }
}
