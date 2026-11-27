using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MuMech;
using UnityEngine;

namespace MechJebRTBridge.Tests
{
    /// <summary>
    /// Shared fixture and helpers for one test run on the active vessel.
    ///
    /// Delay: the vessel's real signal delay is usually milliseconds (LKO), too short to tell a
    /// delayed command from an instant one. Setup raises the delay mod's manual extra delay
    /// (RemoteTech's "Delay (+ signal)" field, LightLag's flight computer extra delay) so every
    /// queued command takes about <see cref="TestDelay"/> seconds. Teardown restores it. The
    /// delay mod is RemoteTech or LightLag (<see cref="DelayMod"/>).
    /// </summary>
    internal sealed class Ctx
    {
        internal const double TestDelay = 3.0;

        internal Vessel Vessel;
        internal MechJebCore Core;
        internal DelayMod Mod;
        internal string Note; // optional detail shown next to a passing test

        private double _savedTotalDelay;
        private bool _delayChanged;
        private bool _blackout;
        private readonly List<ManeuverNode> _nodes = new List<ManeuverNode>();

        // ---- bridge log capture -------------------------------------------

        private readonly List<string> _log = new List<string>();

        internal void StartLogCapture()  => Application.logMessageReceived += OnLog;
        internal void StopLogCapture()   => Application.logMessageReceived -= OnLog;

        private void OnLog(string msg, string stack, LogType type)
        {
            if (msg != null && msg.Contains("[MJ-RT Bridge]")) _log.Add(msg);
        }

        /// <summary>Position in the captured log; pass to <see cref="Count"/>.</summary>
        internal int Mark() => _log.Count;

        /// <summary>Bridge log lines since <paramref name="mark"/> containing <paramref name="text"/>.</summary>
        internal int Count(string text, int mark) =>
            _log.Skip(mark).Count(l => l.Contains(text));

        internal string LogSince(int mark) =>
            string.Join(" | ", _log.Skip(mark).Select(l => l.Replace("[MJ-RT Bridge] ", "")).ToArray());

        // ---- assertions ----------------------------------------------------

        internal static void Check(bool condition, string message)
        {
            if (!condition) throw new TestFailure(message);
        }

        internal void CheckLog(string text, int mark, int expected, string what)
        {
            int n = Count(text, mark);
            if (n != expected)
                throw new TestFailure($"{what}: expected {expected}× \"{text}\" in the bridge log, got {n}. Log: {LogSince(mark)}");
        }

        internal void CheckLogAtLeast(string text, int mark, int min, string what)
        {
            int n = Count(text, mark);
            if (n < min)
                throw new TestFailure($"{what}: expected at least {min}× \"{text}\" in the bridge log, got {n}. Log: {LogSince(mark)}");
        }

        internal static void Skip(string reason) => throw new TestSkipped(reason);

        // ---- timing --------------------------------------------------------

        internal static double UT => Planetarium.GetUniversalTime();

        internal static IEnumerator Frames(int n)
        {
            for (int i = 0; i < n; i++) yield return null;
        }

        internal static PhysicsStep Physics(Action a) => new PhysicsStep(a);

        /// <summary>Waits for <paramref name="n"/> physics steps (frames can outnumber them).</summary>
        internal static IEnumerator PhysicsTicks(int n)
        {
            for (int i = 0; i < n; i++) yield return Physics(() => { });
        }

        /// <summary>Waits (real time) until <paramref name="cond"/> holds, or fails.</summary>
        internal static IEnumerator WaitUntil(Func<bool> cond, string what, double timeout = TestDelay * 3 + 10)
        {
            float end = Time.unscaledTime + (float)timeout;
            while (!cond())
            {
                if (Time.unscaledTime > end) throw new TestFailure($"timed out after {timeout:F0}s waiting for {what}");
                yield return null;
            }
        }

        /// <summary>Fails unless a command took roughly the test delay to arrive.</summary>
        internal static void CheckDelayed(double sentUT, string what)
        {
            double took = UT - sentUT;
            Check(took >= TestDelay - 0.5, $"{what} arrived after {took:F2}s; expected ≈{TestDelay:F0}s (signal delay)");
        }

        // ---- the delay mod ------------------------------------------------

        /// <summary>Bridge commands currently in the delay mod's queue.</summary>
        internal List<object> Queued() => Mod.Queued(Vessel);

        internal int QueuedCount => Queued().Count;

        /// <summary>Sets the manual extra delay so new commands take <paramref name="seconds"/>.</summary>
        internal void SetCommandDelay(double seconds)
        {
            if (!_delayChanged) { _savedTotalDelay = Mod.ExtraDelaySetting(Vessel); _delayChanged = true; }
            Mod.SetExtraDelaySetting(Vessel, Mod.ExtraDelayFor(Vessel, seconds));
        }

        internal IEnumerator SetBlackout(bool on)
        {
            if (Mod.NoBlackout != null) Skip(Mod.NoBlackout);
            Mod.SetBlackout(Vessel, on);
            _blackout = on;
            // The delay mod recomputes the network periodically, not instantly.
            float end = Time.unscaledTime + 15f;
            while (Mod.Connected(Vessel) == on)
            {
                if (Time.unscaledTime > end)
                {
                    if (on) Skip($"{Mod.Name} didn't drop the link under radio blackout within 15s");
                    throw new TestFailure("link didn't come back within 15s after lifting radio blackout");
                }
                yield return null;
            }
        }

        // ---- MechJeb -------------------------------------------------------

        internal T Module<T>() where T : ComputerModule
        {
            T m = Core.GetComputerModule<T>();
            Check(m != null, $"{typeof(T).Name} not found on the vessel's MechJeb");
            return m;
        }

