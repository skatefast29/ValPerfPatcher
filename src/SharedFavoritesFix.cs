using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Inventory favourites. Quick Stack - Store - Sort - Trash - Restock 1.4.16, AzuAutoStore 3.1.7 and
    /// AzuExtendedPlayerInventory 2.6.4 each carry a copy of the same favouriting code (a UserConfig per player with a
    /// set of favourited slots and a set of favourited item names). They share a file, but each reads it only when the
    /// player's config is first made (at login) and then keeps its own sets. Starring an item through one changes only
    /// that mod's sets: the others still show their border for an item that was a favourite at login (it seems stuck),
    /// and AzuAutoStore, which decides what not to store from its own sets, stores a newly starred item and keeps back
    /// an unstarred one until the next login.
    /// This finds every such UserConfig type in any loaded mod (not by name: by shape) and makes all of them for one
    /// player use the same two sets, the union of what each loaded. A change through any of them is then seen by all
    /// at once, and each mod's own save writes the same favourites to its file. Trash marks are left separate.
    /// A type is only joined when it matches what was analysed: a static GetPlayerConfig(long), a (long) constructor,
    /// exactly one HashSet&lt;Vector2i&gt; and one HashSet&lt;string&gt; field with "favorit" in the name, assigned only
    /// in the constructor, Load and ResetAllFavoriting, and a Save method. If two of these mods ever react to the same
    /// click (both set to the same key), the second one would flip the shared favourite back; a save that exactly
    /// undoes the previous save of another mod in the same frame is therefore treated as that same click and the first
    /// change is kept.
    /// </summary>
    internal static class SharedFavoritesFix
    {
        private sealed class Kind
        {
            public Type Type;
            public FieldInfo Slots, Items;
            public string Mod;
        }

        private sealed class Shared
        {
            public HashSet<Vector2i> Slots;
            public HashSet<string> Items;
            // Guard against two mods handling the same click: the state at the last save, the one before it, who and when.
            public HashSet<Vector2i> SavedSlots = new HashSet<Vector2i>(), BeforeSlots = new HashSet<Vector2i>();
            public HashSet<string> SavedItems = new HashSet<string>(), BeforeItems = new HashSet<string>();
            public Type SavedBy;
            public int SavedFrame = -1;
        }

        private static readonly Dictionary<Type, Kind> Kinds = new Dictionary<Type, Kind>();
        private static readonly Dictionary<long, Shared> ByPlayer = new Dictionary<long, Shared>();
        private static readonly ConditionalWeakTable<object, StrongBox<long>> Owner = new ConditionalWeakTable<object, StrongBox<long>>();
        private static ManualLogSource _log;
        private static bool _reportedDouble;

        public static string Apply(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            var skipped = new List<string>();
            foreach (var asm in Chainloader.PluginInfos.Values.Where(p => p.Instance != null)
                         .Select(p => p.Instance.GetType().Assembly).Distinct())
            {
                if (asm == typeof(SharedFavoritesFix).Assembly) continue;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                foreach (var type in types)
                {
                    var get = type.GetMethod("GetPlayerConfig", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(long) }, null);
                    if (get == null || get.ReturnType != type) continue;
                    string problem = Check(type, out var kind);
                    if (problem != null) { skipped.Add($"{Plugin.ModName(asm)} ({problem})"); continue; }
                    if (kind != null) Kinds[type] = kind; // null: a player config without favourites (e.g. trash only)
                }
            }
            foreach (var s in skipped)
                log.LogWarning($"Shared favourites: left {s} alone; its favourites code has changed since this fix was written.");
            if (Kinds.Count < 2)
                return Plugin.NotNeeded + (Kinds.Count == 0 ? "no installed mod has favourites of this kind" : $"only {Kinds.Values.First().Mod} has favourites of this kind");

            foreach (var kind in Kinds.Values)
            {
                var t = kind.Type;
                harmony.Patch(AccessTools.DeclaredConstructor(t, new[] { typeof(long) }),
                    postfix: new HarmonyMethod(typeof(SharedFavoritesFix), nameof(CtorPostfix)));
                var load = AccessTools.DeclaredMethod(t, "Load", Type.EmptyTypes);
                if (load != null) harmony.Patch(load, postfix: new HarmonyMethod(typeof(SharedFavoritesFix), nameof(Rejoin)));
                var reset = AccessTools.DeclaredMethod(t, "ResetAllFavoriting", Type.EmptyTypes);
                if (reset != null)
                    harmony.Patch(reset, prefix: new HarmonyMethod(typeof(SharedFavoritesFix), nameof(ResetPrefix)),
                        postfix: new HarmonyMethod(typeof(SharedFavoritesFix), nameof(Rejoin)));
                harmony.Patch(AccessTools.DeclaredMethod(t, "Save", Type.EmptyTypes),
                    prefix: new HarmonyMethod(typeof(SharedFavoritesFix), nameof(SavePrefix)) { priority = Priority.First });

                // Players whose config was already made (normally none yet, this runs before any world).
                foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    if (f.FieldType == typeof(Dictionary<,>).MakeGenericType(typeof(long), t) && f.GetValue(null) is System.Collections.IDictionary existing)
                        foreach (System.Collections.DictionaryEntry e in existing)
                            Join(e.Value, (long)e.Key);
            }
            log.LogInfo($"Shared favourites: {string.Join(", ", Kinds.Values.Select(k => k.Mod).OrderBy(n => n))} now use the same favourites.");
            return null;
        }

        private static string Check(Type t, out Kind kind)
        {
            kind = null;
            const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var fields = t.GetFields(I).Where(f => f.Name.IndexOf("favorit", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (fields.Count == 0) return null;
            var slots = fields.Where(f => f.FieldType == typeof(HashSet<Vector2i>)).ToList();
            var items = fields.Where(f => f.FieldType == typeof(HashSet<string>)).ToList();
            if (slots.Count != 1 || items.Count != 1) return "favourite sets";
            if (AccessTools.DeclaredConstructor(t, new[] { typeof(long) }) == null) return "constructor";
            if (AccessTools.DeclaredMethod(t, "Save", Type.EmptyTypes) == null) return "save";

            // The sets may only be replaced where this fix puts the shared ones back afterwards.
            foreach (MethodBase m in t.GetMethods(I).Cast<MethodBase>().Concat(t.GetConstructors(I)))
            {
                if (m.GetMethodBody() == null) continue;
                List<CodeInstruction> code;
                try { code = PatchProcessor.GetOriginalInstructions(m); }
                catch { return "unreadable " + m.Name; }
                bool stores = code.Any(c => c.opcode == OpCodes.Stfld && (Equals(c.operand, slots[0]) || Equals(c.operand, items[0])));
                if (stores && !(m.IsConstructor || m.Name == "Load" || m.Name == "ResetAllFavoriting"))
                    return "sets replaced in " + m.Name;
            }
            kind = new Kind { Type = t, Slots = slots[0], Items = items[0], Mod = Plugin.ModName(t.Assembly) };
            return null;
        }

        private static void CtorPostfix(object __instance, object[] __args)
        {
            if (__args != null && __args.Length == 1 && __args[0] is long uid) Join(__instance, uid);
        }

        // Point this player's config at the shared sets, adding what it loaded.
        private static void Join(object config, long uid)
        {
            if (config == null || !Kinds.TryGetValue(config.GetType(), out var kind)) return;
            try
            {
                var slots = kind.Slots.GetValue(config) as HashSet<Vector2i>;
                var items = kind.Items.GetValue(config) as HashSet<string>;
                if (!ByPlayer.TryGetValue(uid, out var shared))
                {
                    shared = new Shared { Slots = new HashSet<Vector2i>(), Items = new HashSet<string>() };
                    ByPlayer[uid] = shared;
                }
                if (slots != null && !ReferenceEquals(slots, shared.Slots)) shared.Slots.UnionWith(slots);
                if (items != null && !ReferenceEquals(items, shared.Items)) shared.Items.UnionWith(items);
                // The click guard compares against the last saved state; start it from what is there now.
                shared.SavedSlots.Clear(); shared.SavedSlots.UnionWith(shared.Slots);
                shared.SavedItems.Clear(); shared.SavedItems.UnionWith(shared.Items);
                shared.SavedFrame = -1;
                kind.Slots.SetValue(config, shared.Slots);
                kind.Items.SetValue(config, shared.Items);
                Owner.Remove(config);
                Owner.Add(config, new StrongBox<long>(uid));
            }
            catch (Exception e)
            {
                _log.LogWarning($"Shared favourites: could not join {kind.Mod}'s favourites ({e.GetType().Name}: {e.Message}).");
            }
        }

        private static void Rejoin(object __instance)
        {
            if (Owner.TryGetValue(__instance, out var uid)) Join(__instance, uid.Value);
        }

        // Resetting through one mod resets the shared favourites (what every mod sees).
        private static void ResetPrefix(object __instance)
        {
            if (Owner.TryGetValue(__instance, out var uid) && ByPlayer.TryGetValue(uid.Value, out var shared))
            {
                shared.Slots.Clear();
                shared.Items.Clear();
            }
        }

        private static void SavePrefix(object __instance)
        {
            if (!Owner.TryGetValue(__instance, out var uid) || !ByPlayer.TryGetValue(uid.Value, out var shared)) return;
            var type = __instance.GetType();
            int frame = Time.frameCount;
            if (frame == shared.SavedFrame && type != shared.SavedBy &&
                shared.Slots.SetEquals(shared.BeforeSlots) && shared.Items.SetEquals(shared.BeforeItems) &&
                !(shared.Slots.SetEquals(shared.SavedSlots) && shared.Items.SetEquals(shared.SavedItems)))
            {
                // A second mod handled the same click and flipped it back: keep the first change.
                shared.Slots.Clear(); shared.Slots.UnionWith(shared.SavedSlots);
                shared.Items.Clear(); shared.Items.UnionWith(shared.SavedItems);
                if (!_reportedDouble)
                    _log.LogInfo($"Shared favourites: {Kinds[type].Mod} and {Kinds[shared.SavedBy].Mod} both handled the same click (same key); counted once.");
                _reportedDouble = true;
                return;
            }
            Swap(ref shared.BeforeSlots, ref shared.SavedSlots);
            Swap(ref shared.BeforeItems, ref shared.SavedItems);
            shared.SavedSlots.Clear(); shared.SavedSlots.UnionWith(shared.Slots);
            shared.SavedItems.Clear(); shared.SavedItems.UnionWith(shared.Items);
            shared.SavedBy = type;
            shared.SavedFrame = frame;
        }

        private static void Swap<T>(ref T a, ref T b)
        {
            var t = a;
            a = b;
            b = t;
        }
    }
}
