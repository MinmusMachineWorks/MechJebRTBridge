using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MuMech;
using static MechJebRTBridge.Tests.Ctx;
using Target = MuMech.MechJebModuleSmartASS.Target;

namespace MechJebRTBridge.Tests
{
    internal sealed class TestCase
    {
        internal readonly string Name;
        internal readonly Func<Ctx, IEnumerator> Body;
        internal TestCase(string name, Func<Ctx, IEnumerator> body) { Name = name; Body = body; }
    }

    /// <summary>
    /// The test list. Conventions:
    ///   - Code that runs directly in a test body runs in Update, outside the physics step: the
    ///     bridge sees it as a player click.
    ///   - Code inside <c>yield return Physics(() => …)</c> runs in FixedUpdate: the bridge sees
    ///     it as MechJeb (or kOS) acting on board, and must not delay it.
    ///   - Log strings matched here are the bridge's own Debug.Log formats. If you change one in
    ///     src/, change it here too.
    /// </summary>
    internal static class Tests
    {
        internal static readonly List<TestCase> All = new List<TestCase>
        {
            new TestCase("Every patch target is patched",          PatchesApplied),
            new TestCase("SmartASS: click is delayed",              SmartAssClickDelayed),
            new TestCase("SmartASS: on-board call is instant",      SmartAssInternalInstant),
            new TestCase("SmartASS: highlight tracks with the window closed", SmartAssTracksWindowClosed),
            new TestCase("No connection: click is refused",         NoConnectionRefused),
            new TestCase("Node Executor: click is delayed",         NodeClickDelayed),
            new TestCase("Node Executor: on-board call is instant", NodeInternalInstant),
            new TestCase("Landing: click is delayed",               LandingClickDelayed),
            new TestCase("Rendezvous AP: engage is delayed (once)", RendezvousEngageDelayed),
            new TestCase("Rendezvous AP: disengage (Remove) is delayed", RendezvousRemoveDelayed),
            new TestCase("Rendezvous AP: disengage (Clear) is delayed",  RendezvousClearDelayed),
            new TestCase("Rendezvous AP: on-board stop is instant", RendezvousInternalStopInstant),
            new TestCase("Rendezvous AP: non-window user is instant", RendezvousNonWindowInstant),
            new TestCase("Docking AP: engage + disengage delayed",  DockingEngageDisengageDelayed),
            new TestCase("Docking AP: EndDocking is instant",       DockingEndDockingInstant),
            new TestCase("Docking AP: closing the window is instant", DockingWindowCloseInstant),
            new TestCase("Ascent AP: engage is delayed",            AscentEngageDelayed),
            new TestCase("Ascent AP: disengage (Remove, Clear) is delayed", AscentDisengageDelayed),
            new TestCase("Ascent AP: on-board stop is instant",     AscentInternalStopInstant),
            new TestCase("Ascent AP: toggle part action is delayed", AscentToggleActionDelayed),
            new TestCase("cancel: Rendezvous engage can be re-sent", CancelThenResend),
            new TestCase("cancel: SmartASS highlight keeps tracking", CancelSmartAssTracking),
        };

        // ---- patches -----------------------------------------------------------

        private const string BridgeHarmonyId = "com.mechjebrtbridge";

