using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.Items;
using Skua.Core.Models.Monsters;
using Skua.Core.Models.Players;
using Skua.Core.Models.Quests;

namespace Skua.App.Avalonia;

/// <summary>
/// Small local HTTP control surface for the running bot. The MCP server
/// (Skua.Mcp) is a thin stdio client over this API, so an AI agent can run
/// on another machine without embedding an MCP transport in the Avalonia app.
///
/// Security defaults:
/// - binds to 127.0.0.1 only;
/// - refuses a non-loopback bind unless SKUA_CONTROL_TOKEN is set.
/// </summary>
public sealed class ControlServer : IDisposable
{
    private const int DefaultPort = 8766;
    // Keep the general request small, but allow a full script payload plus
    // JSON escaping overhead. The script validator enforces the 256 KB limit.
    private const int MaxBodyBytes = 512 * 1024;
    private const int MaxScriptBytes = 256 * 1024;
    private const int ActionGateTimeoutSeconds = 180;

    private readonly IScriptInterface _bot;
    private readonly IScriptManager _scriptManager;
    private readonly ILogService _logService;
    private readonly string _scriptsRoot;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly SemaphoreSlim _scriptGate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly string _token;
    private readonly string _prefix;
    private Task? _acceptLoop;
    private bool _disposed;

    public ControlServer(IScriptInterface bot, IScriptManager scriptManager, ILogService logService)
    {
        _bot = bot;
        _scriptManager = scriptManager;
        _logService = logService;
        _scriptsRoot = Path.GetFullPath(ClientFileSources.SkuaScriptsDIR);
        _token = (ReadEnv("WAYFER_CONTROL_TOKEN", "SKUA_CONTROL_TOKEN") ?? string.Empty).Trim();

        int port = DefaultPort;
        string? portText = ReadEnv("WAYFER_CONTROL_PORT", "SKUA_CONTROL_PORT");
        if (!string.IsNullOrWhiteSpace(portText) && (!int.TryParse(portText, out port) || port is <= 0 or >= 65536))
            throw new InvalidOperationException($"Invalid WAYFER_CONTROL_PORT: {portText}");

        string bind = (ReadEnv("WAYFER_CONTROL_BIND", "SKUA_CONTROL_BIND") ?? "127.0.0.1").Trim();
        if (string.IsNullOrWhiteSpace(bind))
            bind = "127.0.0.1";

        bool loopback = bind is "127.0.0.1" or "localhost" or "::1" or "[::1]";
        if (!loopback && _token.Length == 0)
            throw new InvalidOperationException(
                "WAYFER_CONTROL_TOKEN is required when WAYFER_CONTROL_BIND is not loopback. " +
                "Set a long random token before exposing the control API to the LAN.");

        _prefix = bind is "0.0.0.0" or "*" or "+"
            ? $"http://*:{port}/"
            : $"http://{bind}:{port}/";
    }

    public string Prefix => _prefix;

