using System;
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Features;
using ActuallyWorkingEmployees.Networking;
using Sideload.Api;
#if DEBUG
using ActuallyWorkingEmployees.Debugging;
#endif

namespace ActuallyWorkingEmployees.Host
{
    /// <summary>
    /// The settings phone app. A feature only so that it is found by <see cref="FeatureHost"/> and needs no line in
    /// <c>Core</c>: Init registers the app and the bridge calls, Tick pushes <c>options.changed</c> to the page when an
    /// option value, a host value or the network role changed. Emits happen here, never inside a bridge call, because a
    /// call runs on the script engine's stack.
    /// </summary>
    internal sealed class PhoneApp : IFeature
    {
        internal const string AppId = "awe";
        internal const string Bundle = "ActuallyWorkingEmployees.Assets.app";

        private static AppHandle _app;
        private static volatile bool _dirty;
        private static string _roleSig = "";
        private static float _sinceRoleCheck;
        private static bool _hooked;

        public string Name => "PhoneApp";

        public string Status => _app == null
            ? "not registered"
            : "registered, sideload " + (Apps.Available ? "present" : "absent") + ", page " + (_app.IsOpen ? "open" : "closed");

        public void Init()
        {
            try
            {
                _app = Apps.Register(AppId, Bundle, "Employees", "Employees");
                _app.Orientation("landscape", "portrait");
                foreach (var kv in Calls.Handlers)
                {
                    var h = kv;
                    _app.OnCall(h.Key, arg => Calls.Run(h.Key, arg, h.Value));
                }
                HookOptions();
                Core.Log?.Msg(Apps.Available
                    ? "[App] registered with Sideload as '" + AppId + "'."
                    : "[App] Sideload is not loaded yet, the registration is queued.");
            }
            catch (Exception e)
            {
                Core.Failed("app: register the phone app", e);
                return;
            }
#if DEBUG
            DevConsole.Register("app", "awe app <open|dump|diag|typing>", DevCommand);
#endif
        }

        public void Tick(float dt)
        {
            if (_app == null) return;

            _sinceRoleCheck += dt;
            if (_sinceRoleCheck >= 0.5f)
            {
                _sinceRoleCheck = 0f;
                string sig = Net.RoleText();
                if (sig != _roleSig) { _roleSig = sig; _dirty = true; }
            }

            if (!_dirty) return;
            _dirty = false;
            try { _app.Emit("options.changed", _roleSig); }
            catch (Exception e) { Core.Caught("app: emit options.changed", e); }
        }

        public void Reset() { _dirty = true; }

        /// <summary>Marks the page stale whenever any option changes, including the options other workers add later.</summary>
        private static void HookOptions()
        {
            if (_hooked) { Core.Log?.Warning("[App] option hooks already installed, skipped."); return; }
            _hooked = true;
            foreach (Option o in Options.All) o.Changed += _ => _dirty = true;
        }

#if DEBUG
        private static void DevCommand(string[] args)
        {
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (_app == null) { Core.Log?.Warning("[Dev] awe app: the app is not registered."); return; }
            if (sub == "open")
            {
                bool shown = _app.Show();
                if (shown) Core.Log?.Msg("[Dev] awe app open: phone raised, app '" + AppId + "' opened.");
                else Core.Log?.Warning("[Dev] awe app open: the game refused to raise the phone.");
            }
            else if (sub == "dump")
            {
                var state = Calls.BuildState();
                int groups = ((Newtonsoft.Json.Linq.JArray)state["groups"]).Count;
                int locked = 0;
                foreach (Option o in Options.All) if (!o.Editable) locked++;
                Core.Log?.Msg("[Dev] awe app dump: " + groups + " groups, " + Options.All.Count + " options rendered, "
                    + locked + " locked, role=" + state["role"] + ", " + state.ToString(Newtonsoft.Json.Formatting.None).Length + " chars.");
            }
            else if (sub == "diag")
            {
                var d = DiagCalls.Build();
                Core.Log?.Msg("[Dev] awe app diag: host=" + ((bool)d["host"] ? 1 : 0)
                    + " badges=" + ((Newtonsoft.Json.Linq.JArray)d["badges"]["items"]).Count + "/" + (int)d["badges"]["problems"]
                    + " doctor=" + ((bool)d["doctor"]["ran"] ? "ran" : "none") + ":" + (int)d["doctor"]["total"]
                    + " analytics=" + ((Newtonsoft.Json.Linq.JArray)d["analytics"]["items"]).Count + "/" + (int)d["analytics"]["samples"]
                    + " notice=" + (string.IsNullOrEmpty((string)Calls.BuildState()["notice"]) ? 0 : 1) + ".");
            }
            else if (sub == "typing")
            {
                // GameInput.HandleExitInputs returns on its first line while IsTyping is true, so ESC cannot close the
                // phone. A field that loses the caret the normal way sets it back to false.
                Core.Log?.Msg("[Dev] awe app typing: IsTyping=" + Il2CppScheduleOne.GameInput.IsTyping
                    + ", page " + (_app.IsOpen ? "open" : "closed") + ".");
            }
            else Core.Log?.Warning("[Dev] awe app <open|dump|diag|typing>");
        }
#endif
    }
}
