using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PrKit
{
    // A mod's monster images, made on the player's machine from their own game (a distributed mod
    // ships no game files): a recipe (JSON, made by the porter: tools/port_sprites.py images mode)
    // lists each image as
    //   a recolor:   the game's art `from` (a texture in the group's asset bundle) with a color table
    //                (game rgb -> the mod's rgb; the PR's monster art has <= 16 colors),
    //   new art:     the mod's own sprite as palette + pixel indexes (zlib, base64),
    //   an overlay:  the game's picture with the mod's own pixels painted over it (zlib RGBA), and
    //                optionally monster layers over that ("parts": a plugin erases one when it dies),
    // each with an optional bestiary thumbnail. Ensure writes them as Magicite sprites (PNG +
    // .spriteData, keys/Export.json per group) into the mod's Magicite folder, where Magicite serves
    // them under the asset addresses the mod's monster_asset rows name
    // (Assets/GameAssets/Serial/Res/Battle/Monster/<GROUP>/<KEY>; thumbnails under
    // Res/UI/Common/Library/Thumbnail/<GROUP>/Thumbnail). Run it before Magicite scans its folder
    // (a plugin's Load); a marker with the recipe's id makes later starts skip it.
    // Game-free (System only): BNW's Rules.Tests runs it on this PC's game, PrKit.Tests on synthetic
    // recipes; tools/pr_images.py and tools/sprite_sheet.build are its Python twins.
    // First used by Brave New World (BnwPlugin/BnwMonsterImages.cs, recipe in bnw_data.json
    // "monster_images").
    public sealed class MonsterImage
    {
        public int Monster, W, H;
        public string Name = "", Key = "", Group = "", From, FromSha1, Sha1 = "", Pixels, Overlay;
        public Dictionary<int, int> Map;       // 0xRRGGBB -> 0xRRGGBB
        // the same as slots (no game colors in the mod's data): the k-th of the source image's distinct opaque
        // colors (sorted as 0xRRGGBB) -> 0xRRGGBB; SlotCount = how many the source has. Resolved into Map against
        // the player's own image (whose sha1 must be FromSha1).
        public List<KeyValuePair<int, int>> SlotMap;
        public int SlotCount;
        public bool SlotsResolved;             // SlotMap already turned into Map entries (ResolveSlots)
        public int[] Palette;                  // new art: 0xRRGGBB per palette slot
        public MonsterThumb Thumb;             // its bestiary thumbnail, if any
        public List<MonsterPart> Parts;        // an overlay's monster layers (drawn over it; a plugin can erase one)
    }

    // One monster's own pixels in an overlay image (zlib RGBA, the image's size): e.g. FF6's final battle
    // tower, where a mod's statue parts are drawn into the picture and vanish when they die.
    public sealed class MonsterPart
    {
        public int Monster;                    // the PR monster id
        public string Overlay = "";
    }

    // The bestiary's habitat-map picture (226x226, group mt_<group>_<asset>, key "Thumbnail"): a
    // recolor's is its base art's thumbnail (FromGroup) with the same color table; new art's is the
    // art at Scale x with its top left at (X, Y).
    public sealed class MonsterThumb
    {
        public const int Size = 226;
        public string Group = "", FromGroup, Sha1 = "";
        public int X, Y, Scale = 3;
    }

    public sealed class MonsterImageRecipe
    {
        public string Id = "";
        public List<MonsterImage> Images = new List<MonsterImage>();

        // The recipe in a JSON document's `property` (e.g. a mod's data file), or null if it has none.
        public static MonsterImageRecipe FromJson(string json, string property)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(property, out var m) ? Parse(m) : null;
        }

        public static MonsterImageRecipe Parse(JsonElement m)
        {
            var r = new MonsterImageRecipe { Id = m.GetProperty("id").GetString() ?? "" };
            foreach (var e in m.GetProperty("images").EnumerateArray())
            {
                var img = new MonsterImage
                {
                    Monster = e.TryGetProperty("monster", out var mo) ? mo.GetInt32() : -1,
                    Name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    Key = e.GetProperty("key").GetString(),
                    Group = e.GetProperty("group").GetString(),
                    W = e.GetProperty("w").GetInt32(),
                    H = e.GetProperty("h").GetInt32(),
                    Sha1 = e.TryGetProperty("sha1", out var s) ? s.GetString() ?? "" : "",
                };
                if (e.TryGetProperty("from", out var f))
                {
                    img.From = f.GetString();
                    img.FromSha1 = e.TryGetProperty("from_sha1", out var fs) ? fs.GetString() : null;
                    img.Map = new Dictionary<int, int>();
                    if (e.TryGetProperty("overlay", out var ov)) img.Overlay = ov.GetString();
                    if (e.TryGetProperty("map", out var mp))
                    foreach (var p in mp.EnumerateArray())
                    {
                        var parts = p.GetString().Split(':');
                        img.Map[Convert.ToInt32(parts[0], 16)] = Convert.ToInt32(parts[1], 16);
                    }
                    if (e.TryGetProperty("map_slots", out var ms))
                    {
                        img.SlotMap = new List<KeyValuePair<int, int>>();
                        foreach (var p in ms.EnumerateArray())
                        {
                            var parts = p.GetString().Split(':');
                            img.SlotMap.Add(new KeyValuePair<int, int>(int.Parse(parts[0]), Convert.ToInt32(parts[1], 16)));
                        }
                        img.SlotCount = e.TryGetProperty("colors", out var nc) ? nc.GetInt32() : -1;
                    }
                }
                else
                {
                    img.Palette = e.GetProperty("palette").EnumerateArray().Select(c => Convert.ToInt32(c.GetString(), 16)).ToArray();
                    img.Pixels = e.GetProperty("pixels").GetString();
                }
                if (e.TryGetProperty("parts", out var pa) && pa.ValueKind == JsonValueKind.Array)
                    img.Parts = pa.EnumerateArray().Select(q => new MonsterPart
                    {
                        Monster = q.GetProperty("monster").GetInt32(),
                        Overlay = q.GetProperty("overlay").GetString() ?? "",
                    }).ToList();
                if (e.TryGetProperty("thumb", out var t) && t.ValueKind == JsonValueKind.Object)
                {
                    img.Thumb = new MonsterThumb
                    {
                        Group = t.GetProperty("group").GetString(),
                        FromGroup = t.TryGetProperty("from_group", out var fg) ? fg.GetString() : null,
                        Sha1 = t.TryGetProperty("sha1", out var ts) ? ts.GetString() ?? "" : "",
                        X = t.TryGetProperty("x", out var x) ? x.GetInt32() : 0,
                        Y = t.TryGetProperty("y", out var y) ? y.GetInt32() : 0,
                        Scale = t.TryGetProperty("scale", out var k) ? k.GetInt32() : 3,
                    };
                }
                r.Images.Add(img);
            }
            return r;
        }
    }

    public static class MonsterImages
    {
        public const string Marker = "monster_images.txt";
        const string MonsterPath = "Assets/GameAssets/Serial/Res/Battle/Monster";
        const string ThumbPath = "Assets/GameAssets/Serial/Res/UI/Common/Library/Thumbnail";
        public static readonly string BundleDir = Path.Combine("FINAL FANTASY VI_Data", "StreamingAssets", "aa", "StandaloneWindows64");

        // The marker: the recipe id, then the group folders it made (removed when the recipe changes).
        public static bool UpToDate(string modDir, MonsterImageRecipe r)
        {
            try
            {
                var p = Path.Combine(modDir, Marker);
                if (!File.Exists(p)) return false;
                var lines = File.ReadAllLines(p);
                if (lines.Length == 0 || lines[0].Trim() != r.Id) return false;
                return r.Images.All(i => File.Exists(PngPath(modDir, i)) && (i.Thumb == null || File.Exists(ThumbPngPath(modDir, i.Thumb))));
            }
            catch { return false; }
        }

        public static string PngPath(string modDir, MonsterImage i) =>
            Path.Combine(modDir, i.Group, MonsterPath, i.Group.ToUpperInvariant(), i.Key + ".png");

        public static string ThumbPngPath(string modDir, MonsterThumb t) =>
            Path.Combine(modDir, t.Group, ThumbPath, t.Group.ToUpperInvariant(), "Thumbnail.png");

        // Makes every image (when the marker isn't this recipe's); returns a one-line summary.
        public static string Ensure(string gameRoot, string modDir, MonsterImageRecipe r, Action<string> warn, bool force = false)
        {
            if (r == null || r.Images.Count == 0) return "monster images: none in this data";
            if (!force && UpToDate(modDir, r)) return $"monster images: {r.Images.Count} up to date";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            RemoveOld(modDir);
            var textures = new Dictionary<string, Dictionary<string, BundleTexture>>(StringComparer.OrdinalIgnoreCase);
            int made = 0, thumbs = 0, mismatched = 0;
            // group -> (key, address) that the group's keys/Export.json lists
            var groups = new SortedDictionary<string, List<(string Key, string Address)>>(StringComparer.OrdinalIgnoreCase);
            void List(string group, string key, string address)
            {
                if (!groups.TryGetValue(group, out var l)) groups[group] = l = new List<(string, string)>();
                l.Add((key, address));
            }
            void Check(byte[] rgba, string sha1, string what)
            {
                if (sha1 != "" && Hex(SHA1.HashData(rgba)) != sha1)
                {
                    mismatched++;
                    warn($"monster image {what}: not the image the mod was made with (a game update?); written anyway");
                }
            }
            foreach (var img in r.Images)
            {
                byte[] rgba;
                try
                {
                    rgba = Build(gameRoot, img, textures, warn);
                }
                catch (Exception e)
                {
                    warn($"monster image {img.Key} ({img.Name}): {e.Message}");
                    continue;
                }
                if (rgba == null) continue;
                Check(rgba, img.Sha1, $"{img.Key} ({img.Name})");
                var png = PngPath(modDir, img);
                Directory.CreateDirectory(Path.GetDirectoryName(png));
                File.WriteAllBytes(png, Png(img.W, img.H, rgba));
                File.WriteAllText(Path.ChangeExtension(png, ".spriteData"),
                    $"Rect = [0,0,{img.W},{img.H}]\nPivot = [0.5,0]\nPixelsPerUnit = 1\nBorder = [0,0,0,0]\nWrapMode = Clamp\nFilterMode = Point\n");
                List(img.Group, img.Key, $"{MonsterPath}/{img.Group.ToUpperInvariant()}/{img.Key}");
                made++;
                if (img.Thumb == null) continue;
                try
                {
                    var t = BuildThumb(gameRoot, img, rgba, textures, warn);
                    if (t == null) continue;
                    Check(t, img.Thumb.Sha1, $"{img.Thumb.Group} ({img.Name}'s bestiary thumbnail)");
                    var tp = ThumbPngPath(modDir, img.Thumb);
                    Directory.CreateDirectory(Path.GetDirectoryName(tp));
                    File.WriteAllBytes(tp, Png(MonsterThumb.Size, MonsterThumb.Size, t));
                    File.WriteAllText(Path.ChangeExtension(tp, ".spriteData"),
                        $"Rect = [0,0,{MonsterThumb.Size},{MonsterThumb.Size}]\nPivot = [0.5,0.5]\nPixelsPerUnit = 100\nBorder = [0,0,0,0]\nWrapMode = Clamp\nFilterMode = Bilinear\n");
                    List(img.Thumb.Group, "Thumbnail", $"{ThumbPath}/{img.Thumb.Group.ToUpperInvariant()}/Thumbnail");
                    thumbs++;
                }
                catch (Exception e)
                {
                    warn($"monster image {img.Thumb.Group} ({img.Name}'s bestiary thumbnail): {e.Message}");
                }
            }
            foreach (var (group, list) in groups)
            {
                var keys = Path.Combine(modDir, group, "keys");
                Directory.CreateDirectory(keys);
                var sb = new StringBuilder("{\n  \"keys\": [\n");
                sb.Append(string.Join(",\n", list.Select(i => $"    \"{i.Key}\""))).Append("\n  ],\n  \"values\": [\n");
                sb.Append(string.Join(",\n", list.Select(i => $"    \"{i.Address}\""))).Append("\n  ]\n}\n");
                File.WriteAllText(Path.Combine(keys, "Export.json"), sb.ToString());
            }
            File.WriteAllLines(Path.Combine(modDir, Marker), new[] { r.Id }.Concat(groups.Keys));
            return $"monster images: made {made} of {r.Images.Count} (+{thumbs} bestiary thumbnails) from this game in {sw.ElapsedMilliseconds} ms" +
                   (mismatched > 0 ? $" ({mismatched} differ from the mod's)" : "");
        }

        static void RemoveOld(string modDir)
        {
            var p = Path.Combine(modDir, Marker);
            if (!File.Exists(p)) return;
            foreach (var g in File.ReadAllLines(p).Skip(1))
            {
                var name = g.Trim();
                if (name.Length == 0 || name.Contains("..") || name.IndexOfAny(new[] { '/', '\\' }) >= 0) continue;
                if (!name.StartsWith("mn_", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("mt_", StringComparison.OrdinalIgnoreCase)) continue;
                var dir = Path.Combine(modDir, name);
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
            try { File.Delete(p); } catch { }
        }

        // One asset group's textures (RGBA32 / RGB24, as RGBA) from the game's bundles (cached per group).
        static Dictionary<string, BundleTexture> GroupTextures(string gameRoot, string group,
                                                         Dictionary<string, Dictionary<string, BundleTexture>> cache, Action<string> warn)
        {
            if (cache.TryGetValue(group, out var tex)) return tex;
            var dir = Path.Combine(gameRoot, BundleDir);
            if (bundleDir != dir)       // the folder, listed once (each listing is slow under Wine)
            {
                bundleList = Directory.Exists(dir) ? Directory.GetFiles(dir, "m*_assets_all_*.bundle") : Array.Empty<string>();
                bundleDir = dir;
            }
            var file = bundleList.FirstOrDefault(f => Path.GetFileName(f).StartsWith(group + "_assets_all_", StringComparison.OrdinalIgnoreCase));
            if (file == null) { warn($"monster images: no {group} bundle in {dir}"); return cache[group] = new Dictionary<string, BundleTexture>(); }
            return cache[group] = UnityBundle.Textures(file);
        }

        // A slot-form color table (MonsterImage.SlotMap) as colors, from the player's own source image: its distinct
        // opaque colors, sorted; false (with a warning) when that image isn't the one the mod's table was made on.
        public static bool ResolveSlots(MonsterImage img, byte[] source, Action<string> warn)
        {
            if (img.SlotMap == null || img.SlotsResolved) return true;
            if (!string.IsNullOrEmpty(img.FromSha1) && Hex(SHA1.HashData(source)) != img.FromSha1)
            {
                warn($"monster image {img.Key}: {img.From} differs from the art its colors were made for");
                return false;
            }
            var colors = new SortedSet<int>();
            for (int k = 0; k < source.Length; k += 4)
                if (source[k + 3] != 0) colors.Add(source[k] << 16 | source[k + 1] << 8 | source[k + 2]);
            if (img.SlotCount >= 0 && colors.Count != img.SlotCount)
            {
                warn($"monster image {img.Key}: {img.From} has {colors.Count} colors, the mod expects {img.SlotCount}");
                return false;
            }
            var list = colors.ToList();
            foreach (var kv in img.SlotMap)
                if (kv.Key >= 0 && kv.Key < list.Count) img.Map[list[kv.Key]] = kv.Value;
            img.SlotsResolved = true;
            return true;
        }

        static byte[] Recolor(byte[] rgba, Dictionary<int, int> map)
        {
            var o = (byte[])rgba.Clone();
            for (int k = 0; k < o.Length; k += 4)
            {
                if (o[k + 3] == 0) continue;
                int c = o[k] << 16 | o[k + 1] << 8 | o[k + 2];
                if (map.TryGetValue(c, out var n)) { o[k] = (byte)(n >> 16); o[k + 1] = (byte)(n >> 8); o[k + 2] = (byte)n; }
            }
            return o;
        }

        // An overlay part's RGBA layer (top-down, the image's size; clear where the part isn't).
        public static byte[] PartLayer(MonsterImage img, MonsterPart part) => Unzip(part.Overlay);

        // The image without its parts (what shows where a part is erased), or null when its source isn't there.
        public static byte[] BuildBase(string gameRoot, MonsterImage img,
                                       Dictionary<string, Dictionary<string, BundleTexture>> cache, Action<string> warn) =>
            Build(gameRoot, img, cache, warn, parts: false);

        static byte[] Unzip(string b64)
        {
            using var z = new ZLibStream(new MemoryStream(Convert.FromBase64String(b64)), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            z.CopyTo(ms);
            return ms.ToArray();
        }

        // The image's RGBA (top-down), or null when its source isn't there.
        public static byte[] Build(string gameRoot, MonsterImage img,
                                   Dictionary<string, Dictionary<string, BundleTexture>> cache, Action<string> warn, bool parts = true)
        {
            if (img.Palette != null)
            {
                var idx = Unzip(img.Pixels);
                if (idx.Length != img.W * img.H) throw new InvalidDataException($"{idx.Length} pixels for {img.W}x{img.H}");
                var px = new byte[idx.Length * 4];
                for (int k = 0; k < idx.Length; k++)
                {
                    if (idx[k] == 0) continue;
                    int c = img.Palette[idx[k]];
                    px[4 * k] = (byte)(c >> 16); px[4 * k + 1] = (byte)(c >> 8); px[4 * k + 2] = (byte)c; px[4 * k + 3] = 255;
                }
                return px;
            }
            var tex = GroupTextures(gameRoot, img.Group, cache, warn);
            if (!tex.TryGetValue(img.From, out var t)) { warn($"monster image {img.Key}: no {img.From} in the game's {img.Group} bundle"); return null; }
            if (t.W != img.W || t.H != img.H) { warn($"monster image {img.Key}: {img.From} is {t.W}x{t.H}, the mod expects {img.W}x{img.H}"); return null; }
            if (!ResolveSlots(img, t.Rgba, warn)) return null;
            var o = Recolor(t.Rgba, img.Map);
            var layers = new List<string>();    // the mod's own pixels painted over the game's picture, then its parts
            if (img.Overlay != null) layers.Add(img.Overlay);
            if (parts && img.Parts != null) layers.AddRange(img.Parts.Select(q => q.Overlay));
            foreach (var layer in layers)
            {
                var ov = Unzip(layer);
                if (ov.Length != o.Length) throw new InvalidDataException($"overlay of {ov.Length} bytes for {img.W}x{img.H}");
                for (int k = 0; k < o.Length; k += 4)
                    if (ov[k + 3] != 0) Buffer.BlockCopy(ov, k, o, k, 4);
            }
            return o;
        }

        // The image's bestiary thumbnail (226x226 RGBA), or null when its source isn't there.
        public static byte[] BuildThumb(string gameRoot, MonsterImage img, byte[] art,
                                        Dictionary<string, Dictionary<string, BundleTexture>> cache, Action<string> warn)
        {
            var th = img.Thumb;
            int size = MonsterThumb.Size;
            if (th.FromGroup != null)
            {
                var tex = GroupTextures(gameRoot, th.FromGroup, cache, warn);
                if (!tex.TryGetValue("Thumbnail", out var t) || t.W != size || t.H != size)
                {
                    warn($"monster image {th.Group}: no 226x226 Thumbnail in the game's {th.FromGroup} bundle");
                    return null;
                }
                return Recolor(t.Rgba, img.Map);
            }
            var o = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
            {
                int dy = y - th.Y;
                if (dy < 0 || dy / th.Scale >= img.H) continue;
                for (int x = 0; x < size; x++)
                {
                    int dx = x - th.X;
                    if (dx < 0 || dx / th.Scale >= img.W) continue;
                    Buffer.BlockCopy(art, 4 * ((dy / th.Scale) * img.W + dx / th.Scale), o, 4 * (y * size + x), 4);
                }
            }
            return o;
        }

        static string bundleDir;
        static string[] bundleList;

        public static string Hex(byte[] b) => string.Concat(b.Select(x => x.ToString("x2")));

        // --- PNG (RGBA 8-bit, no filters) -----------------------------------------------------------
        public static byte[] Png(int w, int h, byte[] rgba)
        {
            var raw = new byte[(w * 4 + 1) * h];
            for (int y = 0; y < h; y++) Buffer.BlockCopy(rgba, y * w * 4, raw, y * (w * 4 + 1) + 1, w * 4);
            byte[] idat;
            using (var ms = new MemoryStream())
            {
                using (var z = new ZLibStream(ms, CompressionLevel.Fastest, true)) z.Write(raw, 0, raw.Length);
                idat = ms.ToArray();
            }
            using var o = new MemoryStream();
            o.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            var ihdr = new byte[13];
            BE(ihdr, 0, (uint)w); BE(ihdr, 4, (uint)h);
            ihdr[8] = 8; ihdr[9] = 6;
            Chunk(o, "IHDR", ihdr);
            Chunk(o, "IDAT", idat);
            Chunk(o, "IEND", Array.Empty<byte>());
            return o.ToArray();
        }

        static void BE(byte[] b, int at, uint v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }

        static void Chunk(Stream o, string kind, byte[] data)
        {
            var len = new byte[4]; BE(len, 0, (uint)data.Length); o.Write(len);
            var body = new byte[4 + data.Length];
            Encoding.ASCII.GetBytes(kind, 0, 4, body, 0);
            Buffer.BlockCopy(data, 0, body, 4, data.Length);
            o.Write(body);
            var crc = new byte[4]; BE(crc, 0, Crc32(body)); o.Write(crc);
        }

        static uint[] crcTable;

        static uint Crc32(byte[] b)
        {
            if (crcTable == null)
            {
                var t = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    t[n] = c;
                }
                crcTable = t;
            }
            uint crc = 0xFFFFFFFFu;
            foreach (var x in b) crc = crcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }

    public sealed class BundleTexture
    {
        public string Name;
        public int W, H;
        public byte[] Rgba;      // top-down
    }

    // A Unity asset bundle's RGBA32 and RGB24 textures (as RGBA), read from the file (UnityFS, LZ4 blocks; Unity 2019.4's
    // serialized files): the same steps as tools/pr_bundle.py + tools/pr_images.py.
    public static class UnityBundle
    {
        public static Dictionary<string, BundleTexture> Textures(string path)
        {
            var b = new Reader(File.ReadAllBytes(path));
            var data = Unpack(b, out var nodes);
            var outp = new Dictionary<string, BundleTexture>();
            foreach (var (name, (off, size)) in nodes)
            {
                if (name.EndsWith(".resS") || name.EndsWith(".resource")) continue;
                foreach (var t in SerializedTextures(data, off, size, nodes))
                    outp[t.Name] = t;
            }
            return outp;
        }

        sealed class Reader
        {
            public readonly byte[] D;
            public int I;
            public Reader(byte[] d) { D = d; }
            public string CStr() { int j = Array.IndexOf(D, (byte)0, I); var s = Encoding.UTF8.GetString(D, I, j - I); I = j + 1; return s; }
            public uint U32BE() { uint v = (uint)(D[I] << 24 | D[I + 1] << 16 | D[I + 2] << 8 | D[I + 3]); I += 4; return v; }
            public long I64BE() { long v = 0; for (int k = 0; k < 8; k++) v = v << 8 | D[I + k]; I += 8; return v; }
            public ushort U16BE() { var v = (ushort)(D[I] << 8 | D[I + 1]); I += 2; return v; }
        }

        // The whole uncompressed stream + its nodes (name -> offset, size).
        static byte[] Unpack(Reader r, out List<(string, (long, long))> nodes)
        {
            if (r.CStr() != "UnityFS") throw new InvalidDataException("not a UnityFS bundle");
            uint version = r.U32BE();
            r.CStr(); r.CStr();
            r.I64BE();
            uint cbi = r.U32BE(), ubi = r.U32BE(), flags = r.U32BE();
            int headerEnd = r.I;
            byte[] info = null;
            int start;
            if ((flags & 0x80) != 0)
            {
                info = Decompress(r.D, r.D.Length - (int)cbi, (int)cbi, (int)ubi, (int)(flags & 0x3F));
                start = version >= 7 ? (headerEnd + 15) & ~15 : headerEnd;
            }
            else
            {
                start = -1;
                foreach (var at in version >= 7 ? new[] { (headerEnd + 15) & ~15, headerEnd } : new[] { headerEnd })
                {
                    try { info = Decompress(r.D, at, (int)cbi, (int)ubi, (int)(flags & 0x3F)); start = at + (int)cbi; break; }
                    catch (Exception) { }
                }
                if (info == null) throw new InvalidDataException("unreadable blocks info");
            }
            if ((flags & 0x200) != 0) start = (start + 15) & ~15;
            var ir = new Reader(info) { I = 16 };
            int count = (int)ir.U32BE();
            var blocks = new List<(int pos, int csize, int usize, int kind)>();
            int pos = start;
            long total = 0;
            for (int k = 0; k < count; k++)
            {
                int usize = (int)ir.U32BE(), csize = (int)ir.U32BE();
                int bflags = ir.U16BE();
                blocks.Add((pos, csize, usize, bflags & 0x3F));
                pos += csize; total += usize;
            }
            int ncount = (int)ir.U32BE();
            nodes = new List<(string, (long, long))>();
            for (int k = 0; k < ncount; k++)
            {
                long off = ir.I64BE(), size = ir.I64BE();
                ir.U32BE();
                nodes.Add((ir.CStr(), (off, size)));
            }
            var all = new byte[total];
            int at2 = 0;
            foreach (var bl in blocks)
            {
                var u = Decompress(r.D, bl.pos, bl.csize, bl.usize, bl.kind);
                Buffer.BlockCopy(u, 0, all, at2, u.Length);
                at2 += u.Length;
            }
            return all;
        }

        static byte[] Decompress(byte[] src, int at, int csize, int usize, int kind)
        {
            if (kind == 0) { var o = new byte[csize]; Buffer.BlockCopy(src, at, o, 0, csize); return o; }
            if (kind == 2 || kind == 3) return Lz4(src, at, csize, usize);
            throw new NotSupportedException($"bundle compression {kind}");
        }

        static byte[] Lz4(byte[] s, int i, int n, int size)
        {
            var d = new byte[size];
            int end = i + n, o = 0;
            while (i < end)
            {
                int token = s[i++];
                int lit = token >> 4;
                if (lit == 15) { int b; do { b = s[i++]; lit += b; } while (b == 255); }
                Buffer.BlockCopy(s, i, d, o, lit);
                i += lit; o += lit;
                if (i >= end) break;
                int off = s[i] | s[i + 1] << 8;
                i += 2;
                int ml = token & 15;
                if (ml == 15) { int b; do { b = s[i++]; ml += b; } while (b == 255); }
                ml += 4;
                int from = o - off;
                if (off <= 0 || from < 0) throw new InvalidDataException("lz4: bad offset");
                for (int k = 0; k < ml; k++) d[o++] = d[from + k];
            }
            if (o != size) throw new InvalidDataException($"lz4: {o} bytes, expected {size}");
            return d;
        }

        static IEnumerable<BundleTexture> SerializedTextures(byte[] all, long fileOff, long fileSize, List<(string, (long, long))> nodes)
        {
            var d = new byte[fileSize];
            Buffer.BlockCopy(all, (int)fileOff, d, 0, (int)fileSize);
            uint BE32(int at) => (uint)(d[at] << 24 | d[at + 1] << 16 | d[at + 2] << 8 | d[at + 3]);
            uint version = BE32(8);
            long dataOffset = BE32(12);
            int i = 16;
            bool little = version < 9 || d[i] == 0;
            i += 4;
            if (version >= 22)
            {
                i += 4;                                                     // metadata size
                i += 8;                                                     // file size
                dataOffset = 0; for (int k = 0; k < 8; k++) dataOffset = dataOffset << 8 | d[i + k];
                i += 16;
            }
            int I32(int at) => little ? BitConverter.ToInt32(d, at) : (int)BE32(at);
            long I64(int at) => little ? BitConverter.ToInt64(d, at) : (long)((ulong)BE32(at) << 32 | BE32(at + 4));
            int j = Array.IndexOf(d, (byte)0, i);
            i = j + 1 + 4;                                                  // engine version, platform
            bool tree = true;
            if (version >= 13) tree = d[i++] != 0;
            int count = I32(i); i += 4;
            var classes = new List<int>();
            for (int k = 0; k < count; k++)
            {
                int cid = I32(i); i += 4;
                if (version >= 16) i += 1;
                if (version >= 17) i += 2;
                if (version >= 13)
                {
                    if ((version < 16 && cid < 0) || (version >= 16 && cid == 114)) i += 16;
                    i += 16;
                }
                if (tree)
                {
                    int nn = I32(i), ss = I32(i + 4);
                    i += 8 + nn * (version >= 19 ? 32 : 24) + ss;
                    if (version >= 21) { int deps = I32(i); i += 4 + 4 * deps; }
                }
                classes.Add(cid);
            }
            if (version >= 7 && version < 14) i += 4;
            int objects = I32(i); i += 4;
            for (int k = 0; k < objects; k++)
            {
                if (version >= 14) i = (i + 3) & ~3;
                i += 8;                                                     // path id
                long startObj;
                if (version >= 22) { startObj = I64(i); i += 8; } else { startObj = (uint)I32(i); i += 4; }
                int size = I32(i), typeId = I32(i + 4); i += 8;
                int cid;
                if (version < 16) { cid = little ? BitConverter.ToUInt16(d, i) : d[i] << 8 | d[i + 1]; i += 2; }
                else cid = classes[typeId];
                if (version < 11) i += 2;
                if (version >= 11 && version < 17) i += 2;
                if (version == 15 || version == 16) i += 1;
                if (cid != 28) continue;
                var t = ReadTexture(d, (int)(dataOffset + startObj), (int)(dataOffset + startObj + size), all, nodes);
                if (t != null) yield return t;
            }
        }

        static BundleTexture ReadTexture(byte[] d, int at, int end, byte[] all, List<(string, (long, long))> nodes)
        {
            int n = BitConverter.ToInt32(d, at);
            var name = Encoding.UTF8.GetString(d, at + 4, n);
            int i = at + 4 + ((n + 3) & ~3);
            int w = BitConverter.ToInt32(d, i + 8), h = BitConverter.ToInt32(d, i + 12), size = BitConverter.ToInt32(d, i + 16);
            int bpp = w > 0 && h > 0 ? size / (w * h) : 0;      // RGBA32 (format 4) or RGB24 (3)
            if ((bpp != 3 && bpp != 4) || size != w * h * bpp) return null;
            i += 20;
            if (BitConverter.ToInt32(d, i) != bpp && BitConverter.ToInt32(d, i + 4) != bpp) return null;   // the format (2019.4; 2020+ after MipsStripped)
            byte[] raw = null;
            for (int k = i + 8; k + 16 <= end && raw == null; k += 4)
            {
                int len = BitConverter.ToInt32(d, k);
                if (len == size && k + 4 + size <= end) { raw = new byte[size]; Buffer.BlockCopy(d, k + 4, raw, 0, size); break; }
                if (len != 0) continue;
                foreach (var wide in new[] { false, true })
                {
                    int q = k + 4;
                    long off = wide ? BitConverter.ToInt64(d, q) : BitConverter.ToUInt32(d, q);
                    q += wide ? 8 : 4;
                    int ssize = BitConverter.ToInt32(d, q), plen = BitConverter.ToInt32(d, q + 4);
                    if (ssize != size || plen <= 8 || q + 8 + plen > d.Length) continue;
                    var p = Encoding.UTF8.GetString(d, q + 8, plen);
                    if (!p.StartsWith("archive:")) continue;
                    var node = p.Substring(p.LastIndexOf('/') + 1);
                    var hit = nodes.FirstOrDefault(x => x.Item1 == node);
                    if (hit.Item1 == null) continue;
                    raw = new byte[size];
                    Buffer.BlockCopy(all, (int)(hit.Item2.Item1 + off), raw, 0, size);
                    break;
                }
            }
            if (raw == null) return null;
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)         // rows bottom-up in the file
            {
                int from = (h - 1 - y) * w * bpp, to = y * w * 4;
                if (bpp == 4) { Buffer.BlockCopy(raw, from, rgba, to, w * 4); continue; }
                for (int x = 0; x < w; x++)
                {
                    rgba[to + 4 * x] = raw[from + 3 * x]; rgba[to + 4 * x + 1] = raw[from + 3 * x + 1];
                    rgba[to + 4 * x + 2] = raw[from + 3 * x + 2]; rgba[to + 4 * x + 3] = 255;
                }
            }
            return new BundleTexture { Name = name, W = w, H = h, Rgba = rgba };
        }
    }
}
