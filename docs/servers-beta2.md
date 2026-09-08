# Dedicated servers, Beta 2

## Before you start

- Install the pack on the server machine with the installer, the same release your players install.
- Do not edit the mod folder by hand afterwards. The join check compares every file under `mods/dlc5` between host and joiner, and any difference refuses the joiner.
- Players run the installer too. The in-game join download carries only the `mods/dlc5` tree; the map zones, the client-side fixes and the Steam-side image and sound files do not travel that way.

## Launch line

```
plutonium-bootstrapper-win32.exe t6zm "<Black Ops II folder>" -dedicated +set fs_game mods/dlc5 +exec server.cfg +map zm_prototype
```

- `fs_game` must be `mods/dlc5`. A bare `dlc5` is rejected and the server runs stock.
- Put URL dvars in `server.cfg`, not on the command line. `//` on the command line is read as a comment and the URL is cut short.

## server.cfg

```
set sv_hostname "Zombies Declassified"
set g_gametype zclassic
set sv_maxclients 4
set sv_wwwDownload 1
set sv_wwwBaseURL "http://your-host/mods"
set sv_wwwDlDisconnected 0
```

FastDL is optional. Without it, a joiner whose files match needs nothing and a joiner whose files differ is refused. With it, the server re-serves the differing `mods/dlc5` files; it still does not deliver zones or client fixes.

## Map names

`zm_prototype` `zm_asylum` `zm_sumpf` `zm_factory` `zm_theater` `zm_pentagon` `zm_cosmodrome` `zm_temple` `zm_moon`

## What the join refusal looks like

On the joiner, right after `Received mod dl info response!`:

```
COM_ERROR (1) A mod is required to connect to this server, but the server has not configured FastDL
```

The server never lists that client as connected. The message is the same for a missing mod and for a single stale file. A joiner whose files match prints `mod already downloaded`, then `EXE_LOADMOD`, restarts into the mod and joins.

## Reporting a problem

From the joiner: `console_zm.log` under `%LOCALAPPDATA%\Plutonium\storage\t6\mods\dlc5\` (or `main\` when the mod was picked in the menu), with the lines around `Received mod dl info response!`, any `COM_ERROR`, and the `Loading fastfile` lines. From the host: its `console_zm.log` with the connected and dropped lines.
