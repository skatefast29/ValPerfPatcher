using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Base game, multiplayer. Character.UpdateWalking (and the swimming / flying versions) hands a creature's forward,
    /// sideways and turn speed to ZSyncAnimation.SetFloat every frame, which writes them to the creature's ZDO whenever
    /// the animator is more than 0.01 off. Those speeds wobble a little on every frame (the turn speed is smoothed over
    /// 0.5 s, so it keeps changing as it settles), and each write raises the ZDO's revision, so the creature is sent to
    /// the server again (Skeletons and Draugr: about 10 sends a second each, three quarters of them for these speeds).
    /// For creatures (not players) this skips the ZDO write of those three speeds while the new value is within
    /// StepSize of what the ZDO already holds; a value that close to zero is sent as zero, so nobody's animation keeps
    /// a small leftover turn or walk. The owner's own animator still gets every value. Other animation values and
    /// other mods' SetFloat calls are untouched. Transpiler: only the one ZDO.Set call in SetFloat is redirected.
    /// </summary>
    internal static class AnimSyncDeadbandFix
    {
        private const float StepSize = 0.1f;
        // ZSyncAnimation salts its ZDO keys with this (c_ZDOSalt).
        private const int Salt = 438569;

        private static int _forwardKey, _sidewayKey, _turnKey;
        private static readonly ConditionalWeakTable<ZSyncAnimation, object> Players = new ConditionalWeakTable<ZSyncAnimation, object>();
        private static readonly object Yes = new object();
        private static readonly object No = new object();

        private static MethodInfo _zdoSet;
        private static ManualLogSource _log;
        // Writes skipped and let through, logged once for the first minute with creatures nearby.
        private static int _skipped, _sent;
        private static float _nextReport;
        private static bool _reported;
        private static bool _redirected;

        public static string Apply(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            var setFloat = AccessTools.DeclaredMethod(typeof(ZSyncAnimation), nameof(ZSyncAnimation.SetFloat), new[] { typeof(int), typeof(float) });
            _zdoSet = AccessTools.Method(typeof(ZDO), nameof(ZDO.Set), new[] { typeof(int), typeof(float) });
            if (setFloat == null || _zdoSet == null)
                return "ZSyncAnimation.SetFloat has changed since this fix was written";
            _forwardKey = Salt + ZSyncAnimation.GetHash("forward_speed");
            _sidewayKey = Salt + ZSyncAnimation.GetHash("sideway_speed");
            _turnKey = Salt + ZSyncAnimation.GetHash("turn_speed");
            harmony.Patch(setFloat, transpiler: new HarmonyMethod(typeof(AnimSyncDeadbandFix), nameof(Transpiler)));
            return _redirected ? null : "ZSyncAnimation.SetFloat no longer looks as it did when this fix was written";
        }

        // Expects one ZDO.Set(int, float) and the salt constant; anything else leaves the method as it is.
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int call = -1, calls = 0;
            bool salted = false;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].Calls(_zdoSet)) { call = i; calls++; }
                if (codes[i].LoadsConstant(Salt)) salted = true;
            }
            if (calls != 1 || !salted) return codes;
            var self = new CodeInstruction(OpCodes.Ldarg_0);
            codes[call].MoveLabelsTo(self);
            codes[call] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(AnimSyncDeadbandFix), nameof(Write)));
            codes.Insert(call, self);
            _redirected = true;
            return codes;
        }

        private static void Write(ZDO zdo, int key, float value, ZSyncAnimation anim)
        {
            if ((key == _forwardKey || key == _sidewayKey || key == _turnKey) && !IsPlayer(anim))
            {
                if (value > -StepSize && value < StepSize) value = 0f;
                float stored = zdo.GetFloat(key, 0f);
                // Zero always goes through, so a leftover small value gets cleared (and a missing key gets written,
                // as the game does: other games fall back to the animator's default for a key that isn't there).
                bool skip = value != stored && value != 0f && Mathf.Abs(value - stored) < StepSize;
                if (!_reported && value != stored) Count(skip);
                if (skip) return;
            }
            zdo.Set(key, value);
        }

        private static void Count(bool skipped)
        {
            if (skipped) _skipped++;
            else _sent++;
            if (Time.time < _nextReport) return;
            if (_nextReport > 0f)
            {
                _log.LogInfo($"Creature walk and turn speeds: {_sent} sent, {_skipped} skipped in the first minute with creatures nearby.");
                _reported = true;
            }
            _skipped = _sent = 0;
            _nextReport = Time.time + 60f;
        }

        private static bool IsPlayer(ZSyncAnimation anim)
        {
            if (!Players.TryGetValue(anim, out var answer))
            {
                answer = anim.GetComponent<Player>() != null ? Yes : No;
                Players.Add(anim, answer);
            }
            return answer == Yes;
        }
    }
}
