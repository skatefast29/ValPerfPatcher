using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace ValPerfPatcher
{
    /// <summary>
    /// A check run by hand (debug/call_method ValPerfPatcher.SaveTiming Measure [3]): times each step of the game's
    /// character save. Patches the steps with timers, runs Game.SavePlayerProfile the given number of times (each a real
    /// save of the current character, the game's way), removes the timers and returns the milliseconds per step.
    /// Times include the steps called inside them. Not applied at startup.
    /// </summary>
    public static class SaveTiming
    {
        public const string HarmonyId = "valheimmods.ValPerfPatcher.SaveTiming";

        private sealed class Stat
        {
            public int Calls;
            public long Ticks;
        }

        private static readonly Dictionary<MethodBase, Stat> _stats = new Dictionary<MethodBase, Stat>();

        // (type, method); ".ctor" for constructors. Every declared overload is timed.
        private static readonly string[][] Steps =
        {
            new[] { "Game", "SavePlayerProfile" },
            new[] { "Game", "UpdateSaving" },
            new[] { "ZNet", "EnoughDiskSpaceAvailable" },
            new[] { "ZNet", "Save" },
            new[] { "SystemResourceManager", "PushIsInLoadingScreen" },
            new[] { "SystemResourceManager", "PopIsInLoadingScreen" },
            new[] { "FileHelpers", "Mount" },
            new[] { "FileHelpers", "Unmount" },
            new[] { "PlayerProfile", "SavePlayerData" },
            new[] { "Player", "Save" },
            new[] { "Inventory", "Save" },
            new[] { "Skills", "Save" },
            new[] { "Minimap", "SaveMapData" },
            new[] { "Minimap", "GetMapData" },
            new[] { "PlayerProfile", "SaveLogoutPoint" },
            new[] { "FileHelpers", "OperationExceedsCloudCapacity" },
            new[] { "PlayerProfile", "Save" },
            new[] { "PlayerProfile", "SavePlayerToDisk" },
            new[] { "Hud", "ProfileSaveStarted" },
            new[] { "Hud", "ProfileSaveFinished" },
            new[] { "SaveSystem", "PreSaveCloudChecksAndOperations" },
            new[] { "SaveSystem", "CheckMove" },
            new[] { "SaveCollection", "Reload" },
            new[] { "ZPackage", "GenerateHash" },
            new[] { "ZPackage", "GetArray" },
            new[] { "FileWriter", ".ctor" },
            new[] { "FileWriter", "Finish" },
            new[] { "FileHelpers", "ReplaceOldFile" },
            new[] { "ZNet", "ConsiderAutoBackup" },
            new[] { "SaveSystem", "ConsiderBackup" },
        };

        private static Harmony _harmony;
        private static List<string> _missing;
        private static bool _syncTimed;

        public static string Measure(int times = 3)
        {
            if (Game.instance == null || Player.m_localPlayer == null || Minimap.instance == null)
                return "not in a world";
            times = Math.Max(1, Math.Min(times, 10));
            try
            {
                Patch();
                var sb = new StringBuilder();
                for (int run = 1; run <= times; run++)
                {
                    _stats.Clear();
                    long before = GC.GetTotalMemory(false);
                    var clock = Stopwatch.StartNew();
                    Game.instance.SavePlayerProfile(false);
                    double ms = clock.Elapsed.TotalMilliseconds;
                    double mb = (GC.GetTotalMemory(false) - before) / 1048576.0;
                    sb.Append($"save {run}: {ms:F1} ms, about {mb:F1} MB allocated |");
                    AppendStats(sb);
                    sb.AppendLine();
                }
                AppendNotes(sb);
                return sb.ToString();
            }
            catch (Exception e)
            {
                return "failed: " + e;
            }
            finally
            {
                Unpatch();
            }
        }

        /// <summary>
        /// Leaves the timers on, for saves the game makes itself (the 30-minute timer, the server's save message).
        /// Read and remove them with End.
        /// </summary>
        public static string Begin()
        {
            try
            {
                Patch();
                return "timing character saves; read with End";
            }
            catch (Exception e)
            {
                Unpatch();
                return "failed: " + e;
            }
        }

        public static string End()
        {
            if (_harmony == null) return "not timing (call Begin first)";
            var sb = new StringBuilder("since Begin:");
            AppendStats(sb);
            sb.AppendLine();
            AppendNotes(sb);
            Unpatch();
            return sb.ToString();
        }

        private static void Patch()
        {
            Unpatch();
            _harmony = new Harmony(HarmonyId);
            var prefix = new HarmonyMethod(typeof(SaveTiming), nameof(Start)) { priority = Priority.First };
            var postfix = new HarmonyMethod(typeof(SaveTiming), nameof(Stop)) { priority = Priority.Last };
            _missing = new List<string>();
            foreach (var step in Steps)
            {
                // The game's own types first: a mod may have its own Inventory or Player.
                var type = typeof(Game).Assembly.GetType(step[0]) ?? typeof(FileHelpers).Assembly.GetType(step[0]);
                var methods = type == null ? new List<MethodBase>() :
                    step[1] == ".ctor"
                        ? AccessTools.GetDeclaredConstructors(type).Where(c => !c.IsStatic).Cast<MethodBase>().ToList()
                        : AccessTools.GetDeclaredMethods(type).Where(m => m.Name == step[1] && !m.IsAbstract).Cast<MethodBase>().ToList();
                if (methods.Count == 0) _missing.Add(step[0] + "." + step[1]);
                foreach (var m in methods)
                    _harmony.Patch(m, prefix, postfix);
            }
            // MapSaveFix runs the regular saves from Game.Update once the map is packed.
            _harmony.Patch(AccessTools.Method(typeof(MapSaveFix), "UpdatePostfix"), prefix, postfix);
            var sync = AchievementSynchronize();
            _syncTimed = sync != null;
            if (sync != null) _harmony.Patch(sync, prefix, postfix);
        }

        private static void Unpatch()
        {
            _harmony?.UnpatchSelf();
            _harmony = null;
            _stats.Clear();
        }

        private static void AppendStats(StringBuilder sb)
        {
            foreach (var kv in _stats.OrderByDescending(kv => kv.Value.Ticks))
            {
                double ms = kv.Value.Ticks * 1000.0 / Stopwatch.Frequency;
                if (ms < 0.05) continue;
                sb.Append($" {kv.Key.DeclaringType?.Name}.{kv.Key.Name} {ms:F1}");
                if (kv.Value.Calls > 1) sb.Append($" (x{kv.Value.Calls})");
                sb.Append(';');
            }
        }

        private static void AppendNotes(StringBuilder sb)
        {
            if (_missing != null && _missing.Count > 0) sb.AppendLine("not found: " + string.Join(", ", _missing));
            if (!_syncTimed) sb.AppendLine("achievement sync not timed (no platform achievement manager)");
        }

        // PlatformManager.DistributionPlatform.AchievementManager.Synchronize(), as Game.SavePlayerProfile calls it.
        private static MethodInfo AchievementSynchronize()
        {
            try
            {
                var platformManager = AccessTools.TypeByName("Splatform.PlatformManager") ?? AccessTools.TypeByName("PlatformManager");
                var platform = platformManager == null ? null : AccessTools.Property(platformManager, "DistributionPlatform")?.GetValue(null);
                var achievements = platform == null ? null : AccessTools.Property(platform.GetType(), "AchievementManager")?.GetValue(platform);
                var m = achievements == null ? null : AccessTools.Method(achievements.GetType(), "Synchronize", Type.EmptyTypes);
                return m != null && !m.IsAbstract && m.GetMethodBody() != null ? m : null;
            }
            catch
            {
                return null;
            }
        }

        private static void Start(out long __state) => __state = Stopwatch.GetTimestamp();

        private static void Stop(long __state, MethodBase __originalMethod)
        {
            long ticks = Stopwatch.GetTimestamp() - __state;
            if (!_stats.TryGetValue(__originalMethod, out var s)) _stats[__originalMethod] = s = new Stat();
            s.Calls++;
            s.Ticks += ticks;
        }
    }
}