    public void Start()
    {
        if (ReadEnv("WAYFER_CONTROL_ENABLED", "SKUA_CONTROL_ENABLED") == "0")
        {
            Console.WriteLine("[control] disabled by WAYFER_CONTROL_ENABLED=0");
            return;
        }

        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Console.WriteLine($"[control] listening on {_prefix} (auth={(_token.Length == 0 ? "none" : "token")})");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[control] accept failed: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleAsync(context, ct), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        int status = 200;
        object? data = null;
        string? error = null;

        try
        {
            if (!IsAuthorized(context))
            {
                status = 401;
                error = "unauthorized";
            }
            else
            {
                data = await DispatchAsync(context, ct).ConfigureAwait(false);
            }
        }
        catch (ControlException ex)
        {
            status = ex.StatusCode;
            error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            status = 499;
            error = "cancelled";
        }
        catch (Exception ex)
        {
            status = 500;
            error = ex.Message;
            Console.Error.WriteLine($"[control] {context.Request.HttpMethod} {context.Request.Url?.AbsolutePath} failed: {ex.Message}");
        }

        try
        {
            object payload = error is null
                ? new { ok = true, data, timestamp = DateTimeOffset.UtcNow }
                : new { ok = false, error, timestamp = DateTimeOffset.UtcNow };
            await WriteJsonAsync(context, status, payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[control] response write failed: {ex.Message}");
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private async Task<object?> DispatchAsync(HttpListenerContext context, CancellationToken ct)
    {
        string method = context.Request.HttpMethod.ToUpperInvariant();
        string path = (context.Request.Url?.AbsolutePath ?? "/").TrimEnd('/').ToLowerInvariant();
        if (path.Length == 0)
            path = "/";

        if (method == "GET")
        {
            return path switch
            {
                "/" or "/health" => Health(),
                "/state" => BuildState(),
                "/map" => BuildMap(),
                "/players" => BuildPlayers(),
                "/monsters" => BuildMonsters(),
                "/servers" => BuildServers(),
                "/quests" => BuildQuests(),
                "/quest" => BuildQuest(QueryInt(context, "id")),
                "/quests/cached" => BuildCachedQuests(context),
                "/scripts" => BuildScripts(),
                "/script" => BuildScriptStatus(),
                "/logs" => BuildLogs(context),
                _ => throw new ControlException(404, "not_found")
            };
        }

        if (method != "POST")
            throw new ControlException(405, "method_not_allowed");

        return path switch
        {
            "/join" => await JoinAsync(context, ct).ConfigureAwait(false),
            "/jump" => await JumpAsync(context, ct).ConfigureAwait(false),
            "/walk" => await WalkAsync(context, ct).ConfigureAwait(false),
            "/walk-speed" => await WalkSpeedAsync(context, ct).ConfigureAwait(false),
            "/goto" => await GotoAsync(context, ct).ConfigureAwait(false),
            "/attack" => await AttackAsync(context, ct).ConfigureAwait(false),
            "/kill" => await KillAsync(context, ct).ConfigureAwait(false),
            "/cancel-attack" => await CancelAttackAsync(ct).ConfigureAwait(false),
            "/exit-combat" => await ExitCombatAsync(ct).ConfigureAwait(false),
            "/rest" => await RestAsync(context, ct).ConfigureAwait(false),
            "/reload-map" => await ReloadMapAsync(ct).ConfigureAwait(false),
            "/relogin" => await ReloginAsync(context, ct).ConfigureAwait(false),
            "/login" => await LoginAsync(context, ct).ConfigureAwait(false),
            "/logout" => await LogoutAsync(ct).ConfigureAwait(false),
            "/select-server" => await SelectServerAsync(context, ct).ConfigureAwait(false),
            "/quests/load" => await LoadQuestsAsync(context, ct).ConfigureAwait(false),
            "/quests/accept" => await AcceptQuestAsync(context, ct).ConfigureAwait(false),
            "/quests/complete" => await CompleteQuestAsync(context, ct).ConfigureAwait(false),
            "/quests/abandon" => await AbandonQuestAsync(context, ct).ConfigureAwait(false),
            "/scripts/write" => await WriteScriptAsync(context, ct).ConfigureAwait(false),
            "/scripts/run" => await RunScriptAsync(context, ct).ConfigureAwait(false),
            "/scripts/pause" => await PauseScriptAsync(ct).ConfigureAwait(false),
            "/scripts/resume" => await ResumeScriptAsync(ct).ConfigureAwait(false),
            "/scripts/stop" => await StopScriptAsync(ct).ConfigureAwait(false),
            _ => throw new ControlException(404, "not_found")
        };
    }

    private object Health()
    {
        return new
        {
            name = "wayfer-control",
            version = Safe(() => _bot.Version.ToString(), "unknown"),
            loggedIn = Safe(() => _bot.Player.LoggedIn, false),
            playing = Safe(() => _bot.Player.Playing, false),
            timestamp = DateTimeOffset.UtcNow
        };
    }

    private async Task<object?> JoinAsync(HttpListenerContext context, CancellationToken ct)
    {
        JoinRequest request = await ReadJsonAsync<JoinRequest>(context, ct).ConfigureAwait(false);
        string map = RequireText(request.Map, "map", 96);
        string cell = OptionalText(request.Cell, "Enter", 64);
        string pad = OptionalText(request.Pad, "Spawn", 64);

        return await RunActionAsync(() =>
        {
            RequirePlaying();
            _bot.Map.Join(map, cell, pad, request.IgnoreCheck);
            return BuildState();
        }, ct).ConfigureAwait(false);
    }

    private async Task<object?> JumpAsync(HttpListenerContext context, CancellationToken ct)
    {
        JumpRequest request = await ReadJsonAsync<JumpRequest>(context, ct).ConfigureAwait(false);
        string cell = RequireText(request.Cell, "cell", 64);
        string pad = OptionalText(request.Pad, "Spawn", 64);

        return await RunActionAsync(() =>
        {
            RequirePlaying();
            _bot.Map.Jump(cell, pad, request.AutoCorrect, request.ClientOnly);
            return BuildState();
        }, ct).ConfigureAwait(false);
    }

    private async Task<object?> WalkAsync(HttpListenerContext context, CancellationToken ct)
    {
        WalkRequest request = await ReadJsonAsync<WalkRequest>(context, ct).ConfigureAwait(false);
        if (request.X is < -100000 or > 100000 || request.Y is < -100000 or > 100000)
            throw new ControlException(400, "x and y must be between -100000 and 100000");
        int speed = request.Speed is >= 1 and <= 50 ? request.Speed : 8;

        return await RunActionAsync(() =>
        {
            RequirePlaying();
            _bot.Player.WalkTo(request.X, request.Y, speed);
            return BuildState();
        }, ct).ConfigureAwait(false);
    }

    private async Task<object?> WalkSpeedAsync(HttpListenerContext context, CancellationToken ct)
    {
        WalkSpeedRequest request = await ReadJsonAsync<WalkSpeedRequest>(context, ct).ConfigureAwait(false);
        if (request.Speed is < 1 or > 50)
            throw new ControlException(400, "speed must be between 1 and 50");

        return await RunActionAsync(() =>
        {
            RequirePlaying();
            _bot.Player.WalkSpeed = request.Speed;
            return BuildState();
        }, ct).ConfigureAwait(false);
    }

    private async Task<object?> GotoAsync(HttpListenerContext context, CancellationToken ct)
    {
        GotoRequest request = await ReadJsonAsync<GotoRequest>(context, ct).ConfigureAwait(false);
        string player = RequireText(request.Player, "player", 64);

        return await RunActionAsync(() =>
        {
            RequirePlaying();
            _bot.Player.Goto(player);
            return BuildState();
        }, ct).ConfigureAwait(false);
    }

    private async Task<object?> AttackAsync(HttpListenerContext context, CancellationToken ct)
    {
        AttackRequest request = await ReadJsonAsync<AttackRequest>(context, ct).ConfigureAwait(false);
        string monster = RequireText(request.Monster, "monster", 96);
        bool attacked = false;

        return await RunActionAsync(() =>
        {
            RequirePlaying();
            attacked = int.TryParse(monster, out int id)
                ? _bot.Combat.Attack(id)
                : _bot.Combat.Attack(monster);
            return new { attacked, state = BuildState() };
        }, ct).ConfigureAwait(false);
    }

    private async Task<object?> KillAsync(HttpListenerContext context, CancellationToken ct)
    {
        KillRequest request = await ReadJsonAsync<KillRequest>(context, ct).ConfigureAwait(false);
        string monster = RequireText(request.Monster, "monster", 96);
        int timeoutMs = request.TimeoutMs is >= 1000 and <= 600000 ? request.TimeoutMs : 120000;

        return await RunActionAsync(() =>
        {
            RequirePlaying();
            bool timedOut = false;
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(timeoutMs);

            try
            {
                if (int.TryParse(monster, out int id))
                    _bot.Kill.Monster(id, timeout.Token);
                else
                    _bot.Kill.Monster(monster, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
            }

            return new { timedOut, state = BuildState() };
        }, ct, timeoutSeconds: (timeoutMs / 1000) + 30).ConfigureAwait(false);
    }

    private async Task<object?> CancelAttackAsync(CancellationToken ct)
    {
        return await RunActionAsync(() =>
        {
            RequirePlaying();
            _bot.Combat.CancelAutoAttack();
            _bot.Combat.CancelTarget();
            return BuildState();
        }, ct).ConfigureAwait(false);
    }

    private async Task<object?> ExitCombatAsync(CancellationToken ct)
    {
        return await RunActionAsync(() =>
        {
            RequirePlaying();
            _bot.Combat.Exit();
            return BuildState();
        }, ct).ConfigureAwait(false);
    }

    private async Task<object?> RestAsync(HttpListenerContext context, CancellationToken ct)
    {
        RestRequest request = await ReadJsonAsync<RestRequest>(context, ct).ConfigureAwait(false);
        return await RunActionAsync(() =>
        {
            RequirePlaying();
            _bot.Player.Rest(request.Full);
            return BuildState();
        }, ct).ConfigureAwait(false);
    }

    private async Task<object?> ReloadMapAsync(CancellationToken ct)
    {
        return await RunActionAsync(() =>
        {
            RequirePlaying();
            _bot.Map.Reload();
            return BuildState();
        }, ct).ConfigureAwait(false);
    }

    private async Task<object?> ReloginAsync(HttpListenerContext context, CancellationToken ct)
    {
        ReloginRequest request = await ReadJsonAsync<ReloginRequest>(context, ct).ConfigureAwait(false);
        string? server = string.IsNullOrWhiteSpace(request.Server) ? null : RequireText(request.Server, "server", 64);

        return await RunActionAsync(() =>
        {
            bool reconnected = string.IsNullOrEmpty(server)
                ? _bot.Servers.Relogin()
                : _bot.Servers.Relogin(server);
            return new { reconnected, state = BuildState() };
        }, ct, timeoutSeconds: 300).ConfigureAwait(false);
    }

    private async Task<object?> LoginAsync(HttpListenerContext context, CancellationToken ct)
    {
        LoginRequest request = await ReadJsonAsync<LoginRequest>(context, ct).ConfigureAwait(false);
        string username = RequireText(request.Username, "username", 64);
        string password = RequireSecret(request.Password, "password", 256);
        string? server = string.IsNullOrWhiteSpace(request.Server)
            ? null
            : RequireText(request.Server, "server", 64);

        return await RunActionAsync(() =>
        {
            // Login with a server uses Relogin because it performs the
            // logout -> login -> connect sequence with the game's own waits.
            if (!string.IsNullOrEmpty(server))
            {
                _bot.Servers.SetLoginInfo(username, password);
                bool connected = _bot.Servers.Relogin(server);
                string actualServer = Safe(() => _bot.Servers.LastName, string.Empty);
                if (string.IsNullOrWhiteSpace(actualServer))
                    actualServer = server;
                return (object)new
                {
                    loginStarted = true,
                    connected,
                    server = actualServer,
                    state = BuildState()
                };
            }

            if (Safe(() => _bot.Player.Playing, false))
            {
                try { _bot.Servers.Logout(); } catch { }
                Safe(() => _bot.Wait.ForTrue(() => !_bot.Player.Playing, 10), false);
            }

            _bot.Servers.SetLoginInfo(username, password);
            _bot.Servers.Login(username, password);
            return (object)new
            {
                loginStarted = true,
                connected = Safe(() => _bot.Player.Playing, false),
                state = BuildState()
            };
        }, ct, timeoutSeconds: 300).ConfigureAwait(false);
    }

    private async Task<object?> LogoutAsync(CancellationToken ct)
    {
        return await RunActionAsync(() =>
        {
            _bot.Servers.Logout();
            Safe(() => _bot.Wait.ForTrue(() => !_bot.Player.Playing, 10), false);
            return new
            {
                loggedOut = !Safe(() => _bot.Player.Playing, false),
                state = BuildState()
            };
        }, ct, timeoutSeconds: 60).ConfigureAwait(false);
    }

    private async Task<object?> SelectServerAsync(HttpListenerContext context, CancellationToken ct)
    {
        SelectServerRequest request = await ReadJsonAsync<SelectServerRequest>(context, ct).ConfigureAwait(false);
        string server = RequireText(request.Server, "server", 64);

        return await RunActionAsync(() =>
        {
            // Relogin performs a full logout/login/connect using the
            // credentials already stored in the client; passwords never
            // travel through this endpoint.
            bool connected = _bot.Servers.Relogin(server);
            string actualServer = Safe(() => _bot.Servers.LastName, string.Empty);
            if (string.IsNullOrWhiteSpace(actualServer))
                actualServer = server;
            return new
            {
                connected,
                server = actualServer,
                state = BuildState()
            };
        }, ct, timeoutSeconds: 300).ConfigureAwait(false);
    }

    private async Task<object?> LoadQuestsAsync(HttpListenerContext context, CancellationToken ct)
    {
        LoadQuestsRequest request = await ReadJsonAsync<LoadQuestsRequest>(context, ct).ConfigureAwait(false);
        int[] ids = ValidateQuestIds(request.Ids);

        return await RunActionAsync(() =>
        {
            RequirePlaying();

            if (ids.Length == 1)
            {
                _bot.Quests.EnsureLoad(ids[0]);
            }
            else
            {
                _bot.Quests.Load(ids);
                _bot.Wait.ForTrue(() => ids.All(id => _bot.Quests.Tree.Any(q => q.ID == id)), 30);
            }

            List<Quest> found = _bot.Quests.Tree.Where(q => ids.Contains(q.ID)).ToList();
            return new
            {
                requested = ids,
                loaded = found.Count,
                quests = found.Select(ToQuestDto).ToList()
            };
        }, ct, timeoutSeconds: 120).ConfigureAwait(false);
    }

    private async Task<object?> AcceptQuestAsync(HttpListenerContext context, CancellationToken ct)
    {
        QuestActionRequest request = await ReadJsonAsync<QuestActionRequest>(context, ct).ConfigureAwait(false);
        int id = RequireQuestId(request.Id);

        return await RunActionAsync(() =>
        {
            RequirePlaying();
            bool accepted = request.Ensure
                ? _bot.Quests.EnsureAccept(id)
                : _bot.Quests.Accept(id);

            return new
            {
                id,
                accepted,
                inProgress = _bot.Quests.IsInProgress(id),
                quest = TryGetQuestDto(id)
            };
        }, ct, timeoutSeconds: 180).ConfigureAwait(false);
    }

    private async Task<object?> CompleteQuestAsync(HttpListenerContext context, CancellationToken ct)
    {
        QuestActionRequest request = await ReadJsonAsync<QuestActionRequest>(context, ct).ConfigureAwait(false);
        int id = RequireQuestId(request.Id);
        int itemId = request.ItemId < -1 ? -1 : request.ItemId;

        return await RunActionAsync(() =>
        {
            RequirePlaying();
            bool completed = request.Ensure
                ? _bot.Quests.EnsureComplete(id, itemId, request.Special)
                : _bot.Quests.Complete(id, itemId, request.Special);

            return new
            {
                id,
                completed,
                inProgress = _bot.Quests.IsInProgress(id),
                quest = TryGetQuestDto(id)
            };
        }, ct, timeoutSeconds: 180).ConfigureAwait(false);
    }

    private async Task<object?> AbandonQuestAsync(HttpListenerContext context, CancellationToken ct)
    {
        QuestActionRequest request = await ReadJsonAsync<QuestActionRequest>(context, ct).ConfigureAwait(false);
        int id = RequireQuestId(request.Id);

        return await RunActionAsync(() =>
        {
            RequirePlaying();
            bool abandoned = _bot.Quests.Abandon(id);
            return new
            {
                id,
                abandoned,
                inProgress = _bot.Quests.IsInProgress(id),
                quest = TryGetQuestDto(id)
            };
        }, ct, timeoutSeconds: 60).ConfigureAwait(false);
    }

    private object BuildQuests()
    {
        List<Quest> tree = Safe(() => _bot.Quests.Tree, new List<Quest>());
        return new
        {
            active = tree.Where(q => q.Active).Select(ToQuestDto).ToList(),
            completed = tree.Where(q => q.Status == "c").Select(ToQuestDto).ToList(),
            loaded = tree.Select(ToQuestDto).ToList(),
            registered = Safe(() => _bot.Quests.Registered.ToList(), new List<int>()),
            cachedCount = Safe(() => _bot.Quests.CachedDictionary.Count, 0)
        };
    }

    private object BuildQuest(int id)
    {
        RequirePlaying();
        if (id <= 0)
            throw new ControlException(400, "id must be a positive quest id");

        Quest? quest = _bot.Quests.EnsureLoad(id);
        if (quest is null)
            throw new ControlException(404, "quest_not_loaded");

        return new
        {
            quest = ToQuestDto(quest),
            inProgress = Safe(() => _bot.Quests.IsInProgress(id), false),
            canComplete = Safe(() => _bot.Quests.CanComplete(id), false),
            canCompleteFullCheck = Safe(() => _bot.Quests.CanCompleteFullCheck(id), false),
            unlocked = Safe(() => _bot.Quests.IsUnlocked(id), false),
            dailyComplete = Safe(() => _bot.Quests.IsDailyComplete(id), false)
        };
    }

    private object BuildCachedQuests(HttpListenerContext context)
    {
        int start = Math.Max(1, QueryInt(context, "start", 1));
        int count = Math.Clamp(QueryInt(context, "count", 50), 1, 200);
        List<QuestData> quests = Safe(() => _bot.Quests.GetCachedQuests(start, count), new List<QuestData>());

        return new
        {
            start,
            count,
            returned = quests.Count,
            quests = quests.Select(ToQuestDataDto).ToList()
        };
    }

    private object BuildScripts()
    {
        string loaded = Safe(() => _scriptManager.LoadedScript, string.Empty);
        List<object> scripts = new();
        try
        {
            if (Directory.Exists(_scriptsRoot))
            {
                scripts = new DirectoryInfo(_scriptsRoot)
                    .EnumerateFiles("*.cs", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(f => (object)new
                    {
                        name = f.Name,
                        bytes = f.Length,
                        modifiedUtc = f.LastWriteTimeUtc
                    })
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            throw new ControlException(500, $"script_list_failed: {ex.Message}");
        }

        return new
        {
            root = _scriptsRoot,
            running = Safe(() => _scriptManager.ScriptRunning, false),
            paused = Safe(() => _scriptManager.ScriptPaused, false),
            loadedScript = Path.GetFileName(loaded),
            scripts
        };
    }

    private object BuildScriptStatus()
    {
        string loaded = Safe(() => _scriptManager.LoadedScript, string.Empty);
        string path = string.Empty;
        long bytes = 0;
        DateTime? modifiedUtc = null;

        if (!string.IsNullOrWhiteSpace(loaded))
        {
            try
            {
                FileInfo file = new(loaded);
                if (file.Exists)
                {
                    path = file.FullName;
                    bytes = file.Length;
                    modifiedUtc = file.LastWriteTimeUtc;
                }
            }
            catch
            {
                // The status endpoint must not fail because a script file
                // was deleted while it was loaded.
            }
        }

        List<string> logs = Safe(() => _logService.GetLogs(LogType.Script), new List<string>());
        return new
        {
            running = Safe(() => _scriptManager.ScriptRunning, false),
            paused = Safe(() => _scriptManager.ScriptPaused, false),
            loadedScript = Path.GetFileName(loaded),
            path,
            bytes,
            modifiedUtc,
            recentLogs = logs.TakeLast(20).ToList()
        };
    }

    private object BuildLogs(HttpListenerContext context)
    {
        string typeText = context.Request.QueryString["type"] ?? "script";
        if (!Enum.TryParse(typeText, true, out LogType type) || !Enum.IsDefined(typeof(LogType), type))
            throw new ControlException(400, "type must be one of: debug, script, flash");

        int limit = Math.Clamp(QueryInt(context, "limit", 200), 1, 2000);
        List<string> logs = Safe(() => _logService.GetLogs(type), new List<string>());
        int total = logs.Count;

        return new
        {
            type = type.ToString().ToLowerInvariant(),
            total,
            returned = Math.Min(total, limit),
            entries = logs.TakeLast(limit).ToList()
        };
    }

    private async Task<object?> WriteScriptAsync(HttpListenerContext context, CancellationToken ct)
    {
        ScriptWriteRequest request = await ReadJsonAsync<ScriptWriteRequest>(context, ct).ConfigureAwait(false);
        string name = ValidateScriptName(request.Name);
        string content = request.Content ?? string.Empty;

        if (string.IsNullOrWhiteSpace(content))
            throw new ControlException(400, "script_content_required");

        int bytes = Encoding.UTF8.GetByteCount(content);
        if (bytes > MaxScriptBytes)
            throw new ControlException(413, $"script_too_large (max {MaxScriptBytes} bytes)");

        string path = ScriptPath(name);
        bool overwritten = File.Exists(path);
        if (overwritten && !request.Overwrite)
            throw new ControlException(409, "script_exists (set overwrite=true to replace it)");
        if (overwritten && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ControlException(400, "script_path_is_a_symlink");

        Directory.CreateDirectory(_scriptsRoot);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct).ConfigureAwait(false);

        FileInfo file = new(path);
        return new
        {
            written = true,
            name,
            bytes,
            overwritten,
            modifiedUtc = file.LastWriteTimeUtc
        };
    }

    private async Task<object?> RunScriptAsync(HttpListenerContext context, CancellationToken ct)
    {
        ScriptRunRequest request = await ReadJsonAsync<ScriptRunRequest>(context, ct).ConfigureAwait(false);
        string name = ValidateScriptName(request.Name);
        string path = ScriptPath(name);
        if (!File.Exists(path))
            throw new ControlException(404, "script_not_found");

        return await RunScriptActionAsync(async () =>
        {
            if (_scriptManager.ScriptRunning)
            {
                if (!request.Force)
                    throw new ControlException(409, "script_already_running (set force=true to stop and restart)");

                await _scriptManager.StopScript().ConfigureAwait(false);
            }

            _scriptManager.SetLoadedScript(path);
            Exception? error = await _scriptManager.StartScript().ConfigureAwait(false);
            return new
            {
                started = error is null,
                running = _scriptManager.ScriptRunning,
                paused = _scriptManager.ScriptPaused,
                loadedScript = Path.GetFileName(_scriptManager.LoadedScript),
                error = error?.Message
            };
        }, ct, timeoutSeconds: 120).ConfigureAwait(false);
    }

    private Task<object?> PauseScriptAsync(CancellationToken ct)
    {
        return RunScriptActionAsync(() =>
        {
            if (!_scriptManager.ScriptRunning)
                throw new ControlException(409, "script_not_running");

            _scriptManager.PauseScript();
            return Task.FromResult<object?>(BuildScriptStatus());
        }, ct, timeoutSeconds: 15);
    }

    private Task<object?> ResumeScriptAsync(CancellationToken ct)
    {
        return RunScriptActionAsync(() =>
        {
            if (!_scriptManager.ScriptPaused)
                throw new ControlException(409, "script_not_paused");

            _scriptManager.ResumeScript();
            return Task.FromResult<object?>(BuildScriptStatus());
        }, ct, timeoutSeconds: 15);
    }

    private Task<object?> StopScriptAsync(CancellationToken ct)
    {
        return RunScriptActionAsync(async () =>
        {
            bool wasRunning = _scriptManager.ScriptRunning;
            if (wasRunning)
                await _scriptManager.StopScript().ConfigureAwait(false);

            return (object?)new
            {
                stopped = wasRunning,
                running = _scriptManager.ScriptRunning,
                paused = _scriptManager.ScriptPaused,
                loadedScript = Path.GetFileName(_scriptManager.LoadedScript)
            };
        }, ct, timeoutSeconds: 120);
    }

    private async Task<T> RunScriptActionAsync<T>(Func<Task<T>> action, CancellationToken ct, int timeoutSeconds = 120)
    {
        if (!await _scriptGate.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), ct).ConfigureAwait(false))
            throw new ControlException(409, "another_script_action_in_progress");

        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _scriptGate.Release();
        }
    }

    private object? TryGetQuestDto(int id)
    {
        return _bot.Quests.TryGetQuest(id, out Quest? quest) && quest is not null
            ? ToQuestDto(quest)
            : null;
    }

    private static object ToQuestDto(Quest quest)
    {
        return new
        {
            id = quest.ID,
            name = quest.Name,
            description = quest.Description,
            endText = quest.EndText,
            status = quest.Status,
            active = quest.Active,
            level = quest.Level,
            once = quest.Once,
            upgrade = quest.Upgrade,
            gold = quest.Gold,
            xp = quest.XP,
            slot = quest.Slot,
            value = quest.Value,
            field = quest.Field,
            index = quest.Index,
            acceptRequirements = quest.AcceptRequirements?.Select(ToItemDto).ToList(),
            requirements = quest.Requirements?.Select(ToItemDto).ToList(),
            rewards = quest.Rewards?.Select(ToItemDto).ToList(),
            simpleRewards = quest.SimpleRewards?.Select(ToItemDto).ToList()
        };
    }

    private static object ToQuestDataDto(QuestData quest)
    {
        return new
        {
            id = quest.ID,
            name = quest.Name,
            level = quest.Level,
            once = quest.Once,
            upgrade = quest.Upgrade,
            gold = quest.Gold,
            xp = quest.XP,
            slot = quest.Slot,
            value = quest.Value,
            acceptRequirements = quest.AcceptRequirements?.Select(ToItemDto).ToList(),
            requirements = quest.Requirements?.Select(ToItemDto).ToList(),
            rewards = quest.Rewards?.Select(ToItemDto).ToList(),
            simpleRewards = quest.SimpleRewards?.Select(ToItemDto).ToList()
        };
    }

    private static object ToItemDto(ItemBase item)
    {
        return new
        {
            id = item.ID,
            name = item.Name,
            quantity = item.Quantity,
            maxStack = item.MaxStack,
            upgrade = item.Upgrade,
            category = item.CategoryString
        };
    }

    private string ScriptPath(string name)
    {
        string full = Path.GetFullPath(Path.Combine(_scriptsRoot, name));
        string rootPrefix = _scriptsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, StringComparison.Ordinal))
            throw new ControlException(400, "invalid_script_path");
        return full;
    }

    private static string ValidateScriptName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ControlException(400, "name is required");

        string name = value.Trim();
        if (name.Length > 128)
            throw new ControlException(400, "name is too long (max 128)");
        if (!name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            throw new ControlException(400, "name must end with .cs");
        if (name != Path.GetFileName(name) || name.Contains('/') || name.Contains('\\'))
            throw new ControlException(400, "name must be a file name, not a path");
        if (name.Any(char.IsControl) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ControlException(400, "name contains invalid characters");

        return name;
    }

    private static int[] ValidateQuestIds(int[]? ids)
    {
        if (ids is null || ids.Length == 0)
            throw new ControlException(400, "ids is required");
        if (ids.Length > 30)
            throw new ControlException(400, "at most 30 quest ids per request");
        if (ids.Any(id => id <= 0))
            throw new ControlException(400, "quest ids must be positive");

        return ids.Distinct().ToArray();
    }

    private static int RequireQuestId(int id)
    {
        if (id <= 0)
            throw new ControlException(400, "id must be a positive quest id");
        return id;
    }

    private static int QueryInt(HttpListenerContext context, string key, int fallback)
    {
        string? raw = context.Request.QueryString[key];
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (!int.TryParse(raw, out int value))
            throw new ControlException(400, $"{key} must be an integer");
        return value;
    }

    private static int QueryInt(HttpListenerContext context, string key)
    {
        string? raw = context.Request.QueryString[key];
        if (!int.TryParse(raw, out int value))
            throw new ControlException(400, $"{key} must be an integer");
        return value;
    }

    private async Task<T> RunActionAsync<T>(Func<T> action, CancellationToken ct, int timeoutSeconds = ActionGateTimeoutSeconds)
    {
        if (!await _actionGate.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), ct).ConfigureAwait(false))
            throw new ControlException(409, "another_action_in_progress");

        try
        {
            return await Task.Run(action, ct).ConfigureAwait(false);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private object BuildState()
    {
        bool playing = Safe(() => _bot.Player.Playing, false);
        Monster? target = Safe<Monster?>(() => _bot.Player.Target, null);
        object? targetDto = target is not null && (target.ID != 0 || target.MapID != 0 || !string.IsNullOrWhiteSpace(target.Name))
            ? new
            {
                name = target.Name,
                id = target.ID,
                mapId = target.MapID,
                hp = target.HP,
                maxHp = target.MaxHP,
                cell = target.Cell,
                alive = target.Alive
            }
            : null;

        return new
        {
            version = Safe(() => _bot.Version.ToString(), "unknown"),
            loggedIn = Safe(() => _bot.Player.LoggedIn, false),
            playing,
            alive = Safe(() => _bot.Player.Alive, false),
            inCombat = Safe(() => _bot.Player.InCombat, false),
            loaded = Safe(() => _bot.Player.Loaded, false),
            username = Safe(() => _bot.Player.Username, string.Empty),
            id = Safe(() => _bot.Player.ID, 0),
            level = Safe(() => _bot.Player.Level, 0),
            health = Safe(() => _bot.Player.Health, 0),
            maxHealth = Safe(() => _bot.Player.MaxHealth, 0),
            mana = Safe(() => _bot.Player.Mana, 0),
            maxMana = Safe(() => _bot.Player.MaxMana, 0),
            gold = Safe(() => _bot.Player.Gold, 0),
            coins = Safe(() => _bot.Player.Coins, 0),
            cell = Safe(() => _bot.Player.Cell, string.Empty),
            pad = Safe(() => _bot.Player.Pad, string.Empty),
            x = Safe(() => _bot.Player.X, 0),
            y = Safe(() => _bot.Player.Y, 0),
            playerState = Safe(() => _bot.Player.State, 0),
            server = Safe(() => _bot.Player.ServerIP, string.Empty),
            script = new
            {
                running = Safe(() => _scriptManager.ScriptRunning, false),
                paused = Safe(() => _scriptManager.ScriptPaused, false),
                loaded = Path.GetFileName(Safe(() => _scriptManager.LoadedScript, string.Empty))
            },
            map = new
            {
                name = Safe(() => _bot.Map.Name, string.Empty),
                fullName = Safe(() => _bot.Map.FullName, string.Empty),
                roomId = Safe(() => _bot.Map.RoomID, 0),
                loaded = playing && Safe(() => _bot.Map.Loaded, false),
                loading = Safe(() => _bot.Map.Loading, false),
                playerCount = Safe(() => _bot.Map.PlayerCount, 0)
            },
            target = targetDto,
            timestamp = DateTimeOffset.UtcNow
        };
    }

    private object BuildMap()
    {
        bool playing = Safe(() => _bot.Player.Playing, false);
        List<object> players = ReadPlayers();
        return new
        {
            name = Safe(() => _bot.Map.Name, string.Empty),
            fullName = Safe(() => _bot.Map.FullName, string.Empty),
            roomId = Safe(() => _bot.Map.RoomID, 0),
            playerCount = Safe(() => _bot.Map.PlayerCount, 0),
            loaded = playing && Safe(() => _bot.Map.Loaded, false),
            loading = Safe(() => _bot.Map.Loading, false),
            filePath = Safe(() => _bot.Map.FilePath, string.Empty),
            fileName = Safe(() => _bot.Map.FileName, string.Empty),
            cells = Safe(() => _bot.Map.Cells, new List<string>()),
            players
        };
    }

    private object BuildPlayers()
    {
        return new
        {
            map = Safe(() => _bot.Map.Name, string.Empty),
            players = ReadPlayers()
        };
    }

    private List<object> ReadPlayers()
    {
        List<PlayerInfo> players = Safe(() => _bot.Map.Players ?? new List<PlayerInfo>(), new List<PlayerInfo>());

        // uoTree binding can be empty immediately after a map change even
        // though areaUsers is populated. Fall back to the player names.
        if (players.Count == 0)
        {
            List<string> names = Safe(() => _bot.Map.PlayerNames ?? new List<string>(), new List<string>());
            foreach (string name in names)
            {
                PlayerInfo? player = Safe<PlayerInfo?>(() => _bot.Map.GetPlayer(name), null);
                if (player is not null)
                    players.Add(player);
            }
        }

        return players.Select(ToPlayerDto).ToList();
    }

    private object BuildMonsters()
    {
        return new
        {
            map = Safe(() => _bot.Map.Name, string.Empty),
            cell = Safe(() => _bot.Player.Cell, string.Empty),
            currentCell = Safe(() => _bot.Monsters.CurrentAvailableMonsters.Select(ToMonsterDto).ToList(), new List<object>()),
            all = Safe(() => _bot.Monsters.MapMonsters.Select(ToMonsterDto).ToList(), new List<object>())
        };
    }

    private object BuildServers()
    {
        return new
        {
            connected = Safe(() => _bot.Servers.IsConnected, false),
            last = Safe(() => _bot.Servers.LastName, string.Empty),
            servers = Safe(() => _bot.Servers.ServerList.Select(s => (object)new
            {
                name = s.Name,
                ip = s.IP,
                port = s.Port,
                online = s.Online,
                playerCount = s.PlayerCount,
                maxPlayers = s.MaxPlayers,
                language = s.Lang,
                upgradeOnly = s.Upgrade
            }).ToList(), new List<object>())
        };
    }

    private static object ToPlayerDto(PlayerInfo player)
    {
        return new
        {
            name = player.Name,
            entId = player.EntID,
            level = player.Level,
            hp = player.HP,
            maxHp = player.MaxHP,
            mp = player.MP,
            cell = player.Cell,
            pad = player.Pad,
            x = player.X,
            y = player.Y,
            state = player.State,
            afk = player.AFK
        };
    }

    private static object ToMonsterDto(Monster monster)
    {
        return new
        {
            name = monster.Name,
            id = monster.ID,
            mapId = monster.MapID,
            race = monster.Race,
            cell = monster.Cell,
            hp = monster.HP,
            maxHp = monster.MaxHP,
            state = monster.State,
            alive = monster.Alive
        };
    }

    private void RequirePlaying()
    {
        if (!Safe(() => _bot.Player.Playing, false))
            throw new ControlException(409, "player_not_playing");
    }

    private async Task<T> ReadJsonAsync<T>(HttpListenerContext context, CancellationToken ct) where T : class
    {
        if (context.Request.ContentLength64 > MaxBodyBytes)
            throw new ControlException(413, "request_body_too_large");
        if (context.Request.ContentLength64 == 0)
            throw new ControlException(400, "request_body_required");

        T? value = await JsonSerializer.DeserializeAsync<T>(context.Request.InputStream, _json, ct).ConfigureAwait(false);
        return value ?? throw new ControlException(400, "invalid_json_body");
    }

    private async Task WriteJsonAsync(HttpListenerContext context, int status, object payload)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, _json);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.Headers["Cache-Control"] = "no-store";
        await context.Response.OutputStream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
    }

