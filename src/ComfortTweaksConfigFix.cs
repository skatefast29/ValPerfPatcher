using System;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValPerfPatcher
{
    /// <summary>
    /// Comfort Tweaks, on the first world load of a session: its ZNetScene.Awake postfix (FetchPieces) creates a
    /// config setting for every comfort piece and every fire (about 260 with this mod list). BepInEx saves the
    /// whole config file after each new setting (ConfigFile.Bind with SaveOnConfigSet), so the 52 KB file is
    /// written about 260 times: about 1.5 s. This holds the saves while that postfix runs and saves once at the
    /// end. The file ends up the same; later world loads find the settings already there and were cheap anyway.
    /// </summary>
    internal static class ComfortTweaksConfigFix
    {
        public const string TargetGuid = "xyz.alcan.comfortcalc";

        private static ConfigFile _config;

        public static string Apply(Harmony harmony)
        {
            if (!Chainloader.PluginInfos.TryGetValue(TargetGuid, out var info) || info.Instance == null)
                return "Comfort Tweaks isn't loaded";
            var type = info.Instance.GetType().GetNestedType("FetchPieces", AccessTools.all);
            var postfix = type == null ? null : AccessTools.DeclaredMethod(type, "Postfix", new[] { typeof(ZNetScene) });
            if (postfix == null || !postfix.IsStatic)
                return "Comfort Tweaks' piece setup has changed since this fix was written";
            _config = info.Instance.Config;
            if (_config == null) return "Comfort Tweaks has no config file";
            harmony.Patch(postfix,
                prefix: new HarmonyMethod(typeof(ComfortTweaksConfigFix), nameof(Prefix)),
                finalizer: new HarmonyMethod(typeof(ComfortTweaksConfigFix), nameof(Finalizer)));
            return null;
        }

        // __state: the number of settings before the call, or -1 when saves were already off (left alone).
        private static void Prefix(out int __state)
        {
            __state = -1;
            if (!_config.SaveOnConfigSet) return;
            __state = _config.Count;
            _config.SaveOnConfigSet = false;
        }

        private static Exception Finalizer(Exception __exception, int __state)
        {
            if (__state >= 0)
            {
                _config.SaveOnConfigSet = true;
                if (_config.Count != __state) _config.Save(); // the one save the held ones add up to
            }
            return __exception;
        }
    }
}
