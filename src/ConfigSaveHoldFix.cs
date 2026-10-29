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
    /// 9 s startup. Some mods do the same on a world load (Comfort Tweaks 3.3.11: a setting per comfort piece and
    /// fire from its ZNetScene.Awake postfix, about 260 saves of a 52 KB file, 1.5 s).
    /// This holds ConfigFile.Save in two windows and remembers which files wanted saving, then saves each of those
    /// once: while mods start (until the main menu has started), and while a world loads (from ZNet.Awake until the
    /// local player has spawned, back at the main menu, or MaxWorldHoldSeconds at most). Save writes the whole file
    /// from what is in memory, so the one save gives the same file the many would have. Saves made while playing
    /// go through at once as before. A Reload of a held file writes it first, so it reads back what it holds.
    /// Server-synced settings: the world-load hold also ends when the game connects to a server (ZNet.OnNewConnection),
    /// before any server value can arrive, so a held save never writes a value a mod got from the server. (ServerSync,
    /// Jotunn and Conditional Config Sync would be safe anyway: they write the local values when a file is saved while
    /// connected; a mod with its own sync might not.) Most world-load settings are added while the scene loads, before
    /// that connection.
    /// A dedicated server (batch mode) has no player to wait for, so there only the
    /// startup window applies, and it ends when the world starts loading at the latest.
    /// </summary>
    internal static class ConfigSaveHoldFix
    {
        private const float MaxWorldHoldSeconds = 180f;
        private static readonly object Lock = new object();
        private static readonly HashSet<ConfigFile> Held = new HashSet<ConfigFile>();
        private static bool _holding, _flushing, _worldLoad, _holdWorldLoads;
        private static DateTime _since;
        [ThreadStatic] private static bool _writeNow;
        private static ManualLogSource _log;
        private static int _savesHeld, _filesWritten;

        public static string Apply(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            var save = AccessTools.DeclaredMethod(typeof(ConfigFile), nameof(ConfigFile.Save), Type.EmptyTypes);
            var menuStart = AccessTools.DeclaredMethod(typeof(FejdStartup), "Start");
            var netAwake = AccessTools.DeclaredMethod(typeof(ZNet), "Awake");
            var spawned = AccessTools.DeclaredMethod(typeof(Player), nameof(Player.OnSpawned), new[] { typeof(bool) });
            var connection = AccessTools.DeclaredMethod(typeof(ZNet), "OnNewConnection", new[] { typeof(ZNetPeer) });
            if (save == null || menuStart == null)
                return "BepInEx's config saving or the game's main menu has changed since this fix was written";
            Hold(false);
            harmony.Patch(save, prefix: new HarmonyMethod(typeof(ConfigSaveHoldFix), nameof(SavePrefix)) { priority = Priority.First });
            var reload = AccessTools.DeclaredMethod(typeof(ConfigFile), nameof(ConfigFile.Reload), Type.EmptyTypes);
            if (reload != null)
                harmony.Patch(reload, prefix: new HarmonyMethod(typeof(ConfigSaveHoldFix), nameof(ReloadPrefix)) { priority = Priority.First });
            harmony.Patch(menuStart, postfix: new HarmonyMethod(typeof(ConfigSaveHoldFix), nameof(Flush)));
            UnityEngine.Application.quitting += Flush;
            if (netAwake != null)
                harmony.Patch(netAwake, prefix: new HarmonyMethod(typeof(ConfigSaveHoldFix), nameof(WorldLoadStart)) { priority = Priority.First });
            if (netAwake == null || spawned == null || connection == null)
                log.LogWarning("Mod config saves: the game's world loading has changed since this fix was written; saves are only held while mods start.");
            else if (!UnityEngine.Application.isBatchMode)
            {
                _holdWorldLoads = true;
                harmony.Patch(spawned, postfix: new HarmonyMethod(typeof(ConfigSaveHoldFix), nameof(Flush)) { priority = Priority.Last });
                harmony.Patch(connection, prefix: new HarmonyMethod(typeof(ConfigSaveHoldFix), nameof(Flush)) { priority = Priority.First });
            }
            return null;
        }

        private static void Hold(bool worldLoad)
        {
            lock (Lock)
            {
                if (_holding) return;
                _holding = true;
                _worldLoad = worldLoad;
                _since = DateTime.UtcNow;
                _savesHeld = _filesWritten = 0;
            }
        }

        // A world starts loading: the startup hold ends here at the latest (a dedicated server may never start the
        // main menu), then on a game with a player the world-load hold begins.
        private static void WorldLoadStart()
        {
            Flush();
            if (_holdWorldLoads) Hold(true);
        }

        // A mod re-reading its file while a save of it is held would get the old file back over its new values:
        // write that file first, as it would have been.
        private static void ReloadPrefix(ConfigFile __instance)
        {
            lock (Lock)
            {
                if (!Held.Remove(__instance)) return;
            }
            _writeNow = true;
            try { __instance.Save(); _filesWritten++; }
            catch (Exception e) { _log?.LogWarning($"Mod config saves: could not write {__instance.ConfigFilePath}: {e.Message}"); }
            finally { _writeNow = false; }
        }

        private static bool SavePrefix(ConfigFile __instance)
        {
            bool overdue;
            lock (Lock)
            {
                if (!_holding || _flushing || _writeNow) return true;
                overdue = _worldLoad && (DateTime.UtcNow - _since).TotalSeconds > MaxWorldHoldSeconds;
                if (!overdue)
                {
                    Held.Add(__instance);
                    _savesHeld++;
                    return false;
                }
            }
            // A world load that never reached the player (stuck, or something unexpected): stop holding.
            Flush();
            return true;
        }

        /// <summary>The main menu has started, the player has spawned or the game is quitting: stop holding and save each held file once.</summary>
        private static void Flush()
        {
            List<ConfigFile> files;
            bool worldLoad;
            lock (Lock)
            {
                if (!_holding) return;
                _holding = false;
                _flushing = true;
                worldLoad = _worldLoad;
                files = new List<ConfigFile>(Held);
                Held.Clear();
            }
            try
            {
                foreach (var file in files)
                {
                    try { file.Save(); _filesWritten++; }
                    catch (Exception e) { _log?.LogWarning($"Mod config saves: could not write {file.ConfigFilePath}: {e.Message}"); }
                }
            }
            finally
            {
                lock (Lock) _flushing = false;
            }
            // A world load where no mod added settings (the usual case after the first) needs no line.
            if (!worldLoad || _savesHeld > 0)
                _log?.LogInfo($"Mod config saves: {_savesHeld} saves while {(worldLoad ? "the world loaded" : "mods started")}, written as {_filesWritten} file saves.");
        }
    }
}
