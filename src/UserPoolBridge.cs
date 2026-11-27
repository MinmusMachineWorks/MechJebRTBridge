using System;
using System.Collections.Generic;
using HarmonyLib;
using MuMech;
using UnityEngine;

namespace MechJebRTBridge
{
    /// <summary>
    /// Generic seam: delays engaging/disengaging MechJeb autopilots that are switched on through
    /// their <c>Users</c> pool rather than a dedicated start method: Rendezvous, Docking, Ascent.
    ///
    /// Most MechJeb autopilots run while their <see cref="UserPool"/> is non-empty:
    /// <c>Users.Add(user)</c> sets <c>Enabled = true</c>, and <c>Users.Remove(user)</c> (when it
    /// empties the pool) or <c>Users.Clear()</c> sets it false. The windows' Engage/Disengage
    /// buttons are just those calls, e.g. <c>Autopilot.Users.Add(window)</c>. Patching
    /// <c>UserPool.Add/Remove/Clear</c> once covers every such autopilot. The pool is used for
    /// internal hand-offs all the time, so a call is delayed only when:
    ///   - its module is on the <see cref="Delayed"/> allowlist,
    ///   - it actually changes the module's Enabled state (engage when off, disengage when on),
    ///   - it's a player command (<see cref="BridgeCore.IsPlayerCommand"/>: not physics step, not
    ///     inside a Bypass-wrapped internal stop path),
    ///   - for Add/Remove, the user is a MechJeb window (<see cref="DisplayModule"/>), not another
    ///     autopilot or a kOS binding.
    /// <c>Clear()</c> has no user. Internal Clears for allowlisted modules all run in the physics
    /// step or inside stop methods that BridgeAddon wraps in Bypass.
    ///
    /// MechJeb 2.15.x windows re-issue Add/Remove from OnGUI every frame until Enabled changes, so
    /// an identical command still in the delay queue is swallowed rather than queued again.
    ///
    /// Patched from <see cref="BridgeAddon"/>:
    ///   - AddPrefix    → prefix on UserPool.Add(object)
    ///   - RemovePrefix → prefix on UserPool.Remove(object)
    ///   - ClearPrefix  → prefix on UserPool.Clear()
    /// </summary>
    public static class UserPoolBridge
    {
        // Modules whose engage/disengage is delayed, by type name (a module matches if it or a base
        // class has one of these names). Names, not typeof: the ascent classes differ between
        // MechJeb versions, and CI builds against 2.15.x. Extend this as more autopilots are verified.
        private static readonly Dictionary<string, string> Delayed = new Dictionary<string, string>
        {
            { "MechJebModuleRendezvousAutopilot",    "Rendezvous AP" },
            { "MechJebModuleDockingAutopilot",       "Docking AP" },
            { "MechJebModuleAscentBaseAutopilot",    "Ascent AP" }, // 2.15.x: base of Classic and PVG
            { "MechJebModuleAscentClassicAutopilot", "Ascent AP" }, // dev
            { "MechJebModulePSGAutopilot",           "Ascent AP" }, // dev
        };

        private static readonly Dictionary<Type, string> _labelCache = new Dictionary<Type, string>();

        // The allowlist label for a module type, or null if it isn't delayed.
        private static string Label(Type t)
        {
            if (_labelCache.TryGetValue(t, out string label)) return label;
            for (Type b = t; b != null && label == null; b = b.BaseType)
                Delayed.TryGetValue(b.Name, out label);
            _labelCache[t] = label;
            return label;
        }

        private enum PoolAction { Add, Remove, Clear }

        private sealed class Pending
        {
            internal ComputerModule Module;
            internal object User;
            internal PoolAction Action;
        }

        private static readonly AccessTools.FieldRef<UserPool, ComputerModule> ControlledModule =
            AccessTools.FieldRefAccess<UserPool, ComputerModule>("_controlledModule");

        private static readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>();

        // (module, action) pairs with a command in the delay queue; see the 2.15.x OnGUI note above.
        // A command the delay mod drops without running it (e.g. cancelled in the queue window) stops
        // counting, so the next click goes out again.
        private static readonly InFlightSet<(ComputerModule, PoolAction)> _inFlight =
            new InFlightSet<(ComputerModule, PoolAction)>(token => _pending.Remove(token));

        // Throttles the "no connection" message, which 2.15.x OnGUI would otherwise post every frame.
        private static readonly Dictionary<(ComputerModule, PoolAction), float> _lastRefusal =
            new Dictionary<(ComputerModule, PoolAction), float>();

        // ---- Harmony hooks -------------------------------------------------

        internal static bool AddPrefix(UserPool __instance, object user)
        {
            ComputerModule m = ControlledModule(__instance);
            if (m == null || Label(m.GetType()) == null || m.Enabled || user == null) return true;
            return Intercept(m, user, PoolAction.Add);
        }

        internal static bool RemovePrefix(UserPool __instance, object user)
        {
            ComputerModule m = ControlledModule(__instance);
            if (m == null || Label(m.GetType()) == null || !m.Enabled) return true;
            // Only a Remove that empties the pool disengages the module.
            if (user == null || !__instance.Contains(user) || __instance.Count > 1) return true;
            return Intercept(m, user, PoolAction.Remove);
        }

        internal static bool ClearPrefix(UserPool __instance)
        {
            ComputerModule m = ControlledModule(__instance);
            if (m == null || Label(m.GetType()) == null || !m.Enabled) return true;
            return Intercept(m, null, PoolAction.Clear);
        }

