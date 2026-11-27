# MechJeb ⇄ RemoteTech Bridge

A standalone KSP compatibility mod that routes **MechJeb autopilot commands** through a
signal-delay queue, **RemoteTech's or LightLag's**, so issuing a command to a distant vessel
takes the correct light-speed time to arrive — while the resulting control loop still runs
locally and instantly on the vessel.

It works with whichever of the two is installed (they conflict, so never both). The name
predates LightLag support.

## Why

RemoteTech makes you respect the speed of light. A probe at Duna is minutes away, so every
command you send it arrives minutes later. MechJeb skips that. Clicking "Execute node" or
"Land" in MechJeb acts on the vessel instantly, however far away it is, so a MechJeb-equipped
probe ignores RemoteTech's signal delay entirely.

This bridge closes that gap. MechJeb is treated as a computer *on board* the probe. Your
clicks are commands that have to cross the link, so they arrive after the real signal delay
and show up in RemoteTech's flight-computer queue like any other command. Once a command
arrives, the program it starts (the attitude hold, the burn, the landing) flies the vessel
locally and in real time, the way an on-board autopilot would.

## Supported commands

| MechJeb feature | Delayed commands | Not delayed |
|---|---|---|
| SmartASS | Every attitude button (`Engage`) | Mode tabs (ORBIT/SURFACE/…), since they only switch which buttons are shown |
| Node Executor | Execute one node, Execute all nodes | Abort (MechJeb calls it internally; it also serves as the instant local cutoff) |
| Landing Autopilot | Land at target, Land somewhere | Stop landing (MechJeb calls it internally at touchdown) |
| Rendezvous Autopilot | Engage, Disengage | The autopilot stopping itself (arrived, failed) |
| Docking Autopilot | Engage, Disengage | The autopilot stopping itself (docked, target lost); closing the 2.15.x Docking window |
| Ascent Autopilot | Engage, Disengage (window buttons and the "Ascent AP toggle" part action) | The autopilot stopping itself (circularised); Abort timed launch (dev); the timed-launch countdown itself |

Everything else in MechJeb still acts instantly. The full per-feature status is in
[`docs/MECHJEB_COVERAGE.md`](docs/MECHJEB_COVERAGE.md). The design and roadmap are in
[`docs/COMMAND_DELAY_PLAN.md`](docs/COMMAND_DELAY_PLAN.md).

## Design

Neither MechJeb2 nor RemoteTech is modified. This is a separate assembly that **hard-
depends on both** (a compatibility shim is pointless without them):

