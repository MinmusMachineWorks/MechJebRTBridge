# MechJeb ⇄ RemoteTech coverage matrix

This is the master list of **every MechJeb feature that does something to the vessel that a
player would otherwise do by hand**. It records which of those hand actions RemoteTech delays, and
whether the bridge delays the MechJeb command that automates them. It is the source of truth for
coverage. `COMMAND_DELAY_PLAN.md` holds the design and patterns. This file holds the inventory.
Keep it current (see `AGENTS.md`, Rule #1).

Sources:
- MechJeb2 `dev` @ `ef540816` (`dev-2.16.0.0-2118`, the new `MechJeb2.UI` KodeWindow UI).
- MechJeb2 `2.15.4.0` tag (what the Testing instance runs, old IMGUI windows).
- RemoteTech `develop` @ `003ce96`. The game runs release 1.9.12, which has the same
  command/delay surface.

Line numbers are against `dev` unless marked *2.15.4*.

The matrix is written against RemoteTech. With LightLag as the delay mod (`src/Backends/`), the
seams are the same; LightLag delays the same kinds of hand action (steering and throttle,
staging, action groups, SAS, part menus; see LightLag's `docs/COVERAGE.md`).

---

## 1. The rule this table applies

RemoteTech delays a player's hand action on a vessel without local control. MechJeb lets the
player skip that action: one click makes MechJeb do it **directly, via the vessel API, bypassing
RemoteTech's input hooks**, so it lands instantly. The bridge's job is to make the **click** (the
command to MechJeb) cross the link with the same delay. The automation that follows runs on board,
in real time.

So for every row, two questions:
1. **Would RT delay this if done by hand?** If not, the MechJeb equivalent needs no delay either
   (category C).
2. **Which MechJeb entry point does the player trigger?** That entry point is the seam to delay,
   *not* the low-level vessel call MechJeb makes later (that's on-board automation).

Status legend:
- ✅ delayed by the bridge
- ⬜ not yet delayed (gap)
- ⚠️ needs design before it can be delayed
- 🐞 delayed when it shouldn't be (bug)
- — not applicable (RT doesn't delay it by hand, so MechJeb shouldn't either)

---

## 2. What RemoteTech delays when done by hand

| Hand action | RT mechanism (src/RemoteTech/…) |
|---|---|
| Pitch / yaw / roll / translate / throttle / wheel input | `FlightComputer.OnFlyByWirePre` → `Enqueue(FlightCtrlState)`. Every frame's input is queued with `TimeStamp += Delay` and replayed later (`FlightComputer.cs:456`, `PopFlightCtrl`) |
| Staging (space bar) | `STAGING` control lock + key → `ActionGroupCommand(Stage)` → `StageManager.ActivateNextStage()` on pop (`RTCore.cs:291`, `ActionGroupCommand.cs:18`) |
| SAS / RCS toggle, SAS hold | `SAS`/`RCS` locks + key → `ActionGroupCommand(SAS/RCS)` (`RTCore.cs:292-293, 306-317`) |
| Gear, brakes, lights, abort, custom AG 1–10 | `GROUPS_ALL` lock + key → `ActionGroupCommand`. The on-screen gear/brakes/light/abort buttons are rewired too (`UI/FlightUIPatcher.cs`) |
| Axis groups (KSP 1.4+) | `AxisGroupCommand` (`RTCore.cs:182`) |
| Stock SAS mode buttons (prograde, …) | Navball autopilot buttons rewired → `StockAutopilotCommand` (`FlightComputer.cs:242`) |
| Part right-click events / fields (extend panel, deploy chute, toggle engine, set limiter, …) | PAW wrapped → `EventCommand` / `PartActionCommand` (`UIPartActionMenuPatcher.cs`, `ModuleSPU.cs:353, 405`) |
| RT flight-computer programs (attitude, burn, maneuver exec, rover drive, PID, hibernate) | Queued directly by the RT UI fragments |
| **Not delayed by RT:** time warp, maneuver node create/edit, target selection*, map/camera/UI | — |

\* RT queues a `TargetCommand` when the target changes, but it only feeds RT's *own* flight
computer (`f.DelayedTarget`). It doesn't stop KSP's target from changing instantly.

A delayed action group ends up calling `Vessel.ActionGroups.ToggleGroup`, which fires **every**
`KSPAction` bound to that group, including MechJeb's own actions (§5.2).

**Raw flight input (WASD/QE/IJKL/HN, Shift/Ctrl/Z/X throttle, wheel keys) is delayed but never
shown in the RT queue window.** Each physics frame, `OnFlyByWirePre` copies the whole
`FlightCtrlState` into a private `_flightCtrlQueue` and replays the frame from `Delay` seconds ago.
The queue window only lists `ICommand`s. So a key held for 2 s acts 2 s long, one delay later. The
navball throttle gauge moves at once (it shows the local input), but the engines follow the delayed
value. While a MechJeb attitude program is active, MechJeb yields an axis to "user input" only when
the *delayed* input on that axis is non-zero (`AttitudeController.Drive`, *2.15.4* `:394-424`), so
stick input still takes over, but late.

---

## 3. Matrix: hand action ↔ MechJeb automation ↔ command seam

### 3.1 Attitude / steering

| MechJeb feature | Replaces by hand | Player command (seam) | Status |
|---|---|---|---|
| SmartASS (all modes, incl. EXECUTE / advanced / surface angles) | Steering / stock SAS modes | `MechJebModuleSmartASS.Engage(bool)` (`:190`) | ✅ |
| SmartASS via part action / action group (`Orbit Prograde`, … `Deactivate SmartACS`) | Steering | `MechJebCore.On*Action` → `EngageSmartASSOrbitalControl` → `Engage` (`MechJebCore.cs:110-132`) | ✅ delayed once, by RT's action group (§5.2) |
| Attitude hold during signal loss | (RT neutralises controls) | Bridge sanctioned pilot (`SmartAssBridge.CaptureAttitudePostfix`) | ✅ attitude only |
| Attitude Adjustment (PID / tuning) | — (settings) | field edits | — |
| Spinup controller | Roll input | Started only by the PSG autopilot (`AssertStart` `:81`), never directly by the player | — (internal) |

### 3.2 Throttle / engines

| MechJeb feature | Replaces by hand | Player command (seam) | Status |
|---|---|---|---|
| Translatron mode (OFF / KEEP ORB / KEEP SURF / KEEP VERT / …) | Throttle | `MechJebModuleTranslatron.SetMode(TMode)` (`:38`); part actions `MechJebCore.cs:203-215` | ⬜ |
| Translatron speed setpoint (+1/−1/0, field) | Throttle | `SetTranslatronSpeed` part actions; `trans_spd` field edit (`Translatron.cs:33`) | ⚠️ setpoint (field write) |
| Translatron PANIC | Throttle cut, staging, decouple | `PanicSwitch()` (`:57`); `#MechJeb_PANIC` part action | ⬜ (should be **B**, one-shot) |
| Thrust limiters (Q, overheat, flameout, unstable ignition, accel, throttle, min, electric, differential) | Throttle / PAW thrust limiter | bool fields + `EditableDouble` setpoints on `MechJebModuleThrustController` (`:38-139`) | ⚠️ setpoint (field write) |
| Differential throttle | PAW thrust limiter per engine | `DifferentialThrottle` toggle (`:105`) | ⚠️ |

### 3.3 Staging

| MechJeb feature | Replaces by hand | Player command (seam) | Status |
|---|---|---|---|
| Autostage (continuous) | Staging | `Core.Staging.Users.Add(window)` from Utilities / Thrust window toggle (`UI/UtilitiesView.cs:64`, `InfoItems/ThrustItemBuilders.cs:84`) | ⬜ |
| Autostage once | Staging | `MechJebModuleStagingController.AutostageOnce(object)` (`:232`) / `CancelAutostageOnce()` (`:246`) | ⬜ |
| Autostage limits / rules | — (settings) | field edits | — (arguably ⚠️: changes on-board behaviour) |
| Launch at T-0 (timed launch stages the vessel) | Staging | `LaunchCountdown.StartToPlaneOfTarget/StartToTargetLan/StartToLan` (`:41/:60/:69`), abort `AbortTimedLaunch` (`AscentMenu.cs:164`) | ⬜ |

### 3.4 Maneuver execution

| MechJeb feature | Replaces by hand | Player command (seam) | Status |
|---|---|---|---|
| Execute next node / all nodes (Maneuver Planner, Node Editor, Rendezvous Planner, toolbar menu) | Steering + throttle for the burn | `MechJebModuleNodeExecutor.ExecuteOneNode(obj)` (`:133`), `ExecuteAllNodes(obj)` (`:141`) | ✅ (player UI only; internal callers run locally, §5.1) |
| Abort node execution | Throttle cut | `NodeExecutor.Abort()` (`:174`) | ⚠️ unpatched by design (MechJeb calls it internally) |
| Create / edit / delete maneuver nodes (Planner, Node Editor, aerobrake nodes) | Node editing | `PlaceManeuverNode`, `RemoveSelf` | — (RT doesn't delay node editing) |
| Node executor auto-warp | Time warp | `NodeUi.AutowarpToggle` | — |

### 3.5 Ascent

| MechJeb feature | Replaces by hand | Player command (seam) | Status |
|---|---|---|---|
| Ascent AP engage (classic + PSG) | Staging, steering, throttle | `MechJebModuleAscentMenu.Engage()` (`:88`) → `_autopilot.Users.Add(this)` (`:104`); part action `OnAscentAPToggleAction` (`MechJebCore.cs:245`); *2.15.4* `Users.Add(this)` (`AscentMenu.cs:112`), toggle `MechJebCore.cs:240` | ✅ generic `UserPool` seam (§6). On dev, `Engage()` starts a timed launch's countdown locally at the click; only the engage is delayed |
| Ascent AP disengage | — | `menu._autopilot.Users.Clear()` (`UI/AscentView.cs:145`), toggle `Users.Clear()`; *2.15.4* `Users.Remove(this)` (`AscentMenu.cs:110`), toggle `Users.Remove` | ✅ generic `UserPool` seam (§6) |
| Ascent AP stopping itself (circularised, PSG finished) | — | Internal `Users.Clear()` from `Drive` / `OnFixedUpdate` (`AscentClassicAutopilot.cs:213`, `PSGAutopilot.cs:358`; *2.15.4* `AscentBaseAutopilot.cs:240/255`) | — runs locally (physics step) |
| Abort timed launch (dev) | — | `AscentMenu.AbortTimedLaunch()` (`:164`) → `Users.Clear()` + countdown abort | ⚠️ instant by design: Bypass-wrapped, like the other aborts (plan doc §8) |
| Ascent settings (target alt, inclination, pitch program, PSG stages, autostage, auto-deploy, …) | — | field edits | ⚠️ setpoint. Only matters while engaged |
| Ascent circularisation burn | — | Internal: `Core.Node.ExecuteOneNode(this)` (`AscentClassicAutopilot.cs:362`; *2.15.4* `AscentBaseAutopilot.cs:281`) | — runs locally (fixed, §5.1) |

### 3.6 Landing / hover

| MechJeb feature | Replaces by hand | Player command (seam) | Status |
|---|---|---|---|
| Land at target / land somewhere | Steering, throttle, staging, chutes, gear | `MechJebModuleLandingAutopilot.LandAtPositionTarget(obj)` (`:181`), `LandUntargeted(obj)` (`:214`); part actions `Land somewhere` / `Land at KSC` (`MechJebCore.cs:134-138`) | ✅ UI; part actions delayed once, by RT's action group (§5.2) |
| Abort autoland | Throttle cut | `StopLanding()` (`:233`) | ⚠️ unpatched by design (called at touchdown) |
| Landing target pick / coordinates / site | — | `Core.Target.*` | — |
| Landing autopilot deploys chutes / stages / extends gear | PAW deploy, staging, gear AG | Internal (`LandingAutopilot.cs:477, 496`) | — (on-board automation) |
| Hoverslam / suicide burn | Steering, throttle | `MechJebModuleHoverslamAutopilot.ToggleEnabled()` (`:17`, via info-item action `HoverslamAutopilot.ToggleEnabled`) | ⬜ |
| Hoverslam engaging SmartASS | — | Internal `Core.SmartASS.Engage()` (`Hoverslam:233`) | — runs locally (fixed, §5.1) |

### 3.7 Rendezvous / docking

| MechJeb feature | Replaces by hand | Player command (seam) | Status |
|---|---|---|---|
| Rendezvous AP engage / disengage | Burns, steering | `Autopilot.Users.Add(window)` / `Users.Clear()` (`UI/RendezvousAutopilotView.cs:47/55`); *2.15.4* `Users.Add/Remove(this)` (`RendezvousAutopilotWindow.cs:39/43`) | ✅ generic `UserPool` seam (§6) |
| Rendezvous AP running node burns | — | Internal `Core.Node.ExecuteAllNodes(this)` (`RendezvousAutopilot.cs:237`) | — runs locally (fixed, §5.1) |
| Rendezvous Planner node execution | Steering + throttle | via `NodeUi.ExecuteButtons` → `ExecuteOneNode/AllNodes(guidance)` | ✅ |
| Docking AP engage / disengage | RCS translate, steering, SAS/RCS AG | `Ap.Users.Add(guidance)` / `Users.Clear()` (`UI/DockingGuidanceView.cs:173/175`); *2.15.4* toggle → `Users.Add/Remove(this)` (`DockingGuidance.cs:112/116`) | ✅ generic `UserPool` seam (§6) |
| Docking speed limits, target port | — | field edits / target | ⚠️ setpoint / — |

### 3.8 RCS

| MechJeb feature | Replaces by hand | Player command (seam) | Status |
|---|---|---|---|
| SmartRCS (ZERO RVEL, …) | RCS translation | `MechJebModuleSmartRcs.Engage()` (`:75`) | ⬜ |
| RCS balancer enable | RCS translation / PAW thrust limits | `MechJebModuleRCSBalancer` `Enabled` (menu checkbox `UI/MenuView.cs:221`) | ⬜ |
| MechJeb toggling the RCS/SAS action groups while flying | RCS/SAS AG | Internal `ActionGroups.SetGroup` (AttitudeController, NodeExecutor, Docking, …) | — (on-board automation) |

### 3.9 Rover

| MechJeb feature | Replaces by hand | Player command (seam) | Status |
|---|---|---|---|
| Rover drive / stop / to target | Wheel steer + throttle, brakes AG | `MechJebModuleRoverWindow.Drive(bool)` (`:56`) / `Stop()` (`:64`); also releases brakes directly (`:42`) | ⬜ |
| Heading / speed hold toggles + setpoints | Wheel input | `RoverController.ControlHeading/ControlSpeed` + fields | ⚠️ setpoint |
| Waypoints (add / reverse / loop) | — | `MechJebModuleWaypointWindow` | ⚠️ setpoint while driving |
| Rover controller retracting panels / braking | PAW, brakes AG | Internal (`RoverController.cs:476, 518`) | — (on-board automation) |

### 3.10 Deployables

| MechJeb feature | Replaces by hand | Player command (seam) | Status |
|---|---|---|---|
| Auto-deploy solar panels toggle | PAW extend/retract | `MechJebModuleDeployableController.AutoDeploy` field / `ToggleDeploy()` (`:217`) | ⬜ |
| Auto-deploy antennas toggle | PAW extend/retract | `MechJebModuleDeployableAntennaController` same API | ⬜ (careful: an antenna retract can cut the link it arrived on) |
| Extend / retract all now | PAW extend/retract | `ExtendAll()` (`:51`) / `RetractAll()` (`:91`) | ⬜ (one-shot **B**) |

### 3.11 Other

| MechJeb feature | Replaces by hand | Player command (seam) | Status |
|---|---|---|---|
| Warp helper / MechJeb warp / pause / resume | Time warp | `WarpHelper.StartWarp/AbortWarp`, `WarpController.*` | — |
| Target controller (pick / clear) | Target selection | `Core.Target.*`, `SetVesselTarget` (`TargetController.cs:53`) | — |
| Info windows, predictions, stage stats, flight recorder, settings | — | — | — |
| Module enable checkboxes (menu) | depends | `m.Enabled = v` (`UI/MenuView.cs:221`) | ⚠️ the window toggles are UI, but the same checkbox list enables RCSBalancer etc. Needs filtering by module type |

---

## 4. Summary

| Bucket | Count | Items |
|---|---|---|
| ✅ Delayed | 4 seams | SmartASS `Engage`; NodeExecutor `ExecuteOneNode/AllNodes`; Landing `LandAtPositionTarget/LandUntargeted`; generic `UserPool.Add/Remove/Clear` (Rendezvous AP, Docking AP, Ascent AP, engage **and** disengage) |
| 🐞 Wrongly delayed | 0 | (§5.1–5.3 fixed; the §5.4 cancel bug is fixed too) |
| ⬜ Clean gaps | ~11 | Timed launch, Hoverslam, Translatron mode + PANIC, SmartRCS, RCS balancer, Autostage + Autostage-once, Rover drive/stop, auto-deploy toggles, extend/retract all |
| ⚠️ Needs design | — | Cancellations (`Abort`, `StopLanding`; `Users.Clear` is done for allowlisted modules); setpoint field writes (thrust limiters, Translatron speed, rover, ascent/docking settings); menu `Enabled` checkbox |
| — Not delayed by design | — | Node planning/editing, warp, targeting, info/predictions/settings, on-board automation MechJeb runs after a command |

---

## 5. Bugs found in the current seams

### 5.1 Internal callers got delayed (✅ fixed)

The patched methods are also called by **MechJeb programs already running on board**. Before the
fix, without local control, the bridge prefix queued those calls with a full light-speed delay:

| Caller | Call | Effect before the fix |
|---|---|---|
| Rendezvous AP | `Core.Node.ExecuteAllNodes(this)` (`RendezvousAutopilot.cs:237`, *2.15.4* `:54`) | every rendezvous burn started one signal delay late |
| Ascent AP | `Core.Node.ExecuteOneNode(this)` (`AscentClassicAutopilot.cs:362`, *2.15.4* `AscentBaseAutopilot.cs:281`) | circularisation burn started one delay late, possibly after apoapsis |
| Hoverslam AP | `Core.SmartASS.Engage()` (`Hoverslam:233`) | hold-upright engaged late |
| SmartASS itself | `Engage(false)` in `OnFixedUpdate` (`SmartASS.cs:364`, dev only) when another autopilot hands attitude back | re-engage arrived late |
| Translatron PANIC | `Core.SmartASS.Engage()` / `StopLanding` via `StopOtherAutopilots` (`Translatron.cs:106`); `LandUntargeted(this)` from `Drive` (`:215`, *2.15.4* `:273`) | parts of the abort arrived late, out of step with the rest |
| SmartASS load | `Engage(false)` in `OnLoad` (`SmartASS.cs:130`, *2.15.4* `:205`) | a saved attitude hold on a vessel loaded without a connection was restored one delay late |

**Fix** (`BridgeCore.IsPlayerCommand`, `BridgeAddon`):
- Every internal call above except PANIC's click path and `OnLoad` runs inside the physics step
  (`Drive` / `OnFixedUpdate`). kOS scripts run there too. Player clicks never do: IMGUI `OnGUI` on
  2.15.x, uGUI events in `Update` on dev. So seams delay only when `!Time.inFixedTimeStep`.
  Skipped calls log `internal call (physics step), not delayed`.
- `Translatron.PanicSwitch` and `SmartASS.OnLoad` are wrapped with a Bypass prefix/finalizer, so
  calls they make are never delayed on their own.
- The `controller is DisplayModule` idea was rejected. Translatron is a `DisplayModule` in 2.15.4,
  and `SmartASS.Engage` has no controller argument.

### 5.2 Double delay through action groups (✅ fixed)

MechJeb's `KSPAction`s on `MechJebCore` (SmartASS modes, land somewhere / at KSC, PANIC,
Translatron modes, Ascent AP toggle) can be bound to action groups. RT already delays the action
group key (`ActionGroupCommand`). When it pops, `ToggleGroup` fires the MechJeb action →
`Engage()` / `LandUntargeted()`. The bridge used to queue that **again**, for a 2× delay.

**Fix:** `BridgeAddon` wraps `RemoteTech.FlightComputer.Commands.ActionGroupCommand.Pop` with the
Bypass prefix/finalizer. RT pops in `Update`, not the physics step, so the §5.1 check alone didn't
cover it. Anything MechJeb does inside that pop has already been delayed once and runs directly.

### 5.3 kOS scripts got delayed (✅ fixed by §5.1)

`MechJebKos` calls `Engage`, `ExecuteOneNode/AllNodes`, `Users.Add/Clear` directly. A kOS
script runs on the vessel's own CPU, so its MechJeb calls are on-board and shouldn't be delayed
(RT/kOS integration delays the *typed* commands already). kOS executes in `FixedUpdate`, so the
§5.1 physics-step check leaves its calls undelayed. Checked in game with kOS on 2026-11-20.

### 5.4 Cancelling in RT's queue left the bridge thinking the command was in flight (✅ fixed)

RT's queue window "x" enqueues a `CancelCommand`, which removes the queued `ExternalAPICommand`.
`ExternalAPICommand` doesn't override `CommandCanceled`, so the bridge gets no callback. RT also
drops queued commands silently in other cases (plan doc §10). In-flight state was cleared only in
`RtPop`, so after a cancel:
- **UserPool seam (Rendezvous / Docking):** `_inFlight` kept `(module, action)`, so every later
  click on that button was swallowed as "already on its way", with no message, until KSP
  restarted.
- **SmartASS:** `_inFlight` kept its count, so `TrackPostfix` stopped tracking the mode that
  SmartASS instance actually held.
- **Node Executor / Landing:** only a `_pending` entry leaked.

A cancel arrives first only when RT's manual extra delay is set: `CancelCommand` ignores extra
delay, so it can overtake a queued command.

**Fix** (`BridgeCore.InFlightSet<T>`, `BridgeCore.IsQueued`): each in-flight entry records its
vessel and token. `Has()` checks the entry against the flight computer's live queue, and forgets
an entry RT no longer holds, along with its `_pending` stash. It logs `no longer in the RT queue`.
If RT's internals can't be read, `IsQueued` errs towards "still queued", which is the old
behaviour. Tested by the harness's two "RT cancel" tests. Node Executor / Landing still leak a
`_pending` entry per dropped command, which is harmless.

### 5.5 SmartASS highlight tracking stopped while its window was closed (✅ fixed)

`TrackPostfix` was a postfix on `ComputerModule.OnFixedUpdate`. MechJeb calls a module's
`OnFixedUpdate` only `if (module.Enabled)` (*2.15.4* `MechJebCore.cs:576`). SmartASS is a
`DisplayModule`, so it's Enabled only while its window is open. With the window closed:
- `_committed` went stale, so a SmartASS click arriving from outside the window (part action,
  action group key, a refused or queued click) reverted `target` to an old mode.
- A cancelled SmartASS command wasn't noticed until the window opened (§5.4).

**Fix:** `TrackPostfix` is a postfix on `MechJebCore.FixedUpdate` (*2.15.4* `:523`, dev `:527`).
That runs every physics step whatever the window state, and looks up the core's SmartASS.
Found by the harness, on a probe with the SmartASS window closed. The harness test "SmartASS:
highlight tracks with the window closed" covers it.

---

## 6. MechJeb version notes

- **MechJeb `dev` replaced the UI.** The IMGUI `*Window.cs` / `*Menu.cs` drawing moved into a
  separate assembly, `MechJeb2.UI` (KodeWindow), which calls into the same core module methods.
  All 7 current patch targets exist with unchanged signatures on `dev` (checked 2026-11-19). Patch
  **core module methods**, not UI code, so one seam covers both UIs, toolbar actions, and part
  actions.
- Most autopilots have no "Start" method. The UI calls `module.Users.Add(window)` /
  `Users.Clear()` (dev) or `Users.Remove(window)` (2.15.4). **Generic seam (implemented,
  `src/UserPoolBridge.cs`):** prefixes on `UserPool.Add/Remove/Clear` (`ComputerModule.cs:357/365/373`,
  *2.15.4* `:323/331/339`). A call is delayed only if all of these hold:
  - the pool's `_controlledModule` is on the allowlist (currently Rendezvous AP, Docking AP, Ascent
    AP). The allowlist matches type **names**, the module's own or a base class's, because the
    ascent classes differ: 2.15.4 `MechJebModuleAscentBaseAutopilot` (Classic, PVG), dev
    `MechJebModuleAscentClassicAutopilot` and `MechJebModulePSGAutopilot`. CI builds against 2.15.x.
  - the call changes `Enabled` (engage while off; disengage while on, and `Remove` only when it
    empties the pool)
  - it's a player command (`IsPlayerCommand`)
  - for Add/Remove, the user is a `DisplayModule` (not an autopilot or kOS binding)

  `Clear()` has no user, so the allowlisted modules' non-physics internal stop paths are wrapped
  in Bypass: `DockingAutopilot.EndDocking/OnPartCouple`, `RendezvousAutopilot.Finish`,
  `AutopilotModule.StopAfterStepFailures`, `MechJebCore.ReloadAllComputerModules`, and *2.15.4*
  `DockingGuidance.OnModuleDisabled` (closing the window disengages instantly), dev
  `AscentMenu.AbortTimedLaunch`, and `AscentSettings.DisableAscentModules`. Switching ascent type
  clears the other type's autopilot: dev uses `Users.Clear()` (`AscentSettings.cs:244`), while
  *2.15.4* sets `Enabled = false` directly (`:293`), so that path never reaches the seam there.
  - **2.15.x windows** re-issue Add/Remove from `OnGUI` every frame until `Enabled` changes, so
    an identical command already in flight is swallowed and the "no connection" message is
    throttled.
  - **To extend:** add the module to `UserPoolBridge.Delayed`, grep `../MechJeb2` for every
    `Users.Add/Remove/Clear` on it in both versions, and Bypass-wrap any internal caller outside
    the physics step.
  - **Known quirk (2.15.4):** the Rendezvous window's automatic disengage when the target is in
    another SOI (`RendezvousAutopilotWindow.cs:26`) runs in `OnGUI`, so it's delayed like a
    click.
- The Testing instance runs **2.15.4**. Anything patched must be checked against both until the
  bridge drops 2.15.x support.
