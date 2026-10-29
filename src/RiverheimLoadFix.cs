using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// World loading with Riverheim: head start. Riverheim builds its whole world map (WorldGenerator.Pregenerate,
    /// about 6.8 s) when the game sets up the network, after a deliberate 1.5 s pause and the scene load. This starts
    /// the same build (Riverheim's own BuildPipeline and Compute) on background threads the moment a world is started
    /// from the menu (ZNet.SetServer). Riverheim still runs its own Pregenerate; when it computes its new build for
    /// the same world, seed and settings, that waits for the early one and takes its results instead of computing
    /// them again. Joining a server: the world only arrives from the server after the scene has loaded, so the build
    /// for the world last played on that server (remembered in BepInEx\cache) starts when the server address is set
    /// at Join. If the server sends another world, seed or settings, the early build is ignored and Riverheim builds
    /// as usual. The biome map that follows is filled by BiomeMapParallelFix, which uses the lookups found here.
    /// </summary>
    internal static class RiverheimLoadFix
    {
        public const string TargetGuid = "dev.gurebu.riverheim.stable";
        internal const string MonitorPrefix = "valheimmods.ValPerfMonitor"; // its timing probes ignore other threads

        private static ManualLogSource _log;

        // ---- Riverheim, found by reflection ------------------------------------------------------

        internal static Func<WorldGenerator, object> _getRenderer;  // WorldGeneratorExtensions.GetRenderer
        private static Func<World, object> _getSettings;            // WorldExtensions.GetSettings
        private static Func<World, bool> _isCustom;                 // WorldExtensions.IsCustomWorldgen
        private static Func<object, object> _getConfig;             // ConfigManager.GetConfig(WorldSettings)
        private static Func<object> _buildPipeline;                 // RiverheimPlugin.WorldGeneratorPatch.BuildPipeline
        private static MethodInfo _compute;                         // GeneratorPipeline.Compute(int, object)
        private static FieldInfo _cacheField;                       // GeneratorPipeline.cache: where a build keeps its results

        internal static Assembly _riverheim;

        internal static bool Resolve()
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
            public World World;          // hosting: the world started from the menu; null for a remembered server world
            public string Key;           // remembered server world: the server, and what it must send to use the build
            public string Name;
            public long Uid;
            public byte[] SettingsBytes;
            public int Seed;
            public object Settings;
            public object Pipeline;
            public Task Task;
        }

        private static Pending _pending;
        private static World _generating; // the world whose Pregenerate is running
        private static Pending _lastGuess; // the remembered-world build for the last join, until a world load decides on it

        // Riverheim's own settings serializer (ZPackageExtensions), the one it sends worlds to clients with
        private static MethodInfo _writeSettings, _readSettings;
        private static string _joinsFile;
        private const int MaxRemembered = 20;

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

            // Joining a server: optional, the hosting part works without it.
            var ext = _riverheim.GetType("Riverheim.Plugin.ZPackageExtensions", false);
            var settingsType = _riverheim.GetType("Riverheim.Configuration.WorldSettings", false);
            _writeSettings = ext == null || settingsType == null ? null : AccessTools.DeclaredMethod(ext, "Write", new[] { typeof(ZPackage), settingsType });
            _readSettings = ext == null ? null : AccessTools.DeclaredMethod(ext, "ReadWorldSettings", new[] { typeof(ZPackage) });
            var hosts = new[]
            {
                AccessTools.DeclaredMethod(typeof(ZNet), nameof(ZNet.SetServerHost), new[] { typeof(string) }),
                AccessTools.DeclaredMethod(typeof(ZNet), nameof(ZNet.SetServerHost), new[] { typeof(ulong) }),
                AccessTools.DeclaredMethod(typeof(ZNet), nameof(ZNet.SetServerHost), new[] { typeof(string), typeof(int), typeof(OnlineBackendType) }),
            };
            if (_writeSettings == null || _readSettings == null || _readSettings.ReturnType != settingsType || Array.IndexOf(hosts, null) >= 0)
            {
                _log.LogInfo("Riverheim: early build when joining a server is off (Riverheim's or the game's join code has changed); hosting is unaffected.");
                return null;
            }
            _joinsFile = Path.Combine(Path.Combine(Paths.CachePath, "ValPerfPatcher"), "riverheim-server-worlds.txt");
            foreach (var m in hosts)
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(RiverheimLoadFix), nameof(SetServerHostPostfix)));
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
                StartBuild(new Pending { World = world, Seed = world.m_seed, Settings = settings }, world.m_name);
            }
            catch (Exception e)
            {
                _pending = null;
                _log.LogWarning($"Riverheim: early world build not started ({e.GetType().Name}: {e.Message}); Riverheim builds it at the usual time.");
            }
        }

        private static void StartBuild(Pending p, string worldName)
        {
            var config = _getConfig(p.Settings);
            var pipeline = _buildPipeline();
            if (config == null || pipeline == null) return;
            int seed = p.Seed;
            p.Pipeline = pipeline;
            p.Task = Task.Run(() =>
            {
                try { _compute.Invoke(pipeline, new object[] { seed, config }); }
                catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
            });
            _pending = p;
            _log.LogInfo($"Riverheim: started building world '{worldName}' early, while the game loads.");
        }

        // Join pressed (the server's address is set): start the world this server had last time.
        private static void SetServerHostPostfix()
        {
            try
            {
                if (ZNet.instance != null) return; // only at Join, before the game scene exists
                string key = ZNet.GetServerString();
                if (_pending != null && _pending.World == null) return; // already building for this join
                // The game retries a failed connection by joining again: keep the build already running for it.
                var last = _lastGuess;
                if (last != null && last.Key == key && !last.Task.IsFaulted)
                {
                    _pending = last;
                    return;
                }
                foreach (var r in ReadRemembered())
                {
                    if (r[0] != key) continue;
                    var bytes = Convert.FromBase64String(r[4]);
                    var settings = _readSettings.Invoke(null, new object[] { new ZPackage(bytes) });
                    var p = new Pending
                    {
                        Key = key, Name = r[1], Seed = int.Parse(r[2], CultureInfo.InvariantCulture), Uid = long.Parse(r[3], CultureInfo.InvariantCulture),
                        SettingsBytes = bytes, Settings = settings,
                    };
                    StartBuild(p, r[1]);
                    if (_pending == p) _lastGuess = p;
                    return;
                }
            }
            catch (Exception e)
            {
                _pending = null;
                _log.LogWarning($"Riverheim: early world build for this server not started ({e.GetType().Name}: {e.Message}); Riverheim builds it at the usual time.");
            }
        }

        private static byte[] SettingsBytes(object settings)
        {
            var pkg = new ZPackage();
            _writeSettings.Invoke(null, new[] { pkg, settings });
            return pkg.GetArray();
        }

        // Lines: server \t world name \t seed \t world uid \t Riverheim settings (base64); newest first.
        private static List<string[]> ReadRemembered()
        {
            var list = new List<string[]>();
            if (_joinsFile == null || !File.Exists(_joinsFile)) return list;
            foreach (var line in File.ReadAllLines(_joinsFile))
            {
                var r = line.Split('\t');
                if (r.Length == 5) list.Add(r);
            }
            return list;
        }

        // A joined server's world is being built: remember it for the next join to that server.
        private static void Remember(World world)
        {
            if (_joinsFile == null || ZNet.instance == null || ZNet.instance.IsServer()) return;
            try
            {
                if (world == null || world.m_seed == 0 || !_isCustom(world)) return;
                var settings = _getSettings(world);
                if (settings == null) return;
                string key = ZNet.GetServerString();
                var row = new[] { key, world.m_name, world.m_seed.ToString(CultureInfo.InvariantCulture), world.m_uid.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(SettingsBytes(settings)) };
                var list = ReadRemembered();
                if (list.Count > 0 && string.Join("\t", list[0]) == string.Join("\t", row)) return;
                list.RemoveAll(r => r[0] == key);
                list.Insert(0, row);
                if (list.Count > MaxRemembered) list.RemoveRange(MaxRemembered, list.Count - MaxRemembered);
                Directory.CreateDirectory(Path.GetDirectoryName(_joinsFile));
                var lines = new List<string>();
                foreach (var r in list) lines.Add(string.Join("\t", r));
                File.WriteAllLines(_joinsFile, lines.ToArray());
            }
            catch (Exception e)
            {
                _log.LogWarning($"Riverheim: could not remember this server's world ({e.GetType().Name}: {e.Message}).");
            }
        }

        private static void PregeneratePrefix(WorldGenerator __instance)
        {
            _generating = __instance.m_world;
            Remember(_generating);
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
            _lastGuess = null; // used or not, a build is never handed out twice
            try
            {
                bool sameWorld = p.World != null
                    ? ReferenceEquals(p.World, world)
                    : p.Name == world.m_name && p.Uid == world.m_uid;
                if (!sameWorld || p.Seed != world.m_seed || seed != p.Seed)
                {
                    _log.LogInfo("Riverheim: the early build was for another world; building normally.");
                    return true;
                }
                var settings = _getSettings(world);
                bool sameSettings = p.World != null
                    ? ReferenceEquals(settings, p.Settings) || Equals(settings, p.Settings)
                    : settings != null && Convert.ToBase64String(SettingsBytes(settings)) == Convert.ToBase64String(p.SettingsBytes);
                if (!sameSettings)
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
    }
}
