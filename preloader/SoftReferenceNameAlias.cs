using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ValPerfPatcher.Preloader
{
    /// <summary>
    /// Valheim 1.0.30 renamed SoftReference&lt;T&gt;.Name (SoftReferenceableAssets.dll) to .name. Mods built against
    /// the old name (Jotunn 2.30.2 and so More World Locations' locations and dungeon rooms, BetterMap 1.1.0's map
    /// pins, Worldhalla 1.0.1's location preview) fail with MissingMethodException: "get_Name() not found".
    /// This adds Name back as a second getter that returns name, before any mod loads. Nothing else changes; once
    /// the game has a Name of its own again, this does nothing.
    /// </summary>
    public static class SoftReferenceNameAlias
    {
        public static IEnumerable<string> TargetDLLs { get; } = new[] { "SoftReferenceableAssets.dll" };

        public static void Patch(AssemblyDefinition assembly)
        {
            var log = Logger.CreateLogSource("ValPerfPatcher.Preloader");
            try
            {
                var module = assembly.MainModule;
                var type = module.GetType("SoftReferenceableAssets.SoftReference`1");
                if (type == null || type.GenericParameters.Count != 1)
                {
                    log.LogWarning("SoftReference.Name alias: the game's SoftReference has changed; nothing added.");
                    return;
                }
                if (type.Methods.Any(m => m.Name == "get_Name" && !m.IsStatic && !m.HasParameters))
                {
                    log.LogInfo("SoftReference.Name alias: the game has Name itself; nothing added.");
                    return;
                }
                var name = type.Methods.FirstOrDefault(m => m.Name == "get_name" && !m.IsStatic && !m.HasParameters &&
                                                            m.ReturnType.MetadataType == MetadataType.String);
                if (name == null)
                {
                    log.LogWarning("SoftReference.Name alias: the game's SoftReference has no name getter; nothing added.");
                    return;
                }

                var self = new GenericInstanceType(type);
                self.GenericArguments.Add(type.GenericParameters[0]);
                var callName = new MethodReference(name.Name, module.TypeSystem.String, self) { HasThis = true };

                var getter = new MethodDefinition("get_Name",
                    MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName, module.TypeSystem.String);
                var il = getter.Body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, callName);
                il.Emit(OpCodes.Ret);
                type.Methods.Add(getter);
                type.Properties.Add(new PropertyDefinition("Name", PropertyAttributes.None, module.TypeSystem.String) { GetMethod = getter });
                log.LogInfo("SoftReference.Name alias: added (Valheim 1.0.30 renamed it to name; older mods still ask for Name).");
            }
            catch (System.Exception e)
            {
                log.LogError("SoftReference.Name alias: failed, nothing added: " + e);
            }
            finally
            {
                Logger.Sources.Remove(log);
            }
        }
    }
}