        // Mirrors BridgeAddon.Start(). Optional targets only exist in some MechJeb versions; if
        // one exists in the loaded version it must be patched.
        private static readonly (string target, bool optional)[] PatchTargets =
        {
            ("MuMech.MechJebModuleSmartASS:Engage", false),
            ("MuMech.MechJebCore:FixedUpdate", false),
            ("MuMech.MechJebModuleAttitudeController:Drive", false),
            ("MuMech.MechJebModuleNodeExecutor:ExecuteOneNode", false),
            ("MuMech.MechJebModuleNodeExecutor:ExecuteAllNodes", false),
            ("MuMech.MechJebModuleLandingAutopilot:LandAtPositionTarget", false),
            ("MuMech.MechJebModuleLandingAutopilot:LandUntargeted", false),
            ("MuMech.MechJebModuleTranslatron:PanicSwitch", false),
            ("MuMech.MechJebModuleSmartASS:OnLoad", false),
            ("RemoteTech.FlightComputer.Commands.ActionGroupCommand:Pop", true), // only with RemoteTech
            ("MuMech.UserPool:Add", false),
            ("MuMech.UserPool:Remove", false),
            ("MuMech.UserPool:Clear", false),
            ("MuMech.MechJebModuleDockingAutopilot:EndDocking", true),
            ("MuMech.MechJebModuleDockingAutopilot:OnPartCouple", true),
            ("MuMech.MechJebModuleRendezvousAutopilot:Finish", true),
            ("MuMech.AutopilotModule:StopAfterStepFailures", true),
            ("MuMech.MechJebCore:ReloadAllComputerModules", true),
            ("MuMech.MechJebModuleDockingGuidance:OnModuleDisabled", true),
            ("MuMech.MechJebModuleAscentMenu:AbortTimedLaunch", true),
            ("MuMech.MechJebModuleAscentSettings:DisableAscentModules", true),
        };

        private static MethodBase Resolve(string target)
        {
            switch (target)
            {
                case "MuMech.UserPool:Add":    return AccessTools.DeclaredMethod(typeof(UserPool), "Add", new[] { typeof(object) });
                case "MuMech.UserPool:Remove": return AccessTools.DeclaredMethod(typeof(UserPool), "Remove", new[] { typeof(object) });
                case "MuMech.UserPool:Clear":  return AccessTools.DeclaredMethod(typeof(UserPool), "Clear", Type.EmptyTypes);
            }
            string[] parts = target.Split(':');
            Type t = AccessTools.TypeByName(parts[0]);
            return t == null ? null : AccessTools.DeclaredMethod(t, parts[1]);
        }

        private static IEnumerator PatchesApplied(Ctx c)
        {
            var missing = new List<string>();
            var absent  = new List<string>();
            foreach (var (target, optional) in PatchTargets)
            {
                MethodBase m = Resolve(target);
                if (m == null)
                {
                    if (optional) absent.Add(target.Split(':')[1]);
                    else missing.Add(target + " (method not found)");
                    continue;
                }
                Patches info = Harmony.GetPatchInfo(m);
                if (info == null || !info.Owners.Contains(BridgeHarmonyId))
                    missing.Add(target);
            }
            Check(missing.Count == 0, "not patched: " + string.Join(", ", missing.ToArray()));
            if (absent.Count > 0) c.Note = "absent in this MechJeb (fine): " + string.Join(", ", absent.ToArray());
            yield break;
        }

        // ---- SmartASS ----------------------------------------------------------

        private static IEnumerator SmartAssClickDelayed(Ctx c)
        {
            var sass = c.Module<MechJebModuleSmartASS>();
            yield return PhysicsTicks(2); // let the bridge record the committed mode (OFF)

            int mark = c.Mark();
            double sent = UT;
            sass.target = Target.PROGRADE;
            sass.Engage();

            c.CheckLog("SCHED PROGRADE ", mark, 1, "click");
            Check(c.QueuedCount == 1, $"expected 1 bridge command in the delay queue, got {c.QueuedCount}");
            Check(sass.target == Target.OFF, $"SmartASS shows {sass.target} before the command arrived; should still show OFF");

            yield return PhysicsTicks(10);
            Check(sass.target == Target.OFF, $"SmartASS switched to {sass.target} before the delay elapsed");

            yield return WaitUntil(() => c.Count("EXEC PROGRADE ", mark) > 0, "SmartASS EXEC");
            CheckDelayed(sent, "SmartASS PROGRADE");
            Check(sass.target == Target.PROGRADE, $"after EXEC SmartASS is {sass.target}, expected PROGRADE");
            Check(c.Core.Attitude.Enabled, "attitude controller not enabled after EXEC");
        }

