using System;
using System.Collections.Generic;
using MuMech;
using UnityEngine;

namespace MechJebRTBridge
{
    /// <summary>
    /// Shared plumbing for all command-delay seams. Each seam (SmartAssBridge, LandingBridge,
    /// etc.) is its own public static class (required by Harmony and by the delay mod's reflection),
    /// but calls into here for everything that isn't seam-specific.
    /// </summary>
    internal static class BridgeCore
    {
        // ---- bypass guard ---------------------------------------------------
        // Set to true only while a seam is re-invoking the real MechJeb method after the
        // signal delay, so the Harmony prefix lets that call through without re-queuing it.
        // [ThreadStatic] because Unity can call flight-control callbacks off the main thread
        // in some versions; the flag must not bleed across threads.
        // Also set while a player command that fans out into other commands runs (e.g. Translatron
        // PANIC calling SmartASS.Engage / StopLanding), so its sub-steps aren't delayed separately.
        [ThreadStatic] internal static bool Bypass;

        // ---- player-command detection ---------------------------------------
        // The patched methods are also called by MechJeb itself: running autopilots call
        // ExecuteAllNodes (Rendezvous), ExecuteOneNode (Ascent circularisation), SmartASS.Engage
        // (Hoverslam), LandUntargeted (Translatron PANIC); kOS scripts call them too. Those are
        // on-board actions and must not be delayed.
        //
        // They all run inside the physics step (ComputerModule.Drive / OnFixedUpdate, kOS's CPU
        // tick), while a player's click arrives from the UI: IMGUI OnGUI in MechJeb 2.15.x, uGUI
        // events in Update on MechJeb dev. So "inside FixedUpdate" = internal. This is more robust
        // than checking the `controller` argument, which is a DisplayModule for Translatron in
        // 2.15.4 and doesn't exist on SmartASS.Engage.
        //
        // A command the delay mod runs on board (an action group bound to a MechJeb action) also
        // isn't the player's: LightLag runs them in the physics step and says so
        // (DelayBackend.RunningCommand); RemoteTech's ActionGroupCommand.Pop is wrapped in Bypass.
        internal static bool IsPlayerCommand(string what)
        {
            if (Bypass) return false;
            if (DelayBackend.Current?.RunningCommand ?? false)
            {
                Debug.Log($"[MJ-RT Bridge] {what}: run by {DelayBackend.Current.Name}'s queue, not delayed");
                return false;
            }
            if (Time.inFixedTimeStep)
            {
                Debug.Log($"[MJ-RT Bridge] {what}: internal call (physics step), not delayed");
                return false;
            }
            return true;
        }

        // Harmony prefix/finalizer pair that sets Bypass for the duration of a wrapped method
        // (restoring the previous value, so it nests safely inside RtPop's own Bypass).
        internal static void BypassPrefix(out bool __state)
        {
            __state = Bypass;
            Bypass  = true;
        }

        internal static Exception BypassFinalizer(Exception __exception, bool __state)
        {
            Bypass = __state;
            return __exception;
        }

        // ---- delay helpers (through DelayBackend.Current) ---------------------

        // True when delay is a real, finite, sane value (the delay mod has a route and will
        // accept the command). Infinity/NaN/absurd values mean no connection.
        internal static bool IsConnected(double delay) =>
            !double.IsNaN(delay) && !double.IsInfinity(delay) && delay >= 0.0 && delay < 1.0e6;

        internal static double SafeDelay(Guid id)
        {
            try { return DelayBackend.Current?.Delay(id) ?? 0.0; }
            catch { return double.PositiveInfinity; }
        }

        // True when this command should be delayed: a delay mod is installed and delays commands
        // to this vessel (RemoteTech: enabled and no local control; LightLag: LightLagAPI.Applies).
        // Callers still need to check IsConnected(SafeDelay(...)) before queuing, because no
        // connection is a different condition (command refused, not just delayed).
        internal static bool NeedsDelay(Vessel vessel)
        {
            DelayBackend backend = DelayBackend.Current;
            return backend != null && backend.NeedsDelay(vessel);
        }

        /// <summary>Queues a seam's command with the delay mod; false if it refused.</summary>
        internal static bool Queue(Guid vesselId, string token, ConfigNode node) =>
            DelayBackend.Current?.Queue(vesselId, token, node) ?? false;

        /// <summary>
        /// True if the delay mod still holds the command with this token. The delay mod can drop
        /// one without telling the bridge (see InFlightSet). Errs towards true when it can't tell.
        /// </summary>
        internal static bool IsQueued(Guid vesselId, string token) =>
            DelayBackend.Current?.IsQueued(vesselId, token) ?? false;

        // ---- module resolution ---------------------------------------------
        // Each seam caches the MechJeb module instance in its Pending object. After a
        // quickload, KSP rebuilds the vessel, leaving the old instance alive but belonging
        // to the replaced vessel — Unity's null check doesn't catch that. This method
        // validates ownership and falls back to a fresh lookup if needed.
        //
        // Returns null if the module can't be found on the active vessel.
        // Sets fallbackReason to a short description when the fallback was needed, else null.
        internal static T ResolveModule<T>(T cached, out string fallbackReason)
            where T : ComputerModule
        {
            fallbackReason = null;
            if (cached != null && cached.Vessel == FlightGlobals.ActiveVessel)
                return cached;

            fallbackReason = cached == null ? "destroyed" : "wrong vessel";
            return FlightGlobals.ActiveVessel
                       ?.GetMasterMechJeb()
                       ?.GetComputerModule<T>();
        }

