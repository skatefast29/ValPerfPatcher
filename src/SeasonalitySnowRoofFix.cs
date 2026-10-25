using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Seasonality's winter snow (SnowBuildUp.UpdateSnow, run from its WearNTear.UpdateWear prefix about once a second per
    /// piece) wipes a piece's snow whenever WearNTear.HaveRoof() says it has a roof. HaveRoof sphere-casts straight up from
    /// the piece's pivot (WearNTear.RoofCheck), and a sphere cast also reports colliders it starts inside (distance 0). A
    /// piece whose pivot sits a few millimetres inside the floor it stands on (charcoal kilns on stone floors) therefore
    /// counts that floor as its roof, and HaveRoof remembers it (m_roof) for as long as the floor exists: identical pieces
    /// side by side get snow or none by chance.
    /// This replaces that one HaveRoof call in UpdateSnow with the same check that ignores what the cast starts inside,
    /// looked up again every RecheckSeconds per piece instead of every update (the game's own cover check runs every 4 s).
    /// The game's own roof check (rain wear, Deep North snow) is untouched.
    /// </summary>
    internal static class SeasonalitySnowRoofFix
    {
        private const float RecheckSeconds = 4f;

        private sealed class Cover
        {
            public float NextCheck;
            public bool Roof;
        }

        private static readonly ConditionalWeakTable<WearNTear, Cover> Covers = new ConditionalWeakTable<WearNTear, Cover>();
        private static readonly RaycastHit[] Hits = new RaycastHit[128];
        private static MethodInfo _haveRoof;
        private static AccessTools.FieldRef<WearNTear, float> _offset;
        private static int _mask;

        public static string Apply(Harmony harmony)
        {
            var snow = Plugin.ModType(SeasonalWaterCacheFix.SeasonalityGuid, "Seasonality.SnowBuildUp");
            var update = snow == null ? null : AccessTools.DeclaredMethod(snow, "UpdateSnow", new[] { typeof(WearNTear) });
            _haveRoof = AccessTools.DeclaredMethod(typeof(WearNTear), "HaveRoof", Type.EmptyTypes);
            var offset = AccessTools.DeclaredField(typeof(WearNTear), "m_roofCheckOffset");
            if (update == null || _haveRoof == null || _haveRoof.ReturnType != typeof(bool) ||
                offset == null || offset.FieldType != typeof(float))
                return "Seasonality's winter snow has changed since this fix was written";

            List<CodeInstruction> code;
            try { code = PatchProcessor.GetOriginalInstructions(update); }
            catch (Exception e) { return $"could not read Seasonality's winter snow ({e.Message})"; }
            int calls = 0;
            foreach (var ci in code)
                if (ci.Calls(_haveRoof)) calls++;
            if (calls != 1)
                return $"Seasonality's winter snow now checks for a roof {calls} times instead of once";

            _offset = AccessTools.FieldRefAccess<WearNTear, float>(offset);
            // Same layers as WearNTear.s_rayMask.
            _mask = LayerMask.GetMask("piece", "Default", "static_solid", "Default_small", "terrain");
            harmony.Patch(update, transpiler: new HarmonyMethod(typeof(SeasonalitySnowRoofFix), nameof(Transpiler)));
            return null;
        }

        // wnt.HaveRoof() becomes HasRoof(wnt).
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var hasRoof = AccessTools.Method(typeof(SeasonalitySnowRoofFix), nameof(HasRoof));
            foreach (var ci in instructions)
            {
                if (ci.Calls(_haveRoof))
                    yield return new CodeInstruction(OpCodes.Call, hasRoof).MoveLabelsFrom(ci).MoveBlocksFrom(ci);
                else yield return ci;
            }
        }

        private static bool HasRoof(WearNTear wnt)
        {
            var cover = Covers.GetOrCreateValue(wnt);
            if (Time.time < cover.NextCheck) return cover.Roof;
            cover.NextCheck = Time.time + RecheckSeconds * UnityEngine.Random.Range(0.9f, 1.1f);

            var t = wnt.transform;
            int n = Physics.SphereCastNonAlloc(t.position + new Vector3(0f, _offset(wnt), 0f), 0.1f, Vector3.up, Hits, 100f, _mask);
            bool roof = false;
            for (int i = 0; i < n && !roof; i++)
            {
                var hit = Hits[i];
                // distance 0: a collider the cast started inside (the floor the piece stands on), not a roof above it.
                if (hit.distance <= 0f || hit.collider == null) continue;
                if (hit.collider.transform.IsAncestor(t) || hit.collider.gameObject.CompareTag("leaky")) continue;
                roof = true;
            }
            cover.Roof = roof;
            return roof;
        }
    }
}