        // ---- command interception ------------------------------------------

        private static bool Intercept(ComputerModule module, object user, PoolAction action)
        {
            try
            {
                string label = Label(module.GetType());
                string verb  = action == PoolAction.Add ? "engage" : "disengage";

                // Add/Remove from another autopilot or a kOS binding is on-board, not a click.
                if (action != PoolAction.Clear && !(user is DisplayModule)) return true;
                if (!BridgeCore.IsPlayerCommand($"{label} {verb}")) return true;

                Vessel vessel = FlightGlobals.ActiveVessel;
                if (vessel == null || module.Vessel != vessel || !BridgeCore.NeedsDelay(vessel)) return true;

                var key = (module, action);
                if (_inFlight.Has(key)) return false; // already on its way

                double delay = BridgeCore.SafeDelay(vessel.id);
                if (!BridgeCore.IsConnected(delay))
                {
                    if (!_lastRefusal.TryGetValue(key, out float t) || Time.unscaledTime - t > 4f)
                    {
                        _lastRefusal[key] = Time.unscaledTime;
                        BridgeCore.Message($"{label}: {verb} — no connection, command not sent");
                        Debug.Log($"[MJ-RT Bridge] No connection (delay={delay}); {label} {verb} not sent.");
                    }
                    return false;
                }

                string token = Guid.NewGuid().ToString("N");
                _pending[token] = new Pending { Module = module, User = user, Action = action };

                if (BridgeCore.Queue(vessel.id, token, BuildCommandNode(vessel.id, token, module, user, action, label, verb)))
                {
                    _inFlight.Add(key, vessel.id, token);
                    if (delay >= 0.1)
                        BridgeCore.Message($"{label}: {verb} queued · signal delay {delay:F1}s");
                    Debug.Log($"[MJ-RT Bridge] SCHED {label} {verb} delay={delay:F2}s token={BridgeCore.Short(token)}");
                    return false;
                }

                _pending.Remove(token);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[MJ-RT Bridge] UserPool intercept error, running locally: " + e);
                return true;
            }
        }

        // ---- arrival callback (the delay mod calls it by reflection) -------

        public static bool RtPop(ConfigNode data)
        {
            try
            {
                string token = BridgeCore.GetValueDeep(data, "Token");

                ComputerModule module = null;
                object user = null;
                PoolAction action;
                string moduleType = BridgeCore.GetValueDeep(data, "UPModule");
                string userType   = BridgeCore.GetValueDeep(data, "UPUser");

                _inFlight.Remove(token);
                if (token != null && _pending.TryGetValue(token, out Pending p))
                {
                    _pending.Remove(token);
                    action = p.Action;
                    if (p.Module != null && p.Module.Vessel == FlightGlobals.ActiveVessel)
                    {
                        module = p.Module;
                        user   = p.User;
                    }
                }
                else
                {
                    string actionStr = BridgeCore.GetValueDeep(data, "UPAction");
                    if (actionStr == null || moduleType == null)
                    {
                        Debug.LogWarning($"[MJ-RT Bridge] UserPool EXEC ?? token={BridgeCore.Short(token)} — no fallback data; skipping");
                        return false;
                    }
                    action = (PoolAction)Enum.Parse(typeof(PoolAction), actionStr);
                }

                // Live references gone (reload / vessel rebuilt): resolve by type name.
                if (module == null)
                {
                    MechJebCore core = FlightGlobals.ActiveVessel?.GetMasterMechJeb();
                    module = moduleType != null ? core?.GetComputerModule(moduleType) : null;
                    if (action != PoolAction.Clear && !string.IsNullOrEmpty(userType))
                        user = core?.GetComputerModule(userType);
                }

                if (module == null || (action != PoolAction.Clear && user == null))
                {
                    Debug.LogWarning($"[MJ-RT Bridge] UserPool EXEC ?? {moduleType} {action} token={BridgeCore.Short(token)} — module/user not found on active vessel; skipping");
                    return false;
                }

                BridgeCore.Bypass = true;
                try
                {
                    switch (action)
                    {
                        case PoolAction.Add:    module.Users.Add(user);    break;
                        case PoolAction.Remove: module.Users.Remove(user); break;
                        case PoolAction.Clear:  module.Users.Clear();      break;
                    }
                }
                finally { BridgeCore.Bypass = false; }

                Debug.Log($"[MJ-RT Bridge] EXEC {module.GetType().Name} {action} token={BridgeCore.Short(token)}" +
                          $" enabled={module.Enabled} | {BridgeCore.StateAtExec()}");
            }
            catch (Exception e)
            {
                Debug.LogError("[MJ-RT Bridge] UserPoolBridge.RtPop error: " + e);
            }
            return false;
        }

        // ---- helpers -------------------------------------------------------

        private static ConfigNode BuildCommandNode(Guid vesselId, string token, ComputerModule module,
                                                   object user, PoolAction action, string label, string verb)
        {
            ConfigNode node = BridgeCore.BuildBaseNode(
                vesselId, token, $"MechJeb ({label})",
                popType:     typeof(UserPoolBridge),
                popMethod:   nameof(RtPop),
                queueLabel:  $"{label} → {verb}",
                activeLabel: $"{label}: {verb}",
                shortLabel:  label);
            // Persisted payload: enough to resolve module and user by type name after a reload.
            node.AddValue("UPModule", module.GetType().Name);
            node.AddValue("UPUser",   user?.GetType().Name ?? "");
            node.AddValue("UPAction", action.ToString());
            return node;
        }
    }
}
