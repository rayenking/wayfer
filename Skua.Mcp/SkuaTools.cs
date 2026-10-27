using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Skua.Mcp;

/// <summary>
/// MCP tools exposed to an AI agent. Each tool is a thin call into the
/// running Wayfer process; no game credentials or packet construction live here.
/// </summary>
[McpServerToolType]
public static class SkuaTools
{
    [McpServerTool(Name = "wayfer_status", ReadOnly = true)]
    [Description("Get the current AQW bot state: login/playing status, HP/MP, level, gold, map, cell/pad, position, combat state, and current target. Call this first and after every movement or combat action.")]
    public static Task<string> Status(ControlClient client, CancellationToken ct)
        => client.GetAsync("state", ct);

    [McpServerTool(Name = "wayfer_get_map", ReadOnly = true)]
    [Description("Get the current map/room details: map name, room id, cells, loaded state, and all players in the room with their cell, pad, position, HP, and level.")]
    public static Task<string> GetMap(ControlClient client, CancellationToken ct)
        => client.GetAsync("map", ct);

    [McpServerTool(Name = "wayfer_get_players", ReadOnly = true)]
    [Description("List all players in the current room, including cell, pad, coordinates, HP, level, state, and AFK flag.")]
    public static Task<string> GetPlayers(ControlClient client, CancellationToken ct)
        => client.GetAsync("players", ct);

    [McpServerTool(Name = "wayfer_get_monsters", ReadOnly = true)]
    [Description("List monsters in the current cell and in the whole map, with monster id, map id, name, cell, HP, and alive state.")]
    public static Task<string> GetMonsters(ControlClient client, CancellationToken ct)
        => client.GetAsync("monsters", ct);

    [McpServerTool(Name = "wayfer_get_servers", ReadOnly = true)]
    [Description("List available AQW game servers and connection state. Use this before relogin if the player is disconnected.")]
    public static Task<string> GetServers(ControlClient client, CancellationToken ct)
        => client.GetAsync("servers", ct);

    [McpServerTool(Name = "wayfer_login")]
    [Description("Log in to AQW with a username and password. If server is set, performs a full login and connects to that server. If server is empty, starts the login flow and leaves the server choice to wayfer_select_server. The password is sent only to the local control API; never put it in scripts or logs.")]
    public static Task<string> Login(
        ControlClient client,
        [Description("AQW username.")] string username,
        [Description("AQW password. Treat it as a secret; never log or echo it.")] string password,
        [Description("Optional server name, e.g. 'Yorumi'. Empty means log in without connecting.")] string server = "",
        CancellationToken ct = default)
        => client.PostAsync("login", new { username, password, server }, ct);

    [McpServerTool(Name = "wayfer_select_server")]
    [Description("Select an AQW server after logging in. This performs a full logout/login/connect using the credentials already stored in the client, so it also works as a server switch while playing.")]
    public static Task<string> SelectServer(
        ControlClient client,
        [Description("Server name, e.g. 'Yorumi'.")] string server,
        CancellationToken ct = default)
        => client.PostAsync("select-server", new { server }, ct);

    [McpServerTool(Name = "wayfer_logout")]
    [Description("Log out of AQW and return to the login screen. The control API and MCP server stay running.")]
    public static Task<string> Logout(ControlClient client, CancellationToken ct = default)
        => client.PostAsync("logout", new { }, ct);

    [McpServerTool(Name = "wayfer_join_map")]
    [Description("Join a map/room by name. Blocks until the map is loaded or the bot's join timeout is reached. A map can include a room instance, for example 'battleon-12345'; plain 'battleon' joins the public room. Optionally set the spawn cell and pad.")]
    public static Task<string> JoinMap(
        ControlClient client,
        [Description("Map name, optionally with room instance, e.g. 'battleon' or 'battleon-12345'.")] string map,
        [Description("Cell to spawn in. Default: Enter.")] string cell = "Enter",
        [Description("Pad to spawn at. Default: Spawn.")] string pad = "Spawn",
        [Description("Join even if the bot is already in the map. Default: false.")] bool ignoreCheck = false,
        CancellationToken ct = default)
        => client.PostAsync("join", new { map, cell, pad, ignoreCheck }, ct);

