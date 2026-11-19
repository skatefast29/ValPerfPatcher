using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Base game, multiplayer; the same problem as StuckFishHideFix, for sea creatures (serpents, sharks, dolphins,
    /// whales, squid... from the game or any mod). A creature is only moved by the game that owns its ZDO; everyone else
    /// shows it where that owner last sent it (Character.CustomFixedUpdate: SetVisible(zdo.HasOwner())). A creature
    /// nobody is near has no owner, is hidden by the game, and stays where it was: on a wave crest or mid-jump. When a
    /// player comes near, ZDOMan hands it to that player at once, but that game only creates the object later (the
    /// creation queue is long when sailing), so for a while nobody moves it while every other game draws it again,
    /// floating above the water. Large simulation distances (5 here: creatures are loaded up to ~350 m away, owned
    /// only within ~100 m) make this common in the distance.
    /// This hides such a creature on this game only: a sea creature (swims, MonsterAI avoids land, not tamed) owned by
    /// another game, no update from its owner for StaleSeconds, and clearly higher than it swims (its water level minus
    /// its swim depth). It shows again with the next update from its owner. Creatures this game owns, creatures being
    /// moved and creatures at their swimming depth are never hidden. Only drawing changes.
    /// </summary>
    internal static class StuckSeaCreatureHideFix
    {
        // No update from the owner for this long counts as not being moved.
        private const float StaleSeconds = 3f;
        // How far above its swimming height a creature must be to count as stuck above the water.
        private const float AboveSwimHeight = 0.75f;

        private sealed class State
        {
            public bool Sea;
            public uint Revision;
            public float Since;
        }

        private static readonly ConditionalWeakTable<Character, State> States = new ConditionalWeakTable<Character, State>();
        private static AccessTools.FieldRef<Character, ZNetView> _nview;

        public static string Apply(Harmony harmony)
        {
            var setVisible = AccessTools.DeclaredMethod(typeof(Character), "SetVisible", new[] { typeof(bool) });
            if (setVisible == null || setVisible.IsStatic)
                return "the game's creature code has changed since this fix was written";
            if (AccessTools.DeclaredField(typeof(MonsterAI), nameof(MonsterAI.m_avoidLand))?.FieldType != typeof(bool) ||
                AccessTools.DeclaredField(typeof(BaseAI), nameof(BaseAI.m_avoidWater))?.FieldType != typeof(bool))
                return "the game's creature AI has changed since this fix was written";
            try
            {
                _nview = AccessTools.FieldRefAccess<Character, ZNetView>("m_nview");
            }
            catch (System.Exception)
            {
                return "the game's creature fields have changed since this fix was written";
            }
            harmony.Patch(setVisible, prefix: new HarmonyMethod(typeof(StuckSeaCreatureHideFix), nameof(Prefix)));
            return null;
        }

        private static void Prefix(Character __instance, ref bool visible)
        {
            if (!visible || !__instance.m_canSwim) return;
            var nview = _nview(__instance);
            if (nview == null || nview.IsOwner()) return;
            var zdo = nview.GetZDO();
            if (zdo == null) return;

            // TryGetValue + Add rather than GetValue(key, factory): a factory capturing zdo would allocate on every call.
            if (!States.TryGetValue(__instance, out var state))
            {
                var ai = __instance.GetBaseAI();
                state = new State
                {
                    Sea = !__instance.IsPlayer() && ai is MonsterAI monster && monster.m_avoidLand && !monster.m_avoidWater,
                    Revision = zdo.DataRevision,
                    Since = Time.time
                };
                States.Add(__instance, state);
            }
            if (!state.Sea) return;
            if (zdo.DataRevision != state.Revision)
            {
                state.Revision = zdo.DataRevision;
                state.Since = Time.time;
                return;
            }
            if (Time.time - state.Since < StaleSeconds) return;

            float water = __instance.GetLiquidLevel();
            if (water <= -10000f || __instance.IsTamed()) return;
            if (__instance.transform.position.y <= water - __instance.m_swimDepth + AboveSwimHeight) return;
            visible = false;
        }
    }
}