        private static IEnumerator SmartAssInternalInstant(Ctx c)
        {
            var sass = c.Module<MechJebModuleSmartASS>();
            int mark = c.Mark();
            yield return Physics(() => { sass.target = Target.RETROGRADE; sass.Engage(); });

            Check(sass.target == Target.RETROGRADE, $"SmartASS is {sass.target} right after an on-board Engage(RETROGRADE)");
            c.CheckLog("SCHED", mark, 0, "on-board call");
            c.CheckLogAtLeast("SmartASS Engage: internal call (physics step)", mark, 1, "on-board call");
            Check(c.QueuedCount == 0, "on-board call put a command in the delay queue");
        }

        // The bridge tracks the mode SmartASS holds every physics step, so a click can revert the
        // highlight to it. MechJeb only runs a module's own OnFixedUpdate while the module is
        // Enabled (for SmartASS: its window is open), so tracking must not depend on that.
        private static IEnumerator SmartAssTracksWindowClosed(Ctx c)
        {
            var sass = c.Module<MechJebModuleSmartASS>();
            bool wasOpen = sass.Enabled;
            sass.Enabled = false; // close the SmartASS window
            try
            {
                yield return Physics(() => { sass.target = Target.RETROGRADE; sass.Engage(); });
                yield return PhysicsTicks(3);

                int mark = c.Mark();
                sass.target = Target.PROGRADE; // e.g. a part action, with the window closed
                sass.Engage();
                c.CheckLog("SCHED PROGRADE ", mark, 1, "click");
                Check(sass.target == Target.RETROGRADE,
                      $"after the click SmartASS shows {sass.target}; expected the mode it holds (RETROGRADE)");
                yield return WaitUntil(() => c.Count("EXEC PROGRADE ", mark) > 0, "SmartASS EXEC");
                Check(sass.target == Target.PROGRADE, $"after EXEC SmartASS is {sass.target}, expected PROGRADE");
            }
            finally { sass.Enabled = wasOpen; }
        }

        // ---- no connection -----------------------------------------------------

        private static IEnumerator NoConnectionRefused(Ctx c)
        {
            var sass = c.Module<MechJebModuleSmartASS>();
            yield return c.SetBlackout(true);
            yield return PhysicsTicks(2);

            int mark = c.Mark();
            sass.target = Target.PROGRADE;
            sass.Engage();

            c.CheckLog("No connection", mark, 1, "SmartASS click without a link");
            c.CheckLog("SCHED", mark, 0, "SmartASS click without a link");
            Check(c.QueuedCount == 0, "a command was queued without a link");
            Check(sass.target == Target.OFF, $"SmartASS shows {sass.target} after a refused command");

            // The 2.15.x Rendezvous window would retry every frame: the refusal message is throttled.
            var ap = c.Core.GetComputerModule<MechJebModuleRendezvousAutopilot>();
            var w  = c.Core.GetComputerModule("MechJebModuleRendezvousAutopilotWindow") as DisplayModule;
            if (ap != null && w != null && !w.Enabled)
            {
                mark = c.Mark();
                for (int i = 0; i < 30; i++) { ap.Users.Add(w); yield return null; }
                Check(!ap.Enabled, "Rendezvous AP engaged without a link");
                c.CheckLog("Rendezvous AP engage not sent", mark, 1, "30 frames of Rendezvous engage without a link");
            }
        }

        // ---- node executor -----------------------------------------------------

        private static IEnumerator NodeClickDelayed(Ctx c)
        {
            yield return c.AddNode();
            int mark = c.Mark();
            double sent = UT;
            c.Core.Node.ExecuteOneNode(c);

            c.CheckLog("SCHED NodeExecutor ONE_NODE", mark, 1, "click");
            Check(c.QueuedCount == 1, $"expected 1 bridge command in the delay queue, got {c.QueuedCount}");
            Check(!c.Core.Node.Enabled, "node executor started before the command arrived");

            yield return WaitUntil(() => c.Count("NodeExecutor EXEC ONE_NODE", mark) > 0, "NodeExecutor EXEC");
            CheckDelayed(sent, "Execute one node");
            Check(c.Core.Node.Enabled, "node executor not running after EXEC");
        }