    private bool IsAuthorized(HttpListenerContext context)
    {
        if (_token.Length == 0)
            return true;

        string? authorization = context.Request.Headers["Authorization"];
        string provided = authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization[7..].Trim()
            : (context.Request.Headers["X-Wayfer-Token"]
               ?? context.Request.Headers["X-Skua-Token"]
               ?? string.Empty).Trim();

        byte[] expectedBytes = Encoding.UTF8.GetBytes(_token);
        byte[] providedBytes = Encoding.UTF8.GetBytes(provided);
        return expectedBytes.Length == providedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    private static string RequireText(string? value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ControlException(400, $"{name} is required");
        return Normalize(value, name, maxLength);
    }

    private static string OptionalText(string? value, string fallback, int maxLength)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : Normalize(value, "value", maxLength);
    }

    private static string Normalize(string value, string name, int maxLength)
    {
        string text = value.Trim();
        if (text.Length == 0)
            throw new ControlException(400, $"{name} is required");
        if (text.Length > maxLength)
            throw new ControlException(400, $"{name} is too long (max {maxLength})");
        if (text.Any(char.IsControl))
            throw new ControlException(400, $"{name} contains control characters");
        return text;
    }

    private static string RequireSecret(string? value, string name, int maxLength)
    {
        // Do not trim a password: leading/trailing spaces can be meaningful.
        if (string.IsNullOrEmpty(value))
            throw new ControlException(400, $"{name} is required");
        if (value.Length > maxLength)
            throw new ControlException(400, $"{name} is too long (max {maxLength})");
        if (value.Any(char.IsControl))
            throw new ControlException(400, $"{name} contains control characters");
        return value;
    }

