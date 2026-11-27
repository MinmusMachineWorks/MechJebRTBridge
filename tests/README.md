# In-game test harness

`MechJebRTBridge.Tests` is a separate, dev-only KSP plugin that runs scripted checks against the
bridge inside a running game. It is never part of the release zip. It covers the command
boundary: whether each command is delayed, how long it takes, that it's sent only once, and that
on-board calls run instantly. It doesn't fly anything.

## Build and install

```
dotnet build tests/MechJebRTBridge.Tests/MechJebRTBridge.Tests.csproj -c Release
```

It uses the same `Directory.Build.props.user` as the mod. The DLL goes to
`tests/MechJebRTBridge.Tests/GameData/MechJebRTBridge.Tests/Plugins/` (gitignored). When
`KSPBT_GameRoot` is set, it's also copied to `<KSP>/GameData/MechJebRTBridge.Tests/Plugins/`.
Delete that folder from the install to remove the harness.

**RemoteTech or LightLag.** The harness drives whichever delay mod is installed
(`DelayMod.cs`, by reflection, so it builds without either): its queue, its manual extra delay
and its cancel. Radio blackout is RemoteTech's, so with LightLag the no-connection test is
skipped.

The harness doesn't reference the bridge. It only uses what the player and the delay mod can see:
- it calls MechJeb's methods the way a button click or an autopilot would
- it reads the delay mod's queue (RemoteTech's flight computer, LightLag's `LightLagVessel`) and MechJeb's module state
- it matches the bridge's `[MJ-RT Bridge]` log lines

Rebuild the mod too when you change it. The harness tests whichever bridge DLL is installed.

## Test vessel

Make a save with a vessel that meets all of these:
- **an uncrewed probe** with MechJeb, an antenna and power. A crewed vessel has local control,
  and the bridge never delays it.
- **in a stable orbit near Kerbin**, with a working link to KSC (RemoteTech, or CommNet with LightLag) **for the whole
  run** (about 2–3 minutes). The real signal delay must be under 3 s, which any Kerbin orbit
  gives. Either:
  - **LKO** (80–100 km) with an activated omni antenna (Communotron 16). Start the run just
    after the probe comes over KSC's horizon. The link drops when Kerbin blocks the line of
    sight, and a test fails if that happens mid-run.
  - **Keostationary** (2,863 km, above KSC) with a dish targeted at Mission Control (e.g. Comms
    DTS-M1). The link is constant. Alt+F12 → Cheats → Set Orbit gets you there in sandbox.
- **maneuver nodes unlocked** (Tracking Station / Mission Control upgraded, or sandbox).
  Otherwise the Node Executor tests are skipped.
- **not in time warp**, with **MechJeb's Rendezvous Autopilot and Docking Autopilot windows
  closed**. An open window's OnGUI would fight the tests over the autopilot's state, so the
  tests that need one are skipped while it's open.

No target is needed. Your maneuver nodes on that vessel get deleted: engaging the Rendezvous AP
removes all of them.

## Running

In flight, click the green square on the toolbar, then **Run all**, or **▶** to run one test.
Results show in the window and go to `KSP.log`:

```
[MJ-RT Test] RUN 22 test(s)
[MJ-RT Test] PASS SmartASS: click is delayed
[MJ-RT Test] SKIP Docking AP: closing the window is instant — close MechJeb's Docking Autopilot window first
[MJ-RT Test] SUMMARY 21 passed, 0 failed, 1 skipped
```

`grep "MJ-RT" KSP.log` shows the test results alongside the bridge's own lines.

During a run the harness:
- **adds a 3 s manual delay**: RemoteTech's flight computer "Delay (+ signal)" field, or
  LightLag's extra delay for the vessel. LKO's real delay is milliseconds, too short to tell a delayed command
  from an instant one. The original value is restored at the end, or if you leave the scene.
