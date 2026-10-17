using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Quick Stack - Store - Sort - Trash - Restock 1.4.16 keeps every Container in ContainerFinder.AllContainers: added in
    /// Container.Awake, removed only in Container.OnDestroyed (the piece being broken). Containers whose area unloads, and
    /// every container of an earlier world session, stay in the list for good, and every quick stack / restock walks all
    /// of them in FindContainersInRange, which skips the destroyed ones. This removes the destroyed ones from the list
    /// just before that search. The search returns the same containers; the list just stops growing as you travel.
    /// </summary>
    internal static class QuickStackContainerListFix
    {
        public const string TargetGuid = "goldenrevolver.quick_stack_store";
        private static System.Reflection.FieldInfo _field;
        private static readonly Predicate<Container> Destroyed = c => c == null;

        public static string Apply(Harmony harmony)
        {
            var finder = Plugin.ModType(TargetGuid, "QuickStackStore.ContainerFinder");
            var field = finder == null ? null : AccessTools.DeclaredField(finder, "AllContainers");
            var find = finder == null ? null : AccessTools.DeclaredMethod(finder, "FindContainersInRange", new[] { typeof(Vector3), typeof(float) });
            if (field == null || !field.IsStatic || field.FieldType != typeof(List<Container>) || find == null)
                return "Quick Stack's container search has changed since this fix was written";
            _field = field;
            harmony.Patch(find, prefix: new HarmonyMethod(typeof(QuickStackContainerListFix), nameof(Prefix)));
            return null;
        }

        // Read the field each time: it is public and not readonly, so the mod could replace the list.
        private static void Prefix() => (_field.GetValue(null) as List<Container>)?.RemoveAll(Destroyed);
    }
}
