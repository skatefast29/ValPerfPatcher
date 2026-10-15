using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace ValPerfPatcher
{
    /// <summary>
    /// BepInEx. A mod whose config has SaveOnConfigSet on (the default) gets its whole config file rewritten after
    /// every new setting it binds. A mod that binds hundreds of settings while it starts writes its file hundreds
    /// of times: RtD Ocean 2.3.1 binds about five per recipe, food and creature into a 127 KB file, 8.5 s of its
    /// 9 s startup. While mods start (until the main menu has started), this holds ConfigFile.Save and remembers
    /// which files wanted saving; then each of those is saved once. Save writes the whole file from what is in
    /// memory, so the one save gives the same file the many would have. Saves made later, while playing, go
    /// through at once as before.
    /// </summary>
    internal static class ConfigSaveHoldFix
    {
        private static readonly object Lock = new object();
        private static readonly HashSet<ConfigFile> Held = new HashSet<ConfigFile>();
        private static bool _holding, _flushing;
        private static ManualLogSource _log;
        internal static int SavesHeld, FilesWritten;

        public static string Apply(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            var save = AccessTools.DeclaredMethod(typeof(ConfigFile), nameof(ConfigFile.Save), Type.EmptyTypes);
            var menuStart = AccessTools.DeclaredMethod(typeof(FejdStartup), "Start");
            if (save == null || menuStart == null)
                return "BepInEx's config saving or the game's main menu has changed since this fix was written";
            _holding = true;
            harmony.Patch(save, prefix: new HarmonyMethod(typeof(ConfigSaveHoldFix), nameof(SavePrefix)) { priority = Priority.First });
            harmony.Patch(menuStart, postfix: new HarmonyMethod(typeof(ConfigSaveHoldFix), nameof(Flush)));
            UnityEngine.Application.quitting += Flush;
            return null;
        }

        private static bool SavePrefix(ConfigFile __instance)
        {
            lock (Lock)
            {
                if (!_holding || _flushing) return true;
                Held.Add(__instance);
                SavesHeld++;
                return false;
            }
        }

        /// <summary>The main menu has started (or the game is quitting): stop holding and save each held file once.</summary>
        private static void Flush()
        {
            List<ConfigFile> files;
            lock (Lock)
            {
                if (!_holding) return;
                _holding = false;
                _flushing = true;
                files = new List<ConfigFile>(Held);
                Held.Clear();
            }
            try
            {
                foreach (var file in files)
                {
                    try { file.Save(); FilesWritten++; }
                    catch (Exception e) { _log?.LogWarning($"Mod config saves: could not write {file.ConfigFilePath}: {e.Message}"); }
                }
            }
            finally
            {
                lock (Lock) _flushing = false;
            }
            _log?.LogInfo($"Mod config saves: {SavesHeld} saves while mods started, written as {FilesWritten} file saves.");
        }
    }
}
