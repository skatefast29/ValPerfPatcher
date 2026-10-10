using System;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// World loading with Riverheim. Two parts, each with its own switch:
    ///
    /// 1. Head start (Riverheim): Riverheim builds its whole world map (WorldGenerator.Pregenerate, about 6.8 s)
    ///    when the game sets up the network, after a deliberate 1.5 s pause and the scene load. This starts the
    ///    same build (Riverheim's own BuildPipeline and Compute) on background threads the moment a world is
    ///    started from the menu (ZNet.SetServer). Riverheim still runs its own Pregenerate; when it computes its
    ///    new build for the same world, seed and settings, that waits for the early one and takes its results
    ///    instead of computing them again. Joining someone else's server is untouched (the world comes from the
    ///    server there).
    ///
    /// 2. Biome map in parallel (base game, with Riverheim): every world load, AltBiomeWorldData.VerifyBiomeData
    ///    samples the biome and height of 4 million map points one at a time (about 5 s with Riverheim).
    ///    Riverheim's answers only read the finished map and keep their per-point cache per thread, because the
    ///    game already asks them from its terrain thread. This fills the same table split across CPU threads.
    ///    Each point is written by exactly one thread, so the table is identical. Only done when Riverheim is
    ///    the world's generator and nothing else runs before or after those two lookups; otherwise the game's own loop.
    /// </summary>
    internal static class RiverheimLoadFix
    {
        public const string TargetGuid = "dev.gurebu.riverheim.stable";
        private const string MonitorPrefix = "valheimmods.ValPerfMonitor"; // its timing probes ignore other threads

        private static ManualLogSource _log;

        // ---- Riverheim, found by reflection ------------------------------------------------------

        private static Func<WorldGenerator, object> _getRenderer;   // WorldGeneratorExtensions.GetRenderer
        private static Func<World, object> _getSettings;            // WorldExtensions.GetSettings
        private static Func<World, bool> _isCustom;                 // WorldExtensions.IsCustomWorldgen
        private static Func<object, object> _getConfig;             // ConfigManager.GetConfig(WorldSettings)
        private static Func<object> _buildPipeline;                 // RiverheimPlugin.WorldGeneratorPatch.BuildPipeline
        private static MethodInfo _compute;                         // GeneratorPipeline.Compute(int, object)
        private static FieldInfo _cacheField;                       // GeneratorPipeline.cache: where a build keeps its results

        private static Assembly _riverheim;

        private static bool Resolve()
        {
            if (_riverheim != null) return _getRenderer != null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                if (asm.GetName().Name == "Riverheim") { _riverheim = asm; break; }
            if (_riverheim == null) return false;
            try
            {
                const BindingFlags PS = BindingFlags.Public | BindingFlags.Static;
                var genExt = _riverheim.GetType("Riverheim.Plugin.WorldGeneratorExtensions", false);
                var worldExt = _riverheim.GetType("Riverheim.Plugin.WorldExtensions", false);
                var getRenderer = genExt?.GetMethod("GetRenderer", PS, null, new[] { typeof(WorldGenerator) }, null);
                var getSettings = worldExt?.GetMethod("GetSettings", PS, null, new[] { typeof(World) }, null);
                var isCustom = worldExt?.GetMethod("IsCustomWorldgen", PS, null, new[] { typeof(World) }, null);
                if (getRenderer == null || getSettings == null || isCustom == null || isCustom.ReturnType != typeof(bool)) return false;
                _getRenderer = g => getRenderer.Invoke(null, new object[] { g });
                _getSettings = w => getSettings.Invoke(null, new object[] { w });
                _isCustom = (Func<World, bool>)Delegate.CreateDelegate(typeof(Func<World, bool>), isCustom);

                // Only needed by the head start; missing pieces just turn that part off.
                var configManager = _riverheim.GetType("Riverheim.Configuration.ConfigManager", false);
                var getConfig = configManager?.GetMethod("GetConfig", PS, null, new[] { getSettings.ReturnType }, null);
                var pipelineType = _riverheim.GetType("Riverheim.Pipeline.GeneratorPipeline", false);
                var patchType = _riverheim.GetType("Riverheim.Plugin.RiverheimPlugin+WorldGeneratorPatch", false);
                var build = patchType == null ? null : AccessTools.DeclaredMethod(patchType, "BuildPipeline", Type.EmptyTypes);
                var compute = pipelineType == null ? null : AccessTools.DeclaredMethod(pipelineType, "Compute", new[] { typeof(int), typeof(object) });
                var cache = pipelineType == null ? null : AccessTools.Field(pipelineType, "cache");
                if (getConfig != null && build != null && build.IsStatic && build.ReturnType == pipelineType && compute != null &&
                    cache != null && cache.FieldType.Name == "PipelineCache")
                {
                    _getConfig = s => getConfig.Invoke(null, new[] { s });
                    _buildPipeline = () => build.Invoke(null, null);
                    _compute = compute;
                    _cacheField = cache;
                }
                return true;
            }
            catch (Exception e)
            {
                _log?.LogDebug($"Riverheim: lookup failed: {e.Message}");
                _getRenderer = null;
                return false;
            }
        }

        // ---- Part 1: head start ------------------------------------------------------------------

        private sealed class Pending
        {
            public World World;
            public int Seed;
            public object Settings;
            public object Pipeline;
            public Task Task;
        }

        private static Pending _pending;
        private static World _generating; // the world whose Pregenerate is running

        public static string ApplyHeadStart(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            if (!Resolve()) return "Riverheim's code has changed since this fix was written";
            if (_buildPipeline == null) return "Riverheim's world builder has changed since this fix was written";
            var setServer = AccessTools.Method(typeof(ZNet), nameof(ZNet.SetServer),
                new[] { typeof(bool), typeof(bool), typeof(bool), typeof(string), typeof(string), typeof(World) });
            var pregenerate = AccessTools.DeclaredMethod(typeof(WorldGenerator), "Pregenerate", Type.EmptyTypes);
            if (setServer == null || pregenerate == null)
                return "the game's world start code has changed since this fix was written";
            if (!HasRiverheimPrefix(pregenerate))
                return "Riverheim no longer builds its world in WorldGenerator.Pregenerate";

            harmony.Patch(setServer, postfix: new HarmonyMethod(typeof(RiverheimLoadFix), nameof(SetServerPostfix)));
            harmony.Patch(pregenerate,
                prefix: new HarmonyMethod(typeof(RiverheimLoadFix), nameof(PregeneratePrefix)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(RiverheimLoadFix), nameof(PregenerateFinalizer)));
            harmony.Patch(_compute, prefix: new HarmonyMethod(typeof(RiverheimLoadFix), nameof(ComputePrefix)));
            return null;
        }

        private static bool HasRiverheimPrefix(MethodBase method)
        {
            var info = Harmony.GetPatchInfo(method);
            if (info == null) return false;
            foreach (var p in info.Prefixes)
                if (p.owner == TargetGuid) return true;
            return false;
        }

        // A world is being started as host from the menu: start Riverheim's build now, the way Pregenerate would.
        private static void SetServerPostfix(bool server, World world)
        {
            try
            {
                _pending = null; // a new start replaces any earlier one (e.g. the player went back to the menu)
                if (!server || world == null || world.m_menu || world.m_seed == 0 || !_isCustom(world)) return;
                var settings = _getSettings(world);
                if (settings == null) return;
                var config = _getConfig(settings);
                var pipeline = _buildPipeline();
                if (config == null || pipeline == null) return;
                int seed = world.m_seed;
                var task = Task.Run(() =>
                {
                    try { _compute.Invoke(pipeline, new object[] { seed, config }); }
                    catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
                });
                _pending = new Pending { World = world, Seed = seed, Settings = settings, Pipeline = pipeline, Task = task };
                _log.LogInfo($"Riverheim: started building world '{world.m_name}' early, while the game loads.");
            }
            catch (Exception e)
            {
                _pending = null;
                _log.LogWarning($"Riverheim: early world build not started ({e.GetType().Name}: {e.Message}); Riverheim builds it at the usual time.");
            }
        }

        private static void PregeneratePrefix(WorldGenerator __instance)
        {
            _generating = __instance.m_world;
        }

        private static Exception PregenerateFinalizer(Exception __exception)
        {
            _generating = null;
            _pending = null;
            return __exception;
        }

        // Riverheim's Pregenerate computes its new build: if the early one is for the same world, seed and
        // settings, wait for it and give the new build its results instead of computing them again.
        private static bool ComputePrefix(object __instance, int seed)
        {
            var p = _pending;
            var world = _generating;
            if (p == null || world == null || ReferenceEquals(__instance, p.Pipeline)) return true; // the early build itself
            _pending = null;
            try
            {
                if (!ReferenceEquals(p.World, world) || p.Seed != world.m_seed || seed != p.Seed)
                {
                    _log.LogInfo("Riverheim: the early build was for another world; building normally.");
                    return true;
                }
                var settings = _getSettings(world);
                if (!ReferenceEquals(settings, p.Settings) && !Equals(settings, p.Settings))
                {
                    _log.LogInfo("Riverheim: world settings changed after the early build started; building normally.");
                    return true;
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { p.Task.Wait(); }
                catch (AggregateException e)
                {
                    var inner = e.InnerException ?? e;
                    _log.LogWarning($"Riverheim: early build failed ({inner.GetType().Name}: {inner.Message}); building normally.");
                    return true;
                }
                _cacheField.SetValue(__instance, _cacheField.GetValue(p.Pipeline));
                _log.LogInfo($"Riverheim: early build used (waited {sw.Elapsed.TotalSeconds:0.0} s for the rest).");
                return false;
            }
            catch (Exception e)
            {
                _log.LogWarning($"Riverheim: early build not used ({e.GetType().Name}: {e.Message}); building normally.");
                return true;
            }
        }

        // ---- Part 2: biome map in parallel -------------------------------------------------------

        private static MethodInfo _getBiome, _getBiomeHeight, _generatePoints;
        private static MethodInfo _rendererBiome, _rendererHeight; // ValheimWorldRenderer.GetBiome / GetHeight

        private delegate Heightmap.Biome BiomeAt(float wx, float wy);
        private delegate float HeightAt(float wx, float wy, out Color mask);
        private static AccessTools.FieldRef<AltBiomeWorldData, World> _dataWorld;
        private static bool _reportedStandDown;

        public static string ApplyParallelBiomes(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            if (!Resolve()) return "Riverheim's code has changed since this fix was written";
            _generatePoints = AccessTools.DeclaredMethod(typeof(AltBiomeWorldData), nameof(AltBiomeWorldData.GenerateBiomePoints), new[] { typeof(World) });
            _getBiome = AccessTools.DeclaredMethod(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), new[] { typeof(float), typeof(float), typeof(float), typeof(bool) });
            _getBiomeHeight = AccessTools.DeclaredMethod(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeHeight),
                new[] { typeof(Heightmap.Biome), typeof(float), typeof(float), typeof(Color).MakeByRefType(), typeof(bool), typeof(bool) });
            var worldField = AccessTools.Field(typeof(AltBiomeWorldData), "m_world");
            // Size of the game's loop as analysed (Valheim 1.0.17); a different size means the game changed it.
            int il = _generatePoints?.GetMethodBody()?.GetILAsByteArray()?.Length ?? -1;
            if (_generatePoints == null || _getBiome == null || _getBiomeHeight == null || worldField?.FieldType != typeof(World) || il != 294)
                return $"the game's biome map code has changed since this fix was written (size {il})";
            // What Riverheim's GetBiome / GetBiomeHeight prefixes answer with; asked directly, because finding the
            // renderer per call goes through a table that locks on every read and makes the threads queue.
            var rendererType = _riverheim.GetType("Riverheim.Plugin.Rendering.ValheimWorldRenderer", false);
            _rendererBiome = rendererType?.GetMethod("GetBiome", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(float), typeof(float) }, null);
            _rendererHeight = rendererType?.GetMethod("GetHeight", BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(float), typeof(float), typeof(Color).MakeByRefType() }, null);
            if (_rendererBiome?.ReturnType != typeof(Heightmap.Biome) || _rendererHeight?.ReturnType != typeof(float))
                return "Riverheim's map lookups have changed since this fix was written";
            _dataWorld = AccessTools.FieldRefAccess<AltBiomeWorldData, World>("m_world");
            harmony.Patch(_generatePoints, prefix: new HarmonyMethod(typeof(RiverheimLoadFix), nameof(GeneratePointsPrefix)));
            return null;
        }

        /// <summary>Why the parallel fill can't be used right now, or null when it can.</summary>
        private static string ParallelBlocker(WorldGenerator gen)
        {
            if (gen == null) return "no world generator";
            if (_getRenderer(gen) == null) return "the world isn't a Riverheim world";
            string other = OtherHooks(_getBiome, false) ?? OtherHooks(_getBiomeHeight, false) ?? OtherHooks(_generatePoints, true);
            return other == null ? null : $"{other} also hooks the biome lookups";
        }

        // Riverheim (prefixes only) and the monitor's thread-aware probes are expected; anyone else stands this down.
        // On the two lookups a transpiler is fine too: it rewrites the game's own body, which never runs on a
        // Riverheim world because Riverheim's prefix answers first (e.g. Valheim Community Patch's biome sector fix).
        private static string OtherHooks(MethodBase method, bool selfTarget)
        {
            var info = Harmony.GetPatchInfo(method);
            if (info == null) return null;
            foreach (var p in info.Prefixes)
                if (!Expected(p.owner, selfTarget, true)) return p.owner;
            foreach (var list in new[] { info.Postfixes, info.Finalizers })
                foreach (var p in list)
                    if (!Expected(p.owner, selfTarget, false)) return p.owner;
            if (selfTarget)
                foreach (var p in info.Transpilers)
                    if (!Expected(p.owner, true, false)) return p.owner;
            return null;
        }

        private static bool Expected(string owner, bool selfTarget, bool prefix)
        {
            if (owner != null && owner.StartsWith(MonitorPrefix, StringComparison.Ordinal)) return true;
            if (selfTarget) return owner == Plugin.Guid;
            return prefix && owner == TargetGuid;
        }

        // Same as the game's GenerateBiomePoints, rows spread over threads.
        private static bool GeneratePointsPrefix(World world)
        {
            var gen = WorldGenerator.instance;
            string blocker;
            try { blocker = ParallelBlocker(gen); }
            catch (Exception e) { blocker = $"{e.GetType().Name}: {e.Message}"; }
            if (blocker != null)
            {
                if (!_reportedStandDown) _log.LogInfo($"Biome map: filled the usual way ({blocker}).");
                _reportedStandDown = true;
                return true;
            }
            _reportedStandDown = false;

            BiomeAt biomeAt;
            HeightAt heightAt;
            try
            {
                var renderer = _getRenderer(gen);
                biomeAt = (BiomeAt)Delegate.CreateDelegate(typeof(BiomeAt), renderer, _rendererBiome);
                heightAt = (HeightAt)Delegate.CreateDelegate(typeof(HeightAt), renderer, _rendererHeight);
            }
            catch (Exception e)
            {
                _log.LogInfo($"Biome map: filled the usual way (Riverheim's map lookups: {e.GetType().Name}: {e.Message}).");
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
                            // = gen.GetBiome / gen.GetBiomeHeight with only Riverheim's prefixes on them
                            biomes[j, i] = biomeAt(v.x, v.y).ToBiomeIndex();
                            heights[j, i] = heightAt(v.x, v.y, out _);
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
            data.PointsGenerated = true;
            world.m_biomeData = data;
            _dataWorld(data) = world;
            _log.LogInfo($"Biome map: filled on {Environment.ProcessorCount} threads in {sw.Elapsed.TotalSeconds:0.00} s.");
            return false;
        }
    }
}
