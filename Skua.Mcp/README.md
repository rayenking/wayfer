# Wayfer MCP

MCP (Model Context Protocol) server for **Wayfer**, the Skua-based Linux/macOS
port. It lets an AI agent control the AQW character through tools instead of
clicking the UI.

The MCP server is a **stdio** process, so it can run on Linux while the Wayfer
game host runs on a Mac. It talks to the Wayfer control API over HTTP.

```
AI agent (Linux/macOS)
  └─ Skua.Mcp (Wayfer MCP, stdio)
       └─ HTTP + Bearer token
            └─ Skua.App.Avalonia control API (game host)
                 └─ IScriptInterface → Ruffle/AQW
```

## 1. Start Wayfer with the control API

From the repo root:

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
cd Skua.App.Avalonia
dotnet build
dotnet run
```

The control API listens on `http://127.0.0.1:8766` by default.

Map discovery (`wayfer_get_map_npcs`) requires Java 17+ on the game host, because
Wayfer uses the bundled FFDec jar to read the map SWF. The first call for a new
map takes a few seconds; the parsed result is cached in
`~/Library/Application Support/Skua/cache/mapinfo`.

### Remote agent on the LAN

Only do this on a trusted network. The API can move, attack, run scripts, and
log in as your character. The optional `wayfer_login` tool sends the AQW
password over this HTTP channel, so use **loopback or an SSH tunnel/VPN**, never
plain LAN or the public internet.

```bash
# macOS game host
export WAYFER_CONTROL_BIND=0.0.0.0
export WAYFER_CONTROL_TOKEN="$(openssl rand -hex 32)"
dotnet run
```

Keep the token secret. Do not expose port `8766` to the internet; use a VPN or
an SSH tunnel if the agent is remote:

```bash
ssh -L 8766:127.0.0.1:8766 user@mac
```

| Variable | Default | Meaning |
|---|---|---|
| `WAYFER_CONTROL_ENABLED` | `1` | Set `0` to disable the API. |
| `WAYFER_CONTROL_BIND` | `127.0.0.1` | Bind address. `0.0.0.0`/`*` for LAN. |
| `WAYFER_CONTROL_PORT` | `8766` | HTTP port. |
| `WAYFER_CONTROL_TOKEN` | empty | Required for non-loopback binds. Sent as `Authorization: Bearer ...`. |

## 2. Run the MCP server on the agent machine

```bash
cd Skua.Mcp
dotnet build
export WAYFER_CONTROL_URL="http://127.0.0.1:8766"   # or http://<mac-lan-ip>:8766
export WAYFER_CONTROL_TOKEN="same-token-as-the-mac"
dotnet run
```

The process speaks MCP over stdin/stdout. Logs go to stderr.

### MCP client config

Use the built DLL to avoid `dotnet run` startup noise. Replace the path and
token. Example for Claude Desktop/Cursor-style JSON:

```json
{
  "mcpServers": {
    "wayfer": {
      "command": "dotnet",
      "args": ["/absolute/path/to/Skua.Mcp/bin/Debug/net10.0/Skua.Mcp.dll"],
      "env": {
        "WAYFER_CONTROL_URL": "http://127.0.0.1:8766",
        "WAYFER_CONTROL_TOKEN": "paste-the-token"
      }
    }
  }
}
```

If the agent runs on Linux and Wayfer on the Mac, set `WAYFER_CONTROL_URL` to the
Mac's LAN address and start Wayfer with `WAYFER_CONTROL_BIND=0.0.0.0` + a token.

## 3. Tools

| Tool | What it does |
|---|---|
| `wayfer_status` | HP/MP, level, gold, map, cell/pad, position, combat, target. |
| `wayfer_get_map` | Map name, room id, cells, players. |
| `wayfer_get_map_npcs` | NPCs in the current map: cell, quests (id/name/status), shops, exits. |
| `wayfer_get_players` | Players in the room with cell/pad/position/HP. |
| `wayfer_get_inventory` | Gold, AC, slots, and every inventory item. |
| `wayfer_get_monsters` | Monsters in the current cell and whole map. |
| `wayfer_get_shop` | Items in the currently open shop (cost, level, requirements). |
| `wayfer_load_shop` | Open a shop by id and return its items. |
| `wayfer_buy_item` | Buy an item from the open shop, with requirement/space checks. |
| `wayfer_get_servers` | Server list and connection state. |
| `wayfer_login` | Log in with username/password; optional server. |
| `wayfer_select_server` | Switch/connect to a server using stored credentials. |
| `wayfer_logout` | Log out and return to the login screen. |
| `wayfer_join_map` | Join a map/room, e.g. `battleon` or `battleon-12345`. |
| `wayfer_jump` | Teleport to a cell/pad in the current map. |
| `wayfer_walk_to` | Walk to X/Y in the current cell. |
| `wayfer_set_walk_speed` | Set walk speed 1–50 (default 8). |
| `wayfer_goto_player` | AQW `/goto <player>`. |
| `wayfer_attack_monster` | Select/start attacking a monster by name or map id. |
| `wayfer_kill_monster` | Attack and wait until the monster dies (timeout). |
| `wayfer_cancel_attack` | Cancel auto-attack and clear target. |
| `wayfer_leave_combat` | Jump out of combat and wait for combat end. |
| `wayfer_rest` | Rest; optionally wait until HP/MP are full. |
| `wayfer_reload_map` | Reload the current map/room. |
| `wayfer_relogin` | Reconnect with stored credentials (destructive). |
| `wayfer_get_quests` | Active/completed/loaded/registered quests. |
| `wayfer_get_quest` | Load and inspect one quest, requirements, and rewards. |
| `wayfer_get_cached_quests` | Page the cached QuestData catalogue by id range. |
| `wayfer_load_quest` | Load up to 30 quests into the quest tree. |
| `wayfer_accept_quest` | Accept a quest (retries by default). |
| `wayfer_complete_quest` | Turn in a quest, optionally choosing a reward item. |
| `wayfer_abandon_quest` | Abandon an active quest. |
| `wayfer_list_scripts` | List `.cs` files in Wayfer's scripts folder. |
| `wayfer_get_script_status` | Running/paused state, loaded script, recent logs. |
| `wayfer_get_script_logs` | Read script, debug, or flash log lines. |
| `wayfer_write_script` | Create/replace a C# script in the scripts folder. |
| `wayfer_run_script` | Compile and start a script. |
| `wayfer_pause_script` | Pause at the next cooperative checkpoint. |
| `wayfer_resume_script` | Resume a paused script. |
| `wayfer_stop_script` | Stop/end the script thread. |
| `wayfer_wait` | Sleep 0–60 seconds between actions. |

