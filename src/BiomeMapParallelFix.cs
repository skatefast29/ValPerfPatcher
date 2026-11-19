using System;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Valheim itself. Every world load, AltBiomeWorldData.VerifyBiomeData samples the biome and height of 4 million
    /// map points one at a time (GenerateBiomePoints): about 2.8 s on a normal world, about 5 s with Riverheim. The
    /// game already asks WorldGenerator.GetBiome and GetBiomeHeight from its terrain thread while the game thread asks
    /// them too, so they are safe side by side (the one shared cache, the river points, is behind a lock); a Worldhalla
    /// generator's answers (Riverheim) only read its finished map, and Worldhalla's own point cache is per thread.
    /// This fills the same table split across CPU threads. Each point is written by exactly one thread with the same
    /// call the game makes, so the table is identical.
    /// On a Worldhalla world its WorldStateWrapper is asked directly (what Worldhalla's prefixes on the two lookups
    /// answer with), because finding it per call goes through a table that locks on every read. Only done while
    /// nothing but Worldhalla (and ValPerfMonitor's timing probes) runs before, after or inside those lookups;
    /// otherwise the game's own loop.
    /// </summary>
    internal static class BiomeMapParallelFix
    {
        private static ManualLogSource _log;
        private static MethodInfo _getBiome, _getBiomeHeight, _generatePoints;
        private static MethodInfo _stateBiome, _stateHeight; // Worldhalla's WorldStateWrapper.GetBiome / GetHeight
        private static AccessTools.FieldRef<AltBiomeWorldData, World> _dataWorld;
        private static string _reportedStandDown;
#if VERIFY_BIOMES
        private static bool _verifying;
#endif

        private delegate Heightmap.Biome BiomeAt(float wx, float wy);
        private delegate float HeightAt(Heightmap.Biome biome, float wx, float wy);
        private delegate float StateHeightAt(Heightmap.Biome biome, float wx, float wy, out Color mask);

        public static string Apply(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            _generatePoints = AccessTools.DeclaredMethod(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.GenerateBiomePoints), new[] { typeof(World) });
            _getBiome = AccessTools.DeclaredMethod(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), new[] { typeof(float), typeof(float), typeof(float), typeof(bool) });
            _getBiomeHeight = AccessTools.DeclaredMethod(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeHeight),
                new[] { typeof(Heightmap.Biome), typeof(float), typeof(float), typeof(Color).MakeByRefType(), typeof(bool), typeof(bool) });
            var worldField = AccessTools.Field(typeof(AltBiomeWorldData), "m_world");
            // Size of the game's loop as analysed (Valheim 1.0.17, unchanged in 1.0.21); a different size means the game changed it.
            int il = _generatePoints?.GetMethodBody()?.GetILAsByteArray()?.Length ?? -1;
            if (_generatePoints == null || _getBiome == null || _getBiomeHeight == null || worldField?.FieldType != typeof(World) || il != 294)
                return $"the game's biome map code has changed since this fix was written (size {il})";
            _dataWorld = AccessTools.FieldRefAccess<AltBiomeWorldData, World>("m_world");

            if (RiverheimLoadFix.Resolve())
            {
                var stateType = RiverheimLoadFix._stateType;
                _stateBiome = stateType.GetMethod("GetBiome", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(float), typeof(float) }, null);
                _stateHeight = stateType.GetMethod("GetHeight", BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(Heightmap.Biome), typeof(float), typeof(float), typeof(Color).MakeByRefType() }, null);
                if (_stateBiome?.ReturnType != typeof(Heightmap.Biome) || _stateHeight?.ReturnType != typeof(float))
                {
                    _stateBiome = _stateHeight = null;
                    log.LogWarning("Biome map: Worldhalla's map lookups have changed since this fix was written; Worldhalla worlds (Riverheim) are filled the usual way.");
                }
            }
            else if (RiverheimLoadFix._worldhalla != null)
                log.LogWarning("Biome map: Worldhalla's code has changed since this fix was written; Worldhalla worlds (Riverheim) are filled the usual way.");

            harmony.Patch(_generatePoints, prefix: new HarmonyMethod(typeof(BiomeMapParallelFix), nameof(GeneratePointsPrefix)));
            return null;
        }

        /// <summary>Why the parallel fill can't be used right now, or null when it can.</summary>
        private static string ParallelBlocker(WorldGenerator gen, out object worldState)
        {
            worldState = null;
            if (gen == null) return "no world generator";
            if (RiverheimLoadFix._worldhalla != null)
            {
                if (RiverheimLoadFix._getState == null) return "Worldhalla's code has changed since this fix was written";
                worldState = RiverheimLoadFix._getState(gen);
                if (worldState != null && _stateBiome == null) return "Worldhalla's map lookups have changed since this fix was written";
            }
            bool customWorld = worldState != null;
            string other = OtherHooks(_getBiome, customWorld) ?? OtherHooks(_getBiomeHeight, customWorld) ?? OtherHooks(_generatePoints, false, self: true);
            return other == null ? null : $"{other} also hooks the biome lookups";
        }

        // Expected: ValPerfMonitor's thread-aware probes, Worldhalla's prefixes on the two lookups (on a Worldhalla
        // world they answer first; on a vanilla world they only find no world state and step aside, as on the game's
        // terrain thread), and this fix's own prefix on the loop. On a Worldhalla world a transpiler on a lookup is fine
        // too: it rewrites the game's own body, which never runs there (e.g. an older Valheim Community Patch's biome
        // fix). Anyone else stands this down: their code would run on these threads.
        private static string OtherHooks(MethodBase method, bool customWorld, bool self = false)
        {
            var info = Harmony.GetPatchInfo(method);
            if (info == null) return null;
            foreach (var p in info.Prefixes)
                if (!Expected(p.owner, self, true)) return p.owner;
            foreach (var list in new[] { info.Postfixes, info.Finalizers })
                foreach (var p in list)
                    if (!Expected(p.owner, self, false)) return p.owner;
            if (self || !customWorld)
                foreach (var p in info.Transpilers)
                    if (!Expected(p.owner, false, false)) return p.owner;
            return null;
        }

        private static bool Expected(string owner, bool self, bool prefix)
        {
            if (owner != null && owner.StartsWith(RiverheimLoadFix.MonitorPrefix, StringComparison.Ordinal)) return true;
            if (self) return owner == Plugin.Guid;
            return prefix && owner == RiverheimLoadFix.WorldhallaGuid;
        }

        // Same as the game's GenerateBiomePoints, rows spread over threads.
        private static bool GeneratePointsPrefix(World world)
        {
#if VERIFY_BIOMES
            if (_verifying) return true;
#endif
            var gen = WorldGenerator.instance;
            string blocker;
            object state;
            try { blocker = ParallelBlocker(gen, out state); }
            catch (Exception e) { blocker = $"{e.GetType().Name}: {e.Message}"; state = null; }
            if (blocker != null)
            {
                if (_reportedStandDown != blocker) _log.LogInfo($"Biome map: filled the usual way ({blocker}).");
                _reportedStandDown = blocker;
                return true;
            }
            _reportedStandDown = null;

            BiomeAt biomeAt;
            HeightAt heightAt;
            try
            {
                if (state != null)
                {
                    biomeAt = (BiomeAt)Delegate.CreateDelegate(typeof(BiomeAt), state, _stateBiome);
                    var stateHeight = (StateHeightAt)Delegate.CreateDelegate(typeof(StateHeightAt), state, _stateHeight);
                    heightAt = (biome, x, y) => stateHeight(biome, x, y, out _);
                }
                else
                {
                    // The calls the game's loop makes, with the same default arguments.
                    biomeAt = (x, y) => gen.GetBiome(x, y);
                    heightAt = (biome, x, y) => gen.GetBiomeHeight(biome, x, y, out _);
                }
            }
            catch (Exception e)
            {
                _log.LogInfo($"Biome map: filled the usual way (map lookups: {e.GetType().Name}: {e.Message}).");
                return true;
            }

            const int size = 2048;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var data = new AltBiomeWorldData(size);
            data.dt = DateTime.Now;
            var biomes = data.PointBiomes;
            var heights = data.PointHeights;
            var ocean = Heightmap.Biome.Ocean.ToBiomeIndex();
            try
            {
                Parallel.For(0, size, i =>
                {
                    float wy = AltBiomeWorldData.MapSpaceToWorldSpace(i);
                    for (int j = 0; j < size; j++)
                    {
                        var v = new Vector2 { x = AltBiomeWorldData.MapSpaceToWorldSpace(j), y = wy };
                        if (v.sqrMagnitude > 110250000f)
                        {
                            biomes[j, i] = ocean;
                            heights[j, i] = -1000f;
                        }
                        else
                        {
                            var biome = biomeAt(v.x, v.y);
                            biomes[j, i] = biome.ToBiomeIndex();
                            heights[j, i] = heightAt(biome, v.x, v.y);
                        }
                    }
                });
            }
            catch (Exception e)
            {
                var inner = (e as AggregateException)?.InnerException ?? e;
                _log.LogWarning($"Biome map: parallel fill failed ({inner.GetType().Name}: {inner.Message}); filling it the usual way.");
                return true;
            }
            double seconds = sw.Elapsed.TotalSeconds;
#if VERIFY_BIOMES
            // Test builds only: fill it the game's way too and compare every point.
            _verifying = true;
            try { AltBiomeWorldData.GenerateBiomePoints(world); }
            finally { _verifying = false; }
            var game = world.m_biomeData;
            int diff = 0;
            for (int i = 0; i < size; i++)
                for (int j = 0; j < size; j++)
                    if (game.PointBiomes[j, i] != biomes[j, i] || !game.PointHeights[j, i].Equals(heights[j, i])) diff++;
            _log.LogWarning($"Biome map check: {diff} of {size * size} points differ from the game's own fill.");
#endif
            data.PointsGenerated = true;
            world.m_biomeData = data;
            _dataWorld(data) = world;
            _log.LogInfo($"Biome map: filled on {Environment.ProcessorCount} threads in {seconds:0.00} s ({(state != null ? "Worldhalla" : "game")} lookups).");
            return false;
        }
    }
}
