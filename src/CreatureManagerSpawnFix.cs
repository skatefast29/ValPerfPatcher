using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;

namespace ValPerfPatcher
{
    /// <summary>
    /// Mods that bundle CreatureManager (Bestiary, Air Animals, Land Animals...) hook SpawnSystem.Awake, which
    /// runs every time a zone loads. Each time, the hook takes the mod's creatures out of the shared spawn list
    /// (one List.Remove each) and builds them again from the mod's config, using reflection and LINQ per
    /// creature (about 0.35 ms per zone with Bestiary). The list is shared by every zone, so the result is the
    /// same each time until the mod's config changes.
    /// This lets the hook run normally once, then skips it while its entries are all still in that same list
    /// and none of the mod's settings has changed (a change, also one synced from a server, makes the next
    /// zone rebuild them exactly as before).
    /// </summary>
    internal static class CreatureManagerSpawnFix
    {
        private sealed class Copy
        {
            public FieldInfo LastField;      // static List<SpawnSystem.SpawnData> lastRegisteredSpawns
            public SpawnSystemList List;     // the list they were added to
            public HashSet<SpawnSystem.SpawnData> Entries;
            public bool Dirty = true;
        }

        // Keyed by CreatureManager.Creature of each mod (Harmony's __originalMethod can be a different MethodBase object).
        private static readonly Dictionary<Type, Copy> Copies = new Dictionary<Type, Copy>();

        /// <summary>Call once every plugin is loaded (a CreatureManager mod can load after this one).</summary>
        public static string Apply(Harmony harmony, ManualLogSource log)
        {
            int patched = 0, skipped = 0;
            var seen = new HashSet<Assembly>();
            foreach (var info in Chainloader.PluginInfos.Values)
            {
                var plugin = info.Instance;
                if (plugin == null || !seen.Add(plugin.GetType().Assembly)) continue;
                Type type;
                try { type = plugin.GetType().Assembly.GetType("CreatureManager.Creature", false); }
                catch { continue; }
                if (type == null) continue;

                var add = AccessTools.DeclaredMethod(type, "AddToSpawnSystem", new[] { typeof(SpawnSystem) });
                var last = AccessTools.DeclaredField(type, "lastRegisteredSpawns");
                if (add == null || !add.IsStatic || add.ReturnType != typeof(void) ||
                    last == null || !last.IsStatic || last.FieldType != typeof(List<SpawnSystem.SpawnData>))
                {
                    skipped++;
                    log.LogInfo($"Creature spawn lists: {info.Metadata.Name}'s CreatureManager looks different; left alone.");
                    continue;
                }

                var copy = new Copy { LastField = last };
                Copies[type] = copy;
                plugin.Config.SettingChanged += (_, __) => copy.Dirty = true;
                plugin.Config.ConfigReloaded += (_, __) => copy.Dirty = true;
                harmony.Patch(add,
                    prefix: new HarmonyMethod(typeof(CreatureManagerSpawnFix), nameof(Prefix)),
                    postfix: new HarmonyMethod(typeof(CreatureManagerSpawnFix), nameof(Postfix)));
                patched++;
                log.LogInfo($"Creature spawn lists: {info.Metadata.Name} reuses its spawn entries.");
            }
            if (patched == 0)
                return skipped == 0 ? "no mod with CreatureManager is installed" : "the CreatureManager copies have changed since this fix was written";
            harmony.Patch(AccessTools.Method(typeof(Game), "Shutdown"),
                postfix: new HarmonyMethod(typeof(CreatureManagerSpawnFix), nameof(Clear)));
            return null;
        }

        // AddToSpawnSystem is static and its parameter is itself named __instance, so it is taken by position (__0).
        private static bool Prefix(SpawnSystem __0, MethodBase __originalMethod, out bool __state)
        {
            var spawn = __0;
            __state = true; // the original runs: record its result afterwards
            if (__originalMethod == null || !Copies.TryGetValue(__originalMethod.DeclaringType, out var c) || c.Dirty || c.List == null) return true;
            if (spawn == null || spawn.m_spawnLists == null || spawn.m_spawnLists.Count == 0) return true;
            var list = spawn.m_spawnLists[0];
            if (list != c.List || list.m_spawners == null) return true;
            var last = c.LastField.GetValue(null) as List<SpawnSystem.SpawnData>;
            if (last == null || last.Count != c.Entries.Count) return true;

            // Every entry the last full run added must still be in the list (another mod may have removed some).
            int found = 0;
            foreach (var s in list.m_spawners)
                if (s != null && c.Entries.Contains(s)) found++;
            if (found != c.Entries.Count) return true;

            __state = false;
            return false;
        }

        private static void Postfix(SpawnSystem __0, MethodBase __originalMethod, bool __state)
        {
            var spawn = __0;
            if (!__state || __originalMethod == null || !Copies.TryGetValue(__originalMethod.DeclaringType, out var c)) return;
            var last = c.LastField.GetValue(null) as List<SpawnSystem.SpawnData>;
            if (spawn == null || spawn.m_spawnLists == null || spawn.m_spawnLists.Count == 0 || last == null)
            {
                c.Dirty = true;
                return;
            }
            c.List = spawn.m_spawnLists[0];
            c.Entries = new HashSet<SpawnSystem.SpawnData>(last);
            c.Dirty = false;
        }

        private static void Clear()
        {
            foreach (var c in Copies.Values)
            {
                c.Dirty = true;
                c.List = null;
            }
        }
    }
}
