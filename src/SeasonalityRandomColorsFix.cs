using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Seasonality 3.8.4 adds its helper components to prefabs from postfixes that run on every world load, and the prefabs
    /// live for the whole game session, so every logout and login adds one more of each:
    ///  - RandomColors.ZNetScene_Awake_Patch.Postfix: prefab.AddComponent&lt;RandomColors&gt;() on each fall-colour prefab
    ///    (beech, birch, oak, ...). Every tree spawned afterwards gets all of them, each doing the same colour work and
    ///    adding itself to RandomColors.m_instances (about 12,600 more objects per relog).
    ///  - FrozenZones.ZoneSystem_Start_Patch.Postfix: m_zonePrefab.AddComponent&lt;FrozenZones&gt;(). Every water area
    ///    afterwards gets all of them, each adding its own water-surface collider and material copy.
    /// This makes those calls add the component only when the prefab has none yet, so later world loads leave the prefabs
    /// as the first one did. The rest of each postfix runs unchanged.
    /// </summary>
    internal static class SeasonalityRandomColorsFix
    {
        private static readonly Dictionary<MethodBase, (MethodInfo add, Type component)> Targets = new Dictionary<MethodBase, (MethodInfo, Type)>();

        public static string Apply(Harmony harmony)
        {
            var colors = Plugin.ModType(SeasonalWaterCacheFix.SeasonalityGuid, "Seasonality.RandomColors");
            var zones = Plugin.ModType(SeasonalWaterCacheFix.SeasonalityGuid, "Seasonality.FrozenZones");
            string problem = Prepare(colors, "ZNetScene_Awake_Patch", typeof(ZNetScene), "fall colour setup") ??
                             Prepare(zones, "ZoneSystem_Start_Patch", typeof(ZoneSystem), "frozen water setup");
            if (problem != null) return problem;
            foreach (var target in Targets.Keys)
                harmony.Patch(target, transpiler: new HarmonyMethod(typeof(SeasonalityRandomColorsFix), nameof(Transpiler)));
            return null;
        }

        private static string Prepare(Type component, string patchName, Type arg, string what)
        {
            var patch = component == null ? null : AccessTools.Inner(component, patchName);
            var postfix = patch == null ? null : AccessTools.DeclaredMethod(patch, "Postfix", new[] { arg });
            var generic = AccessTools.Method(typeof(GameObject), nameof(GameObject.AddComponent), Type.EmptyTypes);
            if (postfix == null || generic == null || !generic.IsGenericMethodDefinition)
                return $"Seasonality's {what} has changed since this fix was written";
            var add = generic.MakeGenericMethod(component);

            List<CodeInstruction> code;
            try { code = PatchProcessor.GetOriginalInstructions(postfix); }
            catch (Exception e) { return $"could not read Seasonality's {what} ({e.Message})"; }
            int calls = 0;
            foreach (var ci in code)
                if (ci.Calls(add)) calls++;
            if (calls != 1)
                return $"Seasonality's {what} now adds its component {calls} times instead of once";
            Targets[postfix] = (add, component);
            return null;
        }

        // prefab.AddComponent<T>() becomes AddOnce(prefab, typeof(T)); the result is discarded by the original either way.
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var (add, component) = Targets[original];
            var addOnce = AccessTools.Method(typeof(SeasonalityRandomColorsFix), nameof(AddOnce));
            foreach (var ci in instructions)
            {
                if (ci.Calls(add))
                {
                    yield return new CodeInstruction(OpCodes.Ldtoken, component).MoveLabelsFrom(ci).MoveBlocksFrom(ci);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Type), nameof(Type.GetTypeFromHandle)));
                    yield return new CodeInstruction(OpCodes.Call, addOnce);
                }
                else yield return ci;
            }
        }

        private static Component AddOnce(GameObject prefab, Type component)
        {
            var existing = prefab.GetComponent(component);
            return existing != null ? existing : prefab.AddComponent(component);
        }
    }
}
