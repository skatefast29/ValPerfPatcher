using System;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Base game. StaticPhysics (rocks, stumps and other objects that drop if the ground under them goes)
    /// is updated by SlowUpdater about 100 objects per frame, and every update casts a ray down to the
    /// ground, forever: its wait time is only set once, when the object spawns. Most of those objects
    /// never move again. After a check that leaves the object where it is, this sets its next check
    /// a couple of seconds later (with a little jitter so they don't all line up).
    /// Works the same with Valheim Community Patch, which replaces SUpdate but keeps the same wait-time
    /// check (m_updateTime). The cost is that an object can take up to that long to start falling
    /// after the ground under it is removed.
    /// </summary>
    internal static class StaticPhysicsThrottle
    {
        internal static float IntervalSeconds = 2f;
        private static readonly System.Random Jitter = new System.Random();

        private static AccessTools.FieldRef<StaticPhysics, float> _updateTime;
        private static AccessTools.FieldRef<StaticPhysics, bool> _falling;

        public static string Apply(Harmony harmony)
        {
            var update = AccessTools.Method(typeof(StaticPhysics), nameof(StaticPhysics.SUpdate), new[] { typeof(float), typeof(Vector2s) });
            if (update == null)
                return "StaticPhysics.SUpdate has changed since this fix was written";
            if (AccessTools.Field(typeof(StaticPhysics), "m_updateTime")?.FieldType != typeof(float) ||
                AccessTools.Field(typeof(StaticPhysics), "m_falling")?.FieldType != typeof(bool))
                return "StaticPhysics' fields have changed since this fix was written";
            _updateTime = AccessTools.FieldRefAccess<StaticPhysics, float>("m_updateTime");
            _falling = AccessTools.FieldRefAccess<StaticPhysics, bool>("m_falling");
            harmony.Patch(update,
                prefix: new HarmonyMethod(typeof(StaticPhysicsThrottle), nameof(Prefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(StaticPhysicsThrottle), nameof(Postfix)));
            return null;
        }

        // Runs before any other mod's prefix, so it sees whether a ground check is due this time.
        private static void Prefix(StaticPhysics __instance, float time, out bool __state)
        {
            __state = !_falling(__instance) && time > _updateTime(__instance);
        }

        private static void Postfix(StaticPhysics __instance, float time, bool __state)
        {
            if (!__state || _falling(__instance) || IntervalSeconds <= 0f)
                return; // no check was due, or the object started falling: leave it alone
            // Own random numbers: the game's shared generator is left exactly as the game uses it.
            _updateTime(__instance) = time + IntervalSeconds * (0.75f + 0.5f * (float)Jitter.NextDouble());
        }
    }
}
