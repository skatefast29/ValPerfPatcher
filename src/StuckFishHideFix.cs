using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Base game, multiplayer. A fish is only moved by the game that owns its ZDO; everyone else shows it at the
    /// position that owner sends (Fish.CustomFixedUpdate: SetVisible(m_nview.HasOwner()), then returns for non-owners).
    /// When nobody is near, a fish has no owner and stays wherever it was, often in the air mid-jump (the sea fish jump
    /// a lot). When players come back, ZDOMan hands it to one of them at once, but that game only creates the object
    /// later (ZNetScene creates about 10 objects a frame; sailing with several players builds a long queue). Until it
    /// does, nobody moves the fish while every other game now draws it, hanging in the air above the water.
    /// This hides such a fish on this game only: owned by someone else, no update from its owner for StaleSeconds,
    /// clearly above the water here, and not resting on anything. It shows again with the next update from its owner.
    /// Fish this game owns, fish being moved (jumping included) and fish lying on something are never hidden. Only
    /// drawing changes.
    /// </summary>
    internal static class StuckFishHideFix
    {
        // No update from the owner for this long counts as not being moved.
        private const float StaleSeconds = 3f;
        // How far the fish's underside must be above the water to count as stuck in the air.
        private const float AboveWater = 0.5f;
        // Without a water area in contact, the highest the sea can reach (sea level plus the biggest waves).
        private const float HighestWave = 3f;
        // A fish with something solid this close under it is lying on it (on a shore, a deck, a floor).
        private const float RestDistance = 1f;

        private sealed class State
        {
            public uint Revision;
            public float Since;
            public bool GroundChecked;
            public bool OnGround;
        }

        private static readonly ConditionalWeakTable<Fish, State> States = new ConditionalWeakTable<Fish, State>();

        private static AccessTools.FieldRef<Fish, ZNetView> _nview;
        private static AccessTools.FieldRef<Fish, float> _inWater;
        private static int _solidMask;

        public static string Apply(Harmony harmony)
        {
            var setVisible = AccessTools.DeclaredMethod(typeof(Fish), "SetVisible", new[] { typeof(bool) });
            if (setVisible == null || setVisible.IsStatic)
                return "the game's fish code has changed since this fix was written";
            try
            {
                _nview = AccessTools.FieldRefAccess<Fish, ZNetView>("m_nview");
                _inWater = AccessTools.FieldRefAccess<Fish, float>("m_inWater");
            }
            catch (System.Exception)
            {
                return "the game's fish fields have changed since this fix was written";
            }
            _solidMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");
            harmony.Patch(setVisible, prefix: new HarmonyMethod(typeof(StuckFishHideFix), nameof(Prefix)));
            return null;
        }

        private static void Prefix(Fish __instance, ref bool visible)
        {
            if (!visible) return;
            var nview = _nview(__instance);
            if (nview == null || nview.IsOwner()) return;
            var zdo = nview.GetZDO();
            if (zdo == null) return;

            // TryGetValue + Add rather than GetValue(key, factory): a factory capturing zdo would allocate on every call.
            if (!States.TryGetValue(__instance, out var state))
            {
                state = new State { Revision = zdo.DataRevision, Since = Time.time };
                States.Add(__instance, state);
            }
            if (zdo.DataRevision != state.Revision)
            {
                state.Revision = zdo.DataRevision;
                state.Since = Time.time;
                state.GroundChecked = false;
                return;
            }
            if (Time.time - state.Since < StaleSeconds) return;

            var pos = __instance.transform.position;
            float water = _inWater(__instance);
            if (water <= -10000f)
            {
                if (ZoneSystem.instance == null) return;
                water = ZoneSystem.instance.m_waterLevel + HighestWave;
            }
            if (pos.y - __instance.m_height <= water + AboveWater) return;

            // The fish doesn't move until its owner sends something, so one check per update is enough.
            if (!state.GroundChecked)
            {
                state.GroundChecked = true;
                state.OnGround = Physics.Raycast(pos + Vector3.up * 0.1f, Vector3.down, RestDistance + 0.1f, _solidMask, QueryTriggerInteraction.Ignore);
            }
            if (state.OnGround) return;
            visible = false;
        }
    }
}
