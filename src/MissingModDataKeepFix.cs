using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Base game, servers and hosts. When a mod that adds locations or objects is missing or fails to load (removed,
    /// or broken by a game update, like Jotunn 2.30.2's location registration on Valheim 1.0.30), the game deletes
    /// that mod's data from the world for good:
    /// - Locations: ZoneSystem.Load skips every location record whose type it doesn't know ("Failed to find
    ///   location"), so the next world save leaves them out. Locations not visited yet are gone.
    /// - Objects: ZNetScene.CreateObjectsSorted / CreateDistantObjects destroy an object whose type isn't loaded as
    ///   soon as a player comes near it ("Destroyed invalid prefab ZDO"). Loading keeps them ("Will load anyway").
    /// This keeps both until the mod is back:
    /// - Location records the game skips are read again from the same save data and written back, unchanged, at the
    ///   end of the location list on the next saves (unless the game has put another location in that zone since).
    /// - An object whose type isn't loaded is marked as handled (ZDO.Created: in memory only, not saved), so the game
    ///   neither destroys it nor tries it again this session. It stays in the world file as it was.
    /// Objects with no type at all (hash 0) are still removed as the game does. Stands down if the save format or the
    /// game's code has changed.
    /// </summary>
    internal static class MissingModDataKeepFix
    {
        private const int EntrySize = 4 + 4 * 3 + 1; // hash, position x y z, placed

        private struct Kept
        {
            public int Hash;
            public Vector3 Position;
            public bool Placed;
        }

        private static ManualLogSource _log;
        private static Func<ZoneSystem, int, ZoneSystem.ZoneLocation> _getLocation;
        private static AccessTools.FieldRef<ZoneSystem, List<ZoneSystem.LocationInstance>> _saveClone;
        private static readonly List<Kept> KeptLocations = new List<Kept>();
        private static List<Kept> _keptForSave; // snapshot made on the main thread for the save thread
        private static ZoneSystem _owner;        // the world session the kept records belong to (a new world has no Load)
        private static long _loadStart = -1;
        private static bool _reportedSave;

        private static ZDO _keeping;
        private static readonly Dictionary<int, int> KeptObjects = new Dictionary<int, int>();

        public static string Apply(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            string locations = ApplyLocations(harmony);
            string objects = ApplyObjects(harmony);
            if (locations != null && objects != null) return locations + "; " + objects;
            if (locations != null) log.LogWarning("Keep missing mods' data: locations: " + locations + " (objects are still kept).");
            if (objects != null) log.LogWarning("Keep missing mods' data: objects: " + objects + " (locations are still kept).");
            return null;
        }

        // ---------------- locations ----------------

        private static string ApplyLocations(Harmony harmony)
        {
            var load = AccessTools.DeclaredMethod(typeof(ZoneSystem), nameof(ZoneSystem.Load), new[] { typeof(BinaryReader), typeof(global::Version.World) });
            var prepare = AccessTools.DeclaredMethod(typeof(ZoneSystem), nameof(ZoneSystem.PrepareSave), Type.EmptyTypes);
            var save = AccessTools.DeclaredMethod(typeof(ZoneSystem), nameof(ZoneSystem.Save), new[] { typeof(BinaryWriter) });
            var getLocation = AccessTools.DeclaredMethod(typeof(ZoneSystem), "GetLocation", new[] { typeof(int) });
            if (load == null || prepare == null || save == null || getLocation == null || getLocation.ReturnType != typeof(ZoneSystem.ZoneLocation) ||
                AccessTools.DeclaredField(typeof(ZoneSystem), "m_tempLocationsSaveClone")?.FieldType != typeof(List<ZoneSystem.LocationInstance>))
                return "the game's world location saving has changed since this fix was written";
            int compressed = 0;
            foreach (var ci in PatchProcessor.GetOriginalInstructions(save))
                if (ci.operand is MethodInfo m && m.DeclaringType == typeof(ZPackage) && m.Name == nameof(ZPackage.GetCompressed)) compressed++;
            if (compressed != 1)
                return "the game's world location saving has changed since this fix was written";

            _getLocation = (Func<ZoneSystem, int, ZoneSystem.ZoneLocation>)Delegate.CreateDelegate(typeof(Func<ZoneSystem, int, ZoneSystem.ZoneLocation>), getLocation);
            _saveClone = AccessTools.FieldRefAccess<ZoneSystem, List<ZoneSystem.LocationInstance>>("m_tempLocationsSaveClone");
            harmony.Patch(load, prefix: new HarmonyMethod(typeof(MissingModDataKeepFix), nameof(LoadPrefix)),
                postfix: new HarmonyMethod(typeof(MissingModDataKeepFix), nameof(LoadPostfix)));
            harmony.Patch(prepare, postfix: new HarmonyMethod(typeof(MissingModDataKeepFix), nameof(PreparePostfix)));
            harmony.Patch(save, transpiler: new HarmonyMethod(typeof(MissingModDataKeepFix), nameof(SaveTranspiler)));
            return null;
        }

        private static void LoadPrefix(ZoneSystem __instance, BinaryReader binaryReader)
        {
            KeptLocations.Clear();
            _owner = __instance;
            _keptForSave = null;
            _reportedSave = false;
            var stream = binaryReader?.BaseStream;
            _loadStart = stream != null && stream.CanSeek ? stream.Position : -1;
        }

        private static void LoadPostfix(ZoneSystem __instance, BinaryReader binaryReader)
        {
            if (_loadStart < 0) return;
            var stream = binaryReader.BaseStream;
            long end = stream.Position;
            try
            {
                stream.Position = _loadStart;
                int count = binaryReader.ReadInt32();
                var pkg = new ZPackage(binaryReader.ReadBytes(count));
                pkg.Decompress();
                int zones = pkg.ReadInt();
                for (int i = 0; i < zones; i++) pkg.ReadVector2s();
                pkg.ReadInt(); // location version
                int keys = pkg.ReadInt();
                for (int i = 0; i < keys; i++) pkg.ReadString();
                pkg.ReadBool(); // locations generated
                int locations = pkg.ReadInt();
                for (int i = 0; i < locations; i++)
                {
                    var kept = new Kept { Hash = pkg.ReadInt(), Position = pkg.ReadVector3(), Placed = pkg.ReadBool() };
                    if (_getLocation(__instance, kept.Hash) == null) KeptLocations.Add(kept);
                }
                if (pkg.GetPos() != pkg.Size())
                {
                    KeptLocations.Clear();
                    _log.LogWarning("Keep missing mods' data: the world's location list did not read as expected; unknown locations are left to the game this time.");
                }
                else if (KeptLocations.Count > 0)
                {
                    var types = new HashSet<int>();
                    foreach (var k in KeptLocations) types.Add(k.Hash);
                    _log.LogWarning($"Keep missing mods' data: {KeptLocations.Count} location records of {types.Count} types that no loaded mod registers " +
                                    "(a location mod is missing or failed to load). The game would drop them on the next save; they are kept and saved as they are.");
                }
            }
            catch (Exception e)
            {
                KeptLocations.Clear();
                _log.LogWarning("Keep missing mods' data: could not read the world's location list (" + e.Message + "); unknown locations are left to the game this time.");
            }
            finally
            {
                stream.Position = end;
                _loadStart = -1;
            }
        }

        // Main thread, just before the save thread runs ZoneSystem.Save.
        private static void PreparePostfix(ZoneSystem __instance)
        {
            if (KeptLocations.Count == 0 || !ReferenceEquals(__instance, _owner)) { _keptForSave = null; return; }
            var list = new List<Kept>(KeptLocations.Count);
            foreach (var k in KeptLocations)
                if (!__instance.m_locationInstances.ContainsKey(ZoneSystem.GetZone(k.Position))) list.Add(k);
            _keptForSave = list;
        }

        // pkg.GetCompressed() becomes WithKept(pkg, this).
        private static IEnumerable<CodeInstruction> SaveTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var with = AccessTools.Method(typeof(MissingModDataKeepFix), nameof(WithKept));
            foreach (var ci in instructions)
            {
                if (ci.operand is MethodInfo m && m.DeclaringType == typeof(ZPackage) && m.Name == nameof(ZPackage.GetCompressed))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(ci).MoveBlocksFrom(ci);
                    yield return new CodeInstruction(OpCodes.Call, with);
                }
                else yield return ci;
            }
        }

        // The location list is the last thing in the package: its count, then 17 bytes per location. Raise the count
        // and append the kept records in the same layout.
        private static byte[] WithKept(ZPackage pkg, ZoneSystem zoneSystem)
        {
            var kept = _keptForSave;
            if (kept == null || kept.Count == 0) return pkg.GetCompressed();
            try
            {
                int n = _saveClone(zoneSystem)?.Count ?? -1;
                byte[] data = pkg.GetArray();
                long countAt = data.Length - (long)n * EntrySize - 4;
                if (n < 0 || countAt < 0 || BitConverter.ToInt32(data, (int)countAt) != n)
                {
                    if (!_reportedSave) _log.LogWarning("Keep missing mods' data: the world save's location list is not laid out as expected; unknown locations were not added back this time.");
                    _reportedSave = true;
                    return pkg.GetCompressed();
                }
                byte[] combined;
                using (var ms = new MemoryStream(data.Length + kept.Count * EntrySize))
                using (var w = new BinaryWriter(ms))
                {
                    w.Write(data, 0, (int)countAt);
                    w.Write(n + kept.Count);
                    w.Write(data, (int)countAt + 4, data.Length - (int)countAt - 4);
                    foreach (var k in kept)
                    {
                        w.Write(k.Hash);
                        w.Write(k.Position.x);
                        w.Write(k.Position.y);
                        w.Write(k.Position.z);
                        w.Write(k.Placed);
                    }
                    w.Flush();
                    combined = ms.ToArray();
                }
                var output = new ZPackage(combined);
                if (!_reportedSave) _log.LogInfo($"Keep missing mods' data: world save includes the {kept.Count} kept location records.");
                _reportedSave = true;
                return output.GetCompressed();
            }
            catch (Exception e)
            {
                if (!_reportedSave) _log.LogWarning("Keep missing mods' data: could not add the kept locations to this save (" + e.Message + ").");
                _reportedSave = true;
                return pkg.GetCompressed();
            }
        }

        // ---------------- objects ----------------

        private static string ApplyObjects(Harmony harmony)
        {
            var create = AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObject", new[] { typeof(ZDO) });
            var destroy = AccessTools.DeclaredMethod(typeof(ZDOMan), nameof(ZDOMan.DestroyZDO), new[] { typeof(ZDO) });
            if (create == null || create.ReturnType != typeof(GameObject) || destroy == null)
                return "the game's object loading has changed since this fix was written";
            harmony.Patch(create, prefix: new HarmonyMethod(typeof(MissingModDataKeepFix), nameof(CreatePrefix)) { priority = Priority.First });
            harmony.Patch(destroy, prefix: new HarmonyMethod(typeof(MissingModDataKeepFix), nameof(DestroyPrefix)) { priority = Priority.First });
            return null;
        }

        // An object whose type no loaded mod registers: on a server or host, mark it handled and skip creating it.
        private static bool CreatePrefix(ZNetScene __instance, ZDO zdo, ref GameObject __result)
        {
            _keeping = null;
            if (zdo == null) return true;
            int prefab = zdo.GetPrefab();
            if (prefab == 0 || __instance.GetPrefab(prefab) != null) return true;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;
            zdo.Created = true; // in memory only: the game neither tries it again nor destroys it this session
            _keeping = zdo;
            __result = null;
            if (KeptObjects.TryGetValue(prefab, out int seen)) KeptObjects[prefab] = seen + 1;
            else
            {
                KeptObjects[prefab] = 1;
                if (KeptObjects.Count <= 25)
                    _log.LogWarning($"Keep missing mods' data: objects of unknown type {prefab} (a mod is missing or failed to load) are kept in the world instead of deleted. " +
                                    "The game's 'Destroyed invalid prefab ZDO' line for them is wrong: they stay.");
            }
            return false;
        }

        // The game's next step for an object CreateObject returned nothing for is DestroyZDO: skip it for the kept one.
        private static bool DestroyPrefix(ZDO zdo)
        {
            if (_keeping == null || !ReferenceEquals(zdo, _keeping)) return true;
            _keeping = null;
            return false;
        }

    }
}
