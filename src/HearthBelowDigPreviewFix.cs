using System;
using System.Collections.Generic;
using HarmonyLib;
using HearthBelow.Digging;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// HearthBelow's dig preview (shown while a digging tool is held) re-fits its rings to the ground every
    /// frame: up to 5 rings x 48 raycasts with the fill on. When the aim hasn't moved, the answer is the
    /// same as last frame. DigProjector.ConformRing is skipped and its last result reused while the ring's
    /// centre, direction and size are unchanged (within 1 cm), for at most a fifth of a second, so a dig
    /// that changes the ground shows up in the preview almost at once. Moving the aim refits as before.
    /// The preview setting itself is untouched.
    /// </summary>
    internal static class HearthBelowDigPreviewFix
    {
        public const string TargetGuid = "Azumatt.HearthBelow";
        private const float MaxAge = 0.2f;
        private const float SameDistance = 0.01f;
        private const float SameDirectionDot = 0.99995f;

        private sealed class Entry
        {
            public Vector3 Center, Normal, Right;
            public float Scale, Probe, Radius, Time;
            public bool Result;
            public readonly Vector3[] Ring = new Vector3[48];
            public readonly bool[] Valid = new bool[48];
        }

        // Typed as object so this assembly still lists its types when HearthBelow isn't installed (see HearthBelowFix).
        private static object _ring;  // AccessTools.FieldRef<DigProjector, Vector3[]>
        private static object _valid; // AccessTools.FieldRef<DigProjector, bool[]>

        private static Vector3[] Ring(DigProjector p) => ((AccessTools.FieldRef<DigProjector, Vector3[]>)_ring)(p);
        private static bool[] Valid(DigProjector p) => ((AccessTools.FieldRef<DigProjector, bool[]>)_valid)(p);
        // One projector in practice (the local player's); keyed by instance and ring size anyway.
        private static readonly Dictionary<(int, float), Entry> Cache = new Dictionary<(int, float), Entry>();

        public static string Apply(Harmony harmony)
        {
            var conform = AccessTools.Method(typeof(DigProjector), "ConformRing",
                new[] { typeof(Vector3), typeof(Vector3), typeof(Vector3), typeof(Vector3), typeof(float), typeof(float) });
            if (conform == null || conform.ReturnType != typeof(bool))
                return "HearthBelow's dig preview has changed since this fix was written";
            if (AccessTools.Field(typeof(DigProjector), "_ring")?.FieldType != typeof(Vector3[]) ||
                AccessTools.Field(typeof(DigProjector), "_valid")?.FieldType != typeof(bool[]) ||
                AccessTools.Field(typeof(DigProjector), nameof(DigProjector.Radius))?.FieldType != typeof(float))
                return "HearthBelow's dig preview fields have changed since this fix was written";
            _ring = AccessTools.FieldRefAccess<DigProjector, Vector3[]>("_ring");
            _valid = AccessTools.FieldRefAccess<DigProjector, bool[]>("_valid");
            harmony.Patch(conform,
                prefix: new HarmonyMethod(typeof(HearthBelowDigPreviewFix), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(HearthBelowDigPreviewFix), nameof(Postfix)));
            return null;
        }

        private static bool Same(Entry e, Vector3 center, Vector3 normal, Vector3 right, float scale, float probe, float radius) =>
            Time.unscaledTime - e.Time <= MaxAge && e.Scale == scale && e.Probe == probe && e.Radius == radius &&
            (e.Center - center).sqrMagnitude <= SameDistance * SameDistance &&
            Vector3.Dot(e.Normal, normal) >= SameDirectionDot && Vector3.Dot(e.Right, right) >= SameDirectionDot;

        private static bool Prefix(DigProjector __instance, Vector3 center, Vector3 normal, Vector3 right, float scale, float probe, ref bool __result, out bool __state)
        {
            __state = false;
            var ring = Ring(__instance);
            var valid = Valid(__instance);
            if (ring == null || valid == null || ring.Length != 48 || valid.Length != 48)
                return true;
            if (!Cache.TryGetValue((__instance.GetInstanceID(), scale), out var e) ||
                !Same(e, center, normal, right, scale, probe, __instance.Radius))
                return true;
            Array.Copy(e.Ring, ring, 48);
            Array.Copy(e.Valid, valid, 48);
            __result = e.Result;
            __state = true;
            return false;
        }

        private static void Postfix(DigProjector __instance, Vector3 center, Vector3 normal, Vector3 right, float scale, float probe, bool __result, bool __state)
        {
            if (__state) return; // served from the cache: nothing new to store
            var ring = Ring(__instance);
            var valid = Valid(__instance);
            if (ring == null || valid == null || ring.Length != 48 || valid.Length != 48)
                return;
            var key = (__instance.GetInstanceID(), scale);
            if (!Cache.TryGetValue(key, out var e))
            {
                if (Cache.Count > 32) Cache.Clear();
                Cache[key] = e = new Entry();
            }
            e.Center = center; e.Normal = normal; e.Right = right; e.Scale = scale; e.Probe = probe;
            e.Radius = __instance.Radius; e.Time = Time.unscaledTime; e.Result = __result;
            Array.Copy(ring, e.Ring, 48);
            Array.Copy(valid, e.Valid, 48);
        }
    }
}
