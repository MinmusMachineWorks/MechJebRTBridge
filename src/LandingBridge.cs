using System;
using System.Collections.Generic;
using MuMech;
using UnityEngine;

namespace MechJebRTBridge
{
    /// <summary>
    /// Route MechJeb's landing autopilot commands through the signal-delay queue (RemoteTech or LightLag, see <see cref="DelayBackend"/>).
    ///
    /// Patched from <see cref="BridgeAddon"/>:
    ///   - LandAtPositionTargetPrefix → prefix on MechJebModuleLandingAutopilot.LandAtPositionTarget
    ///   - LandUntargetedPrefix       → prefix on MechJebModuleLandingAutopilot.LandUntargeted
    /// StopLanding is deliberately not patched (see below).
    /// </summary>
    public static class LandingBridge
    {
        private const string Executor = "MechJeb (Landing Autopilot)";

        // StopLanding() is not patched: MechJeb calls it internally at touchdown, so
        // intercepting it would queue a Stop after the vessel has already landed.
        private enum LandMode { AT_TARGET, UNTARGETED }

        private sealed class Pending
        {
            internal MechJebModuleLandingAutopilot Instance;
            internal LandMode Mode;
        }

        private static readonly Dictionary<string, Pending> _pending =
            new Dictionary<string, Pending>();

        // ---- Harmony hooks -------------------------------------------------

        internal static bool LandAtPositionTargetPrefix(MechJebModuleLandingAutopilot __instance, object controller)
            => OnLand(__instance, LandMode.AT_TARGET);

        internal static bool LandUntargetedPrefix(MechJebModuleLandingAutopilot __instance, object controller)
            => OnLand(__instance, LandMode.UNTARGETED);

        // ---- command interception ------------------------------------------

        private static bool OnLand(MechJebModuleLandingAutopilot instance, LandMode mode)
        {
            try
            {
                if (!BridgeCore.IsPlayerCommand($"Landing {mode}")) return true;

                Vessel vessel = FlightGlobals.ActiveVessel;
                if (vessel == null || !BridgeCore.NeedsDelay(vessel)) return true;

                double delay = BridgeCore.SafeDelay(vessel.id);
                if (!BridgeCore.IsConnected(delay))
                {
                    BridgeCore.Message($"Landing AP: {ModeLabel(mode)} — no connection, command not sent");
                    Debug.Log($"[MJ-RT Bridge] No connection (delay={delay}); Landing '{mode}' not sent.");
                    return false;
                }

                string token = Guid.NewGuid().ToString("N");
                _pending[token] = new Pending { Instance = instance, Mode = mode };

                if (BridgeCore.Queue(vessel.id, token, BuildCommandNode(vessel.id, token, mode)))
                {
                    if (delay >= 0.1)
                        BridgeCore.Message($"Landing AP: {ModeLabel(mode)} queued · signal delay {delay:F1}s");
                    Debug.Log($"[MJ-RT Bridge] SCHED Landing {mode} delay={delay:F2}s token={BridgeCore.Short(token)}");
                    return false;
                }

                _pending.Remove(token);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[MJ-RT Bridge] Landing OnLand error, running locally: " + e);
                return true;
            }
        }

        // ---- arrival callback (the delay mod calls it by reflection) -------

        public static bool RtPop(ConfigNode data)
        {
            try
            {
                string token   = BridgeCore.GetValueDeep(data, "Token");
                string modeStr = BridgeCore.GetValueDeep(data, "LAMode");

                MechJebModuleLandingAutopilot landing;
                LandMode mode;

                if (token != null && _pending.TryGetValue(token, out Pending p))
                {
                    _pending.Remove(token);
                    mode    = p.Mode;
                    landing = BridgeCore.ResolveModule(p.Instance, out string reason);
                    if (reason != null && landing != null)
                        Debug.Log($"[MJ-RT Bridge] Landing EXEC using fresh instance ({reason}) mode={mode} token={BridgeCore.Short(token)}");
                }
                else
                {
                    if (modeStr == null)
                    {
                        Debug.LogWarning($"[MJ-RT Bridge] Landing EXEC ?? token={BridgeCore.Short(token)} — no fallback data; skipping");
                        return false;
                    }
                    mode    = (LandMode)Enum.Parse(typeof(LandMode), modeStr);
                    landing = BridgeCore.ResolveModule<MechJebModuleLandingAutopilot>(null, out _);
                }

                if (landing == null)
                {
                    Debug.LogWarning($"[MJ-RT Bridge] Landing EXEC ?? {mode} token={BridgeCore.Short(token)} — no LandingAutopilot on active vessel; skipping");
                    return false;
                }

                BridgeCore.Bypass = true;
                try
                {
                    switch (mode)
                    {
                        case LandMode.AT_TARGET:
                            landing.LandAtPositionTarget(typeof(LandingBridge));
                            break;
                        case LandMode.UNTARGETED:
                            landing.LandUntargeted(typeof(LandingBridge));
                            break;
                    }
                }
                finally { BridgeCore.Bypass = false; }

                Debug.Log($"[MJ-RT Bridge] Landing EXEC {mode} token={BridgeCore.Short(token)} | {BridgeCore.StateAtExec()}");
            }
            catch (Exception e)
            {
                Debug.LogError("[MJ-RT Bridge] LandingBridge.RtPop error: " + e);
            }
            return false;
        }

        // ---- helpers -------------------------------------------------------

        private static ConfigNode BuildCommandNode(Guid vesselId, string token, LandMode mode)
        {
            ConfigNode node = BridgeCore.BuildBaseNode(
                vesselId, token, Executor,
                popType:     typeof(LandingBridge),
                popMethod:   nameof(RtPop),
                queueLabel:  $"Landing → {ModeLabel(mode)}",
                activeLabel: $"Landing: {ModeLabel(mode)}",
                shortLabel:  "Landing");
            node.AddValue("LAMode", mode.ToString());
            return node;
        }

        private static string ModeLabel(LandMode mode)
        {
            switch (mode)
            {
                case LandMode.AT_TARGET:  return "Land at target";
                case LandMode.UNTARGETED: return "Land somewhere";
                default:                  return mode.ToString();
            }
        }
    }
}