- **stubs out the per-frame work** (`Drive`, `OnFixedUpdate`, `OnUpdate`) of the Node Executor,
  the Landing, Rendezvous, Docking and Ascent autopilots, and the staging and thrust controllers
  (so autostage can't fire a stage). An engaged autopilot then stays engaged
  without a target, and never burns, warps or disengages itself. Engage/disengage code runs for
  real.
- **turns on RemoteTech radio blackout** for the no-connection test, and lifts it afterwards
  (skipped with LightLag, which has no blackout switch).
- **stops everything after each test** with on-board (physics-step) calls: SmartASS OFF, Abort,
  Stop Landing, Rendezvous and Docking off. It first waits for queued bridge commands to arrive.

## What the tests check

| Test | Checks |
|---|---|
| Every patch target is patched | Each method `BridgeAddon` patches is patched by `com.mechjebrtbridge`. Version-specific stop paths that don't exist in the loaded MechJeb are listed, not failed. |
| SmartASS: click is delayed | One SCHED, one entry in the delay queue. The button shows the old mode until arrival. It arrives after ≈3 s and engages. |
| SmartASS: on-board call is instant | `Engage` from the physics step applies at once, logs `internal call`, and queues nothing. |
| SmartASS: highlight tracks with the window closed | With the SmartASS window closed, an on-board mode change is still tracked, so a click (e.g. a part action) reverts the highlight to the mode SmartASS holds (`docs/MECHJEB_COVERAGE.md` §5.5). The window is reopened afterwards if it was open. |
| No connection: click is refused | Under blackout, a SmartASS click is refused and nothing is queued. 30 frames of Rendezvous engage give one refusal message (2.15.x throttle). |
| Node Executor: click / on-board call | Execute one node is delayed. Execute all nodes from the physics step starts at once. |
| Landing: click is delayed | Land somewhere is delayed ≈3 s, then the autopilot is running. |
| Rendezvous AP: engage / disengage | 30 frames of `Users.Add(window)` (the 2.15.x OnGUI pattern) send exactly one command. `Remove` (2.15.x) and `Clear` (dev) disengage after the delay. |
| Rendezvous AP: on-board stop / non-window user | A physics-step `Clear` and an `Add` from another module act at once. |
| Docking AP: engage + disengage | Same as Rendezvous, for the Docking AP. |
| Docking AP: EndDocking / closing the window | The Bypass-wrapped stop paths act at once, from outside the physics step. |
| Ascent AP: engage / disengage | Engage via `Users.Add(menu)`, disengage via `Remove` (2.15.x) and `Clear` (dev): each is delayed ≈3 s. The Ascent window may be open. |
| Ascent AP: on-board stop / toggle action | A physics-step `Clear` acts at once. The "Ascent AP toggle" part action, clicked outside the physics step, is delayed both ways. |
| cancel: Rendezvous / SmartASS | After a queued command is cancelled (RemoteTech's "x", a `CancelCommand`; LightLag's Abort), the Rendezvous engage button works again, and SmartASS goes back to tracking the mode it holds (`docs/MECHJEB_COVERAGE.md` §5.4). |

## Not covered (still manual)

- **Save/load with a command in the queue.** A scene reload destroys the runner.
- **Real MechJeb button clicks.** The tests call the same methods the windows call, from
  `Update`. The windows themselves aren't driven.
- **Real flight.** Autopilots are stubbed, so nothing checks that a delayed program then flies
  correctly.
- **Action-group re-entry (§5.2), Translatron PANIC, kOS.**
- **A crewed vessel / local control** (no delay at all).

## Adding a test

Add a method to `Tests.cs` and a line to `Tests.All`. Test bodies are coroutines:
- Code in the body runs in `Update`, so the bridge treats it as a **player click**.
- `yield return Physics(() => …)` runs the lambda in `FixedUpdate`, so the bridge treats it as
  **on-board**.
- `Check(...)` fails the test, and `Skip(...)` skips it.
- `c.Mark()` and `c.CheckLog(text, mark, n, what)` count bridge log lines.
- `WaitUntil(cond, what)` waits, and fails after a timeout.

The log strings are the bridge's own `Debug.Log` formats. If you change a format in `src/`,
update the strings in `Tests.cs`. If you add a seam, add a click test and an on-board test for
it, and add its patch targets to `PatchTargets`.
