using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ValPerfPatcher
{
    /// <summary>
    /// Adventure Backpacks asks "is this item a backpack?" (ItemDataExtensions.IsBackpack) for the equipped items
    /// many times per frame: its Update, its backpack effects and its Player patches all go through
    /// IsBackpackEquipped. For an item that isn't a backpack, IsBackpack compares the item's prefab name with every
    /// backpack type through a LINQ lambda, which builds a closure, a delegate, an enumerator and the name string
    /// each time; IsBackpackEquipped also asks the game for a new list of equipped items on every call.
    /// Together about 2 KB of garbage per frame with a full set of gear (the most of any client-side mod at a busy
    /// base), so the game's garbage collection stutters come more often.
    /// This remembers the answer per item type (ItemData.m_shared, shared by every item of that type), keeps the
    /// per-item backpack data check, and walks the inventory for equipped items without the new list. Same answers.
    /// Only answers given while in a world are cached, so nothing is remembered before the backpack types are loaded.
    /// </summary>
    internal static class AdventureBackpacksItemCheckFix
    {
        private const string ItemExtensions = "AdventureBackpacks.Extensions.ItemDataExtensions";
        private const string PlayerExtensions = "AdventureBackpacks.Extensions.PlayerExtensions";

        private static readonly Dictionary<ItemDrop.ItemData.SharedData, bool> Cache = new Dictionary<ItemDrop.ItemData.SharedData, bool>();
        private static Func<ItemDrop.ItemData, bool> _isBackpack;
        private static AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> _shoulder;

        public static string Apply(Harmony harmony)
        {
            var items = Plugin.ModType(AdventureBackpacksProxyFix.TargetGuid, ItemExtensions);
            var players = Plugin.ModType(AdventureBackpacksProxyFix.TargetGuid, PlayerExtensions);
            var isBackpack = items == null ? null : AccessTools.DeclaredMethod(items, "IsBackpack", new[] { typeof(ItemDrop.ItemData) });
            var equipped = players == null ? null : AccessTools.DeclaredMethod(players, "IsBackpackEquipped", new[] { typeof(Player) });
            if (isBackpack == null || isBackpack.ReturnType != typeof(bool) || !isBackpack.IsStatic ||
                equipped == null || equipped.ReturnType != typeof(bool) || !equipped.IsStatic)
                return "Adventure Backpacks' backpack checks have changed since this fix was written";
            if (AccessTools.Field(typeof(Humanoid), "m_shoulderItem")?.FieldType != typeof(ItemDrop.ItemData))
                return "the game's shoulder item slot has changed since this fix was written";
            _shoulder = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_shoulderItem");

            harmony.Patch(isBackpack,
                prefix: new HarmonyMethod(typeof(AdventureBackpacksItemCheckFix), nameof(ItemPrefix)),
                postfix: new HarmonyMethod(typeof(AdventureBackpacksItemCheckFix), nameof(ItemPostfix)));
            // Calls IsBackpack through Harmony's patched version, so the cache above answers.
            _isBackpack = (Func<ItemDrop.ItemData, bool>)Delegate.CreateDelegate(typeof(Func<ItemDrop.ItemData, bool>), isBackpack);
            // Last, so any other mod's prefix on it still runs first.
            harmony.Patch(equipped, prefix: new HarmonyMethod(typeof(AdventureBackpacksItemCheckFix), nameof(EquippedPrefix)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(Game), "Shutdown"),
                postfix: new HarmonyMethod(typeof(AdventureBackpacksItemCheckFix), nameof(Clear)));
            return null;
        }

        private static bool ItemPrefix(ItemDrop.ItemData item, ref bool __result, out bool __state)
        {
            __state = false;
            var shared = item?.m_shared;
            if (shared == null)
                return true;
            if (Cache.TryGetValue(shared, out bool cached))
            {
                // A single item also counts as a backpack when it carries backpack data, whatever its type.
                __result = cached || HasBackpackData(item);
                return false;
            }
            // Not cached yet: let the original answer, then remember it. Not from an item with its own backpack
            // data, though: its "yes" is about that item, not about every item of its type.
            __state = !HasBackpackData(item);
            return true;
        }

        private static void ItemPostfix(ItemDrop.ItemData item, bool __result, bool __state)
        {
            if (!__state || Player.m_localPlayer == null)
                return;
            if (Cache.Count > 4096) Cache.Clear();
            Cache[item.m_shared] = __result;
        }

        // Adventure Backpacks' per-item markers (BackpackComponent data, and the one from its JotunnBackpacks predecessor).
        private const string BackpackData = "AdventureBackpacks#AdventureBackpacks.Components.BackpackComponent";
        private const string OldBackpackData = "JotunnBackpacks#JotunnBackpacks.BackpackComponent";

        private static bool HasBackpackData(ItemDrop.ItemData item)
        {
            var data = item.m_customData;
            return data != null && data.Count > 0 && (data.ContainsKey(BackpackData) || data.ContainsKey(OldBackpackData));
        }

        // Same steps as Adventure Backpacks 2.3.4's IsBackpackEquipped, without the list GetEquippedItems builds.
        private static bool EquippedPrefix(Player player, ref bool __result)
        {
            __result = false;
            if (player == null)
                return false;
            var inventory = player.GetInventory();
            if (inventory == null)
                return false;
            var shoulder = _shoulder(player);
            if (shoulder != null && _isBackpack(shoulder))
            {
                __result = true;
                return false;
            }
            foreach (var item in inventory.GetAllItems())
            {
                if (item != null && item.m_equipped && _isBackpack(item))
                {
                    __result = true;
                    return false;
                }
            }
            return false;
        }

        private static void Clear() => Cache.Clear();
    }
}
