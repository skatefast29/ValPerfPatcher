using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValPerfPatcher
{
    /// <summary>
    /// Many mods ship their models and textures as LZMA-compressed asset bundles: inside their DLL (loaded with
    /// AssetBundle.LoadFromStream, directly or through Jotunn's AssetUtils or the ItemManager/PieceManager
    /// libraries), or as loose files (More World Locations' ~180 location bundles, loaded by the game's own asset
    /// streaming with LoadFromFile/LoadFromFileAsync whenever such a location is needed). Unity has to unpack an
    /// LZMA bundle completely every time it is loaded (RtD Ocean 2.5 s, More World Locations 2 s, Bestiary 1.7 s,
    /// Valheim Armory 1 s, OdinShip at startup: about 8 s in all).
    ///
    /// This works for any mod, whichever way it loads a bundle (stream, memory or file, normal or async): the first
    /// time an LZMA bundle is loaded, the load happens as usual and Unity converts a copy to LZ4 (the format Unity
    /// recommends for runtime loading) in the background, into BepInEx\cache. From then on the same bundle is loaded
    /// from that copy, which only reads what is used. Same assets: only the file's compression changes. The game's
    /// own bundles are already LZ4 and are left alone.
    ///
    /// A copy is found by the bundle's size, a hash of its start and its last megabyte, and the game's Unity version.
    /// LZMA output after any change differs all the way to the end, so a mod update gets a new copy, and so does a
    /// game update that changes Unity. Copies not used for 30 days are
    /// deleted. Anything unexpected (not a Unity bundle, not LZMA, a CRC check asked for, a copy that won't load)
    /// falls back to the usual load.
    /// </summary>
    internal static class BundleCacheFix
    {
        private const long MinBundleBytes = 256 * 1024;
        private const int TailBytes = 1 << 20;
        private const string Extension = ".lz4bundle";

        private static string _dir;
        private static ManualLogSource _log;
        private static readonly HashSet<string> Converting = new HashSet<string>();
        private static readonly Dictionary<string, FileKey> FileKeys = new Dictionary<string, FileKey>(StringComparer.OrdinalIgnoreCase);
        internal static int LoadedFromCopy, Queued, Converted, Failed;
        internal static double SourceMegabytes;

        private sealed class FileKey
        {
            public long Length;
            public DateTime Written;
            public string Key; // null: not an LZMA bundle
        }

        public static string Apply(Harmony harmony, ManualLogSource log)
        {
            _log = log;
            _dir = Path.Combine(Path.Combine(Paths.CachePath, "ValPerfPatcher"), "bundles");
            Directory.CreateDirectory(_dir);
            CleanUp();

            var t = typeof(AssetBundle);
            var patches = new (string method, Type[] args, string prefix)[]
            {
                (nameof(AssetBundle.LoadFromStream), new[] { typeof(Stream) }, nameof(Stream1)),
                (nameof(AssetBundle.LoadFromStream), new[] { typeof(Stream), typeof(uint) }, nameof(Stream2)),
                (nameof(AssetBundle.LoadFromStream), new[] { typeof(Stream), typeof(uint), typeof(uint) }, nameof(Stream2)),
                (nameof(AssetBundle.LoadFromStreamAsync), new[] { typeof(Stream) }, nameof(StreamAsync1)),
                (nameof(AssetBundle.LoadFromStreamAsync), new[] { typeof(Stream), typeof(uint) }, nameof(StreamAsync2)),
                (nameof(AssetBundle.LoadFromStreamAsync), new[] { typeof(Stream), typeof(uint), typeof(uint) }, nameof(StreamAsync2)),
                (nameof(AssetBundle.LoadFromMemory), new[] { typeof(byte[]) }, nameof(Memory1)),
                (nameof(AssetBundle.LoadFromMemory), new[] { typeof(byte[]), typeof(uint) }, nameof(Memory2)),
                (nameof(AssetBundle.LoadFromMemoryAsync), new[] { typeof(byte[]) }, nameof(MemoryAsync1)),
                (nameof(AssetBundle.LoadFromMemoryAsync), new[] { typeof(byte[]), typeof(uint) }, nameof(MemoryAsync2)),
                (nameof(AssetBundle.LoadFromFile), new[] { typeof(string) }, nameof(File1)),
                (nameof(AssetBundle.LoadFromFile), new[] { typeof(string), typeof(uint) }, nameof(File2)),
                (nameof(AssetBundle.LoadFromFile), new[] { typeof(string), typeof(uint), typeof(ulong) }, nameof(File3)),
                (nameof(AssetBundle.LoadFromFileAsync), new[] { typeof(string) }, nameof(File1)),
                (nameof(AssetBundle.LoadFromFileAsync), new[] { typeof(string), typeof(uint) }, nameof(File2)),
                (nameof(AssetBundle.LoadFromFileAsync), new[] { typeof(string), typeof(uint), typeof(ulong) }, nameof(File3)),
            };
            var recompress = AccessTools.DeclaredMethod(t, nameof(AssetBundle.RecompressAssetBundleAsync));
            var targets = new List<(System.Reflection.MethodInfo, string)>();
            foreach (var (method, args, prefix) in patches)
            {
                var m = AccessTools.DeclaredMethod(t, method, args);
                if (m == null || recompress == null)
                    return "Unity's asset bundle loading has changed since this fix was written";
                targets.Add((m, prefix));
            }
            foreach (var (m, prefix) in targets)
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(BundleCacheFix), prefix));
            return null;
        }

        // Loads from a stream or memory: when there is a fast copy, load that instead (prefix returns false).
        private static bool Stream1(Stream stream, ref AssetBundle __result) => Replace(CopyFor(stream), ref __result);
        private static bool Stream2(Stream stream, uint crc, ref AssetBundle __result) => crc != 0 || Replace(CopyFor(stream), ref __result);
        private static bool StreamAsync1(Stream stream, ref AssetBundleCreateRequest __result) => ReplaceAsync(CopyFor(stream), ref __result);
        private static bool StreamAsync2(Stream stream, uint crc, ref AssetBundleCreateRequest __result) => crc != 0 || ReplaceAsync(CopyFor(stream), ref __result);
        private static bool Memory1(byte[] binary, ref AssetBundle __result) => Replace(CopyFor(binary), ref __result);
        private static bool Memory2(byte[] binary, uint crc, ref AssetBundle __result) => crc != 0 || Replace(CopyFor(binary), ref __result);
        private static bool MemoryAsync1(byte[] binary, ref AssetBundleCreateRequest __result) => ReplaceAsync(CopyFor(binary), ref __result);
        private static bool MemoryAsync2(byte[] binary, uint crc, ref AssetBundleCreateRequest __result) => crc != 0 || ReplaceAsync(CopyFor(binary), ref __result);

        // Loads from a file: point the same call at the fast copy.
        private static void File1(ref string path) => Redirect(ref path);
        private static void File2(ref string path, uint crc) { if (crc == 0) Redirect(ref path); }
        private static void File3(ref string path, uint crc, ulong offset) { if (crc == 0 && offset == 0) Redirect(ref path); }

        private static bool Replace(string copy, ref AssetBundle __result)
        {
            if (copy == null) return true;
            var bundle = AssetBundle.LoadFromFile(copy);
            if (bundle == null)
            {
                _log.LogWarning($"Asset bundle copy {Path.GetFileName(copy)} didn't load; using the mod's own bundle.");
                return true;
            }
            LoadedFromCopy++;
            __result = bundle;
            return false;
        }

        private static bool ReplaceAsync(string copy, ref AssetBundleCreateRequest __result)
        {
            if (copy == null) return true;
            var request = AssetBundle.LoadFromFileAsync(copy);
            if (request == null) return true;
            LoadedFromCopy++;
            __result = request;
            return false;
        }

        private static void Redirect(ref string path)
        {
            string copy = CopyFor(path);
            if (copy == null) return;
            LoadedFromCopy++;
            path = copy;
        }

        private static string CopyFor(byte[] binary)
        {
            if (binary == null || binary.Length < MinBundleBytes) return null;
            using (var stream = new MemoryStream(binary, false))
                return CopyFor(stream);
        }

        /// <summary>The fast copy's path if there is one; else null (and a conversion is started for an LZMA bundle).</summary>
        private static string CopyFor(Stream stream)
        {
            try
            {
                if (stream == null || !stream.CanRead || !stream.CanSeek) return null;
                long start = stream.Position;
                long length = stream.Length - start;
                if (length < MinBundleBytes) return null;
                string key;
                try { key = SolidLzmaKey(stream, start, length); }
                finally { stream.Position = start; }
                if (key == null) return null;
                string copy = Ready(key);
                if (copy == null && Converting.Add(key))
                {
                    string source = Path.Combine(_dir, key + ".src");
                    try { using (var file = File.Create(source)) stream.CopyTo(file); }
                    finally { stream.Position = start; }
                    StartConversion(key, source, true);
                }
                return copy;
            }
            catch (Exception e)
            {
                _log.LogWarning($"Asset bundle cache: {e.GetType().Name}: {e.Message}; using the usual load.");
                return null;
            }
        }

        private static string CopyFor(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || path.StartsWith(_dir, StringComparison.OrdinalIgnoreCase)) return null;
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < MinBundleBytes) return null;
                // Each file is looked at once per session (the game streams its own bundles through here too).
                if (!FileKeys.TryGetValue(path, out var known) || known.Length != info.Length || known.Written != info.LastWriteTimeUtc)
                {
                    known = new FileKey { Length = info.Length, Written = info.LastWriteTimeUtc };
                    using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        known.Key = SolidLzmaKey(file, 0, info.Length);
                    FileKeys[path] = known;
                }
                if (known.Key == null) return null;
                string copy = Ready(known.Key);
                if (copy == null && Converting.Add(known.Key))
                    StartConversion(known.Key, path, false);
                return copy;
            }
            catch (Exception e)
            {
                _log.LogWarning($"Asset bundle cache: {e.GetType().Name}: {e.Message}; using the usual load.");
                return null;
            }
        }

        // The copy's path if it exists, marked as used for the 30-day clean-up. Only touched before Unity opens it:
        // Unity stops reading a bundle file whose modified time changes while it is open ("Archive file was modified").
        private static string Ready(string key)
        {
            string copy = Path.Combine(_dir, key + Extension);
            if (!File.Exists(copy)) return null;
            try { File.SetLastWriteTimeUtc(copy, DateTime.UtcNow); } catch { }
            return copy;
        }

        // Has Unity convert the bundle at 'source' to LZ4 in the background.
        private static void StartConversion(string key, string source, bool deleteSource)
        {
            string temp = Path.Combine(_dir, key + ".tmp");
            string copy = Path.Combine(_dir, key + Extension);
            SourceMegabytes += new FileInfo(source).Length / 1e6;
            var op = AssetBundle.RecompressAssetBundleAsync(source, temp, BuildCompression.LZ4Runtime, 0, ThreadPriority.Low);
            if (op == null)
            {
                if (deleteSource) TryDelete(source);
                Failed++;
                return;
            }
            Queued++;
            op.completed += _ =>
            {
                try
                {
                    if (op.success && File.Exists(temp))
                    {
                        File.Move(temp, copy);
                        Converted++;
                        _log.LogInfo($"Asset bundle {Path.GetFileName(source)}: fast-loading copy ready ({new FileInfo(copy).Length / 1e6:F1} MB), used from the next load.");
                    }
                    else
                    {
                        Failed++;
                        _log.LogWarning($"Asset bundle {Path.GetFileName(source)}: conversion failed ({op.humanReadableResult}); it keeps loading the usual way.");
                    }
                }
                catch (Exception e) { _log.LogWarning($"Asset bundle {key}: {e.Message}"); }
                finally
                {
                    if (deleteSource) TryDelete(source);
                    TryDelete(temp);
                }
            };
        }

        /// <summary>
        /// A name for the bundle if it is a Unity bundle whose data is LZMA-compressed, else null:
        /// size + FNV-1a hash of the first 4 KB and the last megabyte.
        /// </summary>
        private static string SolidLzmaKey(Stream stream, long start, long length)
        {
            var head = new byte[(int)Math.Min(length, 64 * 1024)];
            ReadFully(stream, head, head.Length);
            if (!IsLzmaBundle(stream, start, length, head)) return null;

            // The Unity version is part of the name: copies are made by this game's Unity, so a game update that
            // brings a new Unity makes new ones instead of trusting copies from the old version.
            var unity = System.Text.Encoding.UTF8.GetBytes(Application.unityVersion);
            ulong hash = Fnv(14695981039346656037UL, unity, unity.Length);
            hash = Fnv(hash, head, Math.Min(head.Length, 4096));
            int tailLength = (int)Math.Min(length, TailBytes);
            var tail = new byte[tailLength];
            stream.Position = start + length - tailLength;
            ReadFully(stream, tail, tailLength);
            hash = Fnv(hash, tail, tailLength);
            return $"{length:x}-{hash:x16}";
        }

        // UnityFS header, then the block list (often LZ4-compressed); true if any data block is LZMA.
        private static bool IsLzmaBundle(Stream stream, long start, long length, byte[] head)
        {
            int i = 0;
            if (head.Length < 64 || ReadCString(head, ref i) != "UnityFS") return false;
            uint version = ReadU32(head, ref i);
            ReadCString(head, ref i);
            ReadCString(head, ref i);
            i += 8; // total size
            int packed = (int)ReadU32(head, ref i);
            int unpacked = (int)ReadU32(head, ref i);
            uint flags = ReadU32(head, ref i);
            if (version >= 7) i = (i + 15) / 16 * 16;
            if (packed <= 0 || unpacked <= 0 || packed > length || unpacked > 64 << 20) return false;

            byte[] info = new byte[packed];
            if ((flags & 0x80) != 0)
            {
                stream.Position = start + length - packed;
                ReadFully(stream, info, packed);
            }
            else if (i + packed <= head.Length) Buffer.BlockCopy(head, i, info, 0, packed);
            else
            {
                stream.Position = start + i;
                ReadFully(stream, info, packed);
            }
            switch (flags & 0x3F)
            {
                case 0: break;
                case 2:
                case 3: info = Lz4Block(info, unpacked); break;
                default: return false; // block list compressed some other way: leave it
            }
            int j = 16; // data hash
            int blocks = ReadI32BE(info, j); j += 4;
            if (blocks <= 0 || 20 + blocks * 10 > info.Length) return false;
            for (int b = 0; b < blocks; b++, j += 10)
                if ((((info[j + 8] << 8) | info[j + 9]) & 0x3F) == 1) return true;
            return false;
        }

        private static byte[] Lz4Block(byte[] src, int size)
        {
            var dst = new byte[size];
            int i = 0, o = 0;
            while (i < src.Length && o < size)
            {
                int token = src[i++];
                int literals = token >> 4;
                if (literals == 15) { int b; do { b = src[i++]; literals += b; } while (b == 255); }
                Buffer.BlockCopy(src, i, dst, o, literals);
                i += literals; o += literals;
                if (i >= src.Length || o >= size) break;
                int offset = src[i] | (src[i + 1] << 8); i += 2;
                int match = token & 15;
                if (match == 15) { int b; do { b = src[i++]; match += b; } while (b == 255); }
                match += 4;
                for (int k = 0; k < match; k++, o++) dst[o] = dst[o - offset];
            }
            return dst;
        }

        private static ulong Fnv(ulong hash, byte[] data, int count)
        {
            for (int k = 0; k < count; k++) hash = (hash ^ data[k]) * 1099511628211UL;
            return hash;
        }

        private static string ReadCString(byte[] b, ref int i)
        {
            int s = i;
            while (i < b.Length && b[i] != 0) i++;
            string text = System.Text.Encoding.ASCII.GetString(b, s, i - s);
            i++;
            return text;
        }

        private static uint ReadU32(byte[] b, ref int i)
        {
            uint v = ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3];
            i += 4;
            return v;
        }

        private static int ReadI32BE(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];

        private static void ReadFully(Stream s, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buffer, read, count - read);
                if (n <= 0) throw new EndOfStreamException();
                read += n;
            }
        }

        // Leftovers of a conversion cut short by closing the game, and copies unused for 30 days.
        private static void CleanUp()
        {
            foreach (var f in Directory.GetFiles(_dir))
            {
                if (f.EndsWith(".src") || f.EndsWith(".tmp")) TryDelete(f);
                else if (f.EndsWith(Extension) && File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-30)) TryDelete(f);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
