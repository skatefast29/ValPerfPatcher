using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// World cleanup, off by default. "No stacked copies in new areas" stops mod vegetation from placing extra
    /// plants exactly on top of the first one, but only where new areas are generated (on a dedicated server, on the
    /// server), and areas generated earlier keep theirs: near the coast about a third of RtD Ocean's seaweed is such
    /// an invisible copy (about 2,000 of them loaded at one busy base), each a networked object with a collider and
    /// ground checks that has to be drawn.
    /// When an object loads, this checks whether it is one of those mod plants (an entry that asks for groups with no
    /// spread) and whether another copy of the same plant sits on the same spot (within 1 cm). The extra copy is
    /// destroyed once this game owns it (the player whose game owns an area is the one who can delete in it: the zones
    /// right around you, so copies farther out go as you move around; at most 50 a second), so the
    /// world, also on a server, keeps one plant there. Only those mod entries; never the game's own vegetation,
    /// pickable plants (their harvest stays the same) or anything a player built.
    /// </summary>
    internal static class StackedVegetationCleanup
    {
        private static ManualLogSource _log;
        private static HashSet<int> _prefabs;
        private static ZoneSystem _prefabsFor;
        // One plant per (prefab, spot in cm): the copy that is kept.
        private static readonly Dictionary<(int, int, int, int), ZDOID> Kept = new Dictionary<(int, int, int, int), ZDOID>();
        private static readonly List<(ZDO zdo, (int, int, int, int) key, float since)> Extra = new List<(ZDO, (int, int, int, int), float)>();
        private static float _nextPass, _nextLog;
        private static int _removed, _loggedRemoved;
        private const int PerPass = 50; // at most this many deletions per second, so a big area doesn't hitch

        public static string Apply(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            var create = AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObject", new[] { typeof(ZDO) });
            var update = AccessTools.DeclaredMethod(typeof(ZNetScene), "Update");
            if (create == null || create.ReturnType != typeof(GameObject) || update == null ||
                AccessTools.Field(typeof(ZoneSystem.ZoneVegetation), nameof(ZoneSystem.ZoneVegetation.m_groupRadius)) == null)
                return "the game's object loading has changed since this fix was written";
            harmony.Patch(create, postfix: new HarmonyMethod(typeof(StackedVegetationCleanup), nameof(CreatePostfix)));
            harmony.Patch(update, postfix: new HarmonyMethod(typeof(StackedVegetationCleanup), nameof(UpdatePostfix)));
            harmony.Patch(AccessTools.Method(typeof(Game), "Shutdown"),
                postfix: new HarmonyMethod(typeof(StackedVegetationCleanup), nameof(Clear)));
            return null;
        }

        /// <summary>Prefab hashes of the mod vegetation entries that stack copies (as found by StackedVegetationFix).</summary>
        private static HashSet<int> Prefabs()
        {
            var zs = ZoneSystem.instance;
            if (zs == null || zs.m_vegetation == null) return null;
            if (_prefabs != null && _prefabsFor == zs) return _prefabs;
            var game = StackedVegetationFix.GameVegetation();
            var set = new HashSet<int>();
            var names = new List<string>();
            foreach (var veg in zs.m_vegetation)
            {
                if (veg == null || veg.m_prefab == null || game.Contains(veg)) continue;
                bool stacks = (veg.m_groupSizeMax > 1 && veg.m_groupRadius <= 0f) || StackedVegetationFix.Changed.Contains(veg);
                if (!stacks || veg.m_prefab.GetComponent<Pickable>() != null || veg.m_prefab.GetComponent<Piece>() != null) continue;
                if (set.Add(veg.m_prefab.name.GetStableHashCode())) names.Add(veg.m_prefab.name);
            }
            _prefabs = set;
            _prefabsFor = zs;
            if (names.Count > 0)
                _log.LogInfo($"World: stacked mod plants: watching {names.Count} plants: {string.Join(", ", names)}");
            return set;
        }

        private static void CreatePostfix(ZDO zdo, GameObject __result)
        {
            if (__result == null || zdo == null) return;
            var prefabs = Prefabs();
            if (prefabs == null || prefabs.Count == 0 || !prefabs.Contains(zdo.GetPrefab())) return;
            if (zdo.GetLong(ZDOVars.s_creator, 0L) != 0L) return; // placed by a player
            var p = zdo.GetPosition();
            var key = (zdo.GetPrefab(), Mathf.RoundToInt(p.x * 100f), Mathf.RoundToInt(p.y * 100f), Mathf.RoundToInt(p.z * 100f));
            if (Kept.TryGetValue(key, out var kept) && kept != zdo.m_uid && ZDOMan.instance.GetZDO(kept) != null)
                Extra.Add((zdo, key, Time.time));
            else
                Kept[key] = zdo.m_uid;
        }

        // Every second: destroy the extra copies this game owns now; others wait for ownership (or are dropped after a while).
        private static void UpdatePostfix(ZNetScene __instance)
        {
            float now = Time.time;
            if (now < _nextPass || Extra.Count == 0) return;
            _nextPass = now + 1f;
            int done = 0;
            for (int i = Extra.Count - 1; i >= 0; i--)
            {
                var (zdo, key, _) = Extra[i];
                if (zdo == null || !zdo.IsValid()) { Extra.RemoveAt(i); continue; }
                // The copy that stays must still be there; if it is gone, this one stays instead.
                if (!Kept.TryGetValue(key, out var kept) || kept == zdo.m_uid || ZDOMan.instance.GetZDO(kept) == null)
                {
                    Kept[key] = zdo.m_uid;
                    Extra.RemoveAt(i);
                    continue;
                }
                var view = __instance.FindInstance(zdo);
                if (view == null) { Extra.RemoveAt(i); continue; } // unloaded: checked again when it loads
                // Only the game that owns an area may delete in it: the zones right around the player. Farther copies
                // wait until the player comes closer (or are checked again the next time they load).
                if (!zdo.IsOwner() || done >= PerPass) continue;
                __instance.Destroy(view.gameObject);
                done++;
                Extra.RemoveAt(i);
                _removed++;
            }
            if (_removed != _loggedRemoved && now >= _nextLog)
            {
                _nextLog = now + 30f;
                _log.LogInfo($"World: stacked mod plants: {_removed} extra copies removed so far this session.");
                _loggedRemoved = _removed;
            }
        }

        private static void Clear()
        {
            Kept.Clear();
            Extra.Clear();
            _prefabs = null;
            _prefabsFor = null;
        }
    }
}
