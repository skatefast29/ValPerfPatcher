using System.Collections.Generic;
using HarmonyLib;

namespace ValPerfPatcher
{
    /// <summary>
    /// AzuExtendedPlayerInventory's Adventure Backpacks compatibility (AdvBackpacksCompat) runs around
    /// GetEquippedBackpack and several Player methods, and each run asks IsBackpackItem(item) for the
    /// equipped items. The answer comes from Adventure Backpacks' API or the prefab name, both of which
    /// allocate. Whether an item is a backpack depends only on its item type, so the answer is cached per
    /// item type (ItemData.m_shared, shared by every item of that type).
    /// Only answers given while in a world are cached, so nothing is remembered before both mods have
    /// finished registering their items.
    /// </summary>
    internal static class AzuEpiBackpackFix
    {
        public const string TargetGuid = "Azumatt.AzuExtendedPlayerInventory";
        private const string TypeName = "AzuEPI.Game.Compatibility.AdvBackpacks.AdvBackpacksCompat";

        private static readonly Dictionary<ItemDrop.ItemData.SharedData, bool> Cache = new Dictionary<ItemDrop.ItemData.SharedData, bool>();

        public static string Apply(Harmony harmony)
        {
            var type = AccessTools.TypeByName(TypeName);
            var method = type == null ? null : AccessTools.Method(type, "IsBackpackItem", new[] { typeof(ItemDrop.ItemData) });
            if (method == null || method.ReturnType != typeof(bool))
                return "AzuExtendedPlayerInventory's backpack check has changed since this fix was written";
            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(AzuEpiBackpackFix), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(AzuEpiBackpackFix), nameof(Postfix)));
            harmony.Patch(AccessTools.Method(typeof(Game), "Shutdown"),
                postfix: new HarmonyMethod(typeof(AzuEpiBackpackFix), nameof(Clear)));
            return null;
        }

        private static bool Prefix(ItemDrop.ItemData item, ref bool __result, out bool __state)
        {
            __state = false;
            var shared = item?.m_shared;
            if (shared == null)
                return true;
            if (Cache.TryGetValue(shared, out bool cached))
            {
                // Adventure Backpacks (2.2.9) also counts a single item as a backpack when the item itself carries
                // backpack data, whatever its type. A "no" for the type doesn't cover that item: ask the original.
                if (!cached && HasBackpackData(item))
                    return true;
                __result = cached;
                return false;
            }
            // Not cached yet: let the original answer, then remember it. Not from an item with its own backpack
            // data, though: its "yes" is about that item, not about every item of its type.
            __state = !HasBackpackData(item);
            return true;
        }

        // Adventure Backpacks' per-item markers (BackpackComponent data, and the one from its JotunnBackpacks predecessor).
        private const string BackpackData = "AdventureBackpacks#AdventureBackpacks.Components.BackpackComponent";
        private const string OldBackpackData = "JotunnBackpacks#JotunnBackpacks.BackpackComponent";

        private static bool HasBackpackData(ItemDrop.ItemData item)
        {
            var data = item.m_customData;
            return data != null && data.Count > 0 && (data.ContainsKey(BackpackData) || data.ContainsKey(OldBackpackData));
        }

        private static void Postfix(ItemDrop.ItemData item, bool __result, bool __state)
        {
            if (!__state || Player.m_localPlayer == null)
                return;
            if (Cache.Count > 4096) Cache.Clear();
            Cache[item.m_shared] = __result;
        }

        private static void Clear() => Cache.Clear();
    }
}