    private static string? ReadEnv(string primary, string legacy)
    {
        string? value = Environment.GetEnvironmentVariable(primary);
        return string.IsNullOrEmpty(value)
            ? Environment.GetEnvironmentVariable(legacy)
            : value;
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch
        {
            return fallback;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
        _actionGate.Dispose();
        _scriptGate.Dispose();
    }

    private sealed class ControlException : Exception
    {
        public int StatusCode { get; }

        public ControlException(int statusCode, string message) : base(message)
        {
            StatusCode = statusCode;
        }
    }

    private sealed class JoinRequest
    {
        public string? Map { get; set; }
        public string? Cell { get; set; }
        public string? Pad { get; set; }
        public bool IgnoreCheck { get; set; }
    }

    private sealed class JumpRequest
    {
        public string? Cell { get; set; }
        public string? Pad { get; set; }
        public bool AutoCorrect { get; set; } = true;
        public bool ClientOnly { get; set; }
    }

    private sealed class WalkRequest
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Speed { get; set; } = 8;
    }

    private sealed class WalkSpeedRequest
    {
        public int Speed { get; set; } = 8;
    }

    private sealed class GotoRequest
    {
        public string? Player { get; set; }
    }

    private sealed class AttackRequest
    {
        public string? Monster { get; set; }
    }

    private sealed class KillRequest
    {
        public string? Monster { get; set; }
        public int TimeoutMs { get; set; } = 120000;
    }

    private sealed class RestRequest
    {
        public bool Full { get; set; }
    }

    private sealed class ReloginRequest
    {
        public string? Server { get; set; }
    }

    private sealed class LoginRequest
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? Server { get; set; }
    }

    private sealed class SelectServerRequest
    {
        public string? Server { get; set; }
    }

    private sealed class LoadQuestsRequest
    {
        public int[]? Ids { get; set; }
    }

    private sealed class QuestActionRequest
    {
        public int Id { get; set; }
        public int ItemId { get; set; } = -1;
        public bool Special { get; set; }
        public bool Ensure { get; set; } = true;
    }

    private sealed class ScriptWriteRequest
    {
        public string? Name { get; set; }
        public string? Content { get; set; }
        public bool Overwrite { get; set; }
    }

    private sealed class ScriptRunRequest
    {
        public string? Name { get; set; }
        public bool Force { get; set; }
    }
}
