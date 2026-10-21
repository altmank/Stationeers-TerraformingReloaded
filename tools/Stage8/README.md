# tools/Stage8: a dedicated server and a rendered client

Stage 8 of `docs/COMET-ROCKETS.md` ("Proof as it is built"): a dedicated server and a rendered client on one
machine see the same comet, the same icons and the same impact, through the rocket path and through the console
comet, with a late join and a save and load in the middle. For bt, run by the bt operator. Never on LU's PC.

| File | What it is |
|---|---|
| `Stage8.cs`, `ServerDriver.cs`, `ClientDriver.cs`, `Stage8.csproj`, `About/` | The driver, one BepInEx plugin (`TRStage8.dll`) for both ends. `TR_STAGE8_ROLE` says which end, `TR_STAGE8_PHASE` which launch. It never touches `Mod.Networking`: the two games talk through the game and the mod, the two drivers through small files in the run's folder |
| `setup.ps1` | Makes `G:\TRStage8\server`, `client`, `server-data` and `client-data`. Run once, and again after a game or LaunchPad update |
| `run.ps1` | The orchestrator: builds, checks, starts and stops every process by its PID, judges, writes the verdict. Runs in the desktop session |
| `task.ps1` | Starts `run.ps1` in a logged-on user's desktop session through a one-off scheduled task, from ssh, and waits for it |
| `stop.ps1` | Stops an interrupted run and puts back what it set aside |

## What it touches

Everything it writes is under `G:\TRStage8`, except one thing: the game's per-user `PlayerCookie` in
`%USERPROFILE%\AppData\LocalLow\Rocketwerkz\rocketstation`, which the client shares with bt's own game. `run.ps1`
copies it aside into the run's folder before the client first starts and puts it back byte for byte at the end
(`stop.ps1` does it for a run that was stopped).

It only reads: bt's Steam client install (`setup.ps1` copies it), an installed dedicated server's game files
(`setup.ps1 -ServerSource`, only `rocketstation_DedicatedServer.exe`, `rocketstation_DedicatedServer_Data`,
`MonoBleedingEdge` and the other top-level `.exe` and `.dll`), and the `setting.xml` files under
`G:\StationeersDedicated` (to pick a port none of those servers uses). It never reads or writes
`G:\StationeersTest*`, `G:\StationeersGenTest`, bt's real saves or any other server's folder or tab, and never
stops a process it did not start: every stop is by a PID it recorded, and only when that process's executable is
under `G:\TRStage8`.

The server binds `127.0.0.1` only, with an empty password, autosave off and `AutoPauseServer` off. Both ends get
the same random `ServerAuthSecret` for each run, so the client can call the console comet with the game's own
`serverrun`.

## Set up (once)

From any session, ssh is fine:

```powershell
cd <the comet-all worktree on bt>
# the server's game files copied from an installed dedicated server, read only:
.\tools\Stage8\setup.ps1 -Root G:\TRStage8 -ServerSource G:\StationeersDedicated\server
# or installed with steamcmd instead (Steam app 600760, anonymous):
.\tools\Stage8\setup.ps1 -Root G:\TRStage8 -SteamCmd <path to steamcmd.exe>
```

It ends with `server build X, client build X` and `setup done`. Different builds exit 1: the game refuses a join
between them. bt's Steam client updates itself, so run `setup.ps1` again after a game update (the server too:
`-SteamCmd`, or `-ServerSource` once that server has updated). `-SkipServer` and `-SkipClient` refresh one side.

The client copy is a mirror of `G:\SteamLibrary\steamapps\common\Stationeers` without its `mods`, `saves`,
`scripts`, `thumbnails`, `setting.xml`, `modconfig.xml`, `BepInEx\config`, `BepInEx\plugins` (but
StationeersLaunchPad) and logs. Both installs get their own `BepInEx\config\stationeers.launchpad.cfg`:
`SavePathOverride` to their own data folder, Steam off, no update, news, repo or server pack checks, no countdowns.

## Run

The client draws frames, so `run.ps1` must run in the interactive desktop session of a logged-on user (lu's RDP
session, as the CometShot rig runs), not in session 0. It refuses to start in session 0.

From ssh:

```powershell
.\tools\Stage8\task.ps1 -Root G:\TRStage8 -User lu
```

