using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// The map draws its icons in the order they were created, so another player's icon (made when that player
    /// joins) ends up underneath any icon created later: pins that scroll into view, location pins, mod pins.
    /// After every Minimap.UpdatePins this moves the other players' icons to the top of the pin layer. While
    /// TargetPortal's portal-picking map is open, its portal icons go above the players so they stay clickable.
    /// Only the drawing order changes; it is checked first and only changed when something is out of place.
    /// BossGatedPortals 1.1.1+ also lifts TargetPortal's portal icons to the top while its portal map is open
    /// (same postfix, same pins). Both end with the portal icons last, so whichever runs second finds them in place.
    /// </summary>
    internal static class PlayerPinsOnTopFix
    {
        public const string TargetPortalGuid = "org.bepinex.plugins.targetportal";

        private static AccessTools.FieldRef<Minimap, List<Minimap.PinData>> _playerPins;
        private static AccessTools.FieldRef<Minimap, List<Minimap.PinData>> _pins;

        // TargetPortal.Map.Teleporting (true while its portal map is open) and its portal pin type.
        private static FieldInfo _portalMapOpen;
        private static FieldInfo _portalPinType;
        private static bool _targetPortalLooked;

        private static readonly List<Transform> Top = new List<Transform>();

        private static BepInEx.Logging.ManualLogSource _log;

        public static string Apply(Harmony harmony, BepInEx.Logging.ManualLogSource log)
        {
            _log = log;
            var playerPins = AccessTools.DeclaredField(typeof(Minimap), "m_playerPins");
            var pins = AccessTools.DeclaredField(typeof(Minimap), "m_pins");
            var update = AccessTools.DeclaredMethod(typeof(Minimap), "UpdatePins", System.Type.EmptyTypes);
            if (playerPins == null || playerPins.FieldType != typeof(List<Minimap.PinData>) ||
                pins == null || pins.FieldType != typeof(List<Minimap.PinData>) || update == null)
                return "the game's map pins have changed since this fix was written";
            _playerPins = AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>(playerPins);
            _pins = AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>(pins);
            harmony.Patch(update, postfix: new HarmonyMethod(typeof(PlayerPinsOnTopFix), nameof(Postfix)) { priority = Priority.Last });
            return null;
        }

        // TargetPortal may load after this plugin, so look it up on first use.
        private static void FindTargetPortal()
        {
            _targetPortalLooked = true;
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(TargetPortalGuid)) return;
            var map = AccessTools.TypeByName("TargetPortal.Map");
            var icon = AccessTools.TypeByName("TargetPortal.Map+AddMinimapPortalIcon");
            var open = map == null ? null : AccessTools.DeclaredField(map, "Teleporting");
            var type = icon == null ? null : AccessTools.DeclaredField(icon, "pinType");
            if (open == null || !open.IsStatic || open.FieldType != typeof(bool) ||
                type == null || !type.IsStatic || type.FieldType != typeof(int))
            {
                _log?.LogWarning("Map: other players on top: TargetPortal has changed since this fix was written; " +
                                       "players are kept on top of its portal icons too.");
                return;
            }
            _portalMapOpen = open;
            _portalPinType = type;
        }

        private static void Postfix(Minimap __instance)
        {
            if (!_targetPortalLooked) FindTargetPortal();
            var root = __instance.m_mode == Minimap.MapMode.Large ? __instance.m_pinRootLarge : __instance.m_pinRootSmall;
            if (root == null) return;

            Top.Clear();
            Add(_playerPins(__instance), root, null);
            if (_portalMapOpen != null && (bool)_portalMapOpen.GetValue(null))
                Add(_pins(__instance), root, (Minimap.PinType)(int)_portalPinType.GetValue(null));
            if (Top.Count == 0) return;

            // Already the last children, in this order? Then leave the layout alone.
            int first = root.childCount - Top.Count;
            bool inPlace = first >= 0;
            for (int i = 0; inPlace && i < Top.Count; i++)
                inPlace = Top[i].GetSiblingIndex() == first + i;
            if (!inPlace)
                foreach (var t in Top) t.SetAsLastSibling();
            Top.Clear();
        }

        private static void Add(List<Minimap.PinData> pins, Transform root, Minimap.PinType? only)
        {
            if (pins == null) return;
            foreach (var pin in pins)
            {
                if (only.HasValue && pin.m_type != only.Value) continue;
                var ui = pin.m_uiElement;
                if (ui != null && ui.parent == root) Top.Add(ui);
            }
        }
    }
}
