using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// MultiUserChest 0.6.2 keeps a slot-block record for every inventory it has looked at (InventoryBlock.Inventories,
    /// keyed by Inventory) and never removes one, so every chest and player inventory of every world session stays in
    /// memory (about 1,500 more objects per relog), and within a session every chest it has touched stays in memory with
    /// all its items after the chest unloads (+5,600 objects in 15 minutes of farming with auto-store). The mod never
    /// keeps a record itself: it always asks InventoryBlock.Get(inventory), which makes a fresh empty one when there is
    /// none, so a record with nothing blocked is the same as no record. This empties the list when a world starts
    /// (Game.Start), when the previous world's inventories are all gone, and while playing drops the records with nothing
    /// blocked once the list grows past PruneAbove, at most every PruneSeconds.
    /// </summary>
    internal static class MultiUserChestBlockResetFix
    {
        public const string TargetGuid = "com.maxsch.valheim.MultiUserChest";
        private const int PruneAbove = 64;
        private const float PruneSeconds = 10f;
        private static IDictionary _inventories;
        private static MethodInfo _anyBlocked;
        private static readonly List<object> Empty = new List<object>();
        private static float _nextPrune;

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

            // Pruning while playing needs the mod's own "anything blocked?" check (blocked slots, all slots, consume).
            _anyBlocked = AccessTools.DeclaredMethod(block, "IsAnySlotBlocked", System.Type.EmptyTypes);
            if (_anyBlocked == null || _anyBlocked.ReturnType != typeof(bool) || _anyBlocked.IsStatic)
                return "only partly applied: MultiUserChest's 'anything blocked' check has changed, so records are only cleared per world";
            harmony.Patch(get, prefix: new HarmonyMethod(typeof(MultiUserChestBlockResetFix), nameof(GetPrefix)));
            return null;
        }

        private static void Postfix() => _inventories.Clear();

        private static void GetPrefix()
        {
            if (_inventories.Count <= PruneAbove || Time.unscaledTime < _nextPrune)
                return;
            _nextPrune = Time.unscaledTime + PruneSeconds;
            foreach (DictionaryEntry entry in _inventories)
                if (!(bool)_anyBlocked.Invoke(entry.Value, null))
                    Empty.Add(entry.Key);
            foreach (var key in Empty)
                _inventories.Remove(key);
            Empty.Clear();
        }
    }
}
