using System.Collections;
using HarmonyLib;

namespace ValPerfPatcher
{
    /// <summary>
    /// MultiUserChest 0.6.2 keeps a slot-block record for every inventory it has looked at (InventoryBlock.Inventories,
    /// keyed by Inventory) and never removes one, so every chest and player inventory of every world session stays in
    /// memory (about 1,500 more objects per relog). The mod never keeps a record itself: it always asks
    /// InventoryBlock.Get(inventory), which makes a fresh empty one when there is none. This empties the list when a world
    /// starts (Game.Start), when the previous world's inventories are all gone.
    /// </summary>
    internal static class MultiUserChestBlockResetFix
    {
        public const string TargetGuid = "com.maxsch.valheim.MultiUserChest";
        private static IDictionary _inventories;

        public static string Apply(Harmony harmony)
        {
            var block = Plugin.ModType(TargetGuid, "MultiUserChest.InventoryBlock");
            var field = block == null ? null : AccessTools.DeclaredField(block, "Inventories");
            var get = block == null ? null : AccessTools.DeclaredMethod(block, "Get", new[] { typeof(Inventory) });
            if (field == null || !field.IsStatic || get == null || !(field.GetValue(null) is IDictionary dict) ||
                field.FieldType.GetGenericArguments().Length != 2 || field.FieldType.GetGenericArguments()[0] != typeof(Inventory))
                return "MultiUserChest's slot blocking has changed since this fix was written";
            var start = AccessTools.DeclaredMethod(typeof(Game), "Start", System.Type.EmptyTypes);
            if (start == null)
                return "the game's world start has changed since this fix was written";
            _inventories = dict;
            harmony.Patch(start, postfix: new HarmonyMethod(typeof(MultiUserChestBlockResetFix), nameof(Postfix)));
            return null;
        }

        private static void Postfix() => _inventories.Clear();
    }
}
