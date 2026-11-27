using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MechJebRTBridge
{
    /// <summary>
    /// One-time bootstrap. Patches each MechJeb seam independently so that one failing
    /// patch (e.g. a MechJeb version whose method signature changed) can't abort the others.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, once: true)]
    public class BridgeAddon : MonoBehaviour
    {
        private const string HarmonyId = "com.mechjebrtbridge";

        public void Start()
        {
            var harmony = new Harmony(HarmonyId);

            // SmartASS: delay Engage; track committed mode each frame; capture attitude for
            // sanctioned-pilot hold during signal blackout.
            Patch(harmony, "MuMech.MechJebModuleSmartASS:Engage",
                  prefix:  H(typeof(SmartAssBridge),  nameof(SmartAssBridge.EngagePrefix)));
            Patch(harmony, "MuMech.MechJebCore:FixedUpdate",
                  postfix: H(typeof(SmartAssBridge),  nameof(SmartAssBridge.TrackPostfix)));
            Patch(harmony, "MuMech.MechJebModuleAttitudeController:Drive",
                  postfix: H(typeof(SmartAssBridge),  nameof(SmartAssBridge.CaptureAttitudePostfix)));

            // Node executor: delay activation only. Abort() is intentionally not patched —
            // MechJeb calls it internally when nodes are consumed or deleted, and those
            // internal calls must not be queued. The instant local abort is the §8 cutoff.
            Patch(harmony, "MuMech.MechJebModuleNodeExecutor:ExecuteOneNode",
                  prefix: H(typeof(NodeExecutorBridge), nameof(NodeExecutorBridge.ExecuteOneNodePrefix)));
            Patch(harmony, "MuMech.MechJebModuleNodeExecutor:ExecuteAllNodes",
                  prefix: H(typeof(NodeExecutorBridge), nameof(NodeExecutorBridge.ExecuteAllNodesPrefix)));

            // Landing autopilot: delay activation only. StopLanding() is intentionally not
            // patched — MechJeb calls it internally at touchdown, same issue as Abort above.
            Patch(harmony, "MuMech.MechJebModuleLandingAutopilot:LandAtPositionTarget",
                  prefix: H(typeof(LandingBridge), nameof(LandingBridge.LandAtPositionTargetPrefix)));
            Patch(harmony, "MuMech.MechJebModuleLandingAutopilot:LandUntargeted",
                  prefix: H(typeof(LandingBridge), nameof(LandingBridge.LandUntargetedPrefix)));

            // Commands that run other patched commands internally: the whole thing is one
            // command (or no command at all), so its sub-calls must not be delayed separately.
            //   - Translatron PANIC stops other autopilots (SmartASS.Engage(OFF), StopLanding, …).
            //     PANIC itself isn't delayed yet (see docs/MECHJEB_COVERAGE.md).
            //   - SmartASS.OnLoad re-engages the saved target when the vessel loads.
            // Physics-step callers (running autopilots, kOS) are excluded by
            // BridgeCore.IsPlayerCommand instead.
            var bypassPrefix    = H(typeof(BridgeCore), nameof(BridgeCore.BypassPrefix));
            var bypassFinalizer = H(typeof(BridgeCore), nameof(BridgeCore.BypassFinalizer));
            Patch(harmony, "MuMech.MechJebModuleTranslatron:PanicSwitch",
                  prefix: bypassPrefix, finalizer: bypassFinalizer);
            Patch(harmony, "MuMech.MechJebModuleSmartASS:OnLoad",
                  prefix: bypassPrefix, finalizer: bypassFinalizer);

            // RemoteTech's delayed action group. MechJeb KSPActions (SmartASS modes, land
            // somewhere, PANIC, …) bound to an action group fire from inside this Pop, after RT
            // has already applied the signal delay. Without the bypass the bridge would queue
            // them a second time (2× delay). Only with RemoteTech installed; LightLag's queue
            // says when it runs a command instead (BridgeCore.IsPlayerCommand).
            if (AccessTools.TypeByName("RemoteTech.FlightComputer.Commands.ActionGroupCommand") != null)
                Patch(harmony, "RemoteTech.FlightComputer.Commands.ActionGroupCommand:Pop",
                      prefix: bypassPrefix, finalizer: bypassFinalizer);

            // Generic seam: autopilots engaged/disengaged via their Users pool (UserPoolBridge
            // allowlist: Rendezvous, Docking, Ascent). Declared-method lookup with exact parameters,
            // because UserPool's Add/Remove/Clear hide List<object>'s.
            Patch(harmony, AccessTools.DeclaredMethod(typeof(MuMech.UserPool), "Add", new[] { typeof(object) }),
                  "MuMech.UserPool:Add", prefix: H(typeof(UserPoolBridge), nameof(UserPoolBridge.AddPrefix)));
            Patch(harmony, AccessTools.DeclaredMethod(typeof(MuMech.UserPool), "Remove", new[] { typeof(object) }),
                  "MuMech.UserPool:Remove", prefix: H(typeof(UserPoolBridge), nameof(UserPoolBridge.RemovePrefix)));
            Patch(harmony, AccessTools.DeclaredMethod(typeof(MuMech.UserPool), "Clear", Type.EmptyTypes),
                  "MuMech.UserPool:Clear", prefix: H(typeof(UserPoolBridge), nameof(UserPoolBridge.ClearPrefix)));

            // Internal stop paths for the UserPool-seam autopilots that can run outside the
            // physics step. Their Users.Clear/Remove is on-board, never a player command.
            // Names differ between MechJeb versions; missing ones are logged and skipped.
            foreach (string stop in new[]
                     {
                         "MuMech.MechJebModuleDockingAutopilot:EndDocking",       // docked / target lost
                         "MuMech.MechJebModuleDockingAutopilot:OnPartCouple",     // docking event
                         "MuMech.MechJebModuleRendezvousAutopilot:Finish",        // dev
                         "MuMech.AutopilotModule:StopAfterStepFailures",          // dev
                         "MuMech.MechJebCore:ReloadAllComputerModules",           // settings reload
                         "MuMech.MechJebModuleDockingGuidance:OnModuleDisabled",  // 2.15.x: window closed
                         // dev: Abort (timed launch) disengages the ascent AP with the countdown.
                         // Instant, like the other aborts (plan doc §8, cancellation delay).
                         "MuMech.MechJebModuleAscentMenu:AbortTimedLaunch",
                         // Switching ascent type turns off the other type's autopilot. Dev does it
                         // with Users.Clear(); 2.15.x sets Enabled directly, so there the wrapper
                         // changes nothing.
                         "MuMech.MechJebModuleAscentSettings:DisableAscentModules",
                     })
                Patch(harmony, stop, prefix: bypassPrefix, finalizer: bypassFinalizer);

            Debug.Log($"[MJ-RT Bridge] Initialised; delay backend {DelayBackend.Current?.Name ?? "none"}.");
        }

        private static HarmonyMethod H(Type t, string methodName) =>
            new HarmonyMethod(t, methodName);

        private static void Patch(Harmony harmony, string target,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null, HarmonyMethod finalizer = null)
        {
            MethodBase method;
            try { method = AccessTools.Method(target); }
            catch (Exception e)
            {
                Debug.LogError($"[MJ-RT Bridge] failed to resolve {target} (skipped): {e.Message}");
                return;
            }
            Patch(harmony, method, target, prefix, postfix, finalizer);
        }

        private static void Patch(Harmony harmony, MethodBase method, string target,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null, HarmonyMethod finalizer = null)
        {
            try
            {
                if (method == null)
                {
                    Debug.LogWarning($"[MJ-RT Bridge] patch target not found (skipped): {target}");
                    return;
                }
                harmony.Patch(method, prefix: prefix, postfix: postfix, finalizer: finalizer);
                Debug.Log($"[MJ-RT Bridge] patched {target}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[MJ-RT Bridge] failed to patch {target} (skipped): {e.Message}");
            }
        }
    }
}
