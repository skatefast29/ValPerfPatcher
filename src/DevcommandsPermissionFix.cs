using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace ValPerfPatcher
{
    /// <summary>
    /// Server Devcommands checks a permission many times per frame (flying, god mode, and more), and each
    /// check starts with section.ToLower(), which builds a new string even though the section name is
    /// always the same ("serverdevcommands"). This transpiler swaps that call for a lookup that lowercases
    /// each section name once and reuses it. Same strings, same results.
    /// </summary>
    internal static class DevcommandsPermissionFix
    {
        public const string TargetGuid = "server_devcommands";
        private static readonly Dictionary<string, string> Lower = new Dictionary<string, string>();
        private static int _replaced;

        public static string Apply(Harmony harmony)
        {
            var type = Plugin.ModType(TargetGuid, "ServerDevcommands.PermissionManager");
            var method = type == null ? null : AccessTools.Method(type, "GetFeaturePermissionByHash", new[] { typeof(string), typeof(int) });
            if (method == null)
                return "Server Devcommands' permission check has changed since this fix was written";
            _replaced = 0;
            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(DevcommandsPermissionFix), nameof(Transpiler)));
            return _replaced == 1 ? null : "Server Devcommands' permission check has changed since this fix was written";
        }

        public static string LowerCached(string s)
        {
            if (s == null) return null;
            if (!Lower.TryGetValue(s, out var lower))
            {
                lower = s.ToLower();
                Lower[s] = lower;
            }
            return lower;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var toLower = AccessTools.Method(typeof(string), nameof(string.ToLower), new System.Type[0]);
            var cached = AccessTools.Method(typeof(DevcommandsPermissionFix), nameof(LowerCached));
            // Harmony runs this again whenever another mod patches the same method, so "first" is per run.
            bool done = false;
            foreach (var code in instructions)
            {
                // Only the first ToLower (the section name at the top of the method).
                if (!done && (code.opcode == OpCodes.Callvirt || code.opcode == OpCodes.Call) && Equals(code.operand, toLower))
                {
                    code.opcode = OpCodes.Call;
                    code.operand = cached;
                    done = true;
                    _replaced++;
                }
                yield return code;
            }
        }
    }
}
