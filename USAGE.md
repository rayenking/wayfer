# Wayfer Usage Guide

Wayfer is an AQW desktop client with a built-in bot engine and an MCP control
surface. This guide covers the normal desktop workflow and the AI-agent setup.

## Requirements

- macOS for the game host (validated)
- .NET SDK 10 (`~/.dotnet`)
- AQW assets in `Skua.App.Avalonia/` (`skua.swf`, `wwwroot/ruffle`)

Linux can run the MCP agent and Avalonia UI, but the native game host needs a
WebKitGTK/WPE bridge and is not yet validated. Use a Mac as the game host for
now.

## Start Wayfer

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"

cd Skua.App.Avalonia
dotnet build
dotnet run
```

The app opens at the AQW login screen. Log in normally, choose a server, and
wait for the character to load. The **+** button in the menu bar opens the bot
tools.

### Optional auto-login

```bash
export WAYFER_USER="your-username"
export WAYFER_PASS="your-password"
dotnet run -- --server Yorumi
```

Legacy `SKUA_USER` / `SKUA_PASS` still work. Prefer the environment variables or
CLI options over putting credentials in a script.

### Server-only mode

For browser or automation tests:

```bash
dotnet run -- --server-only
```

The game page is available at `http://127.0.0.1:8765/`.

## Bot tools

The tools window includes:

- Script Loader, Script Repo, and script logs
- Auto, Jump, Fast Travel, and Drops
- Options, Plugins, Console, and packet tools
- Runtime helpers and stats

C# scripts live in:

```text
~/Library/Application Support/Skua/Scripts
```

The folder keeps the Skua path for compatibility with existing scripts.

## MCP / AI control

Start Wayfer with the control API enabled (loopback is the default):

```bash
cd Skua.App.Avalonia
dotnet run
```

The control API listens on `http://127.0.0.1:8766`.

Run the MCP server on the agent machine:

```bash
cd Skua.Mcp
dotnet build
export WAYFER_CONTROL_URL="http://127.0.0.1:8766"
export WAYFER_CONTROL_TOKEN=""   # set this when using a non-loopback bind
dotnet run
```

For a Linux agent controlling a Mac game host, use an SSH tunnel instead of
exposing the port:

```bash
ssh -L 8766:127.0.0.1:8766 user@mac
```

The MCP tools cover login, server selection, logout, movement, map/room
joining, combat, quests, and script lifecycle. See
[`Skua.Mcp/README.md`](./Skua.Mcp/README.md) for the full tool table and client
configuration.

> The login tool sends the AQW password over the control API. Keep the API on
> loopback or behind an SSH tunnel/VPN, and treat the control token like a
> remote-shell token.

## Common workflows

### Join a room and move

```text
wayfer_status
wayfer_join_map  map="battleon" cell="Enter" pad="Spawn"
wayfer_get_map
wayfer_jump      cell="Center" pad="Spawn"
```

### Fight a monster

```text
wayfer_get_monsters
wayfer_jump cell="<monster-cell>" pad="Spawn"
wayfer_kill_monster monster="<name-or-map-id>"
wayfer_rest full=true
```

### Quests

```text
wayfer_get_cached_quests start=1 count=50
wayfer_load_quest ids=[1234]
wayfer_accept_quest id=1234
wayfer_complete_quest id=1234
wayfer_abandon_quest id=1234
```

### Long-running automation

Use a script instead of polling every action:

```text
wayfer_write_script name="Farm.cs" content="..."
wayfer_run_script   name="Farm.cs"
wayfer_get_script_status
wayfer_get_script_logs
wayfer_pause_script
wayfer_resume_script
wayfer_stop_script
```

## Rendering

Wayfer forces AQW's smooth background and `MEDIUM` quality to avoid the
`BitmapData.draw` path unsupported by Ruffle's vanilla `webgl` renderer.

Optional query parameters:

- `?renderer=webgl|wgpu-webgl|webgpu|canvas`
- `?smooth=0`
- `?lowq=0`
- `?verbose`

If a future game update needs `BitmapData.draw`, try `?renderer=wgpu-webgl`.

## Troubleshooting

- Run `dotnet build` after editing `wwwroot` or assets; `--no-build` does not
  copy changed files.
- If port `8765` is busy, close the other Wayfer instance.
- If port `8766` is busy, change `WAYFER_CONTROL_PORT` and update the MCP
  `WAYFER_CONTROL_URL`.
- If the character does not appear, check that smooth background is on and the
  renderer is `webgl`.
- Check the app console for Ruffle or relay errors.
