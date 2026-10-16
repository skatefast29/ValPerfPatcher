using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// AutomaticFuel's prefix on Smelter.Awake (BlastFurnacePatch.Prefix) counts the smelters you have access to, for
    /// splitting wood between charcoal kilns, by calling Resources.FindObjectsOfTypeAll&lt;Smelter&gt;(): a search
    /// through every object Unity has loaded, assets included. With a big mod list that takes about 35 ms, and it runs
    /// for every smelter, kiln and furnace that is created: entering a base, loading an area, and the hammer's placement
    /// preview of one.
    /// This does the full search once per world (when the first smelter wakes up after ZNetScene starts) and keeps that
    /// list up to date from then on: smelters that wake up are added, destroyed ones dropped. The prefix then loops over
    /// the same set of smelters, with the same access check. Only smelters created inactive after the world started and
    /// never woken would be missing from the count; the game and these mods don't do that. Outside a world (no
    /// ZNetScene) the original search runs.
    /// </summary>
    internal static class AutomaticFuelSmelterScanFix
    {
        public const string TargetGuid = "TastyChickenLegs.AutomaticFuel";

        private static readonly List<Smelter> Known = new List<Smelter>();
        private static readonly HashSet<Smelter> KnownSet = new HashSet<Smelter>();
        private static ZNetScene _scannedFor;
        private static MethodInfo _find;

        public static string Apply(Harmony harmony)
        {
            var type = Plugin.ModType(TargetGuid, "AutomaticFuel.GameClasses.Smelters+BlastFurnacePatch");
            var prefix = type == null ? null : AccessTools.DeclaredMethod(type, "Prefix", new[] { typeof(Smelter) });
            var generic = AccessTools.Method(typeof(Resources), nameof(Resources.FindObjectsOfTypeAll), Type.EmptyTypes);
            if (prefix == null || !prefix.IsStatic || generic == null || !generic.IsGenericMethodDefinition)
                return "AutomaticFuel's smelter setup has changed since this fix was written";
            _find = generic.MakeGenericMethod(typeof(Smelter));

            List<CodeInstruction> code;
            try { code = PatchProcessor.GetOriginalInstructions(prefix); }
            catch (Exception e) { return "could not read AutomaticFuel's smelter setup (" + e.Message + ")"; }
            int calls = 0;
            foreach (var ci in code)
                if (ci.Calls(_find)) calls++;
            if (calls != 1)
                return $"AutomaticFuel's smelter setup now searches for smelters {calls} times instead of once";

            harmony.Patch(prefix, transpiler: new HarmonyMethod(typeof(AutomaticFuelSmelterScanFix), nameof(Transpiler)));
            return null;
        }

        // Resources.FindObjectsOfTypeAll<Smelter>() becomes Smelters(__instance).
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var replacement = AccessTools.Method(typeof(AutomaticFuelSmelterScanFix), nameof(Smelters));
            foreach (var ci in instructions)
            {
                if (ci.Calls(_find))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(ci).MoveBlocksFrom(ci);
                    yield return new CodeInstruction(OpCodes.Call, replacement);
                }
                else yield return ci;
            }
        }

        private static Smelter[] Smelters(Smelter self)
        {
            var scene = ZNetScene.instance;
            if (scene == null)
                return Resources.FindObjectsOfTypeAll<Smelter>();

            if (scene != _scannedFor)
            {
                // First smelter of this world: one full search, like the original, then keep the list current.
                _scannedFor = scene;
                Known.Clear();
                KnownSet.Clear();
                foreach (var smelter in Resources.FindObjectsOfTypeAll<Smelter>())
                    if (KnownSet.Add(smelter)) Known.Add(smelter);
            }
            else
            {
                for (int i = Known.Count - 1; i >= 0; i--)
                {
                    if (Known[i] != null) continue;
                    KnownSet.Remove(Known[i]);
                    Known.RemoveAt(i);
                }
                if (self != null && KnownSet.Add(self)) Known.Add(self);
            }
            return Known.ToArray();
        }
    }
}
