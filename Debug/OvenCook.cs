#if DEBUG
using System;
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Features;
using ActuallyWorkingEmployees.Networking;
using ActuallyWorkingEmployees.Features.Reliability;
using Il2CppScheduleOne.ObjectScripts;
using PropertyT = Il2CppScheduleOne.Property.Property;

namespace ActuallyWorkingEmployees.Debugging
{
    /// <summary>
    /// DEBUG-only measurement of one lab oven cook. 'awe ovencook setup [item] [qty]' assigns a free lab oven on the
    /// chemist's property to that chemist and fills its input, so the chemist starts the cook himself. While the cook
    /// runs, every change of state is logged once, in game minutes of cook progress: when the oven timer shows 00:00,
    /// when the oven reports ready for harvest (what the chemist picks ovens by), when the operation reports complete
    /// (the ding) and ready (what the chemist's collect action waits for), when the progress stops rising, and when
    /// the chemist has collected. All of these should land on the same minute.
    /// </summary>
    internal sealed class OvenCook : IFeature
    {
        public string Name => "ovencook";

        private LabOven _oven;
        private Chemist _chemist;
        private bool _started, _timerZero, _harvest, _complete, _ready, _stopped, _collected;
        private int _lastProgress = -1, _duration, _startQty;
        private float _nextRead, _lastRise, _t0;
        private string _last = "no cook yet";
        // name@progress/seconds since start on the scaled clock (Time.time), one per event.
        private readonly System.Text.StringBuilder _events = new System.Text.StringBuilder();

        public void Init()
        {
            DevConsole.Register("ovencook", "awe ovencook <setup [item] [qty]|status|clear>  (one lab oven cook: timer, ding, ready and chemist collect in cook minutes)", Dev);
        }

        public void Reset()
        {
            _oven = null;
            _chemist = null;
            Clear("reset");
        }

        public string Status => _oven == null ? "no oven" : Line();

        private void Clear(string why)
        {
            _started = _timerZero = _harvest = _complete = _ready = _stopped = _collected = false;
            _lastProgress = -1;
            _duration = 0;
            _events.Clear();
            _last = "cleared (" + why + ")";
        }

        private void Dev(string[] args)
        {
            if (!DevConsole.RequireHost("ovencook")) return;
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            try
            {
                if (mode == "setup") Setup(args.Length > 1 ? args[1] : null, args.Length > 2 && int.TryParse(args[2], out int q) ? q : 10);
                else if (mode == "clear") { _oven = null; _chemist = null; Clear("command"); Core.Log?.Msg("[Dev] [ovencook] cleared."); }
                else Core.Log?.Msg("[Dev] [ovencook] " + Status);
            }
            catch (Exception e) { Core.Failed("ovencook " + mode, e); }
        }

        private void Setup(string itemId, int qty)
        {
            _chemist = null;
            foreach (Employee e in Rel.Employees())
            {
                var c = e.TryCast<Chemist>();
                if (c != null) { _chemist = c; break; }
            }
            if (_chemist == null) { Core.Log?.Warning("[Dev] [ovencook] no chemist; 'awe ovenrig place' hires one."); return; }
            PropertyT property = _chemist.AssignedProperty;
            if (property == null) { Core.Log?.Warning("[Dev] [ovencook] " + Rel.Name(_chemist) + " has no property."); return; }
            var cfg = _chemist.configuration;
            if (cfg == null) { Core.Log?.Warning("[Dev] [ovencook] " + Rel.Name(_chemist) + " has no configuration."); return; }

            _oven = null;
            var items = property.BuildableItems;
            for (int i = 0; items != null && i < items.Count && _oven == null; i++)
            {
                var o = items[i]?.TryCast<LabOven>();
                if (o != null && o.CurrentOperation == null && o.IngredientSlot != null && o.IngredientSlot.Quantity == 0 && o.OutputSlot.Quantity == 0) _oven = o;
            }
            if (_oven == null) { Core.Log?.Warning("[Dev] [ovencook] no empty lab oven on " + property.PropertyCode + "; 'awe ovenrig place' builds some."); return; }

            string[] tries = itemId != null ? new[] { itemId } : new[] { "liquidmeth", "cocainebase" };
            Il2CppScheduleOne.ItemFramework.ItemDefinition def = null;
            foreach (string id in tries) { def = Il2CppScheduleOne.Registry.GetItem(id); if (def != null) break; }
            if (def == null) { Core.Log?.Warning("[Dev] [ovencook] none of " + string.Join(",", tries) + " is in the registry."); return; }

            _oven.IngredientSlot.InsertItem(def.GetDefaultInstance(qty));
            if (!_oven.IsIngredientCookable()) { Core.Log?.Warning("[Dev] [ovencook] " + def.ID + " is not cookable in a lab oven."); return; }
            bool assigned = cfg.LabOvens != null && cfg.LabOvens.Contains(_oven);
            if (!assigned) cfg.Stations.AddItem(_oven);
            Clear("setup");
            _startQty = _oven.OutputSlot.Quantity;
            _last = "waiting for the chemist to start";
            Core.Log?.Msg("[Dev] [ovencook] setup: " + qty + "x " + def.ID + " in " + _oven.name + " on " + property.PropertyCode
                + ", assigned to " + Rel.Name(_chemist) + (assigned ? " (already)" : "") + ", speed=" + Options.LabOvenSpeed.Value
                + " stationSpeed=" + Options.EnableStationSpeed.Value + ".");
        }

