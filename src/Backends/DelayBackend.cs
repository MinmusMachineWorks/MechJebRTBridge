using System;
using UnityEngine;

namespace MechJebRTBridge
{
    /// <summary>
    /// The signal-delay mod the bridge sends MechJeb's commands through: RemoteTech or
    /// LightLag. Neither is referenced at build time; each backend finds its mod's API by
    /// reflection, so the bridge loads with either one installed (they conflict, so never both).
    ///
    /// Both take the same command node (RemoteTech's <c>QueueCommandToFlightComputer</c> keys)
    /// and call the seam's <c>public static bool RtPop(ConfigNode)</c> when it arrives.
    /// </summary>
    internal abstract class DelayBackend
    {
        /// <summary>"RemoteTech" or "LightLag", for logs and messages.</summary>
        internal abstract string Name { get; }

        /// <summary>Commands to the vessel are delayed (or refused with no signal).</summary>
        internal abstract bool NeedsDelay(Vessel vessel);

        /// <summary>Seconds a command takes to arrive; +Infinity (or NaN) with no connection.</summary>
        internal abstract double Delay(Guid vesselId);

        /// <summary>Queues the command; false if the mod refused it.</summary>
        internal abstract bool Queue(Guid vesselId, string token, ConfigNode node);

        /// <summary>The command with this token is still on its way. Errs towards true when it can't tell.</summary>
        internal abstract bool IsQueued(Guid vesselId, string token);

        /// <summary>The mod is running one of its queued commands on board now: calls made then aren't the player's.</summary>
        internal virtual bool RunningCommand => false;

        /// <summary>Has the vessel keep flying <paramref name="pilot"/>'s attitude without a link, if the mod would otherwise wipe it.</summary>
        internal virtual void HoldAttitude(Guid vesselId, Action<FlightCtrlState> pilot) { }

        /// <summary>The installed delay mod's backend, or null with neither installed.</summary>
        internal static DelayBackend Current
        {
            get
            {
                if (_resolved) return _current;
                _resolved = true;
                _current = RemoteTechBackend.TryCreate() ?? (DelayBackend)LightLagBackend.TryCreate();
                Debug.Log(_current != null
                    ? $"[MJ-RT Bridge] delay backend: {_current.Name}"
                    : "[MJ-RT Bridge] neither RemoteTech nor LightLag found: MechJeb commands aren't delayed");
                return _current;
            }
        }

        private static bool _resolved;
        private static DelayBackend _current;
    }
}