    [McpServerTool(Name = "wayfer_jump")]
    [Description("Jump/teleport to a cell and pad in the current map (the AQW goto-cell action). Blocks until the cell changes or times out.")]
    public static Task<string> Jump(
        ControlClient client,
        [Description("Destination cell name.")] string cell,
        [Description("Destination pad name. Default: Spawn.")] string pad = "Spawn",
        [Description("Correct the cell/pad to a valid one. Default: true.")] bool autoCorrect = true,
        [Description("Move only on the client and do not send a move packet. Default: false.")] bool clientOnly = false,
        CancellationToken ct = default)
        => client.PostAsync("jump", new { cell, pad, autoCorrect, clientOnly }, ct);

    [McpServerTool(Name = "wayfer_walk_to")]
    [Description("Walk to X/Y coordinates in the current cell. Blocks until the player reaches the position or the wait times out. Use wayfer_jump for cell-to-cell movement.")]
    public static Task<string> WalkTo(
        ControlClient client,
        [Description("Target X coordinate in the current cell.")] int x,
        [Description("Target Y coordinate in the current cell.")] int y,
        [Description("Walk speed, 1-50. Default: 8.")] int speed = 8,
        CancellationToken ct = default)
        => client.PostAsync("walk", new { x, y, speed }, ct);

    [McpServerTool(Name = "wayfer_set_walk_speed")]
    [Description("Set the player walk speed (1-50). Higher values make wayfer_walk_to cover ground faster; the default is 8.")]
    public static Task<string> SetWalkSpeed(
        ControlClient client,
        [Description("Walk speed from 1 to 50.")] int speed = 8,
        CancellationToken ct = default)
        => client.PostAsync("walk-speed", new { speed }, ct);

    [McpServerTool(Name = "wayfer_goto_player")]
    [Description("Use the AQW /goto command to move to another player's cell and pad. The player must be in the same map.")]
    public static Task<string> GotoPlayer(
        ControlClient client,
        [Description("Exact player username to goto.")] string player,
        CancellationToken ct = default)
        => client.PostAsync("goto", new { player }, ct);

    [McpServerTool(Name = "wayfer_attack_monster")]
    [Description("Select and approach a monster by name or map id (targeting). Returns immediately; use wayfer_status to inspect the target and wayfer_kill_monster to attack until it dies.")]
    public static Task<string> AttackMonster(
        ControlClient client,
        [Description("Monster name (e.g. 'Slime') or numeric map id.")] string monster,
        CancellationToken ct = default)
        => client.PostAsync("attack", new { monster }, ct);

    [McpServerTool(Name = "wayfer_kill_monster")]
    [Description("Attack a monster by name or map id and block until it dies or the timeout is reached. Use this for a single target; use wayfer_attack_monster plus wayfer_status for non-blocking control.")]
    public static Task<string> KillMonster(
        ControlClient client,
        [Description("Monster name (e.g. 'Slime') or numeric map id.")] string monster,
        [Description("Maximum wait in milliseconds (1000-600000). Default: 120000.")] int timeoutMs = 120000,
        CancellationToken ct = default)
        => client.PostAsync("kill", new { monster, timeoutMs }, ct);

    [McpServerTool(Name = "wayfer_cancel_attack")]
    [Description("Cancel the current auto-attack and clear the selected target without leaving combat.")]
    public static Task<string> CancelAttack(ControlClient client, CancellationToken ct = default)
        => client.PostAsync("cancel-attack", new { }, ct);

    [McpServerTool(Name = "wayfer_leave_combat")]
    [Description("Cancel the current target and jump out of combat, then wait until combat ends.")]
    public static Task<string> LeaveCombat(ControlClient client, CancellationToken ct = default)
        => client.PostAsync("exit-combat", new { }, ct);

    [McpServerTool(Name = "wayfer_rest")]
    [Description("Rest to recover HP/MP. If full=true, wait until both are full.")]
    public static Task<string> Rest(
        ControlClient client,
        [Description("Wait until HP and MP are full. Default: false.")] bool full = false,
        CancellationToken ct = default)
        => client.PostAsync("rest", new { full }, ct);

