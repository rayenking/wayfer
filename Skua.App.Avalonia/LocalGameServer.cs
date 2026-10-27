using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Skua.App.Avalonia;

/// <summary>
/// Local origin for the webview: serves the loader page, the self-hosted Ruffle
/// runtime and the patched skua.swf, and reverse-proxies /ga/* to
/// https://game.aq.com/game/* so every request the game makes stays same-origin
/// (game.aq.com sends no CORS headers, which would otherwise block the SWF).
/// </summary>
public sealed class LocalGameServer : IDisposable
{
    public const int Port = 8765;
    private const string GameOrigin = "https://game.aq.com/game";

    public string BaseUrl => $"http://127.0.0.1:{Port}/";

    private readonly HttpListener _listener = new();
    private readonly HttpClient _http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All
    });
    private CancellationTokenSource? _cts;
    private string _root = AppContext.BaseDirectory;

    // Flash cannot open raw TCP sockets from a webview; Ruffle bridges them
    // through WebSocket. The game sees rewritten sIP=127.0.0.1 / iPort=20000+i
    // in the login response, and each mapped port is relayed here to the real
    // SmartFoxServer endpoint.
    private readonly ConcurrentDictionary<int, (string Host, int Port)> _socketMappings = new();
    public const int SocketPortBase = 20000;
    public const int SocketPortCount = 48;
    public const int DirectSocketPortBase = 20100;

    private static readonly (string Host, int Port)[] DirectWorldServers =
    {
        ("sock7.aq.com", 5588), ("sock7.aq.com", 5589), ("sock7.aq.com", 5591),
        ("sock7.aq.com", 5592), ("sock7.aq.com", 5593), ("sock7.aq.com", 5594),
        ("sock8.aq.com", 5588), ("sock8.aq.com", 5589), ("sock8.aq.com", 5590),
        ("sock8.aq.com", 5591), ("sock8.aq.com", 5593),
        ("euro.aqw.artix.com", 5588), ("asia.game.artix.com", 5588)
    };

    public void Start()
    {
        for (int i = 0; i < DirectWorldServers.Length; i++)
            _socketMappings[DirectSocketPortBase + i] = DirectWorldServers[i];
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenAsync(_cts.Token));
        Console.WriteLine($"[skua] local game server listening on {BaseUrl}");
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                break;
            }
            _ = Task.Run(() => HandleAsync(ctx), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            Console.WriteLine($"[skua] {ctx.Request.HttpMethod} {path}{ctx.Request.Url?.Query}");

            switch (path)
            {
                case "/":
                case "/index.html":
                    await ServeFileAsync(ctx, Path.Combine(_root, "wwwroot", "index.html"), "text/html; charset=utf-8");
                    return;
                case "/skua.swf":
                    await ServeFileAsync(ctx, Path.Combine(_root, "skua.swf"), "application/x-shockwave-flash");
                    return;
                case "/favicon.ico":
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    return;
            }

            if (path.StartsWith("/ruffle/"))
            {
                string rel = path["/ruffle/".Length..];
                string full = Path.GetFullPath(Path.Combine(_root, "wwwroot", "ruffle", rel));
                if (!full.StartsWith(Path.Combine(_root, "wwwroot")) || !File.Exists(full))
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    return;
                }
                await ServeFileAsync(ctx, full, MimeFor(rel));
                return;
            }

            if (path.StartsWith("/ga/"))
            {
                // Serve the locally patched Ruffle-adapted client: upstream
                // spider.swf (Aug 2026) hardcodes clientVersion=4.26 which the
                // server rejects (needs >= 5.3); ours patches that double.
                if (path == "/ga/gamefiles/spider.swf")
                {
                    string patched = Path.Combine(_root, "spider.swf");
                    if (File.Exists(patched))
                    {
                        await ServeFileAsync(ctx, patched, "application/x-shockwave-flash");
                        return;
                    }
                }
                await ProxyAsync(ctx);
                return;
            }

            if (path.StartsWith("/wsrelay/"))
            {
                await RelayAsync(ctx);
                return;
            }

            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[skua] request failed: {ex.Message}");
            try
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.Close();
            }
            catch { }
        }
    }

    private static async Task ServeFileAsync(HttpListenerContext ctx, string file, string mime)
    {
        if (!File.Exists(file))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }
        ctx.Response.ContentType = mime;
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        byte[] bytes = await File.ReadAllBytesAsync(file);
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private async Task ProxyAsync(HttpListenerContext ctx)
    {
        string path = ctx.Request.Url!.AbsolutePath;
        // "/ga/gamefiles/x" -> "https://game.aq.com/game/gamefiles/x"
        string target = GameOrigin + path["/ga".Length..] + ctx.Request.Url.Query;

        using HttpRequestMessage req = new(new HttpMethod(ctx.Request.HttpMethod), target);
        if (req.Method != HttpMethod.Get && req.Method != HttpMethod.Head)
        {
            using MemoryStream ms = new();
            await ctx.Request.InputStream.CopyToAsync(ms);
            byte[] body = ms.ToArray();
            if (body.Length > 0)
            {
                req.Content = new ByteArrayContent(body);
                if (ctx.Request.ContentType is not null)
                    req.Content.Headers.TryAddWithoutValidation("Content-Type", ctx.Request.ContentType);
            }
        }
        req.Headers.TryAddWithoutValidation("User-Agent", ctx.Request.UserAgent ?? "Skua.Avalonia");
        req.Headers.TryAddWithoutValidation("Referer", "https://game.aq.com/game/");

        using HttpResponseMessage res = await _http.SendAsync(req);
        byte[] resBody = await res.Content.ReadAsByteArrayAsync();
        ctx.Response.StatusCode = (int)res.StatusCode;
        ctx.Response.ContentType = res.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        if (res.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies))
        {
            foreach (string? cookie in cookies)
                ctx.Response.Headers.Add("Set-Cookie", cookie);
        }
        // The login response carries the world server list (sIP/iPort); point
        // those at the local WebSocket relay so Ruffle can reach them.
        if (path.StartsWith("/ga/api/login/now") && res.IsSuccessStatusCode)
        {
            string text = Encoding.UTF8.GetString(resBody);
            string rewritten = RewriteServerList(text);
            if (!ReferenceEquals(rewritten, text))
            {
                resBody = Encoding.UTF8.GetBytes(rewritten);
                Console.WriteLine("[skua] login response: server list rewritten to local relay");
            }
        }
        // gameversion points at the raw Flash-era client (Game####.swf) which
        // has broken avatars under Ruffle; the Ruffle-adapted official client
        // is spider.swf (see Loader_Spider.swf on the official page).
        // Spider (Aug 2026) predates the cellArrivalWatch Ruffle shims and its
        // clientVersion needed patching, so default to the live client now that
        // the Z-message unpack relay exists; flip to true to compare behavior.
        bool useSpiderClient = Environment.GetEnvironmentVariable("SKUA_SPIDER") == "1";
        if (useSpiderClient && path.StartsWith("/ga/api/data/gameversion") && res.IsSuccessStatusCode)
        {
            string text = Encoding.UTF8.GetString(resBody);
            System.Text.RegularExpressions.Match m =
                System.Text.RegularExpressions.Regex.Match(text, "\"sFile\":\"(Game[^\"]*)\"");
            if (m.Success)
            {
                // skua appends '?ver=<random>' itself, so keep sFile query-free.
                string rewritten = text.Replace("\"sFile\":\"" + m.Groups[1].Value + "\"", "\"sFile\":\"spider.swf\"");
                resBody = Encoding.UTF8.GetBytes(rewritten);
                Console.WriteLine($"[skua] gameversion: sFile {m.Groups[1].Value} -> spider.swf");
            }
        }
        await ctx.Response.OutputStream.WriteAsync(resBody);
        ctx.Response.Close();
    }

    private string RewriteServerList(string json)
    {
        try
        {
            Newtonsoft.Json.Linq.JObject root = Newtonsoft.Json.Linq.JObject.Parse(json);
            Newtonsoft.Json.Linq.JToken? servers = root["servers"];
            if (servers is not Newtonsoft.Json.Linq.JArray arr)
                return json;

            int index = 0;
            foreach (Newtonsoft.Json.Linq.JToken entry in arr)
            {
                Newtonsoft.Json.Linq.JObject server = (Newtonsoft.Json.Linq.JObject)entry;
                string? realIp = server["sIP"]?.ToString();
                int realPort = (int?)server["iPort"] ?? 0;
                if (string.IsNullOrEmpty(realIp) || realPort <= 0)
                    continue;

                int localPort = SocketPortBase + index++;
                server["sIP"] = "127.0.0.1";
                server["iPort"] = localPort;
                _socketMappings[localPort] = (realIp, realPort);
                Console.WriteLine($"[skua] server '{server["sName"]}' {realIp}:{realPort} -> 127.0.0.1:{localPort}");
            }
            return root.ToString(Newtonsoft.Json.Formatting.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[skua] server list rewrite failed: {ex.Message}");
            return json;
        }
    }

    /// <summary>
    /// WebSocket-to-TCP relay for the game's SmartFoxServer connections:
    /// Ruffle opens ws://.../wsrelay/{mappedPort} and every byte is piped to
    /// the real world server.
    /// </summary>
    private async Task RelayAsync(HttpListenerContext ctx)
    {
        if (!int.TryParse(ctx.Request.Url!.AbsolutePath["/wsrelay/".Length..], out int localPort) ||
            !_socketMappings.TryGetValue(localPort, out (string Host, int Port) target))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        WebSocket ws;
        try
        {
            var wsContext = await ctx.AcceptWebSocketAsync(null, TimeSpan.FromMinutes(30));
            ws = wsContext.WebSocket;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[skua] ws upgrade failed for {localPort}: {ex.Message}");
            return;
        }

        Console.WriteLine($"[skua] socket relay open: {localPort} -> {target.Host}:{target.Port}");
        using TcpClient tcp = new();
        try
        {
            await tcp.ConnectAsync(target.Host, target.Port);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[skua] tcp connect to {target.Host}:{target.Port} failed: {ex.Message}");
            await ws.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "target unreachable", CancellationToken.None);
            return;
        }

        NetworkStream stream = tcp.GetStream();
        Task wsToTcp = PumpWebSocketToTcp(ws, stream);
        Task tcpToWs = PumpTcpToWebSocket(stream, ws);
        await Task.WhenAny(wsToTcp, tcpToWs);

        Console.WriteLine($"[skua] socket relay closed: {localPort}");
        try { tcp.Close(); } catch { }
        try { ws.Dispose(); } catch { }
    }

    private static async Task PumpWebSocketToTcp(WebSocket ws, NetworkStream stream)
    {
        byte[] buf = new byte[64 * 1024];
        long total = 0;
        try
        {
            while (true)
            {
                WebSocketReceiveResult msg = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                if (msg.MessageType == WebSocketMessageType.Close)
                    return;
                await stream.WriteAsync(buf, 0, msg.Count);
                while (!msg.EndOfMessage)
                {
                    msg = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    if (msg.MessageType == WebSocketMessageType.Close)
                        return;
                    await stream.WriteAsync(buf, 0, msg.Count);
                }
                await stream.FlushAsync();
                total += msg.Count;
                if (total <= 2048)
                    Console.WriteLine($"[relay] ws->tcp [{buf.AsSpan(0, Math.Min(msg.Count, 160)).ToArray().Length}B] {Preview(buf, msg.Count)}");
            }
        }
        catch { }
    }

    /// <summary>
    /// TCP->WebSocket pump with SmartFox protocol rewriting: the server now
    /// sends zlib+base64-compressed messages prefixed with 'Z' (only the
    /// latest official client's SmartFoxClient knows how to unpack them).
    /// Spider.swf's older client drops them silently, so the relay unpacks
    /// every 'Z' message and delivers the plain message instead.
    /// </summary>
    private static async Task PumpTcpToWebSocket(NetworkStream stream, WebSocket ws)
    {
        byte[] buf = new byte[64 * 1024];
        List<byte> pending = new();
        long total = 0;
        long unpacked = 0;
        try
        {
            while (true)
            {
                int n = await stream.ReadAsync(buf);
                if (n == 0)
                {
                    Console.WriteLine($"[relay] tcp closed by remote (after {total}B, {unpacked} msgs unpacked)");
                    return;
                }
                total += n;
                pending.AddRange(buf.AsSpan(0, n).ToArray());

                int zero;
                while ((zero = pending.IndexOf(0)) >= 0)
                {
                    byte[] msg = pending.Take(zero).ToArray();
                    pending.RemoveRange(0, zero + 1);
                    if (msg.Length == 0)
                        continue;

                    if (msg[0] == (byte)'Z' && msg.Length > 1)
                    {
                        byte[]? plain = TryUnpack(msg);
                        if (plain is not null)
                        {
                            unpacked++;
                            if (unpacked <= 5)
                                Console.WriteLine($"[relay] unpacked Z-msg {msg.Length}B -> {plain.Length}B: {Preview(plain, Math.Min(plain.Length, 120))}");
                            msg = plain;
                        }
                    }

                    byte[] frame = new byte[msg.Length + 1];
                    msg.CopyTo(frame, 0);
                    frame[^1] = 0;
                    await ws.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, CancellationToken.None);
                }
            }
        }
        catch { }
    }

    /// <summary>Replicates the official client's unpack(): skip 'Z', base64
    /// decode (padding stripped), zlib inflate.</summary>
    private static byte[]? TryUnpack(byte[] zmsg)
    {
        try
        {
            // strip '=' padding the same way the AS3 code does
            int len = zmsg.Length;
            while (len > 1 && zmsg[len - 1] == (byte)'=')
                len--;

            string b64 = Encoding.ASCII.GetString(zmsg, 1, len - 1);
            byte[] compressed = Convert.FromBase64String(b64);
            using MemoryStream outMs = new();
            try
            {
                // AS3 ByteArray.uncompress() expects zlib-framed data
                using (MemoryStream inMs = new(compressed))
                using (System.IO.Compression.ZLibStream zlib = new(inMs, System.IO.Compression.CompressionMode.Decompress))
                    zlib.CopyTo(outMs);
            }
            catch (System.IO.InvalidDataException)
            {
                outMs.SetLength(0);
                using MemoryStream inMs = new(compressed);
                using (System.IO.Compression.DeflateStream deflate = new(inMs, System.IO.Compression.CompressionMode.Decompress))
                    deflate.CopyTo(outMs);
            }
            return outMs.ToArray();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[relay] unpack failed: {ex.Message}");
            return null;
        }
    }

    private static string Preview(byte[] buf, int n)
    {
        int len = Math.Min(n, 160);
        StringBuilder sb = new();
        for (int i = 0; i < len; i++)
        {
            char c = (char)buf[i];
            sb.Append(char.IsLetterOrDigit(c) || char.IsPunctuation(c) || c == ' ' || c == '<' || c == '>' ? c : '.');
        }
        // Never echo SmartFox login credentials into the host log.
        return Regex.Replace(
            sb.ToString(),
            @"(<pword><!\[CDATA\[).*?(\]\]></pword>)",
            "$1***$2",
            RegexOptions.IgnoreCase);
    }

    private static string MimeFor(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".js" => "text/javascript; charset=utf-8",
        ".wasm" => "application/wasm",
        ".json" => "application/json",
        ".map" => "application/json",
        ".css" => "text/css",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        _ => "application/octet-stream"
    };

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener.Stop(); } catch { }
        _http.Dispose();
    }
}
