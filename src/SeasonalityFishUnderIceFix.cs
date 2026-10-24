using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Seasonality's frozen water (FrozenZones) puts a solid collider on each area's water surface in winter so players
    /// can walk on it. It moves a fish 2 m down when the fish wakes (its Fish.Awake patch), but fish keep jumping
    /// (Fish.ConsiderJump / Jump) and keep picking swim points just under the surface (Fish.RandomizeWaypoint uses
    /// m_minDepth, often a fraction of a metre). A fish that jumps lands on top of the ice; the game then thinks it is
    /// out of the water and leaves it flopping there.
    /// While the water is frozen (the same rule FrozenZones uses: season Winter, from SeasonTime on 3.8.5 and later,
    /// and 'Water Freezes' on) this:
    ///  - stops fish jumping;
    ///  - keeps their swim points at least IceClearance below the ice (never below the sea floor);
    ///  - moves a wild fish found lying on the ice back under it (only the fish this game owns, never a hooked fish or
    ///    one a player has picked up and dropped, and only where the water under it is deep enough).
    /// Sea creatures (characters that swim and don't avoid water: serpents, sharks, dolphins, whales... from the game or
    /// any mod) swim at Character.GetLiquidLevel() - m_swimDepth, so their bodies stick up through the ice and the
    /// collider pushes them on top. While frozen this:
    ///  - lowers the water level each such creature is given (Character.SetLiquidLevel from an area's WaterVolume
    ///    whose ice is on) just enough that its whole body swims below the ice, so it still swims normally, only deeper;
    ///  - moves one found standing on the ice back under it (owned by this game, not tamed or ridden, deep enough).
    /// When the water is not frozen nothing changes.
    /// </summary>
    internal static class SeasonalityFishUnderIceFix
    {
        // How far below the ice surface fish stay, in metres (plus the fish's own m_height).
        private const float IceClearance = 1f;
        // Gap between the top of a sea creature's body and the ice, in metres.
        private const float CreatureClearance = 0.3f;

        private static bool _frozen;
        private static ConfigEntryBase _season, _freezes;
        private static System.Reflection.FieldInfo _currentSeason; // Seasonality 3.8.5+: SeasonTime.m_currentSeason
        private static int _waterMask;

        // Set in Apply, so a renamed field makes this fix stand down there instead of failing when the class loads.
        private static AccessTools.FieldRef<Fish, ZNetView> _fishNView;
        private static AccessTools.FieldRef<Fish, Rigidbody> _fishBody;
        private static AccessTools.FieldRef<Fish, Vector3> _waypoint;
        private static AccessTools.FieldRef<Fish, bool> _haveWaypoint;
        private static AccessTools.FieldRef<Fish, bool> _isJumping;
        private static AccessTools.FieldRef<Fish, ItemDrop> _fishItem;
        private static AccessTools.FieldRef<Character, ZNetView> _charNView;
        private static AccessTools.FieldRef<Character, Rigidbody> _charBody;
        private static AccessTools.FieldRef<Character, CapsuleCollider> _charCollider;
        private static AccessTools.FieldRef<Character, Collider> _lastGround;
        private static AccessTools.FieldRef<Character, float> _waterLevel;
        private static AccessTools.FieldRef<Character, float> _tarLevel;
        private static AccessTools.FieldRef<Character, float> _liquidLevel;
        private static AccessTools.FieldRef<Character, GameObject[]> _waterFx;

        public static string Apply(Harmony harmony)
        {
            var configs = Plugin.ModType(SeasonalWaterCacheFix.SeasonalityGuid, "Seasonality.Configs");
            _season = Plugin.FieldOrNull(configs, "m_season")?.GetValue(null) as ConfigEntryBase;
            _freezes = Plugin.FieldOrNull(configs, "m_waterFreezes")?.GetValue(null) as ConfigEntryBase;
            if (_season == null || _freezes == null || !_season.SettingType.IsEnum || !_freezes.SettingType.IsEnum ||
                !Enum.IsDefined(_season.SettingType, "Winter") || !Enum.IsDefined(_freezes.SettingType, "On"))
                return "Seasonality's season or frozen water settings have changed since this fix was written";
            // From 3.8.5 the season in play is SeasonTime.m_currentSeason (FrozenZones reads it there); a client takes
            // it from the server's season sync before its own setting catches up. Older versions only have the setting.
            var seasonTime = Plugin.ModType(SeasonalWaterCacheFix.SeasonalityGuid, "Seasonality.SeasonTime");
            var current = Plugin.FieldOrNull(seasonTime, "m_currentSeason");
            var onChange = seasonTime == null ? null : AccessTools.DeclaredMethod(
                Plugin.ModType(SeasonalWaterCacheFix.SeasonalityGuid, "Seasonality.SeasonalityPlugin"), "OnSeasonChange", Type.EmptyTypes);
            if (seasonTime != null && (current == null || !current.IsStatic || current.FieldType != _season.SettingType || onChange == null || !onChange.IsStatic))
                return "Seasonality's season timer has changed since this fix was written";
            _currentSeason = current;
            var problem = FrozenSurfaces.Ensure(harmony);
            if (problem != null) return problem;

            var consider = AccessTools.DeclaredMethod(typeof(Fish), "ConsiderJump", new[] { typeof(float) });
            var randomize = AccessTools.DeclaredMethod(typeof(Fish), "RandomizeWaypoint", new[] { typeof(bool), typeof(float) });
            var update = AccessTools.DeclaredMethod(typeof(Fish), nameof(Fish.CustomFixedUpdate), new[] { typeof(float) });
            if (consider == null || randomize == null || randomize.ReturnType != typeof(bool) || update == null)
                return "the game's fish code has changed since this fix was written";
            var setLevel = AccessTools.DeclaredMethod(typeof(Character), nameof(Character.SetLiquidLevel), new[] { typeof(float), typeof(LiquidType), typeof(Component) });
            var charUpdate = AccessTools.DeclaredMethod(typeof(Character), nameof(Character.CustomFixedUpdate), new[] { typeof(float) });
            var effects = AccessTools.DeclaredMethod(typeof(Character), "UpdateContinousEffects", Type.EmptyTypes);
            if (setLevel == null || charUpdate == null || effects == null)
                return "the game's swimming code has changed since this fix was written";

            try
            {
                _fishNView = AccessTools.FieldRefAccess<Fish, ZNetView>("m_nview");
                _fishBody = AccessTools.FieldRefAccess<Fish, Rigidbody>("m_body");
                _waypoint = AccessTools.FieldRefAccess<Fish, Vector3>("m_waypoint");
                _haveWaypoint = AccessTools.FieldRefAccess<Fish, bool>("m_haveWaypoint");
                _isJumping = AccessTools.FieldRefAccess<Fish, bool>("m_isJumping");
                _fishItem = AccessTools.FieldRefAccess<Fish, ItemDrop>("m_itemDrop");
                _charNView = AccessTools.FieldRefAccess<Character, ZNetView>("m_nview");
                _charBody = AccessTools.FieldRefAccess<Character, Rigidbody>("m_body");
                _charCollider = AccessTools.FieldRefAccess<Character, CapsuleCollider>("m_collider");
                _lastGround = AccessTools.FieldRefAccess<Character, Collider>("m_lastGroundCollider");
                _waterLevel = AccessTools.FieldRefAccess<Character, float>("m_waterLevel");
                _tarLevel = AccessTools.FieldRefAccess<Character, float>("m_tarLevel");
                _liquidLevel = AccessTools.FieldRefAccess<Character, float>("m_liquidLevel");
                _waterFx = AccessTools.FieldRefAccess<Character, GameObject[]>("m_waterEffects_instances");
            }
            catch (Exception)
            {
                _fishNView = null;
            }
            if (_fishNView == null || _fishBody == null || _waypoint == null || _haveWaypoint == null || _isJumping == null ||
                _fishItem == null || _charNView == null || _charBody == null || _charCollider == null || _lastGround == null ||
                _waterLevel == null || _tarLevel == null || _liquidLevel == null || _waterFx == null)
                return "the game's fish or swimming fields have changed since this fix was written";

            _waterMask = LayerMask.GetMask("Water");
            // ConfigEntry<T>.SettingChanged needs Seasonality's enum types; the file's event covers both settings.
            _season.ConfigFile.SettingChanged += Refresh;
            if (_freezes.ConfigFile != _season.ConfigFile) _freezes.ConfigFile.SettingChanged += Refresh;
            Refresh(null, null);
            // A finalizer, so the new season is picked up even if one of Seasonality's season-change steps throws.
            if (onChange != null)
                harmony.Patch(onChange, finalizer: new HarmonyMethod(typeof(SeasonalityFishUnderIceFix), nameof(SeasonChanged)));

            harmony.Patch(consider, prefix: new HarmonyMethod(typeof(SeasonalityFishUnderIceFix), nameof(NoJumpWhileFrozen)));
            harmony.Patch(randomize, postfix: new HarmonyMethod(typeof(SeasonalityFishUnderIceFix), nameof(WaypointUnderIce)));
            harmony.Patch(update, postfix: new HarmonyMethod(typeof(SeasonalityFishUnderIceFix), nameof(OffTheIce)));
            harmony.Patch(setLevel, postfix: new HarmonyMethod(typeof(SeasonalityFishUnderIceFix), nameof(SwimUnderIce)));
            harmony.Patch(charUpdate, postfix: new HarmonyMethod(typeof(SeasonalityFishUnderIceFix), nameof(CreatureOffTheIce)));
            harmony.Patch(effects, postfix: new HarmonyMethod(typeof(SeasonalityFishUnderIceFix), nameof(NoRipplesOnIce)));
            return null;
        }

        // Prefab names the creature part leaves alone (amphibious ones that belong on the shore and the ice).
        private static HashSet<string> LeftAlone = new HashSet<string>();

        // What IsSeaCreature found per creature, so the name lookup (it makes a string) runs once per creature.
        // Top: how far the creature reaches above its pivot. The visible body counts, not only the collider: a
        // shark's CapsuleCollider ends well below its dorsal fin, which then shows above the ice.
        private sealed class Kind { public bool Sea; public float Top; }
        private static ConditionalWeakTable<Character, Kind> Swimmers = new ConditionalWeakTable<Character, Kind>();

        public static void SetLeftAlone(string list)
        {
            LeftAlone = new HashSet<string>((list ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
            Swimmers = new ConditionalWeakTable<Character, Kind>();
        }

        private static bool IsSeaCreature(Character c)
        {
            if (!Swimmers.TryGetValue(c, out var kind))
            {
                var ai = c.GetBaseAI();
                kind = new Kind
                {
                    Sea = c.m_canSwim && !c.m_flying && !c.IsPlayer() && ai != null && !ai.m_avoidWater &&
                          !LeftAlone.Contains(Utils.GetPrefabName(c.gameObject.name))
                };
                if (kind.Sea) kind.Top = MeasureTop(c);
                Swimmers.Add(c, kind);
            }
            return kind.Sea && !c.IsTamed() && !c.HaveRider();
        }

        // Highest point of the creature's collider and visible body above its pivot, measured once (capped at 15 m).
        private static float MeasureTop(Character c)
        {
            float pivot = c.transform.position.y;
            var col = _charCollider(c);
            float top = col != null ? col.bounds.max.y - pivot : 2f;
            foreach (var r in c.GetComponentsInChildren<Renderer>())
                if (r is SkinnedMeshRenderer || r is MeshRenderer)
                    top = Mathf.Max(top, r.bounds.max.y - pivot);
            return Mathf.Clamp(top, 0f, 15f);
        }

        // Pivot height at which the creature's body top sits CreatureClearance under the ice (after IsSeaCreature).
        private static float PivotUnderIce(Character c, float ice) =>
            ice - (Swimmers.TryGetValue(c, out var kind) ? kind.Top : 2f) - CreatureClearance;

        private static void SwimUnderIce(Character __instance, float level, LiquidType type, Component liquidObj)
        {
            if (!_frozen || type != LiquidType.Water || level <= -10000f || !(liquidObj is WaterVolume volume) ||
                ZoneSystem.instance == null || !FrozenSurfaces.IsFrozen(volume) || !IsSeaCreature(__instance)) return;
            // The creature swims with its pivot at level - m_swimDepth; give it the level that puts its body under the ice.
            float lowered = PivotUnderIce(__instance, ZoneSystem.instance.m_waterLevel) + __instance.m_swimDepth;
            if (lowered >= level) return;
            _waterLevel(__instance) = lowered;
            _liquidLevel(__instance) = Mathf.Max(lowered, _tarLevel(__instance));
        }

        private static void CreatureOffTheIce(Character __instance)
        {
            if (!_frozen || ZoneSystem.instance == null || !FrozenSurfaces.Contains(_lastGround(__instance)) ||
                !__instance.IsOnGround() || !IsSeaCreature(__instance)) return;
            var nview = _charNView(__instance);
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;

            var pos = __instance.transform.position;
            float target = PivotUnderIce(__instance, ZoneSystem.instance.m_waterLevel);
            if (ZoneSystem.instance.GetGroundHeight(pos) > target - 0.2f) return; // too shallow to fit under

            pos.y = target;
            var body = _charBody(__instance);
            if (body != null)
            {
                body.position = pos;
                body.linearVelocity = Vector3.zero;
            }
            __instance.transform.position = pos;
        }

        // Character.UpdateContinousEffects keeps a ripple effect (m_waterEffects) at GetLiquidLevel() + 0.05 while the
        // character is in water, so swimmers under the ice show ripples on it. Hide those effect objects while frozen
        // and show them again after a thaw. (Switching them off the game's way would make it build new ones every frame.)
        private static void NoRipplesOnIce(Character __instance)
        {
            var fx = _waterFx(__instance);
            if (fx == null) return;
            bool hide = _frozen && _waterLevel(__instance) > _tarLevel(__instance);
            foreach (var go in fx)
                if (go != null && go.activeSelf == hide)
                    go.SetActive(!hide);
        }

        // Settings change rarely (season timer, server sync), so the check per fish is a plain bool.
        private static void Refresh(object sender, EventArgs e) =>
            _frozen = (_currentSeason != null ? _currentSeason.GetValue(null) : _season.BoxedValue).ToString() == "Winter" &&
                      _freezes.BoxedValue.ToString() == "On";

        private static void SeasonChanged() => Refresh(null, null);

        private static bool NoJumpWhileFrozen() => !_frozen;

        private static void WaypointUnderIce(Fish __instance, bool __result)
        {
            if (!_frozen || !__result || ZoneSystem.instance == null) return;
            ref var wp = ref _waypoint(__instance);
            float top = ZoneSystem.instance.m_waterLevel - IceClearance - __instance.m_height;
            if (wp.y <= top) return;
            float floor = ZoneSystem.instance.GetGroundHeight(wp) + __instance.m_height;
            wp.y = Mathf.Max(top, floor);
        }

        private static void OffTheIce(Fish __instance)
        {
            if (!_frozen || ZoneSystem.instance == null) return;
            var pos = __instance.transform.position;
            float ice = ZoneSystem.instance.m_waterLevel;
            if (pos.y < ice - 0.3f || pos.y > ice + 1.5f) return;
            if (__instance.IsHooked()) return;
            var nview = _fishNView(__instance);
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;
            // A fish a player has had in their inventory and put down stays where it was put.
            var item = _fishItem(__instance);
            if (item != null && item.m_itemData != null && item.m_itemData.m_pickedUp) return;

            // Only a fish resting on the frozen surface itself (not on a dock, boat or shore above the water line).
            if (!Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.down, out var hit, 2f, _waterMask, QueryTriggerInteraction.Ignore) ||
                !FrozenSurfaces.Contains(hit.collider)) return;

            float target = ice - IceClearance - __instance.m_height;
            if (ZoneSystem.instance.GetGroundHeight(pos) > target - 0.2f) return; // too shallow to fit under

            pos.y = target;
            // Level it (keep its heading): a fish that flopped on the ice can lie at any angle, and Fish.Stop turns
            // a straight-up or straight-down fish with LookRotation(zero), which logs a warning every physics step.
            var forward = __instance.transform.forward;
            forward.y = 0f;
            var level = forward.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(forward) : Quaternion.identity;
            var body = _fishBody(__instance);
            if (body != null)
            {
                body.position = pos;
                body.rotation = level;
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            __instance.transform.SetPositionAndRotation(pos, level);
            _isJumping(__instance) = false;
            _haveWaypoint(__instance) = false;
        }
    }
}
