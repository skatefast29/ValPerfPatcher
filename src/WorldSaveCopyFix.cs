using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Base game. On every world save (every 30 minutes on a server, and on the host's game), ZDOExtraData.PrepareSave
    /// makes a full copy of the extra data (floats, ints, strings, ...) of every object in the world, so the save
    /// thread can write it while the game goes on. But the save only writes the chunks that changed since the last
    /// save (ZDOMan.GetSaveClonePerChunk: often 0 or 1 of ~40), and the copy is only ever read back for the objects
    /// in those chunks (ZDOExtraData.GetSaveData, from ZDO.Save in ZDOMan.SaveChunk). With 1.2 million objects the
    /// full copy froze the server for 250-680 ms on every save, even with nothing to write.
    /// This makes the same copies, but only for the objects that are about to be written: same data in the same
    /// file. The connection table is still rebuilt from the whole world first, as the game does (its hash numbers
    /// depend on every connection). A full save (every chunk) copies everything, as before. Stands down if the
    /// game's code or fields have changed, or if another mod hooks the same step.
    /// </summary>
    internal static class WorldSaveCopyFix
    {
        private const string SteadyFrameGuid = "ru.ivest.steadyframe";
        private static ManualLogSource _log;
        private static MethodInfo _prepare;
        private static Action _regenerate;
        private static FieldInfo _saveData, _objectsByChunk;
        private static readonly List<Pair> Pairs = new List<Pair>();
        private static FieldInfo _liveConnections, _saveConnections;
        private static string _reportedStandDown;
        private static int _saves;

        private sealed class Pair
        {
            public FieldInfo Live, Save;
            public Func<object, HashSet<ZDOID>, object> Copy;
        }

        public static string Apply(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            var t = typeof(ZDOExtraData);
            _prepare = AccessTools.DeclaredMethod(t, "PrepareSave", Type.EmptyTypes);
            var regen = AccessTools.DeclaredMethod(t, "RegenerateConnectionHashData", Type.EmptyTypes);
            _saveData = AccessTools.DeclaredField(typeof(ZDOMan), "m_saveData");
            _objectsByChunk = _saveData == null ? null : AccessTools.DeclaredField(_saveData.FieldType, "m_objectsByChunk");
            if (_prepare == null || !_prepare.IsStatic || regen == null || !regen.IsStatic || _saveData == null || _objectsByChunk == null ||
                _objectsByChunk.FieldType != typeof(List<Tuple<ZoneSystem.ChunkIndex, List<ZDO>>>))
                return "the game's world save has changed since this fix was written";

            if (!AddPair<float>("s_floats", "s_saveFloats") || !AddPair<Vector3>("s_vec3", "s_saveVec3s") ||
                !AddPair<Quaternion>("s_quats", "s_saveQuats") || !AddPair<int>("s_ints", "s_saveInts") ||
                !AddPair<long>("s_longs", "s_saveLongs") || !AddPair<string>("s_strings", "s_saveStrings") ||
                !AddPair<byte[]>("s_byteArrays", "s_saveByteArrays"))
                return "the game's object data tables have changed since this fix was written";
            _liveConnections = AccessTools.DeclaredField(t, "s_connectionsHashData");
            _saveConnections = AccessTools.DeclaredField(t, "s_saveConnections");
            if (_liveConnections?.FieldType != typeof(Dictionary<ZDOID, ZDOConnectionHashData>) || _saveConnections?.FieldType != _liveConnections.FieldType)
                return "the game's object connection table has changed since this fix was written";

            // The game's step must still be: rebuild the connection table, then copy each of the 8 tables whole.
            int clones = 0;
            bool regenerates = false;
            foreach (var ci in PatchProcessor.GetOriginalInstructions(_prepare))
            {
                if (ci.Calls(regen)) regenerates = true;
                else if (ci.operand is MethodInfo m && m.Name == "Clone") clones++;
            }
            if (!regenerates || clones != 8)
                return $"the game's world save copy has changed since this fix was written ({clones} copies)";

            _regenerate = (Action)Delegate.CreateDelegate(typeof(Action), regen);
            harmony.Patch(_prepare, prefix: new HarmonyMethod(typeof(WorldSaveCopyFix), nameof(Prefix)));
            return null;
        }

        private static bool AddPair<T>(string live, string save)
        {
            var t = typeof(ZDOExtraData);
            var l = AccessTools.DeclaredField(t, live);
            var s = AccessTools.DeclaredField(t, save);
            if (l?.FieldType != typeof(Dictionary<ZDOID, BinarySearchDictionary<int, T>>) || s?.FieldType != l.FieldType) return false;
            Pairs.Add(new Pair { Live = l, Save = s, Copy = (src, ids) => CopySubset((Dictionary<ZDOID, BinarySearchDictionary<int, T>>)src, ids) });
            return true;
        }

        private static Dictionary<ZDOID, BinarySearchDictionary<int, T>> CopySubset<T>(Dictionary<ZDOID, BinarySearchDictionary<int, T>> src, HashSet<ZDOID> ids)
        {
            var dst = new Dictionary<ZDOID, BinarySearchDictionary<int, T>>();
            if (src.Count < ids.Count)
            {
                foreach (var kv in src)
                    if (ids.Contains(kv.Key)) dst[kv.Key] = (BinarySearchDictionary<int, T>)kv.Value.Clone();
            }
            else
            {
                foreach (var id in ids)
                    if (src.TryGetValue(id, out var v)) dst[id] = (BinarySearchDictionary<int, T>)v.Clone();
            }
            return dst;
        }

        // Expected besides ours: ValPerfMonitor's timing probes.
        private static string OtherHooks()
        {
            var info = Harmony.GetPatchInfo(_prepare);
            if (info == null) return null;
            foreach (var list in new[] { info.Prefixes, info.Postfixes, info.Transpilers, info.Finalizers })
                foreach (var p in list)
                    if (p.owner != Plugin.Guid && (p.owner == null || !p.owner.StartsWith("valheimmods.ValPerfMonitor", StringComparison.Ordinal)))
                        return p.owner;
            return null;
        }

        private static bool Prefix()
        {
            string blocker;
            HashSet<ZDOID> ids = null;
            try
            {
                blocker = OtherHooks();
                if (blocker == SteadyFrameGuid)
                {
                    // SteadyFrame 1.3.4+ (LeanSaveCopy) makes the same subset copy itself, from its own prefix that runs after this one.
                    if (_reportedStandDown != blocker) _log.LogInfo("World save: SteadyFrame makes the same small copy itself, so this fix leaves it to SteadyFrame.");
                    _reportedStandDown = blocker;
                    return true;
                }
                if (blocker != null) blocker += " also hooks it";
                else
                {
                    var data = ZDOMan.instance == null ? null : _saveData.GetValue(ZDOMan.instance);
                    var chunks = data == null ? null : (List<Tuple<ZoneSystem.ChunkIndex, List<ZDO>>>)_objectsByChunk.GetValue(data);
                    if (chunks == null) blocker = "no save list";
                    else
                    {
                        ids = new HashSet<ZDOID>();
                        foreach (var chunk in chunks)
                            foreach (var zdo in chunk.Item2)
                                ids.Add(zdo.m_uid);
                    }
                }
            }
            catch (Exception e) { blocker = $"{e.GetType().Name}: {e.Message}"; }
            if (blocker != null)
            {
                if (_reportedStandDown != blocker) _log.LogInfo($"World save: copied the usual way ({blocker}).");
                _reportedStandDown = blocker;
                return true;
            }
            _reportedStandDown = null;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            _regenerate();
            foreach (var p in Pairs)
                p.Save.SetValue(null, p.Copy(p.Live.GetValue(null), ids));
            var live = (Dictionary<ZDOID, ZDOConnectionHashData>)_liveConnections.GetValue(null);
            var conns = new Dictionary<ZDOID, ZDOConnectionHashData>();
            foreach (var id in ids)
                if (live.TryGetValue(id, out var c)) conns[id] = c;
            _saveConnections.SetValue(null, conns);
            sw.Stop();
            if (_saves++ < 3)
                _log.LogInfo($"World save: copied the data of the {ids.Count} objects being written in {sw.Elapsed.TotalMilliseconds:0} ms (the game copies every object's).");
#if VERIFY_SAVE
            Verify(ids);
#endif
            return false;
        }

#if VERIFY_SAVE
        // Test builds only: make the game's full copies too and check every written object's data is the same.
        private static void Verify(HashSet<ZDOID> ids)
        {
            var ours = new List<object>();
            foreach (var p in Pairs) ours.Add(p.Save.GetValue(null));
            var ourConns = _saveConnections.GetValue(null);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _regenerate();
            var fullConns = (Dictionary<ZDOID, ZDOConnectionHashData>)_liveConnections.GetValue(null);
            int diff = 0, compared = 0;
            for (int i = 0; i < Pairs.Count; i++)
            {
                var full = (IDictionary)Pairs[i].Live.GetValue(null);
                var mine = (IDictionary)ours[i];
                foreach (var id in ids)
                {
                    compared++;
                    bool a = full.Contains(id), b = mine.Contains(id);
                    if (a != b) { diff++; continue; }
                    if (!a) continue;
                    if (!Same((IEnumerable)full[id], (IEnumerable)mine[id])) diff++;
                }
            }
            // Connection hash numbers are new on every rebuild (GetUniqueHash counts up), so compare the types only.
            var oc = (Dictionary<ZDOID, ZDOConnectionHashData>)ourConns;
            int connDiff = 0, conns = 0;
            foreach (var id in ids)
            {
                bool a = fullConns.TryGetValue(id, out var x), b = oc.TryGetValue(id, out var y);
                if (a) conns++;
                if (a != b || (a && x.m_type != y.m_type)) connDiff++;
            }
            _log.LogWarning($"World save check: {diff} differences in {compared} object tables compared, {connDiff} in {conns} connections ({ids.Count} objects, {sw.ElapsedMilliseconds} ms).");
        }

        private static bool Same(IEnumerable a, IEnumerable b)
        {
            var ea = a.GetEnumerator();
            var eb = b.GetEnumerator();
            while (true)
            {
                bool na = ea.MoveNext(), nb = eb.MoveNext();
                if (na != nb) return false;
                if (!na) return true;
                var sa = ea.Current?.ToString();
                var sb = eb.Current?.ToString();
                if (ea.Current is KeyValuePair<int, byte[]> ka && eb.Current is KeyValuePair<int, byte[]> kb)
                {
                    sa = ka.Key + ":" + Convert.ToBase64String(ka.Value ?? new byte[0]);
                    sb = kb.Key + ":" + Convert.ToBase64String(kb.Value ?? new byte[0]);
                }
                if (sa != sb) return false;
            }
        }
#endif
    }
}