- **The delay mod** is found at load time, by reflection, so neither is a build-time reference
  and one DLL works with either (`src/Backends/`):
  - **RemoteTech** through its public `RemoteTech.API` class (`IsRemoteTechEnabled`,
    `HasLocalControl`, `GetShortestSignalDelay`, `QueueCommandToFlightComputer`,
    `AddSanctionedPilot`), plus its flight computer's queue to notice cancelled commands.
  - **LightLag** through `LightLag.LightLagAPI` (`Applies`, `IsConnected`, `GetDelay`,
    `QueueCommand`, `IsQueued`, `IsRunningCommand`).
  Both take the same command node (RemoteTech's keys) and call the same `RtPop` back.
- **MechJeb** is intercepted with **Harmony**, because it exposes no event to hook — we
  prefix-patch the internal `MechJebModuleSmartASS.Engage`.

References use `SpecificVersion=False`, so a MechJeb/RemoteTech *rebuild* is fine; only an
actual change to a signature we touch (e.g. `Engage`) forces a bridge recompile.

## The model

MechJeb is treated as a computer *on board* the vessel. Every action taken in the MechJeb
UI is a **command** that must travel the light-speed link (delayed). The program it starts
(attitude hold, landing descent, node burn, …) then has **local, real-time** control of
the vessel — its per-frame `Drive(FlightCtrlState)` loop is never delayed.

So we delay only the *command boundary* — the public activation / parameter-change call —
not the control loop. This mirrors how RemoteTech's own `AttitudeCommand` works.

## How it works (example: SmartASS)

1. A Harmony **prefix** on `MechJebModuleSmartASS.Engage(bool)` intercepts the button press.
2. If RemoteTech is active and the vessel lacks local control, the bridge snapshots the
   intent, packages it as a RemoteTech `ExternalAPICommand` via
   `RemoteTech.API.API.QueueCommandToFlightComputer(ConfigNode)`, and **suppresses** the
   immediate Engage.
3. RemoteTech holds the command for the light-speed delay (it appears in the flight-computer
   queue UI with the label `SmartASS → PROGRADE`, etc.).
4. When the delay elapses, RemoteTech reflects back into `SmartAssBridge.RtPop`, which
   re-invokes the real `Engage` locally (guarded so the prefix lets that call through).

The Node Executor and Landing seams follow the same pattern without the highlight handling.
Rendezvous, Docking and Ascent share one generic seam on MechJeb's `UserPool`
(`src/UserPoolBridge.cs`). Every seam is registered in `BridgeAddon.Start()`. Shared plumbing
lives in `src/BridgeCore.cs`.

While the vessel has no link, SmartASS also keeps holding the attitude it was last commanded
to. The bridge registers a RemoteTech sanctioned pilot that re-applies MechJeb's attitude
output after RemoteTech neutralizes the controls. Throttle isn't re-applied, so engines stay
cut without a connection.

## Building

Requires a local KSP install with **MechJeb2 and Harmony (`000_Harmony`) installed in
`GameData`**. RemoteTech and LightLag aren't build references (the harness still needs RemoteTech,
see *Testing*). These provide the reference assemblies, resolved through
[KSPBuildTools](https://github.com/KSPModdingLibs/KSPBuildTools) `ModReference` items. You also
need `dotnet` (or Mono's `msbuild`).

Point the build at your install with a gitignored `Directory.Build.props.user`:

```xml
<Project>
  <PropertyGroup>
    <KSPBT_GameRoot>/path/to/Kerbal Space Program</KSPBT_GameRoot>
    <!-- Only needed if Managed/ isn't where KSPBuildTools expects (e.g. Linux KSP_x64_Data) -->
    <KSPBT_ManagedPath>/path/to/Kerbal Space Program/KSP_x64_Data/Managed</KSPBT_ManagedPath>
  </PropertyGroup>
</Project>
```

```
dotnet build MechJebRTBridge.csproj -c Release
```

The build writes the DLL to the repo's `GameData/MechJebRTBridge/Plugins/` (for packaging) and,
when `KSPBT_GameRoot` is set, also copies it into that install's GameData. The version comes
from the latest git tag (MinVer).

Every push and pull request runs `.github/workflows/release.yml`. It builds against the
CKAN-released MechJeb2 and Harmony2, packages the zip and validates its `.ckan` against the CKAN schema, and
uploads both as an artifact (a dry run). Pushing a `v*.*.*` tag does the same, then attaches
`MechJebRTBridge-vX.Y.Z.zip` and `MechJebRTBridge.version` to a GitHub release and commits
`MechJebRTBridge/MechJebRTBridge-X.Y.Z.ckan` to the [KSP-Mods-CKAN](https://github.com/MinmusMachineWorks/KSP-Mods-CKAN) meta repo.
Packaging and publishing are done by that repo's shared `release-mod.yml` workflow. The CKAN
metadata (everything except the per-release fields) is in `ckan/metadata.json`. The annotated
tag's message becomes the top of the release notes. Publishing needs the `CKAN_META_TOKEN` org
secret: a token with Contents write access to KSP-Mods-CKAN.

## Testing

`tests/MechJebRTBridge.Tests/` is a dev-only in-game test harness (never shipped). Build it with
`dotnet build tests/MechJebRTBridge.Tests/MechJebRTBridge.Tests.csproj -c Release`, put an
uncrewed MechJeb probe in low Kerbin orbit, and run the tests from the green toolbar button in
flight. Results go to the window and to `KSP.log` (`[MJ-RT Test]`). See
[`tests/README.md`](tests/README.md) for the test vessel, what's covered, and what's still
manual.

## Install

Via CKAN: add the meta repo with
`ckan repo add MinmusMachineWorks https://github.com/MinmusMachineWorks/KSP-Mods-CKAN/archive/main.tar.gz`,
then `ckan install MechJebRTBridge`.

Manually: the release zip holds `GameData/MechJebRTBridge/`, plus `LICENSE` and `README.md`. Unzip it
into your KSP folder so its `GameData/` merges with yours, so that the DLL ends up at
`GameData/MechJebRTBridge/Plugins/MechJebRTBridge.dll`. Requires MechJeb2, Harmony
(000_Harmony), and RemoteTech or LightLag. LightLag is in the same Minmus Machine Works CKAN
repository. With neither installed, the bridge loads and delays nothing.

## Known limitations

- **Only SmartASS, Node Executor, Landing, Rendezvous, Docking, and Ascent are delayed** (see
  *Supported commands*). Timed launch, Translatron, staging, and the rest still act instantly.
  On MechJeb dev, Ascent's Engage starts a timed launch's countdown at the click; only the
  autopilot engage waits for the signal.
- MechJeb part actions bound to an action group (SmartASS modes, land somewhere, PANIC, …) are
  delayed once, by the delay mod's action-group delay. The bridge doesn't add a second delay.
- Only player clicks are delayed. MechJeb's own internal calls (an autopilot starting its node
  burns, Hoverslam engaging SmartASS, PANIC, restoring SmartASS on load) and kOS scripts run
  locally.
- **Cancellations aren't delayed.** Node Executor Abort and Stop Landing act instantly.
- **Persistence across save/load.** Each command node carries a small payload (`SATarget` +
  `SAResetPID`, `NEMode`, `LAMode`). If the in-memory pending map is lost on a reload, `RtPop`
  rebuilds the command against the fresh MechJeb module. Only the *mode* is persisted. The
  landing target comes from MechJeb's current target when the command arrives, not from when
  it was sent, and Node Executor works on whatever nodes exist on arrival (it skips with a
  message if there are none).
- **Harmony patches MechJeb internals**, whose signatures can change between MechJeb versions.
  With the hard reference this surfaces as a load/JIT failure (logged) rather than a silent
  no-op, and requires a bridge recompile against the new MechJeb.
- **Signal-loss policy** (should a running local program keep flying, or cut out?) is not yet
  wired to `MechJebCore.DeactivateControl`. Currently only *new* commands are delayed.
- **Telemetry is not delayed** — the MechJeb UI still shows live vessel state. Out of scope.
- Commands are only accepted for the **active vessel** (a RemoteTech API constraint; LightLag
  needs a loaded vessel, and the seams only act on the active one).
- **Signal loss differs between the two.** RemoteTech wipes the controls without a link; the
  bridge then keeps SmartASS's attitude hold flying (a sanctioned pilot) but not the throttle.
  LightLag doesn't touch an autopilot's output, so a running MechJeb program keeps full control,
  throttle included, as an on-board computer would. Not yet checked in game with LightLag.
- **LightLag support isn't checked in game yet,** and the test harness covers RemoteTech only.