### Suggested agent loop

0. If the client is at the login screen, call `wayfer_login` (optionally with a
   server); otherwise `wayfer_relogin` or `wayfer_select_server`.
1. `wayfer_status` until `playing=true`.
2. `wayfer_join_map` with the target map/room.
3. `wayfer_get_map_npcs` to see who is in the map, which cell each NPC stands
   in, and what they offer. Jump to that cell when the player needs to interact
   with the NPC, or use the returned quest/shop ids directly.
4. `wayfer_get_monsters` to find the monster and its cell.
5. `wayfer_jump` to that cell, then `wayfer_kill_monster` (or `wayfer_attack_monster`
   plus `wayfer_status` for non-blocking control).
6. For a shop item required by a quest: `wayfer_load_shop` with the shop id from
   `wayfer_get_map_npcs`, `wayfer_get_shop` to pick the item, then
   `wayfer_buy_item`.
7. `wayfer_rest` when HP/MP is low.
8. `wayfer_get_players` + `wayfer_goto_player` to follow another player.
9. For repeatable work, `wayfer_write_script` + `wayfer_run_script`, then check
   `wayfer_get_script_status`/`wayfer_get_script_logs` instead of polling every
   game action. Use `wayfer_pause_script`/`wayfer_resume_script` for a temporary
   stop and `wayfer_stop_script` to end it.

The agent should always read the JSON response. A failed action returns
`{"ok":false,"error":...}` instead of pretending it succeeded.

## Scripts

`wayfer_write_script` + `wayfer_run_script` let the agent create a C# script and
keep it running on the game host. This is the right choice for long farms:
the agent can start the script, call `wayfer_status`/`wayfer_get_script_status`
occasionally, and stop it later instead of polling every action.

A script uses the normal Wayfer format:

```csharp
using Skua.Core.Interfaces;

public class MyFarm
{
    public static void ScriptMain(IScriptInterface bot)
    {
        while (!bot.ShouldExit)
        {
            // bot.Map.Join(...), bot.Combat.Attack(...), bot.Quests.Accept(...)
            bot.Sleep(500);
        }
    }
}
```

`wayfer_pause_script` is **cooperative**: the script stops at the next
`bot.ShouldExit`, `bot.Wait`, or `bot.Sleep` checkpoint. A tight custom loop
that never checks those may finish its current step first. `wayfer_stop_script`
cancels the thread and is the reliable “end” operation.

> `wayfer_write_script`/`wayfer_run_script` execute arbitrary C# on the game host.
> Treat the control token like a remote shell token. Do not expose port `8766`
> to the internet or give the token to an untrusted agent.

## Notes and limits

- The game/control host is currently the **macOS Avalonia app**. `Skua.Mcp`
  itself is cross-platform and runs on Linux, but the embedded WKWebView game
  host is not a Linux-native path. Use the Mac as the game host over LAN/SSH.
- The optional `wayfer_login` tool sends the AQW password to the local control API
  once; it is held in memory and never written to logs. Prefer `--user/--password`
  or `WAYFER_USER`/`WAYFER_PASS` (legacy `SKUA_USER`/`SKUA_PASS` also work) on the game host when possible, and never put the
  password in a script or MCP client config. The control token is still a shell-level
  secret: anyone with it can log in, run scripts, and control the character.
- `wayfer_kill_monster` blocks; keep `timeoutMs` reasonable (default 120s).
- The control API serializes mutating actions, so two tool calls won't move the
  character at the same time. A long `kill` can make the next action wait.
- If you change the control port, update both `WAYFER_CONTROL_PORT` on the Mac and
  `WAYFER_CONTROL_URL` in the MCP client config.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `control_unreachable` | Wayfer isn't running, wrong URL/port, or firewall blocks 8766. |
| `unauthorized` | Token mismatch between `WAYFER_CONTROL_TOKEN` and MCP env. |
| `player_not_playing` | Wait for login/character load; call `wayfer_status` first. |
| `another_action_in_progress` | A previous long action (join/kill) is still running. Wait and retry. |
| `wayfer_status` shows `playing=false` after auto-login | Check the Wayfer log for login/relay errors; retry `wayfer_relogin`. |
