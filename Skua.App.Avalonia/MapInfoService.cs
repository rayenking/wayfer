using Skua.Core.Interfaces;
using Skua.Core.Models;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Skua.App.Avalonia;

/// <summary>
/// Reads what is actually inside the current AQW map by inspecting the map SWF
/// the game itself downloads. AQW stores NPC placements and their quest/shop
/// buttons in the map timeline, so this is the authoritative source: no wiki
/// scraping and no guessing.
///
/// The SWF is decompiled with the FFDec jar that ships next to the app (or in
/// the repository during development). Results are cached per map file; the
/// first request for a new map takes a few seconds, later requests are instant.
/// </summary>
public sealed class MapInfoService
{
    private const int CacheVersion = 2;
    private const string MapsHost = "https://game.aq.com/game/gamefiles/";

    private static readonly JsonSerializerOptions CacheJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IScriptInterface _bot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(90)
    };
    private readonly string _cacheRoot = Path.Combine(ClientFileSources.SkuaDIR, "cache");
    private bool _disposed;

    public MapInfoService(IScriptInterface bot)
    {
        _bot = bot;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Wayfer map reader)");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://game.aq.com/game/");
    }

    public async Task<MapInfo> GetCurrentMapAsync(bool refresh, CancellationToken ct)
    {
        string mapName = _bot.Map.Name;
        string filePath = _bot.Map.FilePath;

        if (string.IsNullOrWhiteSpace(mapName) || string.IsNullOrWhiteSpace(filePath))
            throw new MapInfoException("map_not_loaded", "The map is not loaded yet. Wait for the character to finish loading.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string cacheFile = CacheFilePath(filePath);
            if (!refresh && TryLoadCache(cacheFile, filePath, out MapInfo? cached) && cached is not null)
                return cached;

            string swf = await EnsureMapSwfAsync(filePath, ct).ConfigureAwait(false);
            string scriptsRoot = await DecompileAsync(swf, ct).ConfigureAwait(false);

            MapInfo info;
            try
            {
                info = Parse(mapName, filePath, _bot.Map.RoomID.ToString(), _bot.Map.Cells ?? new List<string>(), scriptsRoot, swf);
            }
            catch (MapInfoException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new MapInfoException("map_parse_failed", ex.Message);
            }
            finally
            {
                TryDeleteDirectory(Path.GetDirectoryName(scriptsRoot));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            File.WriteAllText(cacheFile, JsonSerializer.Serialize(new MapCache
            {
                Version = CacheVersion,
                FilePath = filePath,
                Map = info
            }, CacheJson));

            return info;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _gate.Dispose();
        _http.Dispose();
    }

    private bool TryLoadCache(string cacheFile, string filePath, out MapInfo? info)
    {
        info = null;
        if (!File.Exists(cacheFile))
            return false;

        try
        {
            MapCache? cache = JsonSerializer.Deserialize<MapCache>(File.ReadAllText(cacheFile), CacheJson);
            if (cache is null || cache.Version != CacheVersion || cache.Map is null ||
                !string.Equals(cache.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                return false;

            cache.Map.FromCache = true;
            info = cache.Map;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string CacheFilePath(string filePath)
    {
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(filePath.ToLowerInvariant()))).ToLowerInvariant();
        return Path.Combine(_cacheRoot, "mapinfo", hash + ".json");
    }

    private async Task<string> EnsureMapSwfAsync(string filePath, CancellationToken ct)
    {
        string relative = SafeRelativePath(filePath);
        string target = Path.Combine(_cacheRoot, "maps", relative);
        if (File.Exists(target) && new FileInfo(target).Length > 1024)
            return target;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        string url;
        if (filePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            filePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = filePath;
        }
        else
        {
            string clean = filePath.Replace('\\', '/').TrimStart('/');
            string escaped = string.Join('/', clean.Split('/').Select(Uri.EscapeDataString));
            url = filePath.StartsWith("maps/", StringComparison.OrdinalIgnoreCase)
                ? MapsHost + escaped
                : MapsHost + "maps/" + escaped;
        }

        Console.WriteLine($"[map] downloading {url}");
        byte[] bytes = await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
        if (bytes.Length < 1024)
            throw new MapInfoException("map_download_failed", $"The map SWF at {url} was unexpectedly small.");

        await File.WriteAllBytesAsync(target, bytes, ct).ConfigureAwait(false);
        return target;
    }

    private static string SafeRelativePath(string filePath)
    {
        string[] parts = filePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p is not "." and not "..")
            .ToArray();

        if (parts.Length == 0)
            throw new MapInfoException("map_path_invalid", "The game returned an empty map file path.");

        string relative = Path.Combine(parts);
        if (!relative.EndsWith(".swf", StringComparison.OrdinalIgnoreCase))
            relative += ".swf";
        return relative;
    }

    private async Task<string> DecompileAsync(string swf, CancellationToken ct)
    {
        string? jar = FindFfdecJar();
        if (jar is null)
            throw new MapInfoException("ffdec_missing",
                "FFDec was not found. Expected FFDec/ffdec.jar next to the app (or in the repository when running from source).");

        string output = Path.Combine(Path.GetTempPath(), "wayfer-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);

        ProcessStartInfo start = new()
        {
            FileName = JavaPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-jar");
        start.ArgumentList.Add(jar);
        start.ArgumentList.Add("-export");
        start.ArgumentList.Add("script");
        start.ArgumentList.Add(output);
        start.ArgumentList.Add(swf);

        Console.WriteLine($"[map] decompiling {Path.GetFileName(swf)}");
        Stopwatch sw = Stopwatch.StartNew();
        using Process process = new() { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(output);
            throw new MapInfoException("java_missing",
                $"Could not start Java to run FFDec: {ex.Message}. Install Java 17+ (or set JAVA_HOME) and try again.");
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = process.StandardError.ReadToEndAsync(ct);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            TryDeleteDirectory(output);
            throw new MapInfoException("map_decompile_timeout", "FFDec took longer than 120 seconds to decompile the map.");
        }

        string error = (await stderr.ConfigureAwait(false)).Trim();
        string scriptsRoot = Path.Combine(output, "scripts");
        if (!Directory.Exists(scriptsRoot))
        {
            string stdoutText = (await stdout.ConfigureAwait(false)).Trim();
            TryDeleteDirectory(output);
            string detail = string.IsNullOrEmpty(error) ? stdoutText : error;
            throw new MapInfoException("map_decompile_failed",
                "FFDec did not produce map scripts." + (string.IsNullOrEmpty(detail) ? string.Empty : " " + detail));
        }

        Console.WriteLine($"[map] decompiled in {sw.Elapsed.TotalSeconds:0.0}s");
        return scriptsRoot;
    }

    private static MapInfo Parse(string mapName, string filePath, string roomId, List<string> cells, string scriptsRoot, string swf)
    {
        List<(int Frame, string Name)> labels = SwfFrameLabels.Read(swf);
        Dictionary<string, List<string>> placements = ParsePlacements(scriptsRoot, labels);
        Dictionary<string, MapNpc> npcs = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string name, List<string> npcCells) in placements)
        {
            if (name.Length == 0)
                continue;
            MapNpc npc = GetOrAdd(npcs, name);
            foreach (string cell in npcCells)
            {
                if (!string.IsNullOrWhiteSpace(cell) && !npc.Cells.Contains(cell, StringComparer.OrdinalIgnoreCase))
                    npc.Cells.Add(cell);
            }
        }

        foreach (string file in Directory.EnumerateFiles(scriptsRoot, "*_APOP.as", SearchOption.AllDirectories))
        {
            string className = Path.GetFileNameWithoutExtension(file);
            string npcName = BaseName(className, "_APOP");
            if (npcName.Length == 0)
                continue;

            MapNpc npc = GetOrAdd(npcs, npcName);
            ParseApop(file, npc);
        }

        foreach (MapNpc npc in npcs.Values)
        {
            npc.Quests = npc.Quests
                .GroupBy(q => q.Id)
                .Select(g => new MapQuest { Id = g.Key, TurnIn = g.Any(q => q.TurnIn) })
                .OrderBy(q => q.Id)
                .ToList();
            npc.Shops = npc.Shops.GroupBy(s => s.Id).Select(g => g.First()).OrderBy(s => s.Id).ToList();
            npc.Joins = npc.Joins.GroupBy(j => j.Map + "|" + j.Cell, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(j => j.Map, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // APOP classes are sometimes embedded as shared assets for other rooms
        // or events (Battleon ships a dozen unused ones). Prefer only NPCs that
        // the map timeline actually places; if a map uses an unknown placement
        // scheme, fall back to all classes instead of returning an empty list.
        List<MapNpc> placed = npcs.Values.Where(n => n.Cells.Count > 0).ToList();
        if (placed.Count == 0)
            placed = npcs.Values.ToList();

        Dictionary<string, int> cellOrder = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < cells.Count; i++)
            cellOrder[cells[i]] = i;

        return new MapInfo
        {
            Map = mapName,
            FilePath = filePath,
            RoomId = roomId,
            Cells = cells,
            Npcs = placed
                .OrderBy(n => n.Cells.Count == 0 ? int.MaxValue : cellOrder.GetValueOrDefault(n.Cells[0], int.MaxValue))
                .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static MapNpc GetOrAdd(Dictionary<string, MapNpc> npcs, string name)
    {
        if (!npcs.TryGetValue(name, out MapNpc? npc))
        {
            npc = new MapNpc { Name = name };
            npcs[name] = npc;
        }
        return npc;
    }

    private static Dictionary<string, List<string>> ParsePlacements(string scriptsRoot, List<(int Frame, string Name)> labels)
    {
        Dictionary<string, List<string>> result = new(StringComparer.OrdinalIgnoreCase);
        string? main = Directory.EnumerateFiles(scriptsRoot, "MainTimeline.as", SearchOption.AllDirectories).FirstOrDefault();
        if (main is null)
            return result;

        string text = File.ReadAllText(main);
        foreach (string block in FunctionBlocks(text))
        {
            if (!block.Contains("param1", StringComparison.Ordinal))
                continue;

            Match npcMatch = Regex.Match(block, @"npcLinkage\s*=\s*""([^""]+)""");
            Match cntMatch = Regex.Match(block, @"cntLinkage\s*=\s*""([^""]+)""");
            Match apopMatch = Regex.Match(block, @"apopLinkage\s*=\s*""([^""]+)""");
            Match frameMatch = Regex.Match(block, @"param1\s*>=\s*(\d+)\s*&&\s*param1\s*<=\s*(\d+)");

            string? linkage = null;
            if (npcMatch.Success)
                linkage = npcMatch.Groups[1].Value;
            else if (cntMatch.Success)
                linkage = cntMatch.Groups[1].Value;
            else if (apopMatch.Success)
                linkage = apopMatch.Groups[1].Value;

            if (string.IsNullOrWhiteSpace(linkage))
                continue;

            string name = BaseName(linkage, "_NPC", "_APOP");
            if (name.Length == 0)
                continue;

            string cell = frameMatch.Success ? CellForFrame(int.Parse(frameMatch.Groups[1].Value), labels) : string.Empty;
            if (!result.TryGetValue(name, out List<string>? cells))
                result[name] = cells = new List<string>();
            if (cell.Length > 0 && !cells.Contains(cell, StringComparer.OrdinalIgnoreCase))
                cells.Add(cell);
        }

        return result;
    }

    private static string CellForFrame(int frame, List<(int Frame, string Name)> labels)
    {
        string cell = string.Empty;
        foreach ((int start, string name) in labels)
        {
            if (start > frame)
                break;
            cell = name;
        }
        return cell;
    }

    private static void ParseApop(string file, MapNpc npc)
    {
        string text = File.ReadAllText(file);

        foreach (Match match in Regex.Matches(text, @"strQuests\s*=\s*""([^""]*)"""))
            AddQuestIds(npc, match.Groups[1].Value, false);
        foreach (Match match in Regex.Matches(text, @"strTurnIns\s*=\s*""([^""]*)"""))
            AddQuestIds(npc, match.Groups[1].Value, true);

        foreach (string block in FunctionBlocks(text))
        {
            Match action = Regex.Match(block, @"strAction\s*=\s*""([^""]*)""");
            if (!action.Success)
                continue;

            Match idMatch = Regex.Match(block, @"intID\s*=\s*(\d+)");
            Match stringMatch = Regex.Match(block, @"strString\s*=\s*""([^""]*)""");
            Match frameMatch = Regex.Match(block, @"strFrame\s*=\s*""([^""]*)""");
            int id = idMatch.Success ? int.Parse(idMatch.Groups[1].Value) : 0;
            string value = stringMatch.Success ? stringMatch.Groups[1].Value.Trim() : string.Empty;
            string frame = frameMatch.Success ? frameMatch.Groups[1].Value.Trim() : string.Empty;

            switch (action.Groups[1].Value)
            {
                case "Show Quests":
                    AddQuestIds(npc, value, false);
                    break;
                case "Item Shop":
                    if (id > 1 && npc.Shops.All(s => s.Id != id))
                        npc.Shops.Add(new MapShop { Id = id });
                    break;
                case "/Join":
                    if (value.Length > 0 && npc.Joins.All(j => !string.Equals(j.Map, value, StringComparison.OrdinalIgnoreCase)))
                        npc.Joins.Add(new MapJoin { Map = value, Cell = frame });
                    break;
                case "GotoAndStop":
                case "GotoAndPlay":
                case "Show Front Movie":
                    npc.HasDialogue = true;
                    break;
            }
        }
    }

    private static void AddQuestIds(MapNpc npc, string value, bool turnIn)
    {
        foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out int id) && id > 0 && npc.Quests.All(q => q.Id != id || q.TurnIn != turnIn))
                npc.Quests.Add(new MapQuest { Id = id, TurnIn = turnIn });
        }
    }

    private static IEnumerable<string> FunctionBlocks(string text)
    {
        MatchCollection matches = Regex.Matches(text, @"\b(?:internal|public|private)\s+function\s+\w+\s*\(");
        for (int i = 0; i < matches.Count; i++)
        {
            int start = matches[i].Index;
            int end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            yield return text[start..end];
        }
    }

    private static string BaseName(string linkage, params string[] suffixes)
    {
        string name = linkage.Trim();
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (string suffix in suffixes)
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    name = name[..^suffix.Length];
                    changed = true;
                }
            }
        }

        name = Regex.Replace(name, @"_NPC\d+$", "", RegexOptions.IgnoreCase);
        return name.Trim();
    }

    private static string JavaPath()
    {
        string? home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            string candidate = Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
            if (File.Exists(candidate))
                return candidate;
        }

        return OperatingSystem.IsWindows() ? "java.exe" : "java";
    }

    private static string? FindFfdecJar()
    {
        string? explicitPath = Environment.GetEnvironmentVariable("WAYFER_FFDEC_JAR");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return explicitPath;

        string local = Path.Combine(AppContext.BaseDirectory, "FFDec", "ffdec.jar");
        if (File.Exists(local))
            return local;

        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "FFDec", "ffdec.jar");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static void TryDeleteDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;
        try { Directory.Delete(directory, true); } catch { }
    }

    private sealed class MapCache
    {
        public int Version { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public MapInfo? Map { get; set; }
    }
}

