using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace MechJebRTBridge
{
    /// <summary>
    /// LightLag through <c>LightLag.LightLagAPI</c>, by reflection. LightLag returns an id for
    /// each queued command; the backend keeps token → id so the seams can ask by token.
    ///
    /// LightLag doesn't wipe the controls without a link (an autopilot's output runs on board),
    /// so there's no attitude hold to add. Its queued commands run in the physics step with
    /// <c>IsRunningCommand</c> true, so an action group bound to a MechJeb action isn't delayed twice.
    /// </summary>
    internal sealed class LightLagBackend : DelayBackend
    {
        private const int MinApiVersion = 1;

        private Func<Guid, bool> _applies;
        private Func<Guid, bool> _connected;
        private Func<Guid, double> _delay;
        private Func<Guid, ConfigNode, Guid> _queue;
        private Func<Guid, Guid, bool> _isQueued;
        private Func<bool> _running;
        private readonly Dictionary<string, Guid> _ids = new Dictionary<string, Guid>();

        internal override string Name => "LightLag";

        internal static LightLagBackend TryCreate()
        {
            Type api = AccessTools.TypeByName("LightLag.LightLagAPI");
            if (api == null) return null;
            try
            {
                int version = (int)AccessTools.Field(api, "ApiVersion").GetRawConstantValue();
                if (version < MinApiVersion)
                {
                    Debug.LogError($"[MJ-RT Bridge] LightLag's API is version {version}; the bridge needs {MinApiVersion} or later");
                    return null;
                }
                return new LightLagBackend
                {
                    _applies = Make<Func<Guid, bool>>(api, "Applies", typeof(Guid)),
                    _connected = Make<Func<Guid, bool>>(api, "IsConnected", typeof(Guid)),
                    _delay = Make<Func<Guid, double>>(api, "GetDelay", typeof(Guid)),
                    _queue = Make<Func<Guid, ConfigNode, Guid>>(api, "QueueCommand", typeof(Guid), typeof(ConfigNode)),
                    _isQueued = Make<Func<Guid, Guid, bool>>(api, "IsQueued", typeof(Guid), typeof(Guid)),
                    _running = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>),
                        AccessTools.PropertyGetter(api, "IsRunningCommand") ?? throw new MissingMemberException(api.FullName, "IsRunningCommand")),
                };
            }
            catch (Exception e)
            {
                Debug.LogError("[MJ-RT Bridge] LightLag is installed but its API doesn't match: " + e.Message);
                return null;
            }
        }

        private static T Make<T>(Type type, string name, params Type[] parameters) where T : Delegate
        {
            var method = AccessTools.Method(type, name, parameters) ?? throw new MissingMethodException(type.FullName, name);
            return (T)Delegate.CreateDelegate(typeof(T), method);
        }

        internal override bool NeedsDelay(Vessel vessel) => _applies(vessel.id);

        internal override double Delay(Guid vesselId)
        {
            try { return _connected(vesselId) ? _delay(vesselId) : double.PositiveInfinity; }
            catch { return double.PositiveInfinity; }
        }

        internal override bool Queue(Guid vesselId, string token, ConfigNode node)
        {
            Guid id = _queue(vesselId, node);
            if (id == Guid.Empty) return false;
            _ids[token] = id;
            return true;
        }

        internal override bool IsQueued(Guid vesselId, string token)
        {
            if (token == null || !_ids.TryGetValue(token, out Guid id)) return false; // ran or was never queued
            if (_isQueued(vesselId, id)) return true;
            _ids.Remove(token);
            return false;
        }

        internal override bool RunningCommand => _running();
    }
}
