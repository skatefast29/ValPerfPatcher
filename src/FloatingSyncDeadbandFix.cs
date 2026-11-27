using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Base game, multiplayer. ZSyncTransform.OwnerSync writes an owned object's position, velocity and rotation to
    /// its ZDO whenever they differ at all from last frame (m_positionCached / m_velocityCached, transform.hasChanged),
    /// and every such write raises the ZDO's revision, so the object is sent to the server again. Floating physics
    /// moves a crate, log or dropped item in the water a little on every frame, so each one is sent 20-26 times a
    /// second (12 floating cargo crates: about 60 KB/s of upload).
    /// For owned objects with a Floating component that are not creatures, ships, carts, fish or projectiles, this
    /// skips the write while the value is still within a small step of what the ZDO already holds: OwnerSync's own
    /// "unchanged since last frame" checks are made to pass (prefix setting the cached values / clearing hasChanged),
    /// so the method's code is not changed and ValheimCommunityPatch's transpiler on it (creatures only) is unaffected.
    /// Real drifting still goes through once it adds up to a step. A velocity under the step is sent as zero, so other
    /// games don't keep sliding the object along a stale velocity.
    /// On waves the step alone is not enough: a bobbing crate moves more than 5 cm or tilts more than 2 degrees almost
    /// every frame and was still sent about 27 times a second. So after a write the object is held for 1/UpdatesPerSecond
    /// seconds (position, velocity, rotation and, if synced, body velocity), unless it moved over BigStep or turned over
    /// BigAngle from what was sent (a hit, a fall). Other games interpolate between updates, so it looks the same there.
    /// </summary>
    internal static class FloatingSyncDeadbandFix
    {
        internal static float PositionStep = 0.05f;
        internal static float UpdatesPerSecond = 5f;
        private const float VelocityStep = 0.1f;
        private const float AngleStep = 2f;
        private const float BigStep = 1f;
        private const float BigAngle = 20f;

        private sealed class State
        {
            public bool Floater;
            public float NextWrite;
        }

        // Handed from the prefix to the postfix: the ZDO revision before the call, and whether body velocity was switched off.
        internal struct CallState
        {
            public bool Tracked;
            public uint Revision;
            public bool RestoreBodyVelocity;
        }

        private static readonly ConditionalWeakTable<ZSyncTransform, State> States = new ConditionalWeakTable<ZSyncTransform, State>();

        private static AccessTools.FieldRef<ZSyncTransform, ZNetView> _nview;
        private static AccessTools.FieldRef<ZSyncTransform, Rigidbody> _body;
        private static AccessTools.FieldRef<ZSyncTransform, Character> _character;
        private static AccessTools.FieldRef<ZSyncTransform, Projectile> _projectile;
        private static AccessTools.FieldRef<ZSyncTransform, bool> _wasOwner;
        private static AccessTools.FieldRef<ZSyncTransform, Vector3> _positionCached;
        private static AccessTools.FieldRef<ZSyncTransform, Vector3> _velocityCached;

        // Writes skipped, held and let through, logged once for the first minute with floating objects nearby.
        private static int _skipped, _sent, _held;
        private static bool _reported;
        private static float _nextReport;
        private static ManualLogSource _log;

        public static string Apply(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            var ownerSync = AccessTools.DeclaredMethod(typeof(ZSyncTransform), "OwnerSync", new System.Type[0]);
            if (ownerSync == null)
                return "ZSyncTransform.OwnerSync has changed since this fix was written";
            try
            {
                _nview = AccessTools.FieldRefAccess<ZSyncTransform, ZNetView>("m_nview");
                _body = AccessTools.FieldRefAccess<ZSyncTransform, Rigidbody>("m_body");
                _character = AccessTools.FieldRefAccess<ZSyncTransform, Character>("m_character");
                _projectile = AccessTools.FieldRefAccess<ZSyncTransform, Projectile>("m_projectile");
                _wasOwner = AccessTools.FieldRefAccess<ZSyncTransform, bool>("m_wasOwner");
                _positionCached = AccessTools.FieldRefAccess<ZSyncTransform, Vector3>("m_positionCached");
                _velocityCached = AccessTools.FieldRefAccess<ZSyncTransform, Vector3>("m_velocityCached");
            }
            catch (System.Exception)
            {
                return "ZSyncTransform's fields have changed since this fix was written";
            }

            // The fix relies on OwnerSync skipping a write when these cached values / hasChanged / m_syncBodyVelocity say so.
            var code = PatchProcessor.GetOriginalInstructions(ownerSync);
            var posField = AccessTools.Field(typeof(ZSyncTransform), "m_positionCached");
            var velField = AccessTools.Field(typeof(ZSyncTransform), "m_velocityCached");
            var bodyVelField = AccessTools.Field(typeof(ZSyncTransform), nameof(ZSyncTransform.m_syncBodyVelocity));
            var hasChanged = AccessTools.PropertyGetter(typeof(Transform), nameof(Transform.hasChanged));
            var setRotation = AccessTools.Method(typeof(ZDO), nameof(ZDO.SetRotation), new[] { typeof(Quaternion) });
            if (!code.Any(c => c.LoadsField(posField, byAddress: true)) || !code.Any(c => c.LoadsField(velField, byAddress: true)) ||
                bodyVelField == null || !code.Any(c => c.LoadsField(bodyVelField)) ||
                !code.Any(c => c.Calls(hasChanged)) || !code.Any(c => c.Calls(setRotation)))
                return "ZSyncTransform.OwnerSync no longer looks as it did when this fix was written";

            harmony.Patch(ownerSync, prefix: new HarmonyMethod(typeof(FloatingSyncDeadbandFix), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(FloatingSyncDeadbandFix), nameof(Postfix)));
            return null;
        }

        private static State GetState(ZSyncTransform sync)
        {
            if (!States.TryGetValue(sync, out var state))
            {
                state = new State
                {
                    Floater = sync.m_syncPosition && !sync.m_characterParentSync && _body(sync) != null && _character(sync) == null &&
                              _projectile(sync) == null && sync.GetComponent<Floating>() != null && sync.GetComponent<Ship>() == null &&
                              sync.GetComponent<Vagon>() == null && sync.GetComponent<Fish>() == null
                };
                States.Add(sync, state);
            }
            return state;
        }

        private static void Prefix(ZSyncTransform __instance, out CallState __state)
        {
            __state = default;
            if (PositionStep <= 0f && UpdatesPerSecond <= 0f) return;
            var state = GetState(__instance);
            if (!state.Floater) return;
            var nview = _nview(__instance);
            var zdo = nview.GetZDO();
            // Not owned, or ownership just arrived (OwnerSync then moves the object to the ZDO first): leave it to the game.
            if (zdo == null || !_wasOwner(__instance) || !zdo.IsOwner()) return;
            var body = _body(__instance);
            if (body == null) return; // a mod removed the Rigidbody: OwnerSync then reads the transform instead

            __state.Tracked = true;
            __state.Revision = zdo.DataRevision;
            var position = body.position;

            // Rate limit: within the hold after a write, write nothing unless the object jumped (hit, fell, was moved).
            if (UpdatesPerSecond > 0f && Time.time < state.NextWrite &&
                (position - zdo.GetPosition()).sqrMagnitude < BigStep * BigStep &&
                (!__instance.m_syncRotation || Quaternion.Angle(body.rotation, zdo.GetRotation()) < BigAngle))
            {
                _positionCached(__instance) = position;
                _velocityCached(__instance) = body.linearVelocity;
                if (!__instance.m_syncScale) __instance.transform.hasChanged = false;
                if (__instance.m_syncBodyVelocity)
                {
                    __instance.m_syncBodyVelocity = false;
                    __state.RestoreBodyVelocity = true;
                }
                if (!_reported) _held++;
                Report();
                return;
            }
            if (PositionStep <= 0f) return;

            if ((position - zdo.GetPosition()).sqrMagnitude < PositionStep * PositionStep)
            {
                _positionCached(__instance) = position;
                if (!_reported) _skipped++;
            }
            else if (!_reported) _sent++;

            var velocity = body.linearVelocity;
            var target = velocity.sqrMagnitude < VelocityStep * VelocityStep ? Vector3.zero : velocity;
            var stored = zdo.GetVec3(ZDOVars.s_velHash, Vector3.zero);
            if ((target - stored).sqrMagnitude < VelocityStep * VelocityStep)
                _velocityCached(__instance) = velocity;
            else if (target == Vector3.zero)
            {
                zdo.Set(ZDOVars.s_velHash, Vector3.zero);
                _velocityCached(__instance) = velocity;
            }

            if (__instance.m_syncRotation && !__instance.m_syncScale && __instance.transform.hasChanged &&
                Quaternion.Angle(body.rotation, zdo.GetRotation()) < AngleStep)
                __instance.transform.hasChanged = false;
            Report();
        }

        private static void Postfix(ZSyncTransform __instance, CallState __state)
        {
            if (!__state.Tracked) return;
            if (__state.RestoreBodyVelocity) __instance.m_syncBodyVelocity = true;
            if (UpdatesPerSecond <= 0f) return;
            var zdo = _nview(__instance).GetZDO();
            // Something was written this frame (the object will be sent): hold it for the next 1/UpdatesPerSecond s.
            if (zdo != null && zdo.DataRevision != __state.Revision)
                GetState(__instance).NextWrite = Time.time + 1f / UpdatesPerSecond;
        }

        private static void Report()
        {
            if (_reported || Time.time < _nextReport) return;
            if (_nextReport > 0f && _skipped + _sent + _held > 0)
            {
                // Once per session: enough to see the fix working without adding a line every minute.
                _log.LogInfo($"Floating objects: {_sent} position updates sent, {_skipped} skipped as too small, {_held} held by the " +
                             "updates-per-second limit in the first minute with floating objects nearby.");
                _reported = true;
            }
            _skipped = _sent = _held = 0;
            _nextReport = Time.time + 60f;
        }
    }
}