        // ---- ConfigNode builder --------------------------------------------
        // Constructs the standard MJRT command node header. Each seam calls this and then
        // appends its own payload values (the params needed to reconstruct after a reload).
        internal static ConfigNode BuildBaseNode(
            Guid vesselId, string token,
            string executor, Type popType, string popMethod,
            string queueLabel, string activeLabel, string shortLabel)
        {
            var node = new ConfigNode("MJRT");
            node.AddValue("GUIDString",            vesselId.ToString());
            node.AddValue("Executor",              executor);
            node.AddValue("ReflectionType",        popType.AssemblyQualifiedName);
            node.AddValue("ReflectionPopMethod",   popMethod);
            node.AddValue("QueueLabel",            queueLabel);
            node.AddValue("ActiveLabel",           activeLabel);
            node.AddValue("ShortLabel",            shortLabel);
            node.AddValue("Token",                 token);
            return node;
        }

        // ---- UI helpers ----------------------------------------------------

        internal static void Message(string msg)
        {
            try { ScreenMessages.PostScreenMessage(msg, 4f, ScreenMessageStyle.UPPER_CENTER); }
            catch (Exception e) { Debug.LogError("[MJ-RT Bridge] Message failed: " + e); }
        }

        // ---- ConfigNode utilities ------------------------------------------
        // RemoteTech wraps our node under an ExternalData/MJRT subnode before handing it
        // back (LightLag hands back the node itself), so search one level deep rather than
        // assuming the key is at the root.
        internal static string GetValueDeep(ConfigNode node, string key)
        {
            if (node == null) return null;
            if (node.HasValue(key)) return node.GetValue(key);
            foreach (ConfigNode child in node.GetNodes())
            {
                string v = GetValueDeep(child, key);
                if (v != null) return v;
            }
            return null;
        }

        // ---- debug helpers -------------------------------------------------

        internal static string Short(string token) =>
            string.IsNullOrEmpty(token) ? "(null)" : token.Substring(0, Math.Min(6, token.Length));

        internal static string StateAtExec()
        {
            try
            {
                Vessel v = FlightGlobals.ActiveVessel;
                double delayNow = v != null ? SafeDelay(v.id) : double.NaN;
                bool onRails = TimeWarp.WarpMode == TimeWarp.Modes.HIGH && TimeWarp.CurrentRate > 1f;
                return $"warp={TimeWarp.CurrentRate:F0}x({TimeWarp.WarpMode}) onRails={onRails} " +
                       $"connectedNow={IsConnected(delayNow)} delayed={v != null && NeedsDelay(v)} " +
                       $"delayNow={delayNow:F1}";
            }
            catch (Exception e) { return "state?(" + e.Message + ")"; }
        }
    }

    /// <summary>
    /// Commands a seam has put in the delay queue and not yet seen arrive, grouped by key (a module
    /// instance, or a module + action). Used to freeze the SmartASS highlight while a command is
    /// travelling and to de-duplicate the UserPool seam's per-frame re-clicks.
    ///
    /// An entry ends when RtPop calls <see cref="Remove"/>, or when the delay mod no longer holds
    /// the command. It can drop a queued command without calling the bridge: RemoteTech's queue
    /// window cancel ("x"), a command coming due while the vessel is unpowered, hibernation, or
    /// LightLag's Abort. So
    /// <see cref="Has"/> checks each entry against the live queue (<see cref="BridgeCore.IsQueued"/>)
    /// and forgets the ones the delay mod no longer has, calling <c>onDropped</c> with their tokens.
    /// </summary>
    internal sealed class InFlightSet<TKey>
    {
        private struct Entry
        {
            internal Guid VesselId;
            internal string Token;
        }

        private readonly Dictionary<TKey, List<Entry>> _dict = new Dictionary<TKey, List<Entry>>();
        private readonly Action<string> _onDropped;

        internal InFlightSet(Action<string> onDropped) { _onDropped = onDropped; }

        internal void Add(TKey key, Guid vesselId, string token)
        {
            if (!_dict.TryGetValue(key, out List<Entry> list)) _dict[key] = list = new List<Entry>();
            list.Add(new Entry { VesselId = vesselId, Token = token });
        }

        /// <summary>The command arrived (RtPop). Matches by token, under whatever key it was added.</summary>
        internal void Remove(string token)
        {
            if (token == null) return;
            TKey emptied = default;
            bool found = false;
            foreach (var kv in _dict)
            {
                if (kv.Value.RemoveAll(e => e.Token == token) == 0) continue;
                found = kv.Value.Count == 0;
                emptied = kv.Key;
                break;
            }
            if (found) _dict.Remove(emptied);
        }

        /// <summary>True if a command for <paramref name="key"/> is still in the delay mod's queue.</summary>
        internal bool Has(TKey key)
        {
            if (!_dict.TryGetValue(key, out List<Entry> list)) return false;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Entry e = list[i];
                if (BridgeCore.IsQueued(e.VesselId, e.Token)) continue;
                list.RemoveAt(i);
                Debug.Log($"[MJ-RT Bridge] command token={BridgeCore.Short(e.Token)} is no longer in the {DelayBackend.Current?.Name} queue " +
                          "(cancelled or dropped); forgetting it");
                _onDropped?.Invoke(e.Token);
            }
            if (list.Count > 0) return true;
            _dict.Remove(key);
            return false;
        }
    }
}
