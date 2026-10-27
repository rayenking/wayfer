using Avalonia.Controls;
using Avalonia.Threading;
using Newtonsoft.Json;
using Skua.Core.Flash;
using Skua.Core.Interfaces;
using System.Collections.Concurrent;
using System.Dynamic;
using System.Text.Json;
using System.Xml.Linq;

namespace Skua.App.Avalonia;

/// <summary>
/// IFlashUtil implementation for the Ruffle-in-webview host. Talks to the
/// page bridge (window.skuaCall in index.html) instead of the Windows
/// ShockwaveFlash COM control. Bot code runs on background threads and
/// blocks here; the call is marshaled to the UI thread where the webview
/// lives.
/// </summary>
public class WebFlashUtil : IFlashUtil
{
    private readonly WebViewHost _host;

    public event FlashCallHandler? FlashCall;

    public WebFlashUtil(WebViewHost host)
    {
        _host = host;
    }

    public void InitializeFlash()
    {
        // The Ruffle player and skua.swf are loaded by the page hosted in
        // LocalGameServer; nothing to create here.
    }

    public string? Call(string function, params object[] args)
    {
        string? raw = InvokeJs(function, args);
        if (raw is null || raw == "null")
            return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            // skuaCall JSON-encodes the AS3 return value; string results arrive
            // quoted and are unwrapped so callers see the raw JSON/text.
            return doc.RootElement.ValueKind == JsonValueKind.String
                ? doc.RootElement.GetString()
                : raw;
        }
        catch
        {
            return raw;
        }
    }

    public T? Call<T>(string function, params object[] args)
    {
        string? result = Call(function, args);
        if (result is null)
            return default;
        try
        {
            return JsonConvert.DeserializeObject<T>(result);
        }
        catch
        {
            return default;
        }
    }

    public object? Call(string function, Type type, params object[] args)
    {
        string? result = Call(function, args);
        if (result is null)
            return null;
        try
        {
            return JsonConvert.DeserializeObject(result, type);
        }
        catch
        {
            return null;
        }
    }

    public object FromFlashXml(XElement el)
    {
        return el.Attribute("type")?.Value switch
        {
            "number" => int.TryParse(el.Value, out int i) ? i : float.TryParse(el.Value, out float f) ? f : 0,
            "true" => true,
            "false" => false,
            "null" => null!,
            "array" => el.Elements().Select(FromFlashXml).ToArray(),
            "object" => ParseExpando(el),
            _ => el.Value
        };
    }

    private static ExpandoObject ParseExpando(XElement el)
    {
        dynamic d = new ExpandoObject();
        var dict = (IDictionary<string, object?>)d;
        foreach (XElement e in el.Elements())
            dict[e.Attribute("id")!.Value] = FromFlashXmlStatic(e.Elements().First());
        return d;
    }

    private static object? FromFlashXmlStatic(XElement el) => el.Attribute("type")?.Value switch
    {
        "number" => int.TryParse(el.Value, out int i) ? i : float.TryParse(el.Value, out float f) ? f : 0,
        "true" => true,
        "false" => false,
        "null" => null,
        "array" => el.Elements().Select(e => (object?)FromFlashXmlStatic(e)).ToArray(),
        "object" => ParseExpandoStatic(el),
        _ => el.Value
    };

    private static ExpandoObject ParseExpandoStatic(XElement el)
    {
        dynamic d = new ExpandoObject();
        var dict = (IDictionary<string, object?>)d;
        foreach (XElement e in el.Elements())
            dict[e.Attribute("id")!.Value] = FromFlashXmlStatic(e.Elements().First());
        return d;
    }

    public IFlashObject<T> CreateFlashObject<T>(string path)
    {
        return new FlashObject<T>(Call<int>("lnkCreate", path), this);
    }

    /// <summary>
    /// Routes a message posted from the page (invokeCSharpAction payload) into
    /// the FlashCall event, mirroring the AxShockwaveFlash.FlashCall behavior.
    /// </summary>
    public void HandleWebMessage(string body)
    {
        try
        {
            JsonDocument doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("name", out JsonElement nameEl))
                return;
            string name = nameEl.GetString() ?? string.Empty;

            if (name.StartsWith("console."))
            {
                string text = doc.RootElement.TryGetProperty("args", out JsonElement argsEl) &&
                              argsEl.ValueKind == JsonValueKind.Array &&
                              argsEl.GetArrayLength() > 0
                    ? argsEl[0].GetString() ?? string.Empty
                    : string.Empty;
                Console.WriteLine($"[page] {text}");
                return;
            }

            object[] args = doc.RootElement.TryGetProperty("args", out JsonElement aEl) &&
                            aEl.ValueKind == JsonValueKind.Array
                ? aEl.EnumerateArray().Select(a => a.ValueKind switch
                {
                    JsonValueKind.String => (object)a.GetString()!,
                    JsonValueKind.Number => a.TryGetInt64(out long l) ? l : a.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null!,
                    _ => a.GetRawText()
                }).ToArray()
                : Array.Empty<object>();

            // AS3's Externalizer.call passes its `...rest` array as a single
            // argument. Ruffle forwards that array, so a one-argument call
            // arrives as ["payload"]. Unwrap it for the callbacks that expect
            // a scalar (packets, URLs, debug text).
            if (name is "pext" or "openWebsite" or "debug" && args.Length == 1 && args[0] is string wrapped)
            {
                try
                {
                    using JsonDocument inner = JsonDocument.Parse(wrapped);
                    if (inner.RootElement.ValueKind == JsonValueKind.Array && inner.RootElement.GetArrayLength() == 1)
                    {
                        JsonElement first = inner.RootElement[0];
                        args[0] = first.ValueKind == JsonValueKind.String
                            ? first.GetString()!
                            : first.GetRawText();
                    }
                }
                catch
                {
                    // Not a wrapped array; leave the original payload.
                }
            }

            FlashCall?.Invoke(name, args);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[skua] bad web message: {ex.Message}");
        }
    }

    public void Dispose()
    {
        // The webview is owned by MainWindow; nothing to release here.
    }

    private readonly ConcurrentDictionary<string, string?> _readCache = new();
    private readonly ConcurrentDictionary<string, byte> _pendingReads = new();

    private static string CacheKey(string function, object[]? args)
        => function + "|" + JsonConvert.SerializeObject(args ?? Array.Empty<object>());

    private string? InvokeJs(string function, object[]? args)
    {
        string key = CacheKey(function, args);

        if (Dispatcher.UIThread.CheckAccess())
        {
            // Never block the Avalonia UI thread on a JS round-trip. Serve the
            // last value if we have one, and warm the cache in the background
            // so object bindings (HP, mana, gold, …) update on the next poll.
            if (_readCache.TryGetValue(key, out string? cached))
                return cached;

            if (_pendingReads.TryAdd(key, 0))
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        string? raw = InvokeJsCore(function, args);
                        if (raw is not null)
                            _readCache[key] = raw;
                    }
                    finally
                    {
                        _pendingReads.TryRemove(key, out _);
                    }
                });
            }
            return null;
        }

        string? result = InvokeJsCore(function, args);
        if (result is not null)
            _readCache[key] = result;
        return result;
    }

    private string? InvokeJsCore(string function, object[]? args)
    {
        string argsJson = JsonConvert.SerializeObject(args ?? Array.Empty<object>());
        string js = $"window.skuaCall({JsonConvert.SerializeObject(function)}, {JsonConvert.SerializeObject(argsJson)})";
        try
        {
            NativeWebView? webView = _host.View;
            if (webView is null)
            {
                Console.WriteLine($"[skua] Call('{function}') ignored before webview is ready");
                return null;
            }
            if (Dispatcher.UIThread.CheckAccess())
            {
                Console.WriteLine($"[skua] Call('{function}') ignored on UI thread");
                return null;
            }
            return Dispatcher.UIThread.InvokeAsync(async () => await webView.InvokeScript(js))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[skua] Call('{function}') failed: {ex.Message}");
            return null;
        }
    }
}
