using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Creature Level &amp; Loot Control shows the area's creature level on the minimap. Its postfix on
    /// Minimap.Update rebuilds that text on every frame: a new dictionary, a text search-and-replace, a
    /// number-to-text conversion and a copy of the level thresholds (about 0.6 KB of garbage per frame), although
    /// the level only changes when you cross into another area or enough creatures die there. This lets the
    /// rebuild run at most a few times per second. The first update after the text is created (or recreated)
    /// always runs right away; afterwards the shown level can lag by up to the interval.
    /// </summary>
    internal static class CreatureLevelSectorTextFix
    {
        public const string TargetGuid = "org.bepinex.plugins.creaturelevelcontrol";
        public static float IntervalSeconds = 0.25f;
        private static FieldInfo _text;
        private static object _lastText;
        private static float _next;

        public static string Apply(Harmony harmony)
        {
            var outer = AccessTools.TypeByName("CreatureLevelControl.CreatureSector");
            var inner = outer == null ? null : AccessTools.Inner(outer, "PatchMinimapSectorUpdate");
            var postfix = inner == null ? null : AccessTools.Method(inner, "Postfix", System.Type.EmptyTypes);
            _text = outer == null ? null : AccessTools.Field(outer, "minimapSectorText");
            if (postfix == null || postfix.ReturnType != typeof(void) || _text == null || !_text.IsStatic)
                return "Creature Level & Loot Control's minimap level code has changed since this fix was written";
            harmony.Patch(postfix, prefix: new HarmonyMethod(typeof(CreatureLevelSectorTextFix), nameof(Prefix)));
            return null;
        }

        private static bool Prefix()
        {
            if (IntervalSeconds <= 0f)
                return true;
            object text = _text.GetValue(null);
            float now = Time.unscaledTime;
            if (text != null && ReferenceEquals(text, _lastText) && now < _next)
                return false; // same text object, refreshed moments ago: skip this frame's rebuild
            _lastText = text;
            _next = now + IntervalSeconds;
            return true;
        }
    }
}
