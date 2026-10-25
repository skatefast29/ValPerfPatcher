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
    /// </summary>
    internal static class FloatingSyncDeadbandFix
    {
        internal static float PositionStep = 0.05f;
        private const float VelocityStep = 0.1f;
        private const float AngleStep = 2f;

        private static readonly ConditionalWeakTable<ZSyncTransform, object> Floaters = new ConditionalWeakTable<ZSyncTransform, object>();
        private static readonly object Yes = new object();
        private static readonly object No = new object();

        private static AccessTools.FieldRef<ZSyncTransform, ZNetView> _nview;
        private static AccessTools.FieldRef<ZSyncTransform, Rigidbody> _body;
        private static AccessTools.FieldRef<ZSyncTransform, Character> _character;
        private static AccessTools.FieldRef<ZSyncTransform, Projectile> _projectile;
        private static AccessTools.FieldRef<ZSyncTransform, bool> _wasOwner;
        private static AccessTools.FieldRef<ZSyncTransform, Vector3> _positionCached;
        private static AccessTools.FieldRef<ZSyncTransform, Vector3> _velocityCached;

        // Writes skipped and let through, logged once for the first minute with floating objects nearby.
        private static int _skipped, _sent;
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

            // The fix relies on OwnerSync skipping a write when these cached values / hasChanged say nothing changed.
            var code = PatchProcessor.GetOriginalInstructions(ownerSync);
            var posField = AccessTools.Field(typeof(ZSyncTransform), "m_positionCached");
            var velField = AccessTools.Field(typeof(ZSyncTransform), "m_velocityCached");
            var hasChanged = AccessTools.PropertyGetter(typeof(Transform), nameof(Transform.hasChanged));
            var setRotation = AccessTools.Method(typeof(ZDO), nameof(ZDO.SetRotation), new[] { typeof(Quaternion) });
            if (!code.Any(c => c.LoadsField(posField, byAddress: true)) || !code.Any(c => c.LoadsField(velField, byAddress: true)) ||
                !code.Any(c => c.Calls(hasChanged)) || !code.Any(c => c.Calls(setRotation)))
                return "ZSyncTransform.OwnerSync no longer looks as it did when this fix was written";

            harmony.Patch(ownerSync, prefix: new HarmonyMethod(typeof(FloatingSyncDeadbandFix), nameof(Prefix)));
            return null;
        }

        private static bool IsFloater(ZSyncTransform sync)
        {
            if (!Floaters.TryGetValue(sync, out var answer))
            {
                answer = sync.m_syncPosition && !sync.m_characterParentSync && _body(sync) != null && _character(sync) == null &&
                         _projectile(sync) == null && sync.GetComponent<Floating>() != null && sync.GetComponent<Ship>() == null &&
                         sync.GetComponent<Vagon>() == null && sync.GetComponent<Fish>() == null
                    ? Yes : No;
                Floaters.Add(sync, answer);
            }
            return answer == Yes;
        }

        private static void Prefix(ZSyncTransform __instance)
        {
            if (PositionStep <= 0f || !IsFloater(__instance)) return;
            var nview = _nview(__instance);
            var zdo = nview.GetZDO();
            // Not owned, or ownership just arrived (OwnerSync then moves the object to the ZDO first): leave it to the game.
            if (zdo == null || !_wasOwner(__instance) || !zdo.IsOwner()) return;
            var body = _body(__instance);
            if (body == null) return; // a mod removed the Rigidbody: OwnerSync then reads the transform instead

            var position = body.position;
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

            if (!_reported && Time.time >= _nextReport)
            {
                if (_nextReport > 0f && _skipped + _sent > 0)
                {
                    // Once per session: enough to see the fix working without adding a line every minute.
                    _log.LogInfo($"Floating objects: {_sent} position updates sent, {_skipped} skipped in the first minute with floating objects nearby.");
                    _reported = true;
                }
                _skipped = _sent = 0;
                _nextReport = Time.time + 60f;
            }
        }
    }
}
