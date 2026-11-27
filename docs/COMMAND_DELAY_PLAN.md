# MechJeb ⇄ RemoteTech — Command Delay Implementation Plan

Status of this doc: **design + inventory**. It captures everything learned while building
the SmartASS proof of concept, so the same treatment can be rolled out to every MechJeb
command. Line references are against the MechJeb2 `dev` source cloned alongside this repo
and may drift between MechJeb versions — re-verify method names before patching.

---

## 1. The principle

**MechJeb is a computer *on board* the vessel.** Every action taken in the MechJeb UI is a
*command*. Under RemoteTech, a command issued to a vessel that lacks local control must
travel the light-speed link before it takes effect. But the *program* a command starts
(attitude hold, landing descent, node burn…) runs **locally and instantly** on the vessel —
its per-frame control loop is never delayed.

Three consequences drive every decision below:

1. **Delay the command boundary, not the loop.** The thing that crosses the link is
   "activate / deactivate / change a setpoint." The `Drive(FlightCtrlState)` loop that
   follows is local.
2. **Navigating the UI is local.** Switching tabs/panels, opening windows, selecting which
   readout to show — none of that commands the vessel, so none of it is delayed. (Example:
   the SmartASS **mode** row — ORBIT/SURFACE/TARGET/ADVANCED — only picks which target
   buttons are shown; it never calls `Engage()`, so it must stay instant.)
3. **The UI must not lie.** Because a command is delayed, the UI should reflect the vessel's
   *actual* state until the command lands, not the just-clicked intent. See §4.

---

## 2. Architecture (the reference pattern)

Established by the SmartASS implementation (`src/SmartAssBridge.cs`). Every seam follows it.

```
Player clicks a MechJeb command button
        │
        ▼
Harmony PREFIX on the command method  ── if RT off OR vessel has local control ──▶ run normally
        │  (RT on AND no local control)
        ▼
Snapshot intent  →  API.QueueCommandToFlightComputer(ConfigNode)   [RemoteTech applies delay]
        │                                                            (command shows in FC queue)
        ▼  return false  (suppress the immediate action)
   ...light-speed delay elapses...
        │
        ▼
RemoteTech reflects back into our public static  RtPop(ConfigNode)
        │
        ▼
Re-invoke the real MechJeb method behind a bypass guard  →  program starts, runs locally
```

Key RemoteTech API (all public, present since the 1.9.x release — verified stable):
- `RemoteTech.API.API.IsRemoteTechEnabled()`
- `RemoteTech.API.API.HasLocalControl(Guid)`
- `RemoteTech.API.API.GetShortestSignalDelay(Guid)` — for UI cues
- `RemoteTech.API.API.QueueCommandToFlightComputer(ConfigNode)` — enqueues an
  `ExternalAPICommand` whose `ReflectionPopMethod`/`ReflectionExecuteMethod` call us back.

Command node contract (see `BuildCommandNode`): `GUIDString`, `Executor`, `ReflectionType`
(assembly-qualified), `ReflectionPopMethod`, labels, plus our own `Token`. One-shot commands
do their work in the Pop method and return `false` (no Execute method → not kept active).

**Delay backends (`src/Backends/`).** The queue isn't RemoteTech's alone: `DelayBackend.Current`
is RemoteTech's backend if `RemoteTech.API.API` is loaded, else LightLag's if `LightLag.LightLagAPI`
is, else none (nothing is delayed). Both are reached by reflection with cached delegates, so
neither mod is a build reference and one DLL works with either. They take the same node and call
the same `RtPop`:
- **RemoteTech:** `NeedsDelay` = enabled and no local control; `Queue` =
  `QueueCommandToFlightComputer`; `IsQueued` reads the flight computer's queue for the token;
  `HoldAttitude` adds the sanctioned pilot.
