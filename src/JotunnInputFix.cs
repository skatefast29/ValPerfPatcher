using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ValPerfPatcher
{
    /// <summary>
    /// Jotunn's InputManager answers every ZInput.GetButton/GetButtonDown/GetButtonUp (about 80 calls per
    /// frame) and, whenever the keyboard button isn't held, also checks the gamepad version by building the
    /// string "Joy!" + name. That's a new string each call. This transpiler replaces the concatenation with
    /// a lookup that builds each "Joy!" name once and reuses it. Same strings, same results.
    /// </summary>
    internal static class JotunnInputFix
    {
        public const string TargetGuid = "com.jotunn.jotunn";
        private static readonly string[] Methods = { "ZInput_GetButtonDown", "ZInput_GetButton", "ZInput_GetButtonUp" };
        private static readonly Dictionary<string, string> JoyNames = new Dictionary<string, string>();
        private static int _replaced;

        public static string Apply(Harmony harmony)
        {
            var type = Plugin.ModType(TargetGuid, "Jotunn.Managers.InputManager");
            if (type == null)
                return "Jotunn's InputManager was not found";
            _replaced = 0;
            int patched = 0;
            foreach (var name in Methods)
            {
                var m = AccessTools.Method(type, name, new[] { typeof(string) });
                if (m == null || m.IsStatic) continue;
                harmony.Patch(m, transpiler: new HarmonyMethod(typeof(JotunnInputFix), nameof(Transpiler)));
                patched++;
            }
            if (patched == 0 || _replaced == 0)
                return "Jotunn's input code has changed since this fix was written";
            return null;
        }

        public static string JoyName(string name)
        {
            if (name == null) return "Joy!";
            if (!JoyNames.TryGetValue(name, out var joy))
            {
                joy = "Joy!" + name;
                JoyNames[name] = joy;
            }
            return joy;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var concat = AccessTools.Method(typeof(string), nameof(string.Concat), new[] { typeof(string), typeof(string) });
            var joyName = AccessTools.Method(typeof(JotunnInputFix), nameof(JoyName));
            var code = new List<CodeInstruction>(instructions);
            for (int i = 0; i + 2 < code.Count; i++)
            {
                // ldstr "Joy!" ; ldarg.1 ; call string.Concat(string, string)
                if (code[i].opcode == OpCodes.Ldstr && (string)code[i].operand == "Joy!" &&
                    code[i + 1].opcode == OpCodes.Ldarg_1 &&
                    (code[i + 2].opcode == OpCodes.Call) && Equals(code[i + 2].operand, concat))
                {
                    // Keep the first instruction object (and any branch labels on it), just change it.
                    code[i].opcode = OpCodes.Ldarg_1;
                    code[i].operand = null;
                    code[i + 1].opcode = OpCodes.Call;
                    code[i + 1].operand = joyName;
                    code.RemoveAt(i + 2);
                    _replaced++;
                }
            }
            return code;
        }
    }
}
