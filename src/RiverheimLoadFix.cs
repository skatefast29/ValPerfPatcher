using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace ValPerfPatcher
{
    /// <summary>
    /// World loading with a Worldhalla world generator (Riverheim 1.3 and later): head start. Worldhalla builds the
    /// whole world map (Riverheim: about 5.5 s) in WorldGenerator.Pregenerate, when the game sets up the network, after
    /// a deliberate 1.5 s pause and the scene load. This starts the same build (Worldhalla's own WorldGenerationTask)
    /// the moment a world is started from the menu (ZNet.SetServer). Worldhalla still runs its own Pregenerate; when it
    /// starts its build for the same world, generator, seed and settings, it is handed the early one instead, and
    /// waits for it the way it waits for its own. Joining a server: the world only arrives from the server after the
    /// scene has loaded, so the build for the world last played on that server (remembered in BepInEx\cache) starts
    /// when the server address is set at Join. If the server sends another world, generator, seed or settings, the
    /// early build is dropped and Worldhalla builds as usual. The biome map that follows is filled by
    /// BiomeMapParallelFix, which uses the lookups found here.
    /// </summary>
    internal static class RiverheimLoadFix
    {
        public const string TargetGuid = "dev.gurebu.riverheim.stable";
        public const string WorldhallaGuid = "dev.gurebu.worldhalla";
        internal const string MonitorPrefix = "valheimmods.ValPerfMonitor"; // its timing probes ignore other threads

        private static ManualLogSource _log;

        // ---- Worldhalla, found by reflection -----------------------------------------------------

        internal static Assembly _worldhalla;
        internal static Func<WorldGenerator, object> _getState;  // WorldGeneratorExtension.GetState: the world's lookups, or null
        internal static Type _stateType;                         // WorldStateWrapper
        private static Func<World, object> _getBinding;          // WorldExtension.GetGenerator
        private static Type _compatibleBinding;                  // CompatibleGeneratorBinding
        private static PropertyInfo _bindingGenerator, _bindingSettings, _generatorGuid, _completion;
        private static MethodInfo _findByGuid, _loadSettings, _saveSettings, _startTask, _cancelTask;

        internal static bool Resolve()
        {
            if (_worldhalla != null) return _getState != null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                if (asm.GetName().Name == "Worldhalla") { _worldhalla = asm; break; }
            if (_worldhalla == null) return false;
            try
            {
                const BindingFlags PS = BindingFlags.Public | BindingFlags.Static;
                const BindingFlags PI = BindingFlags.Public | BindingFlags.Instance;
                var genExt = _worldhalla.GetType("Worldhalla.Plugin.Core.Extensions.WorldGeneratorExtension", false);
                var state = _worldhalla.GetType("Worldhalla.Plugin.Core.WorldStateWrapper", false);
                var getState = genExt?.GetMethod("GetState", PS, null, new[] { typeof(WorldGenerator) }, null);
                if (getState == null || state == null || getState.ReturnType != state) return false;
                _stateType = state;
                _getState = g => getState.Invoke(null, new object[] { g });

                // Only needed by the head start; missing pieces just turn that part off.
                var worldExt = _worldhalla.GetType("Worldhalla.Plugin.Core.Extensions.WorldExtension", false);
                var binding = _worldhalla.GetType("Worldhalla.Plugin.Core.GeneratorBinding", false);
                var compatible = _worldhalla.GetType("Worldhalla.Plugin.Core.CompatibleGeneratorBinding", false);
                var registered = _worldhalla.GetType("Worldhalla.Plugin.Core.RegisteredGenerator", false);
                var registry = _worldhalla.GetType("Worldhalla.Plugin.Core.Registry", false);
                var settings = _worldhalla.GetType("Worldhalla.API.Settings.ISettings", false);
                var task = _worldhalla.GetType("Worldhalla.Plugin.Core.WorldGenerationTask", false);
                if (worldExt == null || binding == null || compatible == null || registered == null || registry == null || settings == null || task == null)
                    return true;
                var getBinding = worldExt.GetMethod("GetGenerator", PS, null, new[] { typeof(World) }, null);
                var genProp = compatible.GetProperty("Generator", PI);
                var setProp = compatible.GetProperty("GeneratorSettings", PI);
                var guidProp = registered.GetProperty("Guid", PI);
                var findByGuid = registry.GetMethod("FindByGuid", PS, null, new[] { typeof(string), registered.MakeByRefType() }, null);
                var loadSettings = registered.GetMethod("LoadSettings", PI, null, new[] { typeof(byte[]) }, null);
                var save = settings.GetMethod("SaveToBinaryBlob", Type.EmptyTypes);
                var start = task.GetMethod("Start", PS, null, new[] { registered, typeof(int), settings }, null);
                var completion = task.GetProperty("Completion", PI);
                var cancel = task.GetMethod("Cancel", PI, null, Type.EmptyTypes, null);
                if (getBinding?.ReturnType != binding || genProp?.PropertyType != registered || setProp?.PropertyType != settings ||
                    guidProp?.PropertyType != typeof(string) || findByGuid?.ReturnType != typeof(bool) || loadSettings?.ReturnType != settings ||
                    save?.ReturnType != typeof(byte[]) || start?.ReturnType != task || completion == null ||
                    !typeof(Task).IsAssignableFrom(completion.PropertyType) || cancel == null)
                    return true;
                _getBinding = w => getBinding.Invoke(null, new object[] { w });
                _compatibleBinding = compatible;
                _bindingGenerator = genProp;
                _bindingSettings = setProp;
                _generatorGuid = guidProp;
                _findByGuid = findByGuid;
                _loadSettings = loadSettings;
                _saveSettings = save;
                _startTask = start;
                _completion = completion;
                _cancelTask = cancel;
                return true;
            }
            catch (Exception e)
            {
                _log?.LogDebug($"Worldhalla: lookup failed: {e.Message}");
                _getState = null;
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
            public int Seed;
            public object Generator;     // Worldhalla's RegisteredGenerator
            public object Settings;      // the generator's ISettings (may be null)
            public byte[] SettingsBytes;
            public object Task;          // Worldhalla's WorldGenerationTask
            public System.Diagnostics.Stopwatch Started;
        }

        private static Pending _pending;
        private static World _generating;  // the world whose Pregenerate is running
        private static Pending _lastGuess; // the remembered-world build for the last join, until a world load decides on it
        [ThreadStatic] private static bool _ownStart; // this fix is calling WorldGenerationTask.Start itself

        private static string _joinsFile;
        private const int MaxRemembered = 20;

        public static string ApplyHeadStart(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            if (!Resolve()) return "Worldhalla's code has changed since this fix was written";
            if (_startTask == null) return "Worldhalla's world builder has changed since this fix was written";
            var setServer = AccessTools.Method(typeof(ZNet), nameof(ZNet.SetServer),
                new[] { typeof(bool), typeof(bool), typeof(bool), typeof(string), typeof(string), typeof(World) });
            var pregenerate = AccessTools.DeclaredMethod(typeof(WorldGenerator), "Pregenerate", Type.EmptyTypes);
            if (setServer == null || pregenerate == null)
                return "the game's world start code has changed since this fix was written";
            if (!HasPrefix(pregenerate, WorldhallaGuid))
                return "Worldhalla no longer builds its worlds in WorldGenerator.Pregenerate";

            harmony.Patch(setServer, postfix: new HarmonyMethod(typeof(RiverheimLoadFix), nameof(SetServerPostfix)));
            harmony.Patch(pregenerate,
                prefix: new HarmonyMethod(typeof(RiverheimLoadFix), nameof(PregeneratePrefix)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(RiverheimLoadFix), nameof(PregenerateFinalizer)));
            harmony.Patch(_startTask, prefix: new HarmonyMethod(typeof(RiverheimLoadFix), nameof(StartTaskPrefix)));

            // Joining a server: optional, the hosting part works without it.
            var hosts = new[]
            {
                AccessTools.DeclaredMethod(typeof(ZNet), nameof(ZNet.SetServerHost), new[] { typeof(string) }),
                AccessTools.DeclaredMethod(typeof(ZNet), nameof(ZNet.SetServerHost), new[] { typeof(ulong) }),
                AccessTools.DeclaredMethod(typeof(ZNet), nameof(ZNet.SetServerHost), new[] { typeof(string), typeof(int), typeof(OnlineBackendType) }),
            };
            if (Array.IndexOf(hosts, null) >= 0)
            {
                _log.LogInfo("Worldhalla: early build when joining a server is off (the game's join code has changed); hosting is unaffected.");
                return null;
            }
            _joinsFile = Path.Combine(Path.Combine(Paths.CachePath, "ValPerfPatcher"), "worldhalla-server-worlds.txt");
            foreach (var m in hosts)
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(RiverheimLoadFix), nameof(SetServerHostPostfix)));
            return null;
        }

        internal static bool HasPrefix(MethodBase method, string owner)
        {
            var info = Harmony.GetPatchInfo(method);
            if (info == null) return false;
            foreach (var p in info.Prefixes)
                if (p.owner == owner) return true;
            return false;
        }

        private static byte[] Bytes(object settings) =>
            settings == null ? Array.Empty<byte>() : (byte[])_saveSettings.Invoke(settings, null) ?? Array.Empty<byte>();

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static void Drop(Pending p)
        {
            if (p?.Task == null) return;
            try { _cancelTask.Invoke(p.Task, null); } catch { /* it only stops wasted work */ }
        }

        private static Task Completion(Pending p) => (Task)_completion.GetValue(p.Task, null);

        // A world is being started as host from the menu: start Worldhalla's build now, the way Pregenerate would.
        private static void SetServerPostfix(bool server, World world)
        {
            try
            {
                var old = _pending;
                _pending = null; // a new start replaces any earlier one (e.g. the player went back to the menu)
                if (old != null && old != _lastGuess) Drop(old);
                if (!server || world == null || world.m_menu || world.m_seed == 0) return;
                var binding = _getBinding(world);
                if (binding == null || !_compatibleBinding.IsInstanceOfType(binding)) return; // a vanilla world
                var generator = _bindingGenerator.GetValue(binding, null);
                if (generator == null) return;
                var settings = _bindingSettings.GetValue(binding, null);
                StartBuild(new Pending { World = world, Seed = world.m_seed, Generator = generator, Settings = settings, SettingsBytes = Bytes(settings) }, world.m_name);
            }
            catch (Exception e)
            {
                _pending = null;
                _log.LogWarning($"Worldhalla: early world build not started ({e.GetType().Name}: {e.Message}); it is built at the usual time.");
            }
        }

        private static void StartBuild(Pending p, string worldName)
        {
            _ownStart = true;
            try { p.Task = _startTask.Invoke(null, new[] { p.Generator, p.Seed, p.Settings }); }
            catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
            finally { _ownStart = false; }
            if (p.Task == null) return;
            p.Started = System.Diagnostics.Stopwatch.StartNew();
            _pending = p;
            _log.LogInfo($"Worldhalla: started building world '{worldName}' ({_generatorGuid.GetValue(p.Generator, null)}) early, while the game loads.");
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
                if (last != null && last.Key == key && !Completion(last).IsFaulted)
                {
                    _pending = last;
                    return;
                }
                foreach (var r in ReadRemembered())
                {
                    if (r[0] != key) continue;
                    var args = new object[] { r[4], null };
                    if (!(bool)_findByGuid.Invoke(null, args)) return; // that generator is not installed any more
                    var bytes = Convert.FromBase64String(r[5]);
                    var p = new Pending
                    {
                        Key = key, Name = r[1], Seed = int.Parse(r[2], CultureInfo.InvariantCulture), Uid = long.Parse(r[3], CultureInfo.InvariantCulture),
                        Generator = args[1], SettingsBytes = bytes, Settings = _loadSettings.Invoke(args[1], new object[] { bytes }),
                    };
                    StartBuild(p, r[1]);
                    if (_pending == p)
                    {
                        if (last != null) Drop(last);
                        _lastGuess = p;
                    }
                    return;
                }
            }
            catch (Exception e)
            {
                _pending = null;
                var inner = (e as TargetInvocationException)?.InnerException ?? e;
                _log.LogWarning($"Worldhalla: early world build for this server not started ({inner.GetType().Name}: {inner.Message}); it is built at the usual time.");
            }
        }

        // Lines: server \t world name \t seed \t world uid \t generator guid \t settings (base64); newest first.
        private static List<string[]> ReadRemembered()
        {
            var list = new List<string[]>();
            if (_joinsFile == null || !File.Exists(_joinsFile)) return list;
            foreach (var line in File.ReadAllLines(_joinsFile))
            {
                var r = line.Split('\t');
                if (r.Length == 6) list.Add(r);
            }
            return list;
        }

        // A joined server's world is being built: remember it for the next join to that server.
        private static void Remember(World world)
        {
            if (_joinsFile == null || ZNet.instance == null || ZNet.instance.IsServer()) return;
            try
            {
                if (world == null || world.m_seed == 0) return;
                var binding = _getBinding(world);
                if (binding == null || !_compatibleBinding.IsInstanceOfType(binding)) return;
                var generator = _bindingGenerator.GetValue(binding, null);
                if (generator == null) return;
                string key = ZNet.GetServerString();
                var row = new[]
                {
                    key, world.m_name, world.m_seed.ToString(CultureInfo.InvariantCulture), world.m_uid.ToString(CultureInfo.InvariantCulture),
                    (string)_generatorGuid.GetValue(generator, null), Convert.ToBase64String(Bytes(_bindingSettings.GetValue(binding, null))),
                };
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
                _log.LogWarning($"Worldhalla: could not remember this server's world ({e.GetType().Name}: {e.Message}).");
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
            var p = _pending;
            _pending = null;
            if (p != null) // never asked for: this world needed no build (e.g. a vanilla world on a remembered server)
            {
                if (p == _lastGuess) _lastGuess = null;
                Drop(p);
            }
            return __exception;
        }

        // Worldhalla's Pregenerate starts its build: if the early one is for the same world, generator, seed and
        // settings, hand it that one instead (Worldhalla then waits for it, answering pings as usual).
        private static bool StartTaskPrefix(object generator, int seed, object settings, ref object __result)
        {
            if (_ownStart) return true; // the early build itself
            var p = _pending;
            var world = _generating;
            if (p == null || world == null) return true; // not a world load (e.g. Worldhalla's world preview)
            _pending = null;
            _lastGuess = null; // used or not, a build is never handed out twice
            try
            {
                bool sameWorld = p.World != null
                    ? ReferenceEquals(p.World, world)
                    : p.Name == world.m_name && p.Uid == world.m_uid;
                if (!sameWorld || !ReferenceEquals(p.Generator, generator) || p.Seed != world.m_seed || seed != p.Seed)
                {
                    _log.LogInfo("Worldhalla: the early build was for another world; building normally.");
                    Drop(p);
                    return true;
                }
                if (!ReferenceEquals(settings, p.Settings) && !SameBytes(Bytes(settings), p.SettingsBytes))
                {
                    _log.LogInfo("Worldhalla: world settings changed after the early build started; building normally.");
                    Drop(p);
                    return true;
                }
                var done = Completion(p);
                if (done.IsFaulted || done.IsCanceled)
                {
                    var inner = done.Exception?.InnerException;
                    _log.LogWarning($"Worldhalla: early build failed ({(inner == null ? "cancelled" : inner.GetType().Name + ": " + inner.Message)}); building normally.");
                    return true;
                }
                __result = p.Task;
                if (done.IsCompleted)
                    _log.LogInfo($"Worldhalla: early build used (it was already finished, {p.Started.Elapsed.TotalSeconds:0.0} s after it started).");
                else
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var log = _log;
                    done.ContinueWith(t => log.LogInfo(t.Status == TaskStatus.RanToCompletion
                        ? $"Worldhalla: early build used (the world load waited {sw.Elapsed.TotalSeconds:0.0} s for the rest)."
                        : "Worldhalla: early build failed after it was handed over; the world generator reports the error."));
                }
                return false;
            }
            catch (Exception e)
            {
                var inner = (e as TargetInvocationException)?.InnerException ?? e;
                _log.LogWarning($"Worldhalla: early build not used ({inner.GetType().Name}: {inner.Message}); building normally.");
                Drop(p);
                return true;
            }
        }
    }
}
