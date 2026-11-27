using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MechJebRTBridge
{
    /// <summary>
    /// RemoteTech through <c>RemoteTech.API.API</c>, by reflection. The queue check reads
    /// RemoteTech's internals (RTCore → satellite → flight computer → queued
    /// <c>ExternalAPICommand</c>s and their private <c>externalData</c>), since the API has no call for it.
    /// </summary>
    internal sealed class RemoteTechBackend : DelayBackend
    {
        private Func<bool> _enabled;
        private Func<Guid, bool> _localControl;
        private Func<Guid, double> _delay;
        private Func<ConfigNode, bool> _queue;
        private Action<Guid, Action<FlightCtrlState>> _addPilot;

        // Queue inspection; any of it missing means "can't tell".
        private PropertyInfo _coreInstance, _satellites, _satelliteById, _flightComputer, _queuedCommands;
        private Type _externalCommand;
        private FieldInfo _externalData;
        private bool _inspectionBroken;

        internal override string Name => "RemoteTech";

        internal static RemoteTechBackend TryCreate()
        {
            Type api = AccessTools.TypeByName("RemoteTech.API.API");
            if (api == null) return null;
            try
            {
                var backend = new RemoteTechBackend
                {
                    _enabled = Make<Func<bool>>(api, "IsRemoteTechEnabled"),
                    _localControl = Make<Func<Guid, bool>>(api, "HasLocalControl", typeof(Guid)),
                    _delay = Make<Func<Guid, double>>(api, "GetShortestSignalDelay", typeof(Guid)),
                    _queue = Make<Func<ConfigNode, bool>>(api, "QueueCommandToFlightComputer", typeof(ConfigNode)),
                    _addPilot = Make<Action<Guid, Action<FlightCtrlState>>>(api, "AddSanctionedPilot", typeof(Guid), typeof(Action<FlightCtrlState>)),
                };
                backend.FindQueueInternals();
                return backend;
            }
            catch (Exception e)
            {
                Debug.LogError("[MJ-RT Bridge] RemoteTech is installed but its API doesn't match: " + e.Message);
                return null;
            }
        }

        private static T Make<T>(Type type, string name, params Type[] parameters) where T : Delegate
        {
            MethodInfo method = AccessTools.Method(type, name, parameters)
                                ?? throw new MissingMethodException(type.FullName, name);
            return (T)Delegate.CreateDelegate(typeof(T), method);
        }

        private void FindQueueInternals()
        {
            Type core = AccessTools.TypeByName("RemoteTech.RTCore");
            Type manager = AccessTools.TypeByName("RemoteTech.SatelliteManager");
            Type satellite = AccessTools.TypeByName("RemoteTech.VesselSatellite");
            Type computer = AccessTools.TypeByName("RemoteTech.FlightComputer.FlightComputer");
            _externalCommand = AccessTools.TypeByName("RemoteTech.FlightComputer.Commands.ExternalAPICommand");
            _coreInstance = core == null ? null : AccessTools.Property(core, "Instance");
            _satellites = core == null ? null : AccessTools.Property(core, "Satellites");
            _satelliteById = manager?.GetProperty("Item", new[] { typeof(Guid) });
            _flightComputer = satellite == null ? null : AccessTools.Property(satellite, "FlightComputer");
            _queuedCommands = computer == null ? null : AccessTools.Property(computer, "QueuedCommands");
            _externalData = _externalCommand == null ? null : AccessTools.Field(_externalCommand, "externalData");
            if (_coreInstance == null || _satellites == null || _satelliteById == null || _flightComputer == null
                || _queuedCommands == null || _externalData == null)
            {
                _inspectionBroken = true;
                Debug.LogError("[MJ-RT Bridge] can't inspect the RemoteTech queue; a command RT cancels or " +
                               "drops will keep blocking its button");
            }
        }

        internal override bool NeedsDelay(Vessel vessel) => _enabled() && !_localControl(vessel.id);

        internal override double Delay(Guid vesselId)
        {
            try { return _delay(vesselId); }
            catch { return double.PositiveInfinity; }
        }

        internal override bool Queue(Guid vesselId, string token, ConfigNode node) => _queue(node);

        internal override bool IsQueued(Guid vesselId, string token)
        {
            if (_inspectionBroken) return true;
            try
            {
                object core = _coreInstance.GetValue(null);
                if (core == null) return true;
                object satellite = _satelliteById.GetValue(_satellites.GetValue(core), new object[] { vesselId });
                if (satellite == null) return false; // vessel gone
                if (!(_queuedCommands.GetValue(_flightComputer.GetValue(satellite)) is IEnumerable commands)) return true;
                foreach (object command in commands)
                    if (_externalCommand.IsInstanceOfType(command)
                        && BridgeCore.GetValueDeep(_externalData.GetValue(command) as ConfigNode, "Token") == token)
                        return true;
                return false;
            }
            catch (Exception)
            {
                return true; // satellite has no flight computer / signal processor yet: can't tell
            }
        }

        // RemoteTech neutralizes the controls of a vessel without a link, after MechJeb's Drive;
        // a sanctioned pilot runs after that (RT deduplicates repeated adds).
        internal override void HoldAttitude(Guid vesselId, Action<FlightCtrlState> pilot)
        {
            try
            {
                if (!_enabled() || _localControl(vesselId)) return;
                _addPilot(vesselId, pilot);
            }
            catch (Exception e) { Debug.LogError("[MJ-RT Bridge] AddSanctionedPilot failed: " + e); }
        }
    }
}
