using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Creature Level &amp; Loot Control's postfix on Inventory.IsTeleportable calls Utils.RequestServerTime on every
    /// call that returns true, and on a client that sends a "CLLC GetServerTime" request to the server each time.
    /// The game's portals check the player's inventory every 0.5 s while nearby, and Adventure Backpacks checks it on
    /// every backpack save and load, so one session sent 240,604 requests (bursts up to 113 a second). The time the
    /// server answers with is stored in Utils.serverTime, which the mod already moves forward itself every physics
    /// step (and, in the analysed version, never reads). This lets a request through at most once every
    /// IntervalSeconds per server connection. On the server itself the mod's call stays as it is (no network).
    /// </summary>
    internal static class CreatureLevelServerTimeFix
    {
        public static float IntervalSeconds = 10f;
        private static ZNetPeer _lastPeer;
        private static float _next;

        public static string Apply(Harmony harmony)
        {
            var utils = Plugin.ModType(CreatureLevelSectorTextFix.TargetGuid, "CreatureLevelControl.Utils");
            var request = utils == null ? null : AccessTools.DeclaredMethod(utils, "RequestServerTime", System.Type.EmptyTypes);
            if (request == null || !request.IsStatic || request.ReturnType != typeof(void) ||
                !PatchProcessor.GetOriginalInstructions(request).Any(c => c.opcode == OpCodes.Ldstr && (c.operand as string) == "CLLC GetServerTime"))
                return "Creature Level & Loot Control's server time request has changed since this fix was written";
            harmony.Patch(request, prefix: new HarmonyMethod(typeof(CreatureLevelServerTimeFix), nameof(Prefix)));
            return null;
        }

        private static bool Prefix()
        {
            var net = ZNet.instance;
            if (IntervalSeconds <= 0f || net == null || net.IsServer())
                return true;
            var peer = net.GetServerPeer();
            float now = Time.unscaledTime;
            if (peer == _lastPeer && now < _next)
                return false; // asked this server moments ago: the mod keeps the time moving forward itself
            _lastPeer = peer;
            _next = now + IntervalSeconds;
            return true;
        }
    }
}