        /// <summary>
        /// A MechJeb window to act as the "user" of a click, the way MechJeb's own windows pass
        /// <c>this</c>. Prefers the named window; skips the test if that window is open, because
        /// its own OnGUI would fight the test over the autopilot's state.
        /// </summary>
        internal DisplayModule Window(string typeName, string title, bool allowOpen = false)
        {
            if (Core.GetComputerModule(typeName) is DisplayModule w)
            {
                if (w.Enabled && !allowOpen) Skip($"close MechJeb's {title} window first");
                return w;
            }
            DisplayModule any = Core.GetComputerModules<DisplayModule>().OfType<DisplayModule>()
                                    .FirstOrDefault(d => !d.Enabled);
            Check(any != null, "no closed MechJeb window to act as the click's user");
            Note = $"{typeName} not in this MechJeb; using {any.GetType().Name} as the window";
            return any;
        }

        /// <summary>
        /// The selected ascent autopilot (Classic / PVG / PSG). <c>Core.Ascent</c> is a class on
        /// MechJeb 2.15.x and an interface on dev, so it's read by reflection.
        /// </summary>
        internal ComputerModule Ascent()
        {
            var m = Traverse.Create(Core).Property("Ascent").GetValue() as ComputerModule;
            Check(m != null, "no ascent autopilot on the vessel's MechJeb");
            return m;
        }

        internal IEnumerator AddNode()
        {
            if (!Vessel.patchedConicsUnlocked() || Vessel.patchedConicSolver == null)
                Skip("maneuver nodes not unlocked (upgrade the Tracking Station / Mission Control)");
            ManeuverNode n = Vessel.patchedConicSolver.AddManeuverNode(UT + 1800);
            n.DeltaV = new Vector3d(0, 0, 1); // 1 m/s prograde, half an hour out
            Vessel.patchedConicSolver.UpdateFlightPlan();
            _nodes.Add(n);
            yield return null;
        }

        // ---- fixture ---------------------------------------------------------

        internal IEnumerator Setup()
        {
            Mod = DelayMod.Find();
            Check(Mod != null, "neither RemoteTech nor LightLag is installed");
            Vessel = FlightGlobals.ActiveVessel;
            Check(Vessel != null, "no active vessel");
            Check(Vessel.situation == Vessel.Situations.ORBITING,
                  $"put the test probe in a stable orbit (situation is {Vessel.situation})");
            Core = Vessel.GetMasterMechJeb();
            Check(Core != null, "active vessel has no MechJeb");
            string notReady = Mod.NotReady(Vessel);
            Check(notReady == null, notReady);
            Check(TimeWarp.CurrentRate <= 1f, "stop time warp first");
            Check(QueuedCount == 0, $"bridge commands are already in the {Mod.Name} queue; let them finish first");
            Check(Mod.Delay(Vessel) < TestDelay,
                  $"signal delay is already ≥ {TestDelay}s; the tests assume a short real delay. Use a probe near Kerbin");

            SetCommandDelay(TestDelay);
            AutopilotStubs.Install();
            AutopilotStubs.Active = true;
            yield return StopEverything();
        }

        internal IEnumerator Teardown()
        {
            try { yield return Cleanup(); }
            finally { RestoreSync(); }
        }

        /// <summary>Synchronous restore, also called if the runner is destroyed mid-run.</summary>
        internal void RestoreSync()
        {
            AutopilotStubs.Active = false;
            if (_blackout && Vessel != null)
            {
                Mod.SetBlackout(Vessel, false);
                _blackout = false;
            }
            if (_delayChanged && Mod != null && Vessel != null)
            {
                Mod.SetExtraDelaySetting(Vessel, _savedTotalDelay);
                _delayChanged = false;
            }
        }

        /// <summary>After each test: let queued commands drain, then stop everything locally.</summary>
        internal IEnumerator Cleanup()
        {
            Note = null;
            if (_blackout) yield return SetBlackout(false);
            SetCommandDelay(TestDelay);

            // Let in-flight commands arrive, so the next test starts from a settled state.
            float end = Time.unscaledTime + (float)(TestDelay * 2 + 10);
            while (QueuedCount > 0 && Time.unscaledTime < end) yield return null;
            foreach (object c in Queued())
            {
                Debug.LogWarning($"[MJ-RT Test] cleanup: removing a stuck bridge command from the {Mod.Name} queue: " + Mod.Label(c));
                Mod.Remove(Vessel, c);
            }

            yield return StopEverything();
        }

        // Physics-step stops are on-board calls, so the bridge runs them instantly.
        private IEnumerator StopEverything()
        {
            yield return Physics(() =>
            {
                MechJebModuleSmartASS sass = Core.GetComputerModule<MechJebModuleSmartASS>();
                if (sass != null && sass.target != MechJebModuleSmartASS.Target.OFF)
                {
                    sass.target = MechJebModuleSmartASS.Target.OFF;
                    sass.Engage();
                }
                if (Core.Node.Enabled) Core.Node.Abort();
                if (Core.Landing.Enabled) Core.Landing.StopLanding();
                Core.GetComputerModule<MechJebModuleRendezvousAutopilot>()?.Users.Clear();
                Core.GetComputerModule<MechJebModuleDockingAutopilot>()?.Users.Clear();
                (Traverse.Create(Core).Property("Ascent").GetValue() as ComputerModule)?.Users.Clear();
            });

            foreach (ManeuverNode n in _nodes)
                if (n != null && n.solver != null && n.solver.maneuverNodes.Contains(n)) n.RemoveSelf();
            _nodes.Clear();
            yield return Frames(2);
        }
    }
}
