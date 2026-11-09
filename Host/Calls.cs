using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Networking;

namespace ActuallyWorkingEmployees.Host
{
    /// <summary>
    /// Every <c>s1.call</c> the settings page makes. Arguments are a JSON object or nothing; answers are JSON with
    /// <c>ok</c>, and <c>msg</c> when it failed. The option list is read from <see cref="Options"/> on each call, so
    /// options added or removed elsewhere show up without a change here. Values go through <c>Option.TrySet</c>, the
    /// same path as <c>awe set</c>, and are never saved explicitly.
    /// </summary>
    internal static class Calls
    {
        internal const string HostMsg = "Set by the host.";

        internal static readonly Dictionary<string, Func<JObject, JObject>> Handlers = new Dictionary<string, Func<JObject, JObject>>(StringComparer.Ordinal)
        {
            ["list"] = _ => BuildState(),
            ["role"] = _ => Role(),
            ["set"] = Set,
            ["reset"] = Reset,
            ["resetAll"] = _ => ResetAll(),
            ["preset"] = Preset,
            ["diag"] = DiagCalls.Diag,
            ["doctorRun"] = DiagCalls.DoctorRun,
            ["analyticsReset"] = DiagCalls.AnalyticsReset,
        };

        internal static string Run(string name, string arg, Func<JObject, JObject> handler)
        {
            try
            {
                JObject a = string.IsNullOrWhiteSpace(arg) ? new JObject() : JObject.Parse(arg);
                return (handler(a) ?? Fail("Something went wrong.")).ToString(Formatting.None);
            }
            catch (Exception e)
            {
                Core.Failed("app: call " + name, e);
                Core.Log?.Error("[App] " + name + "(" + arg + ") failed: " + e.Message);
                return Fail("Something went wrong.").ToString(Formatting.None);
            }
        }

        internal static JObject Fail(string msg) => new JObject { ["ok"] = false, ["msg"] = msg };

        internal static JObject BuildState()
        {
            var groups = new JArray();
            var byGroup = new Dictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);
            foreach (string g in Options.Groups)
            {
                var arr = new JArray();
                byGroup[g] = arr;
                groups.Add(new JObject { ["name"] = g, ["options"] = arr });
            }
            foreach (Option o in Options.All)
            {
                // The header switch of the app, not a row.
                if (o == Options.AllowOutOfRange) continue;
                if (!byGroup.TryGetValue(o.Group, out JArray arr))
                {
                    Core.WarnOnce("app: group " + o.Group, "option " + o.Key + " is in a group the app does not list, skipped.");
                    continue;
                }
                arr.Add(Describe(o));
            }
            JObject state = Role();
            state["groups"] = groups;
            state["notice"] = DiagCalls.Notice();
            state["hints"] = DiagCalls.Hints();
            return state;
        }

        private static JObject Role()
        {
            return new JObject
            {
                ["ok"] = true,
                ["role"] = Net.RoleText(),
                ["locked"] = Net.IsClientOnly,
                ["vanilla"] = Options.VanillaForced,
                ["unlimited"] = Options.Unlimited,
            };
        }

        private static JObject Describe(Option o)
        {
            string value = o.HostValueText ?? o.ValueText;
            string text = o.HostValueText != null || o.Editable ? o.DisplayText : o.Describe(o.ValueText);
            var j = new JObject
            {
                ["key"] = o.Key,
                ["label"] = o.Label,
                ["help"] = o.Help,
                ["kind"] = o.Kind.ToString().ToLowerInvariant(),
                ["value"] = value,
                ["text"] = text,
                ["def"] = o.DefaultText,
                ["defText"] = o.Describe(o.DefaultText),
                ["isDefault"] = o.IsDefault,
                ["editable"] = o.Editable,
                // A client whose host values have not arrived yet: locked, but there is no host value to show.
                ["waiting"] = !o.Editable && o.HostValueText == null,
                ["hostOnly"] = o.HostOnly,
                ["balance"] = o.Balance,
                ["outside"] = o.IsOutside(value),
            };
            switch (o)
            {
                case IntOption i:
                    j["min"] = i.Min; j["max"] = i.Max; j["step"] = i.Step; j["sentinel"] = i.VanillaSentinel;
                    break;
                case FloatOption f:
                    j["min"] = Math.Round(f.Min, 3); j["max"] = Math.Round(f.Max, 3); j["step"] = Math.Round(f.Step, 3); j["sentinel"] = f.VanillaSentinel;
                    break;
                case ChoiceOption c:
                    j["choices"] = new JArray(c.Choices);
                    break;
            }
            return j;
        }

        private static JObject Set(JObject a)
        {
            Option o = Options.Find((string)a["key"]);
            if (o == null) { Core.Log?.Warning("[App] set: no option '" + (string)a["key"] + "'."); return Fail("Unknown setting."); }
            if (!o.Editable) { Core.Log?.Warning("[App] set " + o.Key + " refused: the host controls it."); return Fail(HostMsg); }
            string raw = (string)a["value"] ?? "";
            if (!o.TrySet(raw, out string error))
            {
                Core.Log?.Warning("[App] set " + o.Key + " = '" + raw + "' rejected: " + error);
                return Fail(error);
            }
            Core.Log?.Msg("[App] " + o.Key + " = " + o.ValueText + ".");
            return new JObject { ["ok"] = true, ["option"] = Describe(o) };
        }

        private static JObject Reset(JObject a)
        {
            Option o = Options.Find((string)a["key"]);
            if (o == null) { Core.Log?.Warning("[App] reset: no option '" + (string)a["key"] + "'."); return Fail("Unknown setting."); }
            if (!o.Editable) { Core.Log?.Warning("[App] reset " + o.Key + " refused: the host controls it."); return Fail(HostMsg); }
            o.ResetToDefault();
            Core.Log?.Msg("[App] " + o.Key + " reset to " + o.ValueText + ".");
            return new JObject { ["ok"] = true, ["option"] = Describe(o) };
        }

        private static JObject ResetAll()
        {
            // On a client this resets the local options only: ResetAllToDefault skips every option the host controls.
            int n = Options.ResetAllToDefault();
            Core.Log?.Msg("[App] reset all (" + Net.RoleText() + "): " + n + " setting(s) back to default.");
            var state = BuildState();
            state["changed"] = n;
            return state;
        }

        private static JObject Preset(JObject a)
        {
            string name = (string)a["name"];
            Presets.Result r = Presets.Apply(name);
            if (r.Refused != null)
            {
                Core.Log?.Warning("[App] preset " + name + " refused: " + r.Refused);
                return Fail(Net.IsClientOnly ? HostMsg : "Something went wrong.");
            }
            if (r.Errors.Count > 0) return Fail("Something went wrong.");
            Core.Log?.Msg("[App] preset " + name + " (" + Net.RoleText() + "): " + r.Changed + " of " + r.Total + " setting(s) changed.");
            var state = BuildState();
            state["changed"] = r.Changed;
            return state;
        }
    }
}
