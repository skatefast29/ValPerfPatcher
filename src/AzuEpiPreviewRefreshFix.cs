using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ValPerfPatcher
{
    /// <summary>
    /// AzuExtendedPlayerInventory refreshes its character preview (copy the gear, reset the pose, render the preview
    /// camera, ~1.1 ms) from postfixes on Humanoid.EquipItem and Humanoid.UnequipItem, without checking who equipped.
    /// Every creature that spawns with a weapon or swaps gear therefore re-rendered the local player's preview (1,368
    /// times in 8 minutes at the base). Its VisEquipment refresh already checks for the local player; this does the same
    /// for the two equip postfixes: they are taken off and replaced by one that calls the same refresh only when the
    /// local player equips or unequips. Their postfixes are tiny, so a patch on them could be bypassed by inlining.
    /// </summary>
    internal static class AzuEpiPreviewRefreshFix
    {
        private const string Ns = "AzuEPI.Game.PlayerPreview.";

        private static Action _tryRefresh;

        public static string Apply(Harmony harmony)
        {
            var manager = Plugin.ModType(AzuEpiBackpackFix.TargetGuid, Ns + "PlayerPreviewManager");
            var tryRefresh = manager == null ? null : AccessTools.DeclaredMethod(manager, "TryRefresh", Type.EmptyTypes);
            var equip = AccessTools.DeclaredMethod(typeof(Humanoid), "EquipItem", new[] { typeof(ItemDrop.ItemData), typeof(bool) });
            var unequip = AccessTools.DeclaredMethod(typeof(Humanoid), "UnequipItem", new[] { typeof(ItemDrop.ItemData), typeof(bool) });
            var theirEquip = TheirPostfix(equip, "PreviewInstantRefresh_EquipItem", tryRefresh);
            var theirUnequip = TheirPostfix(unequip, "PreviewInstantRefresh_UnequipItem", tryRefresh);
            if (tryRefresh == null || !tryRefresh.IsStatic || equip == null || unequip == null || theirEquip == null || theirUnequip == null)
                return "AzuExtendedPlayerInventory's preview refresh has changed since this fix was written";

            _tryRefresh = AccessTools.MethodDelegate<Action>(tryRefresh);
            // Ours goes on before theirs comes off, per method: if a patch fails, that method keeps AzuEPI's refresh.
            var postfix = new HarmonyMethod(typeof(AzuEpiPreviewRefreshFix), nameof(Postfix));
            harmony.Patch(equip, postfix: postfix);
            harmony.Unpatch(equip, theirEquip);
            harmony.Patch(unequip, postfix: postfix);
            harmony.Unpatch(unequip, theirUnequip);
            return null;
        }

        /// <summary>AzuEPI's postfix on <paramref name="target"/>, if it still only calls TryRefresh.</summary>
        private static MethodInfo TheirPostfix(MethodBase target, string typeName, MethodInfo tryRefresh)
        {
            if (target == null || tryRefresh == null)
                return null;
            var postfix = Harmony.GetPatchInfo(target)?.Postfixes
                .FirstOrDefault(p => p.owner == AzuEpiBackpackFix.TargetGuid && p.PatchMethod.DeclaringType?.FullName == Ns + typeName)?.PatchMethod;
            if (postfix == null || postfix.GetParameters().Length != 0)
                return null;
            var code = PatchProcessor.GetOriginalInstructions(postfix).Where(c => c.opcode != OpCodes.Nop).ToList();
            return code.Count == 2 && code[0].Calls(tryRefresh) && code[1].opcode == OpCodes.Ret ? postfix : null;
        }

        private static void Postfix(Humanoid __instance)
        {
            if (__instance == Player.m_localPlayer && Player.m_localPlayer != null)
                _tryRefresh();
        }
    }
}
