using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Every character save packs the map (Minimap.GetMapData): one byte per pixel of the 2048 x 2048 explored map, twice
    /// (own and shared exploration), written one at a time into a growing buffer and then compressed. About 105 ms of a
    /// ~135 ms freeze per save on the main thread, plus ~25 MB of garbage. A server tells every player to save on its
    /// 30-minute world save, and each game also saves on its own 30-minute timer.
    ///
    /// For those two regular saves (Game.UpdateSaving and the server's SavePlayerProfile call) this copies the explored
    /// bits and the saved pins on the main thread (about a millisecond), packs and compresses that copy on a background
    /// thread into the same bytes the game would write, and when it is done, on a later frame, runs the game's own
    /// Game.SavePlayerProfile with those bytes ready (its map step then takes them instead of packing). If nothing on the
    /// map changed since the last save, the last bytes are used again right away.
    /// Every other save (logout, quitting, sleeping, the save command) runs exactly as before, and a pending background
    /// save is then dropped, so an older copy never overwrites a newer save. If anything goes wrong the save is done the
    /// game's way and the fix stays off for the session.
    /// Before patching, the game's GetMapData is checked to still write the fields in the order analysed and its packages
    /// to still be GZip-compressed, and every packed map is read back (unpacked as the game's loader does, piece by piece
    /// so it makes no garbage) on the background thread before it is used.
    /// </summary>
    internal static class MapSaveFix
    {
        private static ManualLogSource _log;

        private static AccessTools.FieldRef<Minimap, BitArray> _explored;
        private static AccessTools.FieldRef<Minimap, BitArray> _exploredOthers;
        private static AccessTools.FieldRef<Minimap, List<Minimap.PinData>> _pins;
        private static MethodInfo _getMapData;
        private static int _mapVersion;

        private static bool _inTimer;         // inside Game.UpdateSaving
        private static int _generation;       // bumped by every save that runs the game's way
        private static Job _job;              // the background save in flight, if any
        private static byte[] _replayBytes;   // handed to GetMapData during our own save
        private static Snapshot _last;        // what the last background save packed ...
        private static byte[] _lastBytes;     // ... and its bytes
        private static byte[] _buffer;        // packing buffer, reused (one job at a time)
        private static byte[] _checkBuffer;   // read-back buffer, reused (one job at a time)
        private static bool _disabled;
        private static bool _reported;

        private sealed class Pin
        {
            public string Name, Author;
            public Vector3 Pos;
            public int Type;
            public bool Checked;
            public long Owner;
        }

        private sealed class Snapshot
        {
            public int Size;
            public int[] Explored, Others;
            public List<Pin> Pins;
            public bool PublicPosition;
        }

        private sealed class Job
        {
            public int Generation;
            public bool SetLogoutPoint;
            public Snapshot Snap;
            public byte[] Result;
            public Exception Error;
            public double PackMs, MainMs;
            public volatile bool Done;
        }

        public static string Apply(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            _getMapData = AccessTools.DeclaredMethod(typeof(Minimap), "GetMapData", Type.EmptyTypes);
            var save = AccessTools.DeclaredMethod(typeof(Game), nameof(Game.SavePlayerProfile), new[] { typeof(bool), typeof(bool) });
            var updateSaving = AccessTools.DeclaredMethod(typeof(Game), "UpdateSaving", new[] { typeof(float) });
            var update = AccessTools.DeclaredMethod(typeof(Game), "Update", Type.EmptyTypes);
            if (_getMapData == null || _getMapData.ReturnType != typeof(byte[]) || save == null || updateSaving == null || update == null)
                return "the game's character save has changed since this fix was written";
            if (AccessTools.Field(typeof(Minimap), "m_explored")?.FieldType != typeof(BitArray) ||
                AccessTools.Field(typeof(Minimap), "m_exploredOthers")?.FieldType != typeof(BitArray) ||
                AccessTools.Field(typeof(Minimap), "m_pins")?.FieldType != typeof(List<Minimap.PinData>))
                return "the game's map fields have changed since this fix was written";
            var problem = CheckMapFormat() ?? CheckCompression();
            if (problem != null) return problem;

            _explored = AccessTools.FieldRefAccess<Minimap, BitArray>("m_explored");
            _exploredOthers = AccessTools.FieldRefAccess<Minimap, BitArray>("m_exploredOthers");
            _pins = AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>("m_pins");

            harmony.Patch(updateSaving,
                prefix: new HarmonyMethod(typeof(MapSaveFix), nameof(TimerStart)),
                finalizer: new HarmonyMethod(typeof(MapSaveFix), nameof(TimerEnd)));
            harmony.Patch(save,
                prefix: new HarmonyMethod(typeof(MapSaveFix), nameof(SavePrefix)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(MapSaveFix), nameof(SaveEnd)));
            harmony.Patch(_getMapData, prefix: new HarmonyMethod(typeof(MapSaveFix), nameof(MapDataPrefix)) { priority = Priority.First });
            harmony.Patch(update, postfix: new HarmonyMethod(typeof(MapSaveFix), nameof(UpdatePostfix)));
            harmony.Patch(AccessTools.Method(typeof(Game), "Shutdown"),
                postfix: new HarmonyMethod(typeof(MapSaveFix), nameof(Clear)));
            return null;
        }

        /// <summary>
        /// GetMapData must write: version (a constant), size, own explored bits, shared explored bits, pin count, then per
        /// saved pin name, position, type, checked, owner, author, then the public-position flag, then compress.
        /// Takes the version constant from the code; anything else stands the fix down.
        /// </summary>
        private static string CheckMapFormat()
        {
            List<CodeInstruction> code;
            try { code = PatchProcessor.GetOriginalInstructions(_getMapData); }
            catch (Exception e) { return $"could not read the game's map save ({e.Message})"; }
            var writes = new List<string>();
            var fields = new List<string>();
            int version = -1;
            for (int i = 0; i < code.Count; i++)
            {
                if (code[i].operand is MethodInfo m && m.DeclaringType == typeof(ZPackage) && (m.Name == "Write" || m.Name == "WriteCompressed"))
                {
                    var ps = m.GetParameters();
                    writes.Add(m.Name + "(" + (ps.Length == 1 ? ps[0].ParameterType.Name : "?") + ")");
                    if (writes.Count == 1 && i > 0 && code[i - 1].opcode.Name.StartsWith("ldc.i4"))
                        version = Convert.ToInt32(code[i - 1].opcode == OpCodes.Ldc_I4_8 ? 8 : code[i - 1].operand ?? -1);
                }
                if (code[i].operand is FieldInfo f && (f.DeclaringType == typeof(Minimap.PinData) || f.DeclaringType == typeof(Minimap)) &&
                    (f.Name == "m_explored" || f.Name == "m_exploredOthers" || f.DeclaringType == typeof(Minimap.PinData)))
                    fields.Add(f.Name);
            }
            string expectedWrites = "Write(Int32) Write(Int32) Write(Boolean) Write(Boolean) Write(Int32) Write(String) Write(Vector3) " +
                                    "Write(Int32) Write(Boolean) Write(Int64) Write(String) Write(Boolean) WriteCompressed(ZPackage)";
            string expectedPins = "m_save m_name m_pos m_type m_checked m_ownerID m_author";
            int own = fields.IndexOf("m_explored"), shared = fields.IndexOf("m_exploredOthers");
            if (string.Join(" ", writes) != expectedWrites || !string.Join(" ", fields).EndsWith(expectedPins) ||
                own < 0 || shared < own || version < 1)
                return "the game's map save format has changed since this fix was written";
            _mapVersion = version;
            return null;
        }

        /// <summary>
        /// The packed map is compressed and checked with GZip, as ZPackage.WriteCompressed (Utils.Compress) and
        /// ReadCompressedPackage (Utils.Decompress) do; if either uses something else, the fix stands down.
        /// </summary>
        private static string CheckCompression()
        {
            bool Calls(string method, string utilsMethod)
            {
                var m = AccessTools.DeclaredMethod(typeof(ZPackage), method);
                if (m == null) return false;
                try
                {
                    var calls = PatchProcessor.GetOriginalInstructions(m)
                        .Select(c => c.operand as MethodInfo).Where(c => c != null && c.DeclaringType == typeof(Utils)).ToList();
                    return calls.Count == 1 && calls[0].Name == utilsMethod;
                }
                catch { return false; }
            }
            bool UsesGZip(string utilsMethod)
            {
                var m = AccessTools.DeclaredMethod(typeof(Utils), utilsMethod, new[] { typeof(byte[]) });
                if (m == null) return false;
                try
                {
                    return PatchProcessor.GetOriginalInstructions(m)
                        .Any(c => c.opcode == OpCodes.Newobj && c.operand is ConstructorInfo ctor && ctor.DeclaringType == typeof(GZipStream));
                }
                catch { return false; }
            }
            if (!Calls(nameof(ZPackage.WriteCompressed), "Compress") || !Calls(nameof(ZPackage.ReadCompressedPackage), "Decompress") ||
                !UsesGZip("Compress") || !UsesGZip("Decompress"))
                return "the game's map compression has changed since this fix was written";
            return null;
        }

        private static void TimerStart() => _inTimer = true;
        private static void TimerEnd() => _inTimer = false;

        private static bool SavePrefix(Game __instance, bool setLogoutPoint, bool isFromRpc)
        {
            if (_replayBytes != null)
                return true; // our own save, or a map that hasn't changed: let the game save with those bytes
            if (_disabled || !(_inTimer || isFromRpc))
            {
                _generation++; // a save the game's way is newer than anything still being packed
                return true;
            }
            if (SaveSystem.HasSessionFlag(SaveSystemSessionFlags.DontSaveCharacter) ||
                (isFromRpc && ZNet.instance != null && ZNet.instance.HardSaveBlock()))
                return true; // the game won't save either
            if (Player.m_localPlayer == null || Minimap.instance == null || __instance.GetPlayerProfile() == null)
            {
                _generation++;
                return true;
            }
            if (_job != null)
            {
                if (!_job.Done && _job.Generation == _generation)
                {
                    // One is already on its way; it saves within a moment.
                    _job.SetLogoutPoint |= setLogoutPoint;
                    __instance.m_saveTimer = 0f;
                    return false;
                }
                if (!_job.Done)
                {
                    _generation++; // an outdated one still finishing; do this one the game's way
                    return true;
                }
            }

            var other = OtherMapPatch();
            if (other != null)
            {
                Disable(other + " changes the map save, so it is left to the game");
                _generation++;
                return true;
            }

            Snapshot snap;
            var clock = Stopwatch.StartNew();
            try { snap = Take(Minimap.instance); }
            catch (Exception e)
            {
                Disable("could not copy the map (" + e.Message + ")");
                _generation++;
                return true;
            }
            if (_last != null && _lastBytes != null && Same(snap, _last))
            {
                _replayBytes = _lastBytes; // nothing changed on the map: save now with the last bytes
                return true;
            }

            var job = new Job { Generation = _generation, SetLogoutPoint = setLogoutPoint, Snap = snap, MainMs = clock.Elapsed.TotalMilliseconds };
            _job = job;
            __instance.m_saveTimer = 0f; // as the game does at the start of a save
            Task.Run(() => Pack(job));
            return false;
        }

        private static void SaveEnd() => _replayBytes = null;

        /// <summary>
        /// Another mod's patch on GetMapData (a different map format, extra saved data) would be skipped by our bytes, so
        /// any such patch stands the fix down. Checked at each regular save, as mods can patch it at any time.
        /// </summary>
        private static string OtherMapPatch()
        {
            var info = Harmony.GetPatchInfo(_getMapData);
            if (info == null) return null;
            foreach (var list in new[] { info.Prefixes, info.Postfixes, info.Transpilers, info.Finalizers })
                foreach (var p in list)
                    if (!Ours(p.owner)) return p.owner;
            return null;
        }

        // This patcher, and measuring tools that only time or record calls (ValPerfMonitor, ValAgentBridge's watches).
        private static bool Ours(string owner) =>
            owner == Plugin.Guid || owner == EarlyPlugin.Guid || owner == SaveTiming.HarmonyId ||
            owner.StartsWith("valheimmods.ValPerfMonitor", StringComparison.Ordinal) ||
            owner == "BarryWhite.ValAgentBridge.watch";

        private static bool MapDataPrefix(ref byte[] __result)
        {
            if (_replayBytes == null) return true;
            __result = _replayBytes;
            return false;
        }

        private static void UpdatePostfix(Game __instance)
        {
            var job = _job;
            if (job == null || !job.Done) return;
            _job = null;
            if (job.Generation != _generation) return; // a newer save already ran
            if (job.Error != null || job.Result == null)
            {
                Disable("packing the map failed (" + (job.Error?.Message ?? "no result") + ")");
                __instance.SavePlayerProfile(job.SetLogoutPoint); // the game's way
                return;
            }
            _last = job.Snap;
            _lastBytes = job.Result;
            var clock = Stopwatch.StartNew();
            _replayBytes = job.Result;
            try { __instance.SavePlayerProfile(job.SetLogoutPoint); }
            finally { _replayBytes = null; }
            if (!_reported)
            {
                _reported = true;
                _log.LogInfo($"Character save: map packed on another thread in {job.PackMs:F0} ms ({job.Result.Length:N0} bytes); " +
                             $"on the game's thread {job.MainMs:F1} ms to copy the map, {clock.Elapsed.TotalMilliseconds:F0} ms for the rest of the save.");
            }
        }

        private static void Clear()
        {
            _generation++;
            _last = null;
            _lastBytes = null;
            _buffer = null;
            _replayBytes = null;
        }

        private static void Disable(string why)
        {
            _disabled = true;
            _log.LogWarning($"Character save: {why}; saving the game's way for the rest of this session.");
        }

        // ---- main thread: copy what GetMapData reads ---------------------------------------------

        private static Snapshot Take(Minimap map)
        {
            var explored = _explored(map);
            var others = _exploredOthers(map);
            int size = map.m_textureSize;
            if (explored == null || others == null || explored.Length != size * size || others.Length != size * size)
                throw new InvalidOperationException("unexpected map size");
            var snap = new Snapshot
            {
                Size = size,
                Explored = new int[(explored.Length + 31) / 32],
                Others = new int[(others.Length + 31) / 32],
                Pins = new List<Pin>(),
                PublicPosition = ZNet.instance != null && ZNet.instance.IsReferencePositionPublic(),
            };
            explored.CopyTo(snap.Explored, 0);
            others.CopyTo(snap.Others, 0);
            foreach (var p in _pins(map))
                if (p.m_save)
                    snap.Pins.Add(new Pin
                    {
                        Name = p.m_name, Pos = p.m_pos, Type = (int)p.m_type, Checked = p.m_checked,
                        Owner = p.m_ownerID, Author = p.m_author.ToString(),
                    });
            return snap;
        }

        private static bool Same(Snapshot a, Snapshot b)
        {
            if (a.Size != b.Size || a.PublicPosition != b.PublicPosition || a.Pins.Count != b.Pins.Count) return false;
            for (int i = 0; i < a.Explored.Length; i++)
                if (a.Explored[i] != b.Explored[i] || a.Others[i] != b.Others[i]) return false;
            for (int i = 0; i < a.Pins.Count; i++)
            {
                Pin p = a.Pins[i], q = b.Pins[i];
                // Vector3 == rounds; compare exactly.
                if (p.Name != q.Name || p.Pos.x != q.Pos.x || p.Pos.y != q.Pos.y || p.Pos.z != q.Pos.z || p.Type != q.Type || p.Checked != q.Checked || p.Owner != q.Owner || p.Author != q.Author)
                    return false;
            }
            return true;
        }

        // ---- background thread: the same bytes GetMapData returns -------------------------------

        private static void Pack(Job job)
        {
            var clock = Stopwatch.StartNew();
            try { job.Result = Build(job.Snap, ref _buffer); }
            catch (Exception e) { job.Error = e; }
            job.PackMs = clock.Elapsed.TotalMilliseconds;
            job.Done = true;
        }

        /// <summary>Same layout as GetMapData: version, then compressed (size, explored, shared, pins, public flag).</summary>
        private static byte[] Build(Snapshot s, ref byte[] buffer)
        {
            int cells = s.Size * s.Size;
            var tail = new ZPackage();
            tail.Write(s.Pins.Count);
            foreach (var p in s.Pins)
            {
                tail.Write(p.Name);
                tail.Write(p.Pos);
                tail.Write(p.Type);
                tail.Write(p.Checked);
                tail.Write(p.Owner);
                tail.Write(p.Author);
            }
            tail.Write(s.PublicPosition);
            byte[] tailBytes = tail.GetArray();

            int length = 4 + 2 * cells + tailBytes.Length;
            var buf = buffer;
            if (buf == null || buf.Length < length) buf = buffer = new byte[length];
            WriteInt(buf, 0, s.Size);
            Expand(s.Explored, buf, 4, cells);
            Expand(s.Others, buf, 4 + cells, cells);
            Buffer.BlockCopy(tailBytes, 0, buf, 4 + 2 * cells, tailBytes.Length);

            // Utils.Compress: GZip, fastest, the whole payload in one write.
            byte[] compressed;
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest))
                    gz.Write(buf, 0, length);
                compressed = ms.ToArray();
            }
            // ZPackage: Write(version); WriteCompressed = Write(length) + raw bytes.
            var result = new byte[8 + compressed.Length];
            WriteInt(result, 0, _mapVersion);
            WriteInt(result, 4, compressed.Length);
            Buffer.BlockCopy(compressed, 0, result, 8, compressed.Length);

            // Read it back the way the game loads a map (ZPackage.ReadCompressedPackage: Utils.Decompress, a GZipStream)
            // and compare with what was meant to be written. Unpacked piece by piece into a small reused buffer: the
            // game's own loader copies the 8 MB map about five times, and that much garbage at each save made the game
            // stop for a full garbage collection.
            if (ReadInt(result, 0) != _mapVersion) throw new InvalidDataException("version did not read back");
            if (ReadInt(result, 4) != compressed.Length) throw new InvalidDataException("length did not read back");
            var chunk = _checkBuffer ?? (_checkBuffer = new byte[65536]);
            int read = 0;
            using (var gz = new GZipStream(new MemoryStream(result, 8, compressed.Length, false), CompressionMode.Decompress))
            {
                int n;
                while ((n = gz.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (read + n > length) throw new InvalidDataException("map did not read back (too long)");
                    for (int i = 0; i < n; i++)
                        if (chunk[i] != buf[read + i]) throw new InvalidDataException("map did not read back (byte " + (read + i) + ")");
                    read += n;
                }
            }
            if (read != length) throw new InvalidDataException("map did not read back (length)");
            return result;
        }

        private static int ReadInt(byte[] src, int offset) =>
            src[offset] | (src[offset + 1] << 8) | (src[offset + 2] << 16) | (src[offset + 3] << 24);

        // BitArray.CopyTo(int[]): bit i is bit (i % 32) of word i / 32. ZPackage.Write(bool) writes one byte, 0 or 1.
        private static void Expand(int[] words, byte[] dest, int offset, int cells)
        {
            for (int i = 0; i < cells; i++)
                dest[offset + i] = (byte)((words[i >> 5] >> (i & 31)) & 1);
        }

        private static void WriteInt(byte[] dest, int offset, int value)
        {
            dest[offset] = (byte)value;
            dest[offset + 1] = (byte)(value >> 8);
            dest[offset + 2] = (byte)(value >> 16);
            dest[offset + 3] = (byte)(value >> 24);
        }

        // ---- check, run by hand (debug/call_method ValPerfPatcher.MapSaveFix SelfTest) -------------

        /// <summary>Packs the current map both ways and compares the bytes. Changes nothing.</summary>
        public static string SelfTest()
        {
            var map = Minimap.instance;
            if (map == null || _getMapData == null) return "no map (not in a world, or the fix is not applied)";
            var clock = Stopwatch.StartNew();
            var snap = Take(map);
            double copyMs = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            byte[] buffer = null;
            byte[] ours = Build(snap, ref buffer);
            double oursMs = clock.Elapsed.TotalMilliseconds;
            // Garbage of a regular save: the packing buffer is kept from the last one. Rough (other threads allocate too).
            long before = GC.GetTotalMemory(false);
            Build(snap, ref buffer);
            double oursMb = (GC.GetTotalMemory(false) - before) / 1048576.0;
            clock.Restart();
            var saved = _replayBytes;
            _replayBytes = null;
            byte[] game;
            before = GC.GetTotalMemory(false);
            try { game = (byte[])_getMapData.Invoke(map, null); }
            finally { _replayBytes = saved; }
            double gameMs = clock.Elapsed.TotalMilliseconds;
            double gameMb = (GC.GetTotalMemory(false) - before) / 1048576.0;

            bool sameBytes = ours.Length == game.Length;
            for (int i = 0; sameBytes && i < ours.Length; i++) sameBytes = ours[i] == game[i];
            var a = new ZPackage(ours);
            var b = new ZPackage(game);
            int va = a.ReadInt(), vb = b.ReadInt();
            byte[] pa = a.ReadCompressedPackage().GetArray(), pb = b.ReadCompressedPackage().GetArray();
            bool samePayload = va == vb && pa.Length == pb.Length;
            for (int i = 0; samePayload && i < pa.Length; i++) samePayload = pa[i] == pb[i];
            int explored = 0;
            foreach (var w in snap.Explored) explored += CountBits(w);
            return $"payload identical: {samePayload} ({pa.Length:N0} bytes, version {va}/{vb}), compressed bytes identical: {sameBytes} " +
                   $"({ours.Length:N0} / {game.Length:N0}); {snap.Pins.Count} saved pins, {explored:N0} explored pixels; " +
                   $"copy {copyMs:F1} ms, background pack {oursMs:F0} ms, game's GetMapData {gameMs:F0} ms; " +
                   $"memory used: background pack about {oursMb:F1} MB, game's GetMapData about {gameMb:F1} MB";
        }

        private static int CountBits(int v)
        {
            uint x = (uint)v;
            int n = 0;
            while (x != 0) { x &= x - 1; n++; }
            return n;
        }
    }
}