    [McpServerTool(Name = "wayfer_reload_map")]
    [Description("Reload the current map/room. Useful if the map or avatar is stuck.")]
    public static Task<string> ReloadMap(ControlClient client, CancellationToken ct = default)
        => client.PostAsync("reload-map", new { }, ct);

    [McpServerTool(Name = "wayfer_relogin")]
    [Description("Reconnect using the credentials already stored in Wayfer. Optionally specify a server name (for example 'Yorumi'). Use only when disconnected; this disrupts the current session.")]
    public static Task<string> Relogin(
        ControlClient client,
        [Description("Server name to reconnect to. Empty uses the last server.")] string server = "",
        CancellationToken ct = default)
        => client.PostAsync("relogin", new { server }, ct);

    [McpServerTool(Name = "wayfer_get_quests", ReadOnly = true)]
    [Description("List the player's active quests, completed-but-not-turned-in quests, all loaded quests, registered auto-complete quests, and how many quests are cached. Use this to choose a quest id before loading or accepting it.")]
    public static Task<string> GetQuests(ControlClient client, CancellationToken ct)
        => client.GetAsync("quests", ct);

    [McpServerTool(Name = "wayfer_get_quest", ReadOnly = true)]
    [Description("Load and inspect one quest by id: name, status, level, gold/XP, requirements, rewards, and whether it is in progress, completable, unlocked, or a completed daily.")]
    public static Task<string> GetQuest(
        ControlClient client,
        [Description("Quest id, e.g. 1234.")] int id,
        CancellationToken ct = default)
        => client.GetAsync($"quest?id={id}", ct);

    [McpServerTool(Name = "wayfer_get_cached_quests", ReadOnly = true)]
    [Description("Page through the cached QuestData.json catalogue to find quest ids by range. This does not contact the game; use wayfer_get_quest after finding an id.")]
    public static Task<string> GetCachedQuests(
        ControlClient client,
        [Description("First quest id in the range. Default: 1.")] int start = 1,
        [Description("Number of catalogue entries, 1-200. Default: 50.")] int count = 50,
        CancellationToken ct = default)
        => client.GetAsync($"quests/cached?start={start}&count={count}", ct);

    [McpServerTool(Name = "wayfer_load_quest")]
    [Description("Load one or more quests into the game quest tree. Use this before inspecting a quest or when the quest is not loaded yet. Accepts up to 30 quest ids.")]
    public static Task<string> LoadQuest(
        ControlClient client,
        [Description("Quest ids to load.")] int[] ids,
        CancellationToken ct = default)
        => client.PostAsync("quests/load", new { ids }, ct);

    [McpServerTool(Name = "wayfer_accept_quest")]
    [Description("Accept a quest by id. By default it retries until the quest is in progress; set ensure=false for a single attempt.")]
    public static Task<string> AcceptQuest(
        ControlClient client,
        [Description("Quest id to accept.")] int id,
        [Description("Retry until accepted. Default: true.")] bool ensure = true,
        CancellationToken ct = default)
        => client.PostAsync("quests/accept", new { id, ensure }, ct);

    [McpServerTool(Name = "wayfer_complete_quest")]
    [Description("Turn in a quest by id. Use rewardItemId when the quest offers a choice of rewards; -1 lets the game choose. By default it retries until the quest is no longer in progress.")]
    public static Task<string> CompleteQuest(
        ControlClient client,
        [Description("Quest id to complete.")] int id,
        [Description("Reward item id to choose, or -1 to auto-select. Default: -1.")] int rewardItemId = -1,
        [Description("Mark the quest as special (some event quests). Default: false.")] bool special = false,
        [Description("Retry until completed. Default: true.")] bool ensure = true,
        CancellationToken ct = default)
        => client.PostAsync("quests/complete", new { id, itemId = rewardItemId, special, ensure }, ct);

    [McpServerTool(Name = "wayfer_abandon_quest")]
    [Description("Abandon an active quest by id. Use this before accepting a different questline or to clear a stuck quest.")]
    public static Task<string> AbandonQuest(
        ControlClient client,
        [Description("Quest id to abandon.")] int id,
        CancellationToken ct = default)
        => client.PostAsync("quests/abandon", new { id }, ct);

