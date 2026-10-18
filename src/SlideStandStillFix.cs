using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Base game. Character.UpdateWalking, for a character slipping on slippery ground without moving on its own, turns
    /// it with Quaternion.LookRotation(transform.position - m_lazyPos, flattened): towards where it has been sliding.
    /// m_lazyPos only follows once the character is a little way from it, so after moving straight up or down (a
    /// teleport into a newly generated area, a fall, being put on top of the ice) it can sit exactly above or below the
    /// character. That slide is then zero, Unity logs "Look rotation viewing vector is zero" every physics step (about
    /// 50 a second) for as long as the character stands still, and turns it a little towards north each step.
    /// Seasonality's frozen water ('Water Freezes' in winter) makes every character on it slip, so with it this is
    /// common; the player standing still on the ice is enough.
    /// This replaces that one LookRotation call: when the slide is shorter than a millimetre the character keeps facing
    /// the way it does; otherwise it is the game's own call.
    /// </summary>
    internal static class SlideStandStillFix
    {
        // Slides shorter than this (squared, in metres) count as none: 1 mm.
        private const float MinSlideSqr = 1e-6f;

        private static readonly MethodInfo LookRotation = AccessTools.Method(typeof(Quaternion), nameof(Quaternion.LookRotation), new[] { typeof(Vector3) });

        private static ManualLogSource _log;

        public static string Apply(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            var target = AccessTools.DeclaredMethod(typeof(Character), "UpdateWalking", new[] { typeof(float) });
            if (target == null || LookRotation == null)
                return "the game's walking code has changed since this fix was written";
            // The method as analysed: exactly one LookRotation(Vector3) call, in the slipping branch next to m_slipperySpeed.
            var original = PatchProcessor.GetOriginalInstructions(target);
            if (original.Count(IsLookRotation) != 1 || !original.Any(i => i.operand is FieldInfo f && f.Name == "m_slipperySpeed"))
                return "the game's sliding code has changed since this fix was written";
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(SlideStandStillFix), nameof(Transpiler)));
            return null;
        }

        private static bool IsLookRotation(CodeInstruction i) => (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && i.operand is MethodInfo m && m == LookRotation;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            // Other mods' transpilers (Seasonality's) run on the same method; touch only a single matching call.
            if (list.Count(IsLookRotation) != 1)
            {
                _log?.LogWarning("Base game: no log flood when standing still on ice: another mod has changed the same walking code, so it is left as it is.");
                return list;
            }
            // The slide is on the stack; add the character and call SlideRotation(slide, this) instead.
            var index = list.FindIndex(IsLookRotation);
            var self = new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(list[index]);
            list[index] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SlideStandStillFix), nameof(SlideRotation)));
            list.Insert(index, self);
            return list;
        }

        private static Quaternion SlideRotation(Vector3 slide, Character character)
        {
            if (slide.sqrMagnitude < MinSlideSqr && character != null)
                return character.transform.rotation;
            return Quaternion.LookRotation(slide);
        }
    }
}
