using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace MechJebRTBridge.Tests
{
    /// <summary>
    /// The delay mod the harness drives: RemoteTech or LightLag, whichever is installed, both
    /// by reflection, so the harness builds and loads with either. It covers what the tests need
    /// from the mod: its queue (the bridge's commands in it), an extra delay so commands take a
    /// few seconds in LKO, cancelling a queued command, and (RemoteTech only) cutting the link.
    /// </summary>
    internal abstract class DelayMod
    {
        internal abstract string Name { get; }

        /// <summary>Null if the vessel is ready for the tests, else why not.</summary>
        internal abstract string NotReady(Vessel v);

        internal abstract bool Connected(Vessel v);
        internal abstract double Delay(Vessel v);

        /// <summary>The bridge's commands in the vessel's queue (opaque handles).</summary>
        internal abstract List<object> Queued(Vessel v);

        internal abstract string Label(object command);

        /// <summary>The vessel's extra delay setting, to restore later.</summary>
        internal abstract double ExtraDelaySetting(Vessel v);
        internal abstract void SetExtraDelaySetting(Vessel v, double value);

        /// <summary>The extra delay setting that makes new commands take about <paramref name="seconds"/>.</summary>
        internal abstract double ExtraDelayFor(Vessel v, double seconds);

        /// <summary>Sends a cancel for <paramref name="command"/>, the way the mod's queue window does. False if it refused.</summary>
        internal abstract bool Cancel(Vessel v, object command);

        internal abstract void Remove(Vessel v, object command);

        /// <summary>Null if <see cref="SetBlackout"/> works, else why not.</summary>
        internal virtual string NoBlackout => $"{Name} can't cut the link on demand";
        internal virtual void SetBlackout(Vessel v, bool on) { }

        internal static DelayMod Find()
        {
            Type rt = AccessTools.TypeByName("RemoteTech.API.API");
            if (rt != null) return new RemoteTechMod(rt);
            Type ll = AccessTools.TypeByName("LightLag.LightLagAPI");
            return ll != null ? new LightLagMod(ll) : null;
        }

        protected static bool IsBridgeCommand(ConfigNode node) =>
            (node?.GetValue("ReflectionType") ?? "").Contains("MechJebRTBridge");

        protected static object Call(Type type, string method, params object[] args) =>
            AccessTools.Method(type, method).Invoke(null, args);
    }

    internal sealed class RemoteTechMod : DelayMod
    {
        private readonly Type _api;
        private readonly Type _external = AccessTools.TypeByName("RemoteTech.FlightComputer.Commands.ExternalAPICommand");
        private readonly Type _cancel = AccessTools.TypeByName("RemoteTech.FlightComputer.Commands.CancelCommand");

        internal RemoteTechMod(Type api) { _api = api; }

        internal override string Name => "RemoteTech";

        private object Fc(Vessel v)
        {
            object core = AccessTools.Property(AccessTools.TypeByName("RemoteTech.RTCore"), "Instance").GetValue(null);
            if (core == null) return null;
            object sats = Traverse.Create(core).Property("Satellites").GetValue();
            object sat = sats?.GetType().GetProperty("Item", new[] { typeof(Guid) })?.GetValue(sats, new object[] { v.id });
            return sat == null ? null : Traverse.Create(sat).Property("FlightComputer").GetValue();
        }

        internal override string NotReady(Vessel v)
        {
            if (!(bool)Call(_api, "IsRemoteTechEnabled")) return "RemoteTech is disabled (RemoteTech settings → enable)";
            if ((bool)Call(_api, "HasLocalControl", v.id))
                return "active vessel has local control (crew or a command station): the bridge never delays it. Use an uncrewed probe";
            if (!Connected(v)) return "active vessel has no RemoteTech connection";
            if (Fc(v) == null) return "no RemoteTech flight computer on the active vessel";
            return null;
        }

        internal override double Delay(Vessel v) => (double)Call(_api, "GetShortestSignalDelay", v.id);

        internal override bool Connected(Vessel v)
        {
            double d = Delay(v);
            return !double.IsNaN(d) && !double.IsInfinity(d) && d >= 0 && d < 1e6;
        }

        internal override List<object> Queued(Vessel v)
        {
            var commands = Traverse.Create(Fc(v)).Property("QueuedCommands").GetValue() as IEnumerable;
            if (commands == null) return new List<object>();
            return commands.Cast<object>()
                .Where(c => _external.IsInstanceOfType(c)
                            && (Traverse.Create(c).Field("ReflectionType").GetValue<string>() ?? "").Contains("MechJebRTBridge"))
                .ToList();
        }

        internal override string Label(object command) => Traverse.Create(command).Property("ShortName").GetValue<string>();

        internal override double ExtraDelaySetting(Vessel v) => Traverse.Create(Fc(v)).Property("TotalDelay").GetValue<double>();
        internal override void SetExtraDelaySetting(Vessel v, double value) => Traverse.Create(Fc(v)).Property("TotalDelay").SetValue(value);

        // RT's manual delay is TotalDelay, "Delay (+ signal)": the extra is TotalDelay − signal delay.
        internal override double ExtraDelayFor(Vessel v, double seconds) =>
            Math.Max(Traverse.Create(Fc(v)).Property("Delay").GetValue<double>(), 0) + seconds;

        // The queue window's "x" sends a CancelCommand, which ignores the manual extra delay.
        internal override bool Cancel(Vessel v, object command)
        {
            object fc = Fc(v);
            object cancel = AccessTools.Method(_cancel, "WithCommand").Invoke(null, new[] { command });
            AccessTools.Method(fc.GetType(), "Enqueue").Invoke(fc, new[] { cancel, false, false, false });
            var commands = Traverse.Create(fc).Property("QueuedCommands").GetValue() as IEnumerable;
            return commands != null && commands.Cast<object>().Any(_cancel.IsInstanceOfType);
        }

        internal override void Remove(Vessel v, object command)
        {
            object fc = Fc(v);
            AccessTools.Method(fc.GetType(), "Remove").Invoke(fc, new[] { command });
        }

        internal override string NoBlackout => null;
        internal override void SetBlackout(Vessel v, bool on) =>
            Call(_api, "SetRadioBlackoutGuid", v.id, on, "MechJebRTBridge tests");
    }

    internal sealed class LightLagMod : DelayMod
    {
        private readonly Type _api;
        private readonly Type _vessel = AccessTools.TypeByName("LightLag.LightLagVessel");

        internal LightLagMod(Type api) { _api = api; }

        internal override string Name => "LightLag";

        private object Module(Vessel v) => AccessTools.Method(_vessel, "For").Invoke(null, new object[] { v });

        private IEnumerable<object> Commands(Vessel v)
        {
            object queue = Traverse.Create(Module(v)).Property("Queue").GetValue();
            return (Traverse.Create(queue).Property("Commands").GetValue() as IEnumerable)?.Cast<object>() ?? Enumerable.Empty<object>();
        }

        internal override string NotReady(Vessel v)
        {
            if (!(bool)Call(_api, "IsEnabled")) return "LightLag's light-speed delay is off (Difficulty → Advanced), or CommNet is off";
            if ((bool)Call(_api, "HasLocalControl", v.id))
                return "active vessel has local control (crew): the bridge never delays it. Use an uncrewed probe";
            if (!Connected(v)) return "active vessel has no CommNet connection";
            if (Module(v) == null) return "no LightLagVessel module on the active vessel";
            return null;
        }

        internal override bool Connected(Vessel v) => (bool)Call(_api, "IsConnected", v.id);
        internal override double Delay(Vessel v) => (double)Call(_api, "GetDelay", v.id);

        internal override List<object> Queued(Vessel v) =>
            Commands(v).Where(c => c.GetType().Name == "ExternalCommand"
                                   && IsBridgeCommand(Traverse.Create(c).Field("Data").GetValue<ConfigNode>())).ToList();

        internal override string Label(object command) => Traverse.Create(command).Property("Label").GetValue<string>();

        internal override double ExtraDelaySetting(Vessel v) => Traverse.Create(Module(v)).Field("ExtraDelay").GetValue<double>();
        internal override void SetExtraDelaySetting(Vessel v, double value) => Traverse.Create(Module(v)).Field("ExtraDelay").SetValue(value);

        // LightLag runs a command at the later of its arrival and sent + extra delay.
        internal override double ExtraDelayFor(Vessel v, double seconds) => seconds;

        // The flight computer's Abort: a cancel that reaches the vessel after the signal delay.
        internal override bool Cancel(Vessel v, object command)
        {
            Traverse t = Traverse.Create(command);
            double now = Planetarium.GetUniversalTime(), delay = Delay(v);
            if (double.IsInfinity(delay) || now + delay >= t.Field("ExecuteUT").GetValue<double>()) return false;
            t.Field("CancelArrivalUT").SetValue(now + delay);
            return true;
        }

        internal override void Remove(Vessel v, object command)
        {
            object queue = Traverse.Create(Module(v)).Property("Queue").GetValue();
            AccessTools.Method(queue.GetType(), "Remove").Invoke(queue, new[] { command });
        }
    }
}
