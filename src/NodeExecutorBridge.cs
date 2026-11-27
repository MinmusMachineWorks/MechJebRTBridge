using System;
using System.Collections.Generic;
using MuMech;
using UnityEngine;

namespace MechJebRTBridge
{
    /// <summary>
    /// Route MechJeb's node executor commands through the signal-delay queue (RemoteTech or LightLag, see <see cref="DelayBackend"/>).
    ///
    /// Patched from <see cref="BridgeAddon"/>:
    ///   - ExecuteOneNodePrefix  → prefix on MechJebModuleNodeExecutor.ExecuteOneNode
    ///   - ExecuteAllNodesPrefix → prefix on MechJebModuleNodeExecutor.ExecuteAllNodes
    /// Abort is deliberately not patched (see below).
    /// </summary>
    public static class NodeExecutorBridge
    {
        private const string Executor = "MechJeb (Node Executor)";

        // Abort() is not patched: MechJeb calls it internally when nodes are consumed or
        // deleted, and blocking those calls would leave the executor stuck in a running state.
        // Per §8 of the design doc, instant local abort is the intended emergency cutoff.
        private enum ExecMode { ONE_NODE, ALL_NODES }

        private sealed class Pending
        {
            internal MechJebModuleNodeExecutor Instance;
            internal ExecMode Mode;
        }

        private static readonly Dictionary<string, Pending> _pending =
            new Dictionary<string, Pending>();

        // ---- Harmony hooks -------------------------------------------------

        internal static bool ExecuteOneNodePrefix(MechJebModuleNodeExecutor __instance, object controller)
            => OnExecute(__instance, ExecMode.ONE_NODE);

        internal static bool ExecuteAllNodesPrefix(MechJebModuleNodeExecutor __instance, object controller)
            => OnExecute(__instance, ExecMode.ALL_NODES);

        // ---- command interception ------------------------------------------

        private static bool OnExecute(MechJebModuleNodeExecutor instance, ExecMode mode)
        {
            try
            {
                if (!BridgeCore.IsPlayerCommand($"NodeExecutor {mode}")) return true;

                Vessel vessel = FlightGlobals.ActiveVessel;
                if (vessel == null || !BridgeCore.NeedsDelay(vessel)) return true;

                double delay = BridgeCore.SafeDelay(vessel.id);
                if (!BridgeCore.IsConnected(delay))
                {
                    BridgeCore.Message($"Node Executor: {ModeLabel(mode)} — no connection, command not sent");
                    Debug.Log($"[MJ-RT Bridge] No connection (delay={delay}); NodeExecutor '{mode}' not sent.");
                    return false;
                }

                string token = Guid.NewGuid().ToString("N");
                _pending[token] = new Pending { Instance = instance, Mode = mode };

                if (BridgeCore.Queue(vessel.id, token, BuildCommandNode(vessel.id, token, mode)))
                {
                    if (delay >= 0.1)
                        BridgeCore.Message($"Node Executor: {ModeLabel(mode)} queued · signal delay {delay:F1}s");
                    Debug.Log($"[MJ-RT Bridge] SCHED NodeExecutor {mode} delay={delay:F2}s token={BridgeCore.Short(token)}");
                    return false;
                }

                _pending.Remove(token);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[MJ-RT Bridge] NodeExecutor OnExecute error, running locally: " + e);
                return true;
            }
        }

        // ---- arrival callback (the delay mod calls it by reflection) -------

        public static bool RtPop(ConfigNode data)
        {
            try
            {
                string token   = BridgeCore.GetValueDeep(data, "Token");
                string modeStr = BridgeCore.GetValueDeep(data, "NEMode");

                MechJebModuleNodeExecutor executor;
                ExecMode mode;

                if (token != null && _pending.TryGetValue(token, out Pending p))
                {
                    _pending.Remove(token);
                    mode     = p.Mode;
                    executor = BridgeCore.ResolveModule(p.Instance, out string reason);
                    if (reason != null && executor != null)
                        Debug.Log($"[MJ-RT Bridge] NodeExecutor EXEC using fresh instance ({reason}) mode={mode} token={BridgeCore.Short(token)}");
                }
                else
                {
                    if (modeStr == null)
                    {
                        Debug.LogWarning($"[MJ-RT Bridge] NodeExecutor EXEC ?? token={BridgeCore.Short(token)} — no fallback data; skipping");
                        return false;
                    }
                    mode     = (ExecMode)Enum.Parse(typeof(ExecMode), modeStr);
                    executor = BridgeCore.ResolveModule<MechJebModuleNodeExecutor>(null, out _);
                }

                if (executor == null)
                {
                    Debug.LogWarning($"[MJ-RT Bridge] NodeExecutor EXEC ?? {mode} token={BridgeCore.Short(token)} — no NodeExecutor on active vessel; skipping");
                    return false;
                }

                // Guard: Init() will throw if there are no maneuver nodes.
                if (!executor.Vessel.patchedConicsUnlocked() ||
                    executor.Vessel.patchedConicSolver.maneuverNodes.Count == 0)
                {
                    Debug.LogWarning($"[MJ-RT Bridge] NodeExecutor EXEC {mode} token={BridgeCore.Short(token)} — no maneuver nodes present; skipping");
                    BridgeCore.Message("Node Executor: no maneuver nodes when command arrived");
                    return false;
                }

                BridgeCore.Bypass = true;
                try
                {
                    switch (mode)
                    {
                        case ExecMode.ONE_NODE:
                            executor.ExecuteOneNode(typeof(NodeExecutorBridge));
                            break;
                        case ExecMode.ALL_NODES:
                            executor.ExecuteAllNodes(typeof(NodeExecutorBridge));
                            break;
                    }
                }
                finally { BridgeCore.Bypass = false; }

                Debug.Log($"[MJ-RT Bridge] NodeExecutor EXEC {mode} token={BridgeCore.Short(token)} | {BridgeCore.StateAtExec()}");
            }
            catch (Exception e)
            {
                Debug.LogError("[MJ-RT Bridge] NodeExecutorBridge.RtPop error: " + e);
            }
            return false;
        }

        // ---- helpers -------------------------------------------------------

        private static ConfigNode BuildCommandNode(Guid vesselId, string token, ExecMode mode)
        {
            ConfigNode node = BridgeCore.BuildBaseNode(
                vesselId, token, Executor,
                popType:     typeof(NodeExecutorBridge),
                popMethod:   nameof(RtPop),
                queueLabel:  $"Node Executor → {ModeLabel(mode)}",
                activeLabel: $"Node Executor: {ModeLabel(mode)}",
                shortLabel:  "NodeExec");
            node.AddValue("NEMode", mode.ToString());
            return node;
        }

        private static string ModeLabel(ExecMode mode)
        {
            switch (mode)
            {
                case ExecMode.ONE_NODE:  return "Execute One Node";
                case ExecMode.ALL_NODES: return "Execute All Nodes";
                default:                 return mode.ToString();
            }
        }
    }
}