        private void Event(string what, int progress, OvenCookOperation op)
        {
            int gm = (int)Math.Round(Time.time - _t0);
            string line = "[Dev] [ovencook] event " + what + " progress=" + progress + " t=" + gm + " duration=" + _duration
                + (op != null ? " field=" + op.cookDuration + " module=" + ModuleTime(op) : "");
            string name = what.Split(' ')[0];
            if (_events.Length > 0) _events.Append(',');
            _events.Append(name).Append('@').Append(progress).Append('/').Append(gm);
            if (op != null && name == "started") _events.Append(",module@").Append(ModuleTime(op)).Append(",duration@").Append(_duration);
            _last = what + "@" + progress;
            Core.Log?.Msg(line);
        }

        private static string ModuleTime(OvenCookOperation op)
        {
            try { return op.Cookable != null ? op.Cookable.CookTime.ToString() : "?"; }
            catch (Exception e) { Core.Caught("ovencook: read CookableModule.CookTime", e); return "?"; }
        }

        private string Line()
        {
            OvenCookOperation op = _oven.CurrentOperation;
            if (op == null) return "oven=" + _oven.name + " op=none output=" + _oven.OutputSlot.Quantity + " last=" + _last + " events=" + _events;
            return "oven=" + _oven.name + " progress=" + op.CookProgress + " duration=" + op.GetCookDuration() + " field=" + op.cookDuration
                + " module=" + ModuleTime(op) + " complete=" + op.IsComplete() + " ready=" + op.IsReady() + " harvest=" + _oven.IsReadyForHarvest()
                + " timer=" + (_oven.TimerLabel != null ? _oven.TimerLabel.text : "?") + " last=" + _last + " events=" + _events;
        }

        public void Tick(float dt)
        {
            if (_oven == null || _collected || !Net.IsServer) return;
            float now = Time.unscaledTime;
            if (now < _nextRead) return;
            _nextRead = now + 0.05f;
            OvenCookOperation op;
            try { op = _oven.CurrentOperation; }
            catch (Exception e) { Core.Caught("ovencook: read oven", e); _oven = null; return; }

            if (op == null)
            {
                if (!_started) return;
                _collected = true;
                Event("collected output=" + (_oven.OutputSlot.Quantity - _startQty), _lastProgress, null);
                return;
            }
            int p = op.CookProgress;
            if (!_started)
            {
                _started = true;
                _t0 = Time.time;
                _duration = op.GetCookDuration();
                _lastRise = now;
                Event("started", p, op);
            }
            _duration = op.GetCookDuration();
            if (p != _lastProgress) { _lastProgress = p; _lastRise = now; }
            string timer = _oven.TimerLabel != null ? _oven.TimerLabel.text : "";
            if (!_timerZero && timer == "00:00") { _timerZero = true; Event("timer-zero", p, op); }
            if (!_harvest && _oven.IsReadyForHarvest()) { _harvest = true; Event("harvest-ready", p, op); }
            if (!_complete && op.IsComplete()) { _complete = true; Event("complete", p, op); }
            if (!_ready && op.IsReady()) { _ready = true; Event("ready", p, op); }
            // A game minute takes a few real seconds at time scale 1; 12 s without a rise means the oven stopped counting.
            if (!_stopped && _timerZero && now - _lastRise > 12f) { _stopped = true; Event("progress-stopped", p, op); }
        }
    }
}
#endif