    [McpServerTool(Name = "wayfer_list_scripts", ReadOnly = true)]
    [Description("List C# scripts in Wayfer's scripts folder with file size and modification time. Only .cs files directly in that folder are returned.")]
    public static Task<string> ListScripts(ControlClient client, CancellationToken ct)
        => client.GetAsync("scripts", ct);

    [McpServerTool(Name = "wayfer_get_script_status", ReadOnly = true)]
    [Description("Get script engine state: running, paused, loaded script, file size/modified time, and the last 20 script log lines.")]
    public static Task<string> GetScriptStatus(ControlClient client, CancellationToken ct)
        => client.GetAsync("script", ct);

    [McpServerTool(Name = "wayfer_get_script_logs", ReadOnly = true)]
    [Description("Read recent Wayfer log lines. type can be 'script' (default), 'debug' (includes runtime exceptions/Trace), or 'flash'.")]
    public static Task<string> GetScriptLogs(
        ControlClient client,
        [Description("Log type: script, debug, or flash. Default: script.")] string type = "script",
        [Description("Maximum lines to return, 1-2000. Default: 200.")] int limit = 200,
        CancellationToken ct = default)
        => client.GetAsync($"logs?type={Uri.EscapeDataString(type)}&limit={limit}", ct);

    [McpServerTool(Name = "wayfer_write_script")]
    [Description("Create or replace a C# script in Wayfer's scripts folder. The script must define a class with a public static ScriptMain(IScriptInterface bot) method (standard Skua/Wayfer script format). This writes executable code on the game host; only use it on a trusted machine/token. Set overwrite=true to replace an existing file.")]
    public static Task<string> WriteScript(
        ControlClient client,
        [Description("File name ending in .cs, e.g. 'FarmBoss.cs'. Must be a file name, not a path.")] string name,
        [Description("Full C# source code. Maximum 256 KB.")] string content,
        [Description("Replace the file if it already exists. Default: false.")] bool overwrite = false,
        CancellationToken ct = default)
        => client.PostAsync("scripts/write", new { name, content, overwrite }, ct);

    [McpServerTool(Name = "wayfer_run_script")]
    [Description("Compile and start a script from Wayfer's scripts folder. The script runs on the game host and keeps running while the agent does other work. Use wayfer_get_script_status/logs to monitor it. force=true stops a running script first.")]
    public static Task<string> RunScript(
        ControlClient client,
        [Description("Script file name ending in .cs.")] string name,
        [Description("Stop the currently running script and start this one. Default: false.")] bool force = false,
        CancellationToken ct = default)
        => client.PostAsync("scripts/run", new { name, force }, ct);

    [McpServerTool(Name = "wayfer_pause_script")]
    [Description("Pause the running script at its next cooperative checkpoint. Pause is cooperative: scripts that use Bot.ShouldExit, Bot.Wait, or Bot.Sleep pause reliably; a tight custom loop may finish its current step first.")]
    public static Task<string> PauseScript(ControlClient client, CancellationToken ct = default)
        => client.PostAsync("scripts/pause", new { }, ct);

    [McpServerTool(Name = "wayfer_resume_script")]
    [Description("Resume a paused script.")]
    public static Task<string> ResumeScript(ControlClient client, CancellationToken ct = default)
        => client.PostAsync("scripts/resume", new { }, ct);

    [McpServerTool(Name = "wayfer_stop_script")]
    [Description("Stop/end the running script and cancel its script thread. Safe to call when no script is running.")]
    public static Task<string> StopScript(ControlClient client, CancellationToken ct = default)
        => client.PostAsync("scripts/stop", new { }, ct);

    [McpServerTool(Name = "wayfer_wait")]
    [Description("Wait for a number of seconds before the next action. Use this to pace movement or combat instead of busy polling.")]
    public static async Task<string> Wait(
        [Description("Seconds to wait, 0-60.")] int seconds,
        CancellationToken ct = default)
    {
        int clamped = Math.Clamp(seconds, 0, 60);
        if (clamped > 0)
            await Task.Delay(TimeSpan.FromSeconds(clamped), ct).ConfigureAwait(false);
        return $"{{\"ok\":true,\"data\":{{\"waitedSeconds\":{clamped}}}}}";
    }
}