public sealed class MapInfo
{
    public string Map { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string RoomId { get; set; } = string.Empty;
    public List<string> Cells { get; set; } = new();
    public List<MapNpc> Npcs { get; set; } = new();
    public bool FromCache { get; set; }
}

public sealed class MapNpc
{
    public string Name { get; set; } = string.Empty;
    public List<string> Cells { get; set; } = new();
    public List<MapQuest> Quests { get; set; } = new();
    public List<MapShop> Shops { get; set; } = new();
    public List<MapJoin> Joins { get; set; } = new();
    public bool HasDialogue { get; set; }
}

public sealed class MapQuest
{
    public int Id { get; set; }
    public bool TurnIn { get; set; }
}

public sealed class MapShop
{
    public int Id { get; set; }
}

public sealed class MapJoin
{
    public string Map { get; set; } = string.Empty;
    public string Cell { get; set; } = string.Empty;
}

public sealed class MapInfoException : Exception
{
    public string Code { get; }

    public MapInfoException(string code, string message) : base(message)
    {
        Code = code;
    }
}

/// <summary>
/// Minimal SWF timeline reader: only FrameLabel tags are needed to translate
/// the frame ranges used by NPC placement functions into AQW cell names.
/// </summary>
internal static class SwfFrameLabels
{
    public static List<(int Frame, string Name)> Read(string swfPath)
    {
        List<(int, string)> labels = new();
        try
        {
            byte[] file = File.ReadAllBytes(swfPath);
            if (file.Length < 8)
                return labels;

            byte[] body;
            if (file[0] == 'C' && file[1] == 'W' && file[2] == 'S')
            {
                using MemoryStream input = new(file, 8, file.Length - 8);
                using ZLibStream zlib = new(input, CompressionMode.Decompress);
                using MemoryStream output = new();
                zlib.CopyTo(output);
                body = output.ToArray();
            }
            else if (file[0] == 'F' && file[1] == 'W' && file[2] == 'S')
            {
                body = file[8..];
            }
            else
            {
                return labels;
            }

            if (body.Length < 8)
                return labels;

            int nbits = body[0] >> 3;
            int pos = (5 + 4 * nbits + 7) / 8 + 4;
            int frame = 1;

            while (pos + 2 <= body.Length)
            {
                int header = body[pos] | (body[pos + 1] << 8);
                pos += 2;
                int code = header >> 6;
                int length = header & 0x3F;
                if (length == 0x3F)
                {
                    if (pos + 4 > body.Length)
                        break;
                    length = body[pos] | (body[pos + 1] << 8) | (body[pos + 2] << 16) | (body[pos + 3] << 24);
                    pos += 4;
                }

                if (length < 0 || pos + length > body.Length)
                    break;

                if (code == 43 && length > 0)
                {
                    int end = Array.IndexOf(body, (byte)0, pos, length);
                    string name = Encoding.UTF8.GetString(body, pos, (end < 0 ? pos + length : end) - pos);
                    if (name.Length > 0)
                        labels.Add((frame, name));
                }
                else if (code == 1)
                {
                    frame++;
                }
                else if (code == 0)
                {
                    break;
                }

                pos += length;
            }
        }
        catch
        {
            // A map without readable labels still works: NPC cells are optional.
        }

        return labels;
    }
}
