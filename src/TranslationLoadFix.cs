using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Translations are loaded over and over during startup: Localization.SetupLanguage ran 18 times,
    /// 8.5 s in total, because mods create their own English Localization objects and every call re-runs
    /// everything hooked to it. Applied by <see cref="EarlyPlugin"/>, which loads before every other mod,
    /// so the loads made while other mods start up are covered too. Two parts:
    ///
    /// 1. Valheim itself (base game): SetupLanguage reads the game's 13 translation CSVs with LoadCSV, which
    ///    splits the whole file (every language column) character by character each time. The words one
    ///    file gives for one language are kept after the first read; a later LoadCSV of the same file and
    ///    language adds those words instead of reading the file again. LoadCSV still "runs" for everyone
    ///    else: mods' hooks on it (ItemManager, CreatureManager, SkillManager and some LocalizationManager
    ///    copies add their words there) and on SetupLanguage run exactly as before.
    ///
    /// 2. LocalizationManager, a helper bundled into many mods (Warfare, Monstrum, HearthBelow, AzuEPI...):
    ///    each copy hooks SetupLanguage or LoadCSV and, on every call, searches the whole BepInEx folder for
    ///    its translation files and parses them with a new YAML reader. The words it ends up with for a
    ///    language are already in its own cache (loadedTexts). When that cache already has the language, this
    ///    does only the cheap part: remember the Localization and add the cached words to it. Copies are
    ///    picked up as their mods start, just before each SetupLanguage.
    /// </summary>
    internal static class TranslationLoadFix
    {
        private static ManualLogSource _log;
        private static Harmony _harmony;

        // ---- Part 1: the game's CSV ------------------------------------------------------------

        private static AccessTools.FieldRef<Localization, Dictionary<string, string>> _translations;
        private static MethodInfo _loadCsv;
        // Words per (translation file, language), exactly as LoadCSV added them. Never changed once stored.
        private static readonly Dictionary<KeyValuePair<int, string>, Dictionary<string, string>> FileWords =
            new Dictionary<KeyValuePair<int, string>, Dictionary<string, string>>();

        public static string ApplyVanilla(Harmony harmony)
        {
            var loadCsv = AccessTools.Method(typeof(Localization), "LoadCSV", new[] { typeof(TextAsset), typeof(string) });
            var add = AccessTools.Method(typeof(Localization), "AddWord", new[] { typeof(string), typeof(string) });
            var field = AccessTools.Field(typeof(Localization), "m_translations");
            if (loadCsv == null || loadCsv.ReturnType != typeof(bool) || add == null || field?.FieldType != typeof(Dictionary<string, string>))
                return "Localization.LoadCSV has changed since this fix was written";
            if (Harmony.GetPatchInfo(add) != null)
                return "another mod hooks Localization.AddWord, so the words must keep going through it";
            _translations = AccessTools.FieldRefAccess<Localization, Dictionary<string, string>>("m_translations");
            _loadCsv = loadCsv;
            harmony.Patch(loadCsv,
                // First, so a stored file is added before anything else looks at it; first among the postfixes,
                // so the words are back in place before mods' own LoadCSV hooks add theirs.
                prefix: new HarmonyMethod(typeof(TranslationLoadFix), nameof(CsvPrefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(TranslationLoadFix), nameof(CsvPostfix)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(TranslationLoadFix), nameof(CsvFinalizer)));
            return null;
        }

        // __state = the Localization's own word list while the original reads into an empty one; null otherwise.
        private static bool CsvPrefix(Localization __instance, TextAsset file, string language, ref bool __result,
            out Dictionary<string, string> __state)
        {
            __state = null;
            if (__instance == null || file == null || language == null) return true;
            var words = _translations(__instance);
            if (words == null) return true;
            if (FileWords.TryGetValue(new KeyValuePair<int, string>(file.GetInstanceID(), language), out var cached))
            {
                // What the original does with each word of the file: add it, replacing an existing entry.
                foreach (var kv in cached) words[kv.Key] = kv.Value;
                __result = true;
                return false;
            }
            // First read of this file and language: let the original fill an empty list, so its words alone can
            // be kept. Only when nothing else hooks in before the original, which would see the swapped list.
            var info = Harmony.GetPatchInfo(_loadCsv);
            if (info != null && info.Prefixes.Count > 1) return true;
            __state = words;
            _translations(__instance) = new Dictionary<string, string>();
            return true;
        }

        private static void CsvPostfix(Localization __instance, TextAsset file, string language, bool __result,
            Dictionary<string, string> __state)
        {
            if (__state == null) return;
            var fileWords = Restore(__instance, __state);
            if (__result && fileWords != null)
                FileWords[new KeyValuePair<int, string>(file.GetInstanceID(), language)] = fileWords;
        }

        // If the original threw, the postfix didn't run: put the Localization's own list back all the same.
        private static void CsvFinalizer(Localization __instance, Exception __exception, Dictionary<string, string> __state)
        {
            if (__exception != null && __state != null && __instance != null && !ReferenceEquals(_translations(__instance), __state))
                Restore(__instance, __state);
        }

        /// <summary>Puts the Localization's own word list back with the file's words added, as the original would have.</summary>
        private static Dictionary<string, string> Restore(Localization localization, Dictionary<string, string> own)
        {
            var fileWords = _translations(localization);
            _translations(localization) = own;
            if (fileWords == null || ReferenceEquals(fileWords, own)) return null;
            foreach (var kv in fileWords) own[kv.Key] = kv.Value;
            return fileWords;
        }

        // ---- Part 2: every bundled LocalizationManager -----------------------------------------

        private sealed class Copy
        {
            public FieldInfo LoadedTexts, Language, Objects;
            public Action<Localization, string> UpdatePlaceholderText;
            public bool Broken;
        }

        private static readonly Dictionary<Type, Copy> Copies = new Dictionary<Type, Copy>();
        private static readonly HashSet<MethodBase> Seen = new HashSet<MethodBase>();
        private static MethodInfo _setup;
        public static int CopiesPatched, CopiesSkipped;

        public static string ApplyLocalizationManagers(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            _harmony = harmony;
            _setup = AccessTools.Method(typeof(Localization), nameof(Localization.SetupLanguage), new[] { typeof(string) });
            if (_setup == null || _setup.ReturnType != typeof(bool))
                return "Localization.SetupLanguage has changed since this fix was written";
            if (_loadCsv == null)
                _loadCsv = AccessTools.Method(typeof(Localization), "LoadCSV", new[] { typeof(TextAsset), typeof(string) });
            // Copies register their hook when their mod starts; look for new ones before every translation load.
            harmony.Patch(_setup, prefix: new HarmonyMethod(typeof(TranslationLoadFix), nameof(SetupPrefix)) { priority = Priority.First });
            PatchNewCopies();
            return null;
        }

        private static void SetupPrefix() => PatchNewCopies();

        /// <summary>
        /// Patches the copies whose mod has switched them on, i.e. hooked them into SetupLanguage or LoadCSV.
        /// Patching a copy that was never started starts it (its static setup runs and hooks itself in), and a
        /// copy a mod ships unused may have no translations at all: it then throws on every load and breaks the
        /// menu. So a copy is only touched once its own hook is there.
        /// </summary>
        public static void PatchNewCopies()
        {
            foreach (var target in new[] { _setup, _loadCsv })
            {
                if (target == null) continue;
                var info = Harmony.GetPatchInfo(target);
                if (info == null) continue;
                foreach (var patch in info.Postfixes)
                {
                    var load = patch.PatchMethod;
                    if (load == null || !Seen.Add(load)) continue;
                    var type = load.DeclaringType;
                    if (type == null || type.FullName != "LocalizationManager.Localizer" || load.Name != "LoadLocalization") continue;
                    var parameters = load.GetParameters();
                    if (parameters.Length != 2 || parameters[0].ParameterType != typeof(Localization) || parameters[1].ParameterType != typeof(string))
                    { CopiesSkipped++; continue; }
                    var copy = Inspect(type);
                    if (copy == null) { CopiesSkipped++; continue; }
                    try
                    {
                        _harmony.Patch(load, prefix: new HarmonyMethod(typeof(TranslationLoadFix), nameof(LocalizerPrefix)));
                        Copies[type] = copy;
                        CopiesPatched++;
                    }
                    catch (Exception e)
                    {
                        CopiesSkipped++;
                        _log?.LogDebug($"LocalizationManager in {type.Assembly.GetName().Name} not patched: {e.Message}");
                    }
                }
            }
        }

        /// <summary>Checks this copy has the fields and helper this fix relies on, with the expected types.</summary>
        private static Copy Inspect(Type t)
        {
            const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            var loaded = t.GetField("loadedTexts", S);
            var lang = t.GetField("localizationLanguage", S);
            var objs = t.GetField("localizationObjects", S);
            var update = t.GetMethod("UpdatePlaceholderText", S, null, new[] { typeof(Localization), typeof(string) }, null);
            if (loaded?.FieldType != typeof(Dictionary<string, Dictionary<string, string>>) ||
                lang?.FieldType != typeof(ConditionalWeakTable<Localization, string>) ||
                objs?.FieldType != typeof(List<WeakReference<Localization>>) ||
                update == null || update.ReturnType != typeof(void))
                return null;
            try
            {
                return new Copy
                {
                    LoadedTexts = loaded, Language = lang, Objects = objs,
                    UpdatePlaceholderText = (Action<Localization, string>)Delegate.CreateDelegate(typeof(Action<Localization, string>), update),
                };
            }
            catch { return null; }
        }

        // LoadLocalization is static and names its first parameter "__instance", which Harmony reserves (it passes
        // null for a static method), so both arguments are taken by position.
        private static bool LocalizerPrefix(MethodBase __originalMethod, Localization __0, string __1)
        {
            var localization = __0;
            var language = __1;
            if (localization == null || language == null) return true;
            if (!Copies.TryGetValue(__originalMethod.DeclaringType, out var c) || c.Broken) return true;
            try
            {
                var loaded = (Dictionary<string, Dictionary<string, string>>)c.LoadedTexts.GetValue(null);
                if (loaded == null || !loaded.TryGetValue(language, out var words) || words == null)
                    return true; // first load of this language for this mod: the full original
                var languages = (ConditionalWeakTable<Localization, string>)c.Language.GetValue(null);
                var objects = (List<WeakReference<Localization>>)c.Objects.GetValue(null);
                if (languages == null || objects == null) return true;
                // The same bookkeeping the original does, then the same words, minus the file search and parse.
                if (!languages.Remove(localization))
                    objects.Add(new WeakReference<Localization>(localization));
                languages.Add(localization, language);
                foreach (var key in words.Keys.ToList())
                    c.UpdatePlaceholderText(localization, key);
                return false;
            }
            catch (Exception e)
            {
                c.Broken = true; // never try this copy again; the original runs from now on
                _log?.LogWarning($"Translations: a LocalizationManager copy ({__originalMethod.DeclaringType.Assembly.GetName().Name}) behaved unexpectedly, left alone from now on: {e.Message}");
                return true;
            }
        }
    }
}