- **LightLag:** `NeedsDelay` = `LightLagAPI.Applies`; `Delay` = +Infinity unless `IsConnected`;
  `Queue` = `QueueCommand`, keeping token → command id for `IsQueued`; `RunningCommand` =
  `IsRunningCommand`; no attitude hold (LightLag doesn't wipe the controls).

Dependencies: a hard reference to MechJeb2 (the bridge patches it); **Harmony** for interception
(MechJeb exposes no events). Target `net48`.

---

## 3. Classification of MechJeb actions

### A. Continuous control programs — delay activation + every setpoint change; loop is local
The activation method and any parameter mutation are commands. The `Drive` loop is local.

### B. One-shot discrete actions — delay the whole action as a single command
Instantaneous events (stage, deploy, abort). Delay the event itself (mirrors RemoteTech's
own `ActionGroupCommand`/`PartActionCommand`).

### C. Pure local — never delayed
Planning, information, and UI navigation that never write `FlightCtrlState` / never command
the vessel. Delaying these would just make the UI feel broken.

---

## 4. UI-truthfulness treatment ("hold highlight + queued cue")

For continuous programs whose UI shows a selected state, the delayed command must not flip
the UI immediately. Reference implementation (SmartASS):

- Track the **actual** state every physics step via a postfix on `MechJebCore.FixedUpdate`,
  recording it only while no command is in flight (`_committed`). Not on the module's own
  `OnFixedUpdate`: that one only runs while the module is Enabled (§10).
- On interception: revert the UI field to the actual state, and post a screen message
  `"<action> queued · signal delay X.Xs"` (`ScreenMessages.PostScreenMessage`).
- On `RtPop`: set the UI field to the requested state, update `_committed`, run the command
  — so the highlight snaps over exactly when the vessel acts.
- Track commands in flight per module with `InFlightSet<T>` (`_inFlight`), keyed by token. It
  covers rapid re-clicks, and it forgets a command RemoteTech drops without popping (§10).

This is safe only because the control loop is **latched** at activation (e.g. SmartASS
`Engage()` pushes into `Core.Attitude`; the UI `target` field is display-only). **Verify per
seam** that the UI field is display-only before reverting it.

---

## 5. Master command inventory

The full inventory lives in [`MECHJEB_COVERAGE.md`](MECHJEB_COVERAGE.md). For every MechJeb
feature it lists the hand action it replaces, whether RemoteTech delays that action, the seam,
and its status. Update that file, not this one, when coverage changes.

> Activation-shape note: many programs don't expose a distinct "Start" method — they enable
> via the `ComputerModule.Enabled` property setter (which fires `OnModuleEnabled` →
> `Users.Add`). For those, the interception seam is the **`Enabled` setter** (or the specific
> menu method that sets it), not a bespoke method. Prefer patching the highest-level public
> entry a UI button actually calls.

---

## 6. Generalized pattern to add a seam

1. **Find the command boundary** — the public method a UI button/`KSPAction` invokes that
   changes vessel state (`Users.Add/Clear`, `Enabled=`, or a setpoint write). Confirm it
   *latches* (so the loop is local) and identify the display-only UI field, if any.
2. **Harmony-prefix it.** Guard: `!BridgeCore.IsPlayerCommand(...)` → run; `!IsRemoteTechEnabled()` → run;
   `HasLocalControl` → run. Otherwise snapshot params, queue via RT, revert UI field, post
   cue, return `false`.
3. **Serialize the intent** into the command node (§8 persistence) or stash it by token.
4. **`RtPop`**: rebuild params, set `_bypass`, re-invoke the real method, update UI/committed
   state, clear in-flight.
5. **Cancellation** (`StopLanding`, `Abort`, disable) is *also* a command — delay it too
   (except the emergency local cutoff, §8).
6. Add the seam to `BridgeAddon` / annotate the patch class.

---

## 7. Command vs. setpoint granularity

A running program's parameter changes are **new delayed commands**, not free local tweaks:
- SmartASS PROGRADE → RETROGRADE is a new command; the hold in between is local.
- Landing target coordinates, ascent target apoapsis/inclination/heading, rover
  waypoint/speed, thrust limit value — each mutation must go through the delay layer.
- This means the interception must sit on **every setpoint mutation**, not just the initial
  enable. Some of these are field writes from UI code, not tidy methods → those seams are the
  harder ones (marked ⚠️) and may need patching the specific menu handler.

---

## 8. Cross-cutting concerns / open design decisions

- **Persistence across save/load (✅ basic, mode only).** Each seam stashes its intent in memory
  by token *and* writes a payload into the command `ConfigNode` (`SATarget`/`SAResetPID`,
  `NEMode`, `LAMode`). RemoteTech persists `ExternalAPICommand` nodes, so if the in-memory map is
  gone when `RtPop` fires, the seam rebuilds the command from the payload against a freshly
  resolved module (`BridgeCore.ResolveModule`). Gap: only the mode is serialized. Rich setpoints
  (landing target coordinates, the node list) are read live at arrival time. Seams with
  setpoints (§7) need to serialize them too.
- **Signal-loss policy (⚠️ not wired).** If the link drops while a program runs, should the
  onboard program keep flying (it's local) or cut out? The "computer on board" model says it
  keeps running — which differs from stock RT neutralizing controls. MechJeb exposes
  `MechJebCore.DeactivateControl` (`MechJebCore.cs:73`) for an emergency local cutoff; decide
  and wire a policy. This is the one place an *instant* (undelayed) action is correct.
- **Cancellation delay (⚠️ open).** Deactivations should take the same light-speed time to
  arrive (an instant remote abort breaks the fiction), except for the emergency cutoff. But
  `NodeExecutor.Abort()` and `LandingAutopilot.StopLanding()` are also called *internally* by
  MechJeb (node consumed, touchdown), so a plain prefix would queue those too. They are currently
  unpatched (instant). Delaying them needs a seam that only catches the UI call (e.g. patch the
  window's button handler, or check the caller).
- **Parameter marshalling.** Rich params (Vector3d target coords, quaternions) → prefer
  stashing the live object by token for the in-memory path; serialize to strings only for the
  persistence path.
- **Telemetry is not delayed (out of scope).** The MechJeb UI shows live vessel state; a
  strict model would delay displayed readouts by one-way light time. Large change; deferred.
- **Active vessel only.** `QueueCommandToFlightComputer` accepts commands only for the active
  vessel (a RemoteTech API constraint); LightLag's `QueueCommand` needs a loaded one — fine for
  player-driven UI.
- **Signal loss under LightLag.** LightLag leaves an autopilot's `OnFlyByWire` output alone, so
  a running MechJeb program keeps attitude *and* throttle without a link, unlike under
  RemoteTech (§10). That fits the on-board-computer model; not yet checked in game.
- **Version coupling.** Harmony patches MechJeb internals; a signature change surfaces as a
  load/JIT failure and needs a bridge recompile. Keep a guard/log per patch.

---

## 9. Suggested rollout order

1. ~~**Landing autopilot**~~ ✅ activation done (cancellation open, see §8).
2. ~~**NodeExecutor**~~ ✅ activation done (cancellation open, see §8).
3. ~~**Ascent autopilot**~~ ✅ engage + disengage, window and toggle action (generic `UserPool`
   seam). Timed launch is still a gap.
4. **Persistence (§8)**: basic mode-only payloads done ✅. Extend to setpoints as seams grow.
5. **Signal-loss policy (§8)** — wire `DeactivateControl`.
6. ~~Rendezvous, Docking~~ ✅ engage + disengage (generic `UserPool` seam). Still to do: Hoverslam,
   Translatron, auto-staging. (Spinup needs nothing: only the PSG autopilot starts it.)
7. One-shot **B** actions (stage, PANIC, deploys).
8. Setpoint-granularity seams (⚠️ thrust limits, rover waypoints, ascent/landing params).

---

## 10. Reference implementation gotchas (learned from SmartASS)

- **Command methods have internal callers.** `ExecuteOneNode/AllNodes`, `LandUntargeted` and
  `SmartASS.Engage` are also called by running autopilots (Rendezvous, Ascent, Hoverslam,
  Translatron PANIC, SmartASS `OnLoad`) and by kOS. A bare prefix delays those too. Start every
  seam prefix with `if (!BridgeCore.IsPlayerCommand(...)) return true;`. It treats physics-step
  calls (`Time.inFixedTimeStep`) and Bypass regions as internal. A new non-physics-step internal
  caller needs a Bypass wrapper in `BridgeAddon`. See `MECHJEB_COVERAGE.md` §5.1.
- **RT-delayed action groups re-enter the bridge.** MechJeb `KSPAction`s bound to an action group
  fire from RT's `ActionGroupCommand.Pop`, after RT's delay. `BridgeAddon` wraps that Pop in Bypass
  so they aren't delayed twice. Any other RT command that can fire MechJeb code (e.g. a future
  `EventCommand` path to a MechJeb `KSPEvent`) needs the same wrapper.

- **RT's manual extra delay** is `FlightComputer.TotalDelay` (the "Delay (+ signal)" field in
  RT's queue window). `Enqueue` adds `max(0, TotalDelay − Delay)` as the command's
  `ExtraDelay`. The test harness uses it to get a few seconds of delay in LKO. RT pops queued
  commands in `FlightComputer.OnUpdate` (the `Update` phase), so `RtPop` runs outside the
  physics step.
- **RT can drop a queued command without telling us.** There are three ways:
  - A `CancelCommand` from the queue window's "x" removes our `ExternalAPICommand` without calling
    the bridge. It ignores extra delay, so it can overtake the command.
  - A command that comes due while the vessel is unpowered is removed unpopped.
  - Hibernation drops queued commands.

  `QueueCommandToFlightComputer` also returns true when `Enqueue` silently discards the command
  (`!InputAllowed`, or a queue entry that compares equal). So in-flight state must not be
  cleared only in `RtPop`. `InFlightSet.Has` checks each entry against the live queue
  (`BridgeCore.IsQueued`, which reads the private `ExternalAPICommand.externalData` for our
  `Token`). See `MECHJEB_COVERAGE.md` §5.4.

- The UI sets the state field (`target`) **before** calling the command (`Engage`), so the
  prefix already sees the new value — track the *previous* value out-of-band (per-physics-step
  `MechJebCore.FixedUpdate` postfix) to keep the highlight truthful (§4).
- **MechJeb only runs `OnFixedUpdate` / `OnUpdate` / `Drive` on Enabled modules**
  (`MechJebCore.cs:576/668/1068` on 2.15.4). A window module (`DisplayModule`, SmartASS
  included) is Enabled only while its window is open. A per-frame hook on a module's own
  callbacks therefore stops when its window closes. Hook `MechJebCore.FixedUpdate` instead. See
  `MECHJEB_COVERAGE.md` §5.5.
- `RtPop` must be `public static bool RtPop(ConfigNode)`; RemoteTech invokes it by reflection
  via `Type.GetType(AssemblyQualifiedName)` — keep the type public. LightLag calls the same
  method the same way and ignores the result.
- **LightLag runs its queued commands in the physics step** (`CommandQueue.Process`, from
  `LightLagVessel.FixedUpdate`), with `LightLagAPI.IsRunningCommand` true. So an action group
  bound to a MechJeb action is already an on-board call to `IsPlayerCommand`; it also checks
  `DelayBackend.RunningCommand`. LightLag doesn't hook MechJeb's IMGUI windows, so a MechJeb
  click is delayed only by the bridge; a MechJeb part-menu button (the Ascent AP toggle) is
  delayed by LightLag's part-menu hook and runs on board, so the bridge lets it through.
- **LightLag hands back the node itself**, not wrapped in `ExternalData`; `GetValueDeep` reads both.
- RemoteTech wraps the queued node under an `ExternalData`/`MJRT` subnode before handing it
  back; read values with a shallow recursive lookup (`GetValueDeep`).
- One-shot = provide only `ReflectionPopMethod`, do the work there, return `false`.
- The bridge target framework must be ≤ RemoteTech's (released RT = old .NET 4.5 → build
  `net48`; the unreleased Burst rewrite targeted `net481` and forced a bump — watch for this).
```