        private static IEnumerator NodeInternalInstant(Ctx c)
        {
            yield return c.AddNode();
            int mark = c.Mark();
            yield return Physics(() => c.Core.Node.ExecuteAllNodes(c));

            Check(c.Core.Node.Enabled, "node executor not running right after an on-board ExecuteAllNodes");
            c.CheckLog("SCHED", mark, 0, "on-board call");
            c.CheckLog("NodeExecutor ALL_NODES: internal call", mark, 1, "on-board call");
        }

        // ---- landing -----------------------------------------------------------

        private static IEnumerator LandingClickDelayed(Ctx c)
        {
            int mark = c.Mark();
            double sent = UT;
            c.Core.Landing.LandUntargeted(c);

            c.CheckLog("SCHED Landing UNTARGETED", mark, 1, "click");
            Check(c.QueuedCount == 1, $"expected 1 bridge command in the delay queue, got {c.QueuedCount}");
            Check(!c.Core.Landing.Enabled, "landing autopilot started before the command arrived");

            yield return WaitUntil(() => c.Count("Landing EXEC UNTARGETED", mark) > 0, "Landing EXEC");
            CheckDelayed(sent, "Land somewhere");
            Check(c.Core.Landing.Enabled, "landing autopilot not running after EXEC");
        }

        // ---- UserPool seam: Rendezvous -----------------------------------------

        private const string RdvWindow  = "MechJebModuleRendezvousAutopilotWindow";
        private const string DockWindow = "MechJebModuleDockingGuidance";

        // MechJeb 2.15.x windows re-issue the call every frame until Enabled changes, the way
        // these loops do. Only one command may go out.
        private static IEnumerator ClickEveryFrame(Func<bool> until, Action click, int maxFrames = 30)
        {
            for (int i = 0; i < maxFrames && !until(); i++)
            {
                click();
                yield return null;
            }
        }

        private static IEnumerator RendezvousEngageDelayed(Ctx c)
        {
            var ap = c.Module<MechJebModuleRendezvousAutopilot>();
            var w  = c.Window(RdvWindow, "Rendezvous Autopilot");
            Check(!ap.Enabled, "Rendezvous AP already engaged");

            int mark = c.Mark();
            double sent = UT;
            yield return ClickEveryFrame(() => ap.Enabled, () => ap.Users.Add(w));

            c.CheckLog("SCHED Rendezvous AP engage", mark, 1, "30 frames of engage clicks");
            Check(c.QueuedCount == 1, $"expected 1 bridge command in the delay queue, got {c.QueuedCount}");
            Check(!ap.Enabled, "Rendezvous AP engaged before the command arrived");

            yield return WaitUntil(() => ap.Enabled, "Rendezvous AP to engage");
            CheckDelayed(sent, "Rendezvous engage");
            c.CheckLog("EXEC MechJebModuleRendezvousAutopilot Add", mark, 1, "engage");
            Check(ap.Users.Contains(w), "window isn't a user of the Rendezvous AP after EXEC");
        }

        private static IEnumerator RendezvousRemoveDelayed(Ctx c)
        {
            var ap = c.Module<MechJebModuleRendezvousAutopilot>();
            var w  = c.Window(RdvWindow, "Rendezvous Autopilot");
            yield return Physics(() => ap.Users.Add(w));
            Check(ap.Enabled, "on-board engage didn't engage the Rendezvous AP");

            int mark = c.Mark();
            double sent = UT;
            yield return ClickEveryFrame(() => !ap.Enabled, () => ap.Users.Remove(w));

            c.CheckLog("SCHED Rendezvous AP disengage", mark, 1, "30 frames of disengage clicks");
            Check(ap.Enabled, "Rendezvous AP disengaged before the command arrived");

            yield return WaitUntil(() => !ap.Enabled, "Rendezvous AP to disengage");
            CheckDelayed(sent, "Rendezvous disengage");
            c.CheckLog("EXEC MechJebModuleRendezvousAutopilot Remove", mark, 1, "disengage");
        }

