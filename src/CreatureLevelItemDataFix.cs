using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Creature Level &amp; Loot Control: ItemControl.PatchConfigReading.Postfix (on Game.RequestRespawn, first spawn of each
    /// world load) runs item.gameObject.AddComponent&lt;OriginalItemData&gt;().Init() for every item prefab in ObjectDB.
    /// Item prefabs live for the whole game session, so every logout and login adds another OriginalItemData to each of
    /// them (about 12,500 objects per relog). The mod only ever reads the first one (GetComponent), and the later ones
    /// record values the mod has already changed. This adds and initialises it only when the item has none yet, so later
    /// world loads leave the items as the first one did. Items lying in the world (ItemDrop.s_instances) are new objects
    /// each session and are handled as before.
    /// </summary>
    internal static class CreatureLevelItemDataFix
    {
        private static Type _data;
        private static MethodInfo _addComponent, _init;

        public static string Apply(Harmony harmony)
        {
            _data = Plugin.ModType(CreatureLevelSectorTextFix.TargetGuid, "CreatureLevelControl.OriginalItemData");
            var control = Plugin.ModType(CreatureLevelSectorTextFix.TargetGuid, "CreatureLevelControl.ItemControl");
            var patch = control == null ? null : AccessTools.Inner(control, "PatchConfigReading");
            var postfix = patch == null ? null : AccessTools.DeclaredMethod(patch, "Postfix");
            _init = _data == null ? null : AccessTools.DeclaredMethod(_data, "Init", Type.EmptyTypes);
            var generic = AccessTools.Method(typeof(GameObject), nameof(GameObject.AddComponent), Type.EmptyTypes);
            if (postfix == null || _init == null || generic == null || !generic.IsGenericMethodDefinition)
                return "Creature Level & Loot Control's item setup has changed since this fix was written";
            _addComponent = generic.MakeGenericMethod(_data);

            List<CodeInstruction> code;
            try { code = PatchProcessor.GetOriginalInstructions(postfix); }
            catch (Exception e) { return "could not read Creature Level & Loot Control's item setup (" + e.Message + ")"; }
            int pairs = 0;
            for (int i = 0; i + 1 < code.Count; i++)
                if (code[i].Calls(_addComponent) && code[i + 1].Calls(_init)) pairs++;
            if (pairs != 1)
                return "Creature Level & Loot Control's item setup no longer adds and sets up its item data in one step";

            harmony.Patch(postfix, transpiler: new HarmonyMethod(typeof(CreatureLevelItemDataFix), nameof(Transpiler)));
            return null;
        }

        // go.AddComponent<OriginalItemData>().Init() becomes AddOnce(go).
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var addOnce = AccessTools.Method(typeof(CreatureLevelItemDataFix), nameof(AddOnce));
            var list = new List<CodeInstruction>(instructions);
            for (int i = 0; i < list.Count; i++)
            {
                if (i + 1 < list.Count && list[i].Calls(_addComponent) && list[i + 1].Calls(_init))
                {
                    var call = new CodeInstruction(OpCodes.Call, addOnce).MoveLabelsFrom(list[i]).MoveBlocksFrom(list[i]);
                    call.labels.AddRange(list[i + 1].labels);
                    call.blocks.AddRange(list[i + 1].blocks);
                    yield return call;
                    i++;
                }
                else yield return list[i];
            }
        }

        private static void AddOnce(GameObject item)
        {
            if (item.GetComponent(_data) != null) return;
            var data = item.AddComponent(_data);
            _init.Invoke(data, null);
        }
    }
}
