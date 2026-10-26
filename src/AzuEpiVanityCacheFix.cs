using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace ValPerfPatcher
{
    /// <summary>
    /// AzuExtendedPlayerInventory's vanity code remembers, per item type (ItemData.m_shared), which vanity slot an item
    /// belongs to (CustomSlotVanity._byShared, WeaponVanity._byShared) and only forgets when the vanity setup changes.
    /// MultiUserChest and AzuAutoStore give every item they move its own copy of m_shared, so each stored item adds an
    /// entry that is never used again and keeps that copy alive: +42,800 objects in 15 minutes of farming with
    /// auto-store. Both tables are plain caches (a missing entry is worked out again, as after the mod's own
    /// InvalidateItemCache), so this empties a table when it passes MaxEntries.
    /// </summary>
    internal static class AzuEpiVanityCacheFix
    {
        private const string Ns = "AzuEPI.Game.Panels.Vanity.";
        private const int MaxEntries = 2048;
        private static IDictionary _custom, _weapon;

        public static string Apply(Harmony harmony)
        {
            var custom = Plugin.ModType(AzuEpiBackpackFix.TargetGuid, Ns + "CustomSlotVanity");
            var weapon = Plugin.ModType(AzuEpiBackpackFix.TargetGuid, Ns + "WeaponVanity");
            var forItem = custom == null ? null : AccessTools.DeclaredMethod(custom, "ForItem", new[] { typeof(ItemDrop.ItemData) });
            var getOrAdd = weapon == null ? null : AccessTools.DeclaredMethod(weapon, "GetOrAdd", new[] { typeof(ItemDrop.ItemData.SharedData) });
            _custom = Table(custom);
            _weapon = Table(weapon);
            if (forItem == null || getOrAdd == null || _custom == null || _weapon == null)
                return "AzuExtendedPlayerInventory's vanity lookups have changed since this fix was written";
            harmony.Patch(forItem, prefix: new HarmonyMethod(typeof(AzuEpiVanityCacheFix), nameof(CustomPrefix)));
            harmony.Patch(getOrAdd, prefix: new HarmonyMethod(typeof(AzuEpiVanityCacheFix), nameof(WeaponPrefix)));
            return null;
        }

        // static Dictionary<ItemDrop.ItemData.SharedData, ...> _byShared
        private static IDictionary Table(System.Type type)
        {
            var field = type == null ? null : AccessTools.DeclaredField(type, "_byShared");
            if (field == null || !field.IsStatic || !field.FieldType.IsGenericType ||
                field.FieldType.GetGenericArguments()[0] != typeof(ItemDrop.ItemData.SharedData))
                return null;
            return field.GetValue(null) as IDictionary;
        }

        private static void CustomPrefix()
        {
            if (_custom.Count > MaxEntries) _custom.Clear();
        }

        private static void WeaponPrefix()
        {
            if (_weapon.Count > MaxEntries) _weapon.Clear();
        }
    }
}