        private static IEnumerator RendezvousClearDelayed(Ctx c)
        {
            var ap = c.Module<MechJebModuleRendezvousAutopilot>();
            var w  = c.Window(RdvWindow, "Rendezvous Autopilot");
            yield return Physics(() => ap.Users.Add(w));

            int mark = c.Mark();
            double sent = UT;
            ap.Users.Clear(); // MechJeb dev's Disengage button

            c.CheckLog("SCHED Rendezvous AP disengage", mark, 1, "Clear click");
            Check(ap.Enabled, "Rendezvous AP disengaged before the command arrived");

            yield return WaitUntil(() => !ap.Enabled, "Rendezvous AP to disengage");
            CheckDelayed(sent, "Rendezvous disengage");
            c.CheckLog("EXEC MechJebModuleRendezvousAutopilot Clear", mark, 1, "disengage");
        }

        private static IEnumerator RendezvousInternalStopInstant(Ctx c)
        {
            var ap = c.Module<MechJebModuleRendezvousAutopilot>();
            var w  = c.Window(RdvWindow, "Rendezvous Autopilot");
            yield return Physics(() => ap.Users.Add(w));

            int mark = c.Mark();
            yield return Physics(() => ap.Users.Clear()); // what Drive does on arrival / no target

            Check(!ap.Enabled, "on-board Clear didn't disengage the Rendezvous AP at once");
            c.CheckLog("SCHED", mark, 0, "on-board stop");
            Check(c.QueuedCount == 0, "on-board stop put a command in the delay queue");
        }

        private static IEnumerator RendezvousNonWindowInstant(Ctx c)
        {
            var ap = c.Module<MechJebModuleRendezvousAutopilot>();
            int mark = c.Mark();
            ap.Users.Add(c.Core.Node); // another module, not a window (outside the physics step)

            Check(ap.Enabled, "Add from a non-window user didn't engage at once");
            c.CheckLog("SCHED", mark, 0, "non-window user");
            yield break;
        }

        // ---- UserPool seam: Docking --------------------------------------------

        private static IEnumerator DockingEngageDisengageDelayed(Ctx c)
        {
            var ap = c.Module<MechJebModuleDockingAutopilot>();
            var w  = c.Window(DockWindow, "Docking Autopilot");
            Check(!ap.Enabled, "Docking AP already engaged");

            int mark = c.Mark();
            double sent = UT;
            yield return ClickEveryFrame(() => ap.Enabled, () => ap.Users.Add(w));
            c.CheckLog("SCHED Docking AP engage", mark, 1, "30 frames of engage toggles");
            Check(!ap.Enabled, "Docking AP engaged before the command arrived");
            yield return WaitUntil(() => ap.Enabled, "Docking AP to engage");
            CheckDelayed(sent, "Docking engage");

            mark = c.Mark();
            sent = UT;
            yield return ClickEveryFrame(() => !ap.Enabled, () => ap.Users.Remove(w));
            c.CheckLog("SCHED Docking AP disengage", mark, 1, "30 frames of disengage toggles");
            Check(ap.Enabled, "Docking AP disengaged before the command arrived");
            yield return WaitUntil(() => !ap.Enabled, "Docking AP to disengage");
            CheckDelayed(sent, "Docking disengage");
        }

        private static IEnumerator DockingEndDockingInstant(Ctx c)
        {
            var ap = c.Module<MechJebModuleDockingAutopilot>();
            var w  = c.Window(DockWindow, "Docking Autopilot");
            MethodInfo end = AccessTools.Method(typeof(MechJebModuleDockingAutopilot), "EndDocking");
            if (end == null) Skip("no EndDocking in this MechJeb");
            yield return Physics(() => ap.Users.Add(w));

            int mark = c.Mark();
            end.Invoke(ap, null); // outside the physics step, like OnPartCouple

            Check(!ap.Enabled, "EndDocking didn't disengage at once");
            c.CheckLog("SCHED", mark, 0, "EndDocking");
        }

        private static IEnumerator DockingWindowCloseInstant(Ctx c)
        {
            var ap = c.Module<MechJebModuleDockingAutopilot>();
            var w  = c.Window(DockWindow, "Docking Autopilot");
            if (w.GetType().Name != DockWindow) Skip($"no {DockWindow} in this MechJeb (dev UI)");
            MethodInfo onDisabled = AccessTools.DeclaredMethod(w.GetType(), "OnModuleDisabled");
            if (onDisabled == null) Skip($"{w.GetType().Name} has no OnModuleDisabled in this MechJeb (dev UI)");
            yield return Physics(() => ap.Users.Add(w));

            int mark = c.Mark();
            onDisabled.Invoke(w, null); // what closing the 2.15.x window runs

            Check(!ap.Enabled, "closing the Docking window didn't disengage at once");
            c.CheckLog("SCHED", mark, 0, "window close");
        }

