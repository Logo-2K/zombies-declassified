# Dedicated servers - Beta 2

What is measured (QUEUE MP1 R2, 09-01) and what a server owner needs to do. The crash class
at the end needs a real second machine; nothing here was verified on one this session.

## The host runs the package's bytes

The mod download compares every file of the `mods/dlc5` tree between host and joiner. Any
difference refuses the joiner with the same error a missing mod gives. So the host installs
the SAME release the players install (the installer), and never patches its own tree by hand
after that. A host-side edit to any of the mod files strands every joiner on the published
package until it is re-cut.

Joiners receive ONLY the `mods/dlc5` tree through the join download. The map zones under
`usermaps/<map>/`, the raw-tree client fixes and the Steam-side image/sound files never
travel that way. Every player runs the installer first; joining is then the normal
Plutonium flow (pick the server, the client restarts into the mod, loads the map).

## Launch line

```
plutonium-bootstrapper-win32.exe t6zm "<Black Ops II dir>" -dedicated +set fs_game mods/dlc5 +exec server.cfg +map zm_prototype
```

- `fs_game` is `mods/dlc5`. The bare value `dlc5` is invalid: the engine logs
  `fs_game has invalid value 'dlc5'` and runs stock.
- URL-valued dvars go through the exec'd cfg, never the launch line: the console tokenizer
  reads `//` as a comment, so `+set sv_wwwBaseURL "http://host/"` on the command line
  arrives as `http:/` and prints `invalid download URL provided`.

`server.cfg` (the parts that matter):

```
set sv_hostname "Zombies Declassified"
set g_gametype zclassic
set sv_maxclients 4
set sv_wwwDownload 1
set sv_wwwBaseURL "http://your-host/mods"
set sv_wwwDlDisconnected 0
```

FastDL is optional. Without it a joiner who has the files needs nothing; a joiner whose files
differ is refused. With it, the server re-serves the differing `mods/dlc5` files (the mod
tree only), which heals byte drift but never delivers zones or raw fixes.

## What the join refusal looks like

On the joiner, right after `Received mod dl info response!`:

```
COM_ERROR (1) A mod is required to connect to this server, but the server has not configured FastDL
```

and the server never prints `client '<name>' connected`. The message is the same whether the
joiner lacks the mod entirely or holds one stale file in `mods/dlc5`. A joiner whose files
match prints `mod already downloaded`, then `EXE_LOADMOD`, restarts into the mod and joins.

## What to send with a report

From the joiner: `%LOCALAPPDATA%\Plutonium\storage\t6\mods\dlc5\console_zm.log` (or
`main\console_zm.log` when the mod was picked in the menu), the lines around
`Received mod dl info response!`, any `COM_ERROR`, and the `Loading fastfile` lines.
From the host: `console_zm.log` from the same tree, the `Loading fastfile mod/mod_load` line,
and the server's own `client ... connected` / `dropped` lines.

## The crash class

A joiner that mounts the mod and then dies at `Loading fastfile mod/mod_load/mod_patch`
(0xC0000005) was seen only on a copied Plutonium install used as a second client on the
same machine. That harness is not a second client: its positive control (a full package
copy) crashed the same way, so no content verdict can be read off it. Post-join crashes
need a real second machine; report them with both logs and we will take it from there.
