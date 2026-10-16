using System.Collections;
using HarmonyLib;

namespace ValPerfPatcher
{
    /// <summary>
    /// BetterMap's VehicleTracker keeps the boats and carts it shows on the map in static dictionaries keyed by ZDOID.
    /// A ZDOID stores its owner as an index into a table that the game empties whenever a world is left (ZDOID.Reset),
    /// so after logging out and back in (without restarting the game) the old keys no longer match themselves and
    /// VehicleTracker.Reconcile throws KeyNotFoundException on every frame. BetterMap's own Game.Logout postfix resets its
    /// vehicle network data and memory but not these two dictionaries. This empties them when a world starts
    /// (Game.Start), which covers logouts, lost connections and kicks alike. The pins they pointed to belonged to the
    /// previous world's map, which is gone; BetterMap adds fresh pins on its next update, as on a first world load.
    /// </summary>
    internal static class BetterMapVehicleResetFix
    {
        private static IDictionary _tracked, _wanted;

        public static string Apply(Harmony harmony)
        {
            var tracker = Plugin.ModType(BetterMapLocationPinsFix.TargetGuid, "BetterMap.Scripts.Vehicles.VehicleTracker");
            var tracked = tracker == null ? null : AccessTools.DeclaredField(tracker, "_tracked");
            var wanted = tracker == null ? null : AccessTools.DeclaredField(tracker, "_wanted");
            if (tracked == null || wanted == null || !tracked.IsStatic || !wanted.IsStatic ||
                !(tracked.GetValue(null) is IDictionary t) || !(wanted.GetValue(null) is IDictionary w) ||
                tracked.FieldType.GetGenericArguments().Length != 2 || tracked.FieldType.GetGenericArguments()[0] != typeof(ZDOID))
                return "BetterMap's boat tracker has changed since this fix was written";
            var start = AccessTools.DeclaredMethod(typeof(Game), "Start", System.Type.EmptyTypes);
            if (start == null)
                return "the game's world start has changed since this fix was written";
            _tracked = t;
            _wanted = w;
            harmony.Patch(start, postfix: new HarmonyMethod(typeof(BetterMapVehicleResetFix), nameof(Postfix)));
            return null;
        }

        private static void Postfix()
        {
            _tracked.Clear();
            _wanted.Clear();
        }
    }
}