        // ---- UserPool seam: Ascent ---------------------------------------------

        private const string AscentWindow = "MechJebModuleAscentMenu";

        // The ascent window only acts on a button click, so it may be open.
        private static DisplayModule AscentMenu(Ctx c) => c.Window(AscentWindow, "Ascent Guidance", allowOpen: true);

        private static IEnumerator AscentEngageDelayed(Ctx c)
        {
            ComputerModule ap = c.Ascent();
            DisplayModule  w  = AscentMenu(c);
            Check(!ap.Enabled, "Ascent AP already engaged");

            int mark = c.Mark();
            double sent = UT;
            ap.Users.Add(w); // the Engage button

            c.CheckLog("SCHED Ascent AP engage", mark, 1, "click");
            Check(c.QueuedCount == 1, $"expected 1 bridge command in the delay queue, got {c.QueuedCount}");
            Check(!ap.Enabled, "Ascent AP engaged before the command arrived");

            yield return WaitUntil(() => ap.Enabled, "Ascent AP to engage");
            CheckDelayed(sent, "Ascent engage");
            c.CheckLog($"EXEC {ap.GetType().Name} Add", mark, 1, "engage");
            c.Note = ap.GetType().Name;
        }

        private static IEnumerator AscentDisengageDelayed(Ctx c)
        {
            ComputerModule ap = c.Ascent();
            DisplayModule  w  = AscentMenu(c);

            // Remove(window): the 2.15.x Disengage button and toggle action.
            yield return Physics(() => ap.Users.Add(w));
            int mark = c.Mark();
            double sent = UT;
            ap.Users.Remove(w);
            c.CheckLog("SCHED Ascent AP disengage", mark, 1, "Remove click");
            Check(ap.Enabled, "Ascent AP disengaged before the command arrived");
            yield return WaitUntil(() => !ap.Enabled, "Ascent AP to disengage (Remove)");
            CheckDelayed(sent, "Ascent disengage (Remove)");

            // Clear(): the dev Disengage button and toggle action.
            yield return Physics(() => ap.Users.Add(w));
            mark = c.Mark();
            sent = UT;
            ap.Users.Clear();
            c.CheckLog("SCHED Ascent AP disengage", mark, 1, "Clear click");
            Check(ap.Enabled, "Ascent AP disengaged before the command arrived");
            yield return WaitUntil(() => !ap.Enabled, "Ascent AP to disengage (Clear)");
            CheckDelayed(sent, "Ascent disengage (Clear)");
        }

        private static IEnumerator AscentInternalStopInstant(Ctx c)
        {
            ComputerModule ap = c.Ascent();
            DisplayModule  w  = AscentMenu(c);
            yield return Physics(() => ap.Users.Add(w));

            int mark = c.Mark();
            yield return Physics(() => ap.Users.Clear()); // what the autopilot does after circularising

            Check(!ap.Enabled, "on-board Clear didn't disengage the Ascent AP at once");
            c.CheckLog("SCHED", mark, 0, "on-board stop");
            Check(c.QueuedCount == 0, "on-board stop put a command in the delay queue");
        }