`task.ps1` writes `G:\TRStage8\task-go.ps1`, creates the scheduled task `TR-Stage8` (`schtasks /Create /IT /RU lu`,
which runs only while lu is logged on, in lu's session), starts it, prints the run's progress from its
transcript, and when the run writes `finished.txt` prints the end of the verdict and deletes the task. It exits 0
for `Stage8 OK`, 1 otherwise. Pass `run.ps1` options with `-RunArgs "-MinFreeGB 16"`. If ssh drops, the task goes
on and the run cleans up after itself.

Or, in a PowerShell window on that desktop:

```powershell
.\tools\Stage8\run.ps1 -Root G:\TRStage8
```

`run.ps1` builds the mod and the driver from this checkout against `G:\TRStage8\client` (or takes
`-ModPackage` and `-DriverPackage`, folders holding `About\` and the DLL), and refuses to start while: another
Stage 8 run holds `G:\TRStage8\stage8.lock`; a Stage 8 instance is still running; bt's own game
(`rocketstation.exe` from outside `G:\TRStage8`) is running, since it shares the PlayerCookie; the server's and
the client's builds, LaunchPads or BepInEx differ; less than `-MinFreeGB` (20) of memory is free. Other dedicated
servers on bt may run beside it. It picks the first free port pair from 27716 that no other server's
`setting.xml` names and nothing on bt listens on (`-GamePort` to choose one).

Each run has its own folder, `G:\TRStage8\runs\<date-time>` (`-Out`): `verdict.txt`, `run-transcript.txt`,
each launch's Unity log (`server-make-unity.log`, ...) and BepInEx log (`server-make-LogOutput.log`, ...), the
handshake files, `kit\` (what was installed), and `shots\` (the client's own screen captures).

## Tear down

A run stops everything it started and removes its lock. After an interrupted one (the machine rebooted, the task
was ended):

```powershell
.\tools\Stage8\stop.ps1 -Root G:\TRStage8
```

It stops the server and the client only by the PIDs in `G:\TRStage8\server.pid` and `client.pid` and only when
that PID's executable is under `G:\TRStage8`; stops the run's own PowerShell by the PID in the lock if its command
line is `task-go.ps1` or `tools\Stage8\run.ps1`; puts the PlayerCookie back from the newest run that set one aside
and did not put it back (`-Out` to name the run); removes the lock and the `TR-Stage8` task. To remove the whole
rig, delete `G:\TRStage8` once nothing of it runs.

## Which process runs where

| Process | Started by | Session | Window |
|---|---|---|---|
| `task.ps1` | the operator, over ssh | 0 | none |
| `task-go.ps1` and `run.ps1` | the scheduled task `TR-Stage8` | lu's desktop session | a PowerShell window |
| `G:\TRStage8\server\rocketstation_DedicatedServer.exe -batchmode -nographics` | `run.ps1` | lu's desktop session | hidden |
| `G:\TRStage8\client\rocketstation.exe -screen-fullscreen 0` | `run.ps1` | lu's desktop session | 1600 x 900 |

## The run

| Launch | What happens |
|---|---|
| server, phase `make` | `-file start trstage8 Mars2 Creative`: a new Creative Mars2 world. Up, unpaused, planet ticking; `MaxDamagingRocks` set to 0 so a suit's damage is the wave's alone |
| client, phase `first` | joins `127.0.0.1:<port>` 1.25 s after the game's data has loaded, as LaunchPad's own auto-join does. The server dresses the player in a suit and helmet, spawns a Rocket Control and a Map motherboard by them, a launch pad and a rocket at Entry with a payload bay, and makes two comets at a site the game's discovery made and sends them: A (300,000 mol of water, the most reserve its engines can burn in the fall) and D (100,000 mol of carbon dioxide). While A flies the client opens the rocket map; once A is parked, both ends work its sky direction and the bay's aim out at the same clock; the bay's target is held until a pass brings it in reach and A is brought down by Deorbit. The client is warned, stands where the wave peaks at 300 kPa, outside the fireball, facing the impact, and takes pictures. Then the client calls console comet B, 1,000,000 mol of carbon dioxide, `range=2500 turn=0`, with `serverrun`, standing at a corner of the world facing outward |
| client, phase `late` | the client is stopped; with nobody online the server calls console comet C (10,000,000 mol, `preview`, a long show); the client starts again and joins while C plays |
| server, phase `make` (on) | after C's show, D is brought down untracked and the world saved 8 s into its fall; both ends are stopped |
| server, phase `load` | the save, held paused; D's fall back on the clock |
| client, phase `rejoin` | joins; the join unpauses the world; D lands |

## The judged lines

`verdict.txt` holds every judged line. `Stage8 OK` only when every expected one is there and none failed.

From the server driver:

| Line | PASS means |
|---|---|
| `server up` | the world runs unpaused, at least 5 game ticks in 10 s, every comet part of the mod installed, its rules' checks passed, a space map with Entry |
| `server no falling rocks` | `terraform set MaxDamagingRocks 0` took |
| `server sees the client` | the joined player's body is online on the server within 1.5 m of where their own game says it is (the owner stream reaches the host) |
| `server set up` | the two comets made and sent, the rocket, its bay and the two motherboards spawned, the suit and helmet worn. Before it, the server adds 1,000 mol of carbon dioxide to its planet (a quiet world's planet does not change from one sync to the next) and charts the comet's route the game's way, by its parent nodes' chart points, since the rocket map draws only charted nodes and the sites under them |
| `server sky body` | comet A in the server's sky, the game's body within 0.01 degrees of the sky rule |
| `server burn A` | A lands within 1 m of the bay's target, its event at the landing's point and clock, and the player's spot (the wave over 200 kPa, outside the fireball, inside the world) worked out |
| `server suit damage A` | the suit and helmet took damage across the wave's arrival, the wave over 200 kPa where the player stood |
| `server saw the push A`, `server saw the push B` | the player's body, as their own game sends it, moved across each wave's arrival |
| `server zone pass A` | the impact's one pass destroyed and broke structures (the line of structures built by A's impact) |
| `server console via serverrun` | a comet event followed the player's `serverrun` within 20 s (else the server calls it itself so the run goes on, and this line fails) |
| `server console site` | B lands 2,500 m from the player, within 40 m of straight ahead of them as the server has their facing |
| `server console no suit damage` | B's wave where the player stood was under 200 kPa, and the suit took nothing |
| `server fall saved` | D was still falling, 8 s in, when the save was asked for |
| `server fall back after load` | after the load D falls on to land at the clock it was saved to land at (to 1e-6 s) and the same point, its event made again |
| `server landed at the saved clock` | D's gas was delivered at that clock, within the game's frame |

From the client driver:

| Line | PASS means |
|---|---|
| `client joined`, `client joined late`, `client joined again` | after the join: `ClientFinishedJoining` fired or the game runs, `OwnerHuman` set, `GameState.Running`, the mod has the host's planet |
| `client planet sync` | after the join (the state read at the join comes before this game has the host's clock), at least 3 planet states read over 2 sync windows, each bit for bit one the host wrote within 2 s of this game's clock when it read it; the 1,000 mol of carbon dioxide the server adds to its planet once the player is in arrive here as a new state; the layout byte is 7 |
| `client host settings` | the Celestial terraforming, temperature and sky settings in force here are the host's |
| `client shared clock` | this game's clock and the server's, read at the same wall time (one machine), within 0.5 s |
| `client comet list` | at each clock the server wrote its comets at, this game's comets at that clock are the same (node ids, hop, progress, fuel to 1e-9), at least 3 times, once with a comet between two nodes and once with one parked |
| `client rocket map icon` | the rocket map opened on the Rocket Control motherboard, once the nodes the server charted are charted here: every comet's icon where the game's own rule (a lerp between the hop's two nodes, 14 px below) puts this game's comet, a comet between two nodes among them |
| `client sky body` | comet A's direction at the server's clock, worked out here from this game's own body, within 1e-6 of the server's; the game's body within 0.01 degrees of the rule now |
| `client map motherboard icons` | the bay's aim at the server's clock worked out here: sky point and landing within 1e-6 m of the server's; this game's live aim the rule's from its own direction; and the Map motherboard's panel draws both comet icons (target and landing) at those positions |
| `client fall A` | this game's copy of A's fall: the same landing point, clock and moles as the server's |
| `server mods and paths make`, `... load`, `client mods and paths first`, `... late`, `... rejoin` | that launch loaded exactly `TerraformingReloaded.dll` and `TRStage8.dll`, one each, from its own data folder's `mods`, nothing from the Workshop, and the game's data path is its own data folder. A launch that fails this stops at once, before it joins or saves anything |
| `client stood A`, `client stood B` | the player was moved to the spot for A (the wave's 300 kPa) and for B (a corner of the world) and turned the way asked, and within 30 s both this game (within 2 m, the body and the camera within 3 degrees) and the server (its copy of the body within 3 m and 5 degrees, from what this game sends) have it there. The move is the one `Human.ForceSetPosition` makes on a host, on the player's own game (the place, the transform, the rigidbody, no momentum, the interpolation reset); the turn is the camera controller's `RotationY`, as the game's scripted spawns set it. A new player arrives seated in a landing capsule: the driver first leaves it the game's way (`Human.ExitAnything`, what the movement keys call), and the server, if the capsule is still locked a minute after it first sees the player in it, opens and unlocks it as the end of a landing does. A line that fails says the player's state, the rigidbody's and the camera's. B is called only after its line |
| `client impact event A` | this game's event for A: the server's sequence, point, clock and moles, its show playing and drawn 10 s before the impact |
| `client pushed A`, `client pushed B` | this game threw its own player across the wave's arrival (A over 200 kPa, B between 50 and 200) |
| `client suit damage A` | this game's copy of the suit and helmet damage is the server's, and more than before |
| `client structures` | every structure by A's impact (gone, broken or standing, its Burn and Brute) as the server has it, some gone and some broken |
| `client sky dim` | the darker sky by the rule at the server's clock the same here; this frame's `CometSky.Dim` the rule's at this frame's clock, and dimmer |
| `client console event` | B's event here the server's, its show playing and drawn |
| `client console straight ahead` | B lands within 40 m of 2,500 m straight ahead of the way this player faces |
| `client late join` | joined during C: C's event here the server's, its show playing and drawn, picked up after its start |
| `client fall after rejoin` | D's fall here lands at the saved clock and point, still to come |
| `client impact after rejoin` | D's event here lands at the saved clock, its show playing and drawn |

From `run.ps1`, out of the logs:

| Line | PASS means |
|---|---|
| `server mod live`, `client mod live` | the mod reported `Active:`; its planet tick `live` on the server, `off: client, planet comes from the host` on the client |
| `server warning A` | the server's own warning names no player of its own and lists the joined player with their distance |
| `client warning A` | the client's own warning names its player and reads that player's own distance, as the driver worked it out where the player stood when the fall began |
| `client show drawn A` | the mod's `show of event N` line on the client for A's event |
| `client thrown A`, `client thrown B` | the mod's `the wave threw this player at X kPa` line on the client, X within 10 % of the wave the driver expected where it stood |
| `server suit line A`, `server suit line once` | the host's `the wave damaged the suit of <player>` line, exactly once: for A, not for B |
| `server console site line` | B's site was looked for `from ... (player <name>, who ran the command)` |
| `server late site line` | C's site, with nobody online, from `(the world origin)`, not the server's own scene camera |
| `client late show line` | C reached the client `X s after it began` and its show `joined at t=X`, X above 0 |
| `server landed line` | the mod's `comet D landed at clock X` after the load, X the saved clock within the game's frame |
| `no errors` | no stack frame of the mod or the driver, no error either logged, no index out of range and no list changed under a reader, in any log |

## The pictures

`shots\` in the run's folder, each the game's own screen capture of the client's whole window: `map-rocket`
(the rocket map with A in flight), `rocket-t-008.0` to `rocket-t+045.0` (seconds from A's impact, standing in the
wave's 300 kPa), `console-t...` (B from 2,500 m), `late-joined` and `late-playing` (C picked up late), and
`rejoin-t...` (D landing after the load). Each shot's line in the client's log gives its clock, the sky's dimming
and where the player stood.

## A client launched more than once

Before every launch `run.ps1` writes the data folder's `modconfig.xml` again with only the core and the two
mods, and sets LaunchPad's `SavePathOverride`, `DisableSteam`, `RetainWorkshopMods = false` and server packs off.
The game's own mod menu, at the main menu, adds every Workshop item Steam lists to `modconfig.xml`, enabled
(`WorkshopMenu.GetModFolders`, `ModData.CreateFrom`), and the client's game has Steam; with Steam off,
LaunchPad lists the Workshop mods `modconfig.xml` already holds (`RetainWorkshopMods`), so a second launch used to
load bt's Workshop mods, the published Terraforming Reloaded among them, or stop on LaunchPad's question about mods
outside its pack. Before every client launch it also waits until nothing runs from `G:\TRStage8\client` (stopping,
by PID, what an earlier launch left), removes the mod pack LaunchPad made on the first launch
(`client-data\profiles`, `clientside-mods.xml`) and clears `[Internal] ModProfile`, so every launch starts as the
first did, and sets `[Startup] DevMode` (no countdowns, animations or news). A launch whose driver has not armed
(`client.armed`) within `-ArmMinutes` (6) has the desktop saved (`client-<phase>-stuck-<n>.png`) and its logs
kept, and is tried once more.

## Not known before the first run

- Whether a copied client outside Steam starts without asking for Steam, and which name and client id it gets
  (`Recruit` and a random id without Steam, from the code). The driver takes whatever the client reports.
- Whether a loose Rocket Control motherboard opens the rocket screen (from the code it needs no computer; the
  research left it open), and whether the Map motherboard's panel updates loose (the driver calls its update
  itself).
- Whether the dedicated server's world comes up paused and pauses itself again; the driver unpauses it the
  game's own way after 10 s with nobody joining, and holds it paused in phase `load` until the client joins.
- Whether the body stays on the ground right after a teleport while the terrain around it builds; the driver puts
  it back for 8 s if it falls more than 3 m.
- How long each launch takes on bt; the timeouts are `-JoinMinutes` (20) and `-PhaseMinutes` (120).
