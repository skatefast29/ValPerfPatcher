using System;
using System.Collections.Generic;
using HarmonyLib;
using HearthBelow.VoxelMagic;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// VoxelWorld.PumpAllZones runs from Game.Update every frame. It always copies every voxel zone into a
    /// list and sorts it with a fresh closure (nearest zone first), then calls Pump() on each zone. Pump()
    /// does nothing unless the zone has dirty chunks or a pending swap, which is rare once the world has
    /// loaded. On frames where no zone has work, this prefix does only the per-frame parts that matter
    /// (DistantLod.Tick and the overworld visibility sync) and skips the sort and the loop.
    /// Any frame where at least one zone has work runs the original method unchanged.
    /// </summary>
    internal static class HearthBelowFix
    {
        public const string TargetGuid = "Azumatt.HearthBelow";

        private static Dictionary<Heightmap, VoxelZone> _zones;
        private static AccessTools.FieldRef<VoxelZone, HashSet<Vector3Int>> _dirty;
        private static AccessTools.FieldRef<VoxelZone, bool> _swapPending;
        private static Action _syncOverworldVisibility;

        public static string Apply(Harmony harmony)
        {
            var pump = AccessTools.Method(typeof(VoxelWorld), nameof(VoxelWorld.PumpAllZones));
            var zonesField = AccessTools.Field(typeof(VoxelWorld), "Zones");
            var sync = AccessTools.Method(typeof(VoxelWorld), "SyncOverworldVisibility");
            if (pump == null || zonesField == null || sync == null)
                return "HearthBelow's code has changed since this fix was written";
            if (AccessTools.Field(typeof(VoxelZone), "_dirty")?.FieldType != typeof(HashSet<Vector3Int>) ||
                AccessTools.Field(typeof(VoxelZone), "_swapPending")?.FieldType != typeof(bool))
                return "HearthBelow's zone fields have changed since this fix was written";

            _zones = (Dictionary<Heightmap, VoxelZone>)zonesField.GetValue(null);
            _dirty = AccessTools.FieldRefAccess<VoxelZone, HashSet<Vector3Int>>("_dirty");
            _swapPending = AccessTools.FieldRefAccess<VoxelZone, bool>("_swapPending");
            _syncOverworldVisibility = AccessTools.MethodDelegate<Action>(sync);

            harmony.Patch(pump, prefix: new HarmonyMethod(typeof(HearthBelowFix), nameof(Prefix)) { priority = Priority.First });
            return null;
        }

        private static bool Prefix()
        {
            if (_zones == null || _zones.Count == 0)
                return true; // the original's cheap path (also resets its debug view)

            foreach (var zone in _zones.Values)
            {
                if (zone == null) continue;
                if (_swapPending(zone)) return true;
                var dirty = _dirty(zone);
                if (dirty != null && dirty.Count > 0) return true;
            }

            // Nothing to pump: same per-frame work as the original minus the sort and empty Pump() calls.
            DistantLod.Tick();
            _syncOverworldVisibility();
            return false;
        }
    }
}