        // MechJebCore's "Ascent AP toggle" part action, clicked in the PAW (outside the physics
        // step). Bound to an action group it fires from RT's ActionGroupCommand.Pop instead,
        // which BridgeAddon wraps in Bypass; that path isn't covered here.
        private static IEnumerator AscentToggleActionDelayed(Ctx c)
        {
            ComputerModule ap = c.Ascent();
            AscentMenu(c);
            var param = new KSPActionParam(KSPActionGroup.None, KSPActionType.Activate);

            int mark = c.Mark();
            double sent = UT;
            c.Core.OnAscentAPToggleAction(param);
            c.CheckLog("SCHED Ascent AP engage", mark, 1, "toggle (engage)");
            Check(!ap.Enabled, "Ascent AP engaged before the command arrived");
            yield return WaitUntil(() => ap.Enabled, "Ascent AP to engage (toggle)");
            CheckDelayed(sent, "Ascent toggle (engage)");

            mark = c.Mark();
            sent = UT;
            c.Core.OnAscentAPToggleAction(param);
            c.CheckLog("SCHED Ascent AP disengage", mark, 1, "toggle (disengage)");
            Check(ap.Enabled, "Ascent AP disengaged before the command arrived");
            yield return WaitUntil(() => !ap.Enabled, "Ascent AP to disengage (toggle)");
            CheckDelayed(sent, "Ascent toggle (disengage)");
        }

        // ---- cancel ----------------------------------------------------------

        // The delay mod's own cancel: RemoteTech's queue window "x" (a CancelCommand, which ignores
        // the manual extra delay), or LightLag's Abort (a cancel that arrives after the signal
        // delay, before the command runs). Either way it overtakes the command and removes it
        // without calling the bridge. The bridge must notice the command is gone.
        private static IEnumerator CancelQueued(Ctx c, object cmd)
        {
            c.SetCommandDelay(TestDelay);
            // RT's queue is sorted by TimeStamp and silently drops a command whose TimeStamp
            // equals a queued one's, so the cancel must not be sent in the same frame (same game
            // time) as the command.
            yield return PhysicsTicks(3);
            int mark = c.Mark();
            Check(c.Mod.Cancel(c.Vessel, cmd), $"{c.Mod.Name} didn't accept the cancel");
            yield return WaitUntil(() => c.QueuedCount == 0, $"{c.Mod.Name} to cancel the command", TestDelay * 2 + 10);
            c.CheckLog("EXEC ", mark, 0, "the cancelled command must not arrive");
        }

        private static object SingleQueued(Ctx c)
        {
            List<object> q = c.Queued();
            Check(q.Count == 1, $"expected 1 bridge command in the {c.Mod.Name} queue, got {q.Count}");
            return q[0];
        }

        private static IEnumerator CancelThenResend(Ctx c)
        {
            var ap = c.Module<MechJebModuleRendezvousAutopilot>();
            var w  = c.Window(RdvWindow, "Rendezvous Autopilot");

            c.SetCommandDelay(TestDelay * 4); // long enough for the cancel to overtake it
            ap.Users.Add(w);
            yield return CancelQueued(c, SingleQueued(c));
            Check(!ap.Enabled, "Rendezvous AP engaged although its command was cancelled");

            int mark = c.Mark();
            ap.Users.Add(w);
            c.CheckLog("SCHED Rendezvous AP engage", mark, 1, "engage after a cancelled engage");
            c.CheckLog("queue (cancelled or dropped)", mark, 1, "engage after a cancelled engage");
            yield return WaitUntil(() => ap.Enabled, "Rendezvous AP to engage");
        }

        // After a cancelled SmartASS command, the bridge must go back to tracking the mode SmartASS
        // actually holds. Observable through the highlight revert on the next click: it reverts
        // to the tracked mode.
        private static IEnumerator CancelSmartAssTracking(Ctx c)
        {
            var sass = c.Module<MechJebModuleSmartASS>();
            yield return PhysicsTicks(2);

            c.SetCommandDelay(TestDelay * 4);
            sass.target = Target.PROGRADE;
            sass.Engage();
            yield return CancelQueued(c, SingleQueued(c));

            // An on-board mode change the bridge should now track.
            yield return Physics(() => { sass.target = Target.RETROGRADE; sass.Engage(); });
            yield return PhysicsTicks(3);

            sass.target = Target.NORMAL_PLUS;
            sass.Engage();
            Check(sass.target == Target.RETROGRADE,
                  $"after the click SmartASS shows {sass.target}; expected the mode it holds (RETROGRADE). " +
                  "The bridge stopped tracking after the cancelled command");
            yield return WaitUntil(() => sass.target == Target.NORMAL_PLUS, "SmartASS NORMAL_PLUS to arrive");
        }
    }
}
