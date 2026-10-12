using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValPerfPatcher
{
    /// <summary>
    /// The part of the patcher that has to be in place before other mods start. BepInEx starts mods in order of
    /// their ID (after the ones they depend on), and the main plugin depends on its target mods, so it starts
    /// near the end. Mods load the game's translations while they start (Creature Level &amp; Loot Control and
    /// Comfort Tweaks: 8 full loads, about 3 s), so the translation fix lives here, under an ID that sorts first.
    /// Its switches stay in the main config file (read here, written by the main plugin).
    /// </summary>
    [BepInPlugin(Guid, Name, Plugin.Version)]
    public sealed class EarlyPlugin : BaseUnityPlugin
    {
        public const string Guid = "__valheimmods.ValPerfPatcher.Early";
        public const string Name = "ValPerfPatcher (early start)";

        public const string VanillaFix = "Base game: read the translation table once";
        public const string LocalizerFix = "LocalizationManager mods: reuse loaded text";
        public const string BundleFix = "Mod asset bundles: keep a fast-loading copy";

        // What happened to each fix, for the main plugin to report (null = applied).
        internal static string VanillaResult = "not started", LocalizerResult = "not started", BundleResult = "not started";

        private void Awake()
        {
            var harmony = new Harmony(Guid);
            var config = ReadOnlyMainConfig();
            VanillaResult = Run(config, VanillaFix, () => TranslationLoadFix.ApplyVanilla(harmony));
            LocalizerResult = Run(config, LocalizerFix, () => TranslationLoadFix.ApplyLocalizationManagers(harmony, Logger));
            BundleResult = Run(config, BundleFix, () => BundleCacheFix.Apply(harmony, Logger));
        }

        private string Run(ConfigFile config, string name, Func<string> apply)
        {
            if (!config.Bind("Fixes", name, true).Value) return "off in config";
            try { return apply(); }
            catch (Exception e)
            {
                Logger.LogWarning($"{name}: {e.GetType().Name}: {e.Message}");
                return $"{e.GetType().Name}: {e.Message}";
            }
        }

        /// <summary>The main plugin's settings file, only read: nothing here ever writes it.</summary>
        private static ConfigFile ReadOnlyMainConfig()
        {
            var config = new ConfigFile(Path.Combine(Paths.ConfigPath, Plugin.Guid + ".cfg"), false);
            config.SaveOnConfigSet = false;
            return config;
        }
    }
}
