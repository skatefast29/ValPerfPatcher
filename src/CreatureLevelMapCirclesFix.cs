using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Creature Level &amp; Loot Control can draw its "distance from spawn" level circles on the map. Its postfix on
    /// Minimap.GenerateWorldMap keeps a copy of the map, forest and height textures' pixels (three 64 MB Color[] for
    /// the whole session) so a later redraw can start from a clean map, and redraws at once: three more copies, three
    /// texture uploads. It does all that on every world load even when the circles are off ("Second factor" other than
    /// Distance, or the circles toggled off), when the redraw just writes back the same pixels.
    /// This makes the copies only when the circles are shown: the postfix drops the old copies and goes straight to the
    /// redraw, and the redraw takes the copies first if it has none, or does nothing at all while the circles are off.
    /// Nothing has been drawn on the map before the first time the circles are on, so the copies taken then are the
    /// same clean map the mod would have kept.
    /// </summary>
    internal static class CreatureLevelMapCirclesFix
    {
        private const string Circles = "CreatureLevelControl.WorldLevelMinimapCircles";

        private static FieldInfo _mapColors, _forestColors, _heightColors;
        private static ConfigEntryBase _factor, _show;
        private static Action<Minimap> _draw;
        private static AccessTools.FieldRef<Minimap, Texture2D> _forestTexture, _heightTexture;

        public static string Apply(Harmony harmony)
        {
            var circles = Plugin.ModType(CreatureLevelSectorTextFix.TargetGuid, Circles);
            var main = Plugin.ModType(CreatureLevelSectorTextFix.TargetGuid, "CreatureLevelControl.CreatureLevelControl");
            var inner = circles == null ? null : AccessTools.Inner(circles, "PatchMinimapCircles");
            var postfix = inner == null ? null : AccessTools.DeclaredMethod(inner, "Postfix", new[] { typeof(Minimap) });
            var draw = circles == null ? null : AccessTools.DeclaredMethod(circles, "DrawDistanceLevelCircles", new[] { typeof(Minimap) });
            _mapColors = circles == null ? null : AccessTools.DeclaredField(circles, "originalMapColors");
            _forestColors = circles == null ? null : AccessTools.DeclaredField(circles, "originalForestColors");
            _heightColors = circles == null ? null : AccessTools.DeclaredField(circles, "originalHeightColors");
            var factor = main == null ? null : AccessTools.DeclaredField(main, "difficultySecondFactor");
            var show = main == null ? null : AccessTools.DeclaredField(main, "displayDistanceFromSpawnCircles");
            var forest = AccessTools.DeclaredField(typeof(Minimap), "m_forestMaskTexture");
            var height = AccessTools.DeclaredField(typeof(Minimap), "m_heightTexture");
            if (postfix == null || draw == null || !draw.IsStatic || factor == null || show == null ||
                forest == null || forest.FieldType != typeof(Texture2D) || height == null || height.FieldType != typeof(Texture2D) ||
                new[] { _mapColors, _forestColors, _heightColors }.Any(f => f == null || !f.IsStatic || f.FieldType != typeof(Color[])))
                return "Creature Level & Loot Control's map circles have changed since this fix was written";
            _factor = factor.GetValue(null) as ConfigEntryBase;
            _show = show.GetValue(null) as ConfigEntryBase;
            if (_factor == null || _show == null || !Enum.IsDefined(_factor.SettingType, "Distance") || !Enum.IsDefined(_show.SettingType, "On"))
                return "Creature Level & Loot Control's map circle settings have changed since this fix was written";

            // The redraw must still decide on exactly these two settings, and the postfix must still copy the three
            // textures and then redraw.
            try
            {
                var drawCode = PatchProcessor.GetOriginalInstructions(draw);
                var postfixCode = PatchProcessor.GetOriginalInstructions(postfix);
                if (!drawCode.Any(c => c.LoadsField(factor)) || !drawCode.Any(c => c.LoadsField(show)) ||
                    !postfixCode.Any(c => c.Calls(draw)) ||
                    new[] { _mapColors, _forestColors, _heightColors }.Any(f => !postfixCode.Any(c => c.StoresField(f))))
                    return "Creature Level & Loot Control's map circles have changed since this fix was written";
            }
            catch (Exception e) { return $"could not read Creature Level & Loot Control's map circles ({e.Message})"; }

            _draw = AccessTools.MethodDelegate<Action<Minimap>>(draw);
            _forestTexture = AccessTools.FieldRefAccess<Minimap, Texture2D>(forest);
            _heightTexture = AccessTools.FieldRefAccess<Minimap, Texture2D>(height);
            harmony.Patch(postfix, prefix: new HarmonyMethod(typeof(CreatureLevelMapCirclesFix), nameof(PostfixPrefix)));
            harmony.Patch(draw, prefix: new HarmonyMethod(typeof(CreatureLevelMapCirclesFix), nameof(DrawPrefix)));
            return null;
        }

        private static bool Shown() => _factor.BoxedValue?.ToString() == "Distance" && _show.BoxedValue?.ToString() == "On";

        // Their GenerateWorldMap postfix (static, so its Minimap parameter is __0, not __instance): a new world map,
        // so the copies of the last one are stale. The redraw takes new ones if the circles are shown.
        private static bool PostfixPrefix(Minimap __0)
        {
            _mapColors.SetValue(null, null);
            _forestColors.SetValue(null, null);
            _heightColors.SetValue(null, null);
            _draw(__0);
            return false;
        }

        private static bool DrawPrefix(Minimap __0)
        {
            if (_mapColors.GetValue(null) != null)
                return true;
            if (__0 == null || !Shown())
                return false; // nothing drawn and nothing to draw: the map is already clean
            _mapColors.SetValue(null, __0.m_mapTexture.GetPixels());
            _forestColors.SetValue(null, _forestTexture(__0).GetPixels());
            _heightColors.SetValue(null, _heightTexture(__0).GetPixels());
            return true;
        }
    }
}
