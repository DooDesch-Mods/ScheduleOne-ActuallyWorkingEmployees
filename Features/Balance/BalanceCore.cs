using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.NPCs.Behaviour;
using Il2CppScheduleOne.Tools;
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Networking;
using NpcBehaviour = Il2CppScheduleOne.NPCs.Behaviour.Behaviour;

namespace ActuallyWorkingEmployees.Features.Balance
{
    /// <summary>Logs a message at most once per interval and key, for guards inside per-frame paths.</summary>
    internal static class Throttle
    {
        private static readonly Dictionary<string, float> _next = new Dictionary<string, float>();

        private static bool Due(string key, float every)
        {
            float now = Time.unscaledTime;
            if (_next.TryGetValue(key, out float t) && now < t) return false;
            _next[key] = now + every;
            return true;
        }

        internal static void Info(string key, string message, float every = 30f)
        {
            if (Due(key, every)) Core.Log?.Msg("[" + key + "] " + message);
        }

        internal static void Warn(string key, string message, float every = 30f)
        {
            if (Due(key, every)) Core.Log?.Warning("[" + key + "] " + message);
        }
    }

    /// <summary>
    /// The game's own value of every runtime-changed field, read the first time the object is seen. An option that goes
    /// back to its default writes this value back, so vanilla never has to be hard-coded. Entries are kept across saves:
    /// prefabs outlive them and would otherwise be captured a second time in their changed state.
    /// </summary>
    internal static class Vanilla
    {
        private static readonly Dictionary<string, float> _values = new Dictionary<string, float>();

        internal static float Of(UnityEngine.Object owner, string slot, float live)
        {
            string key = owner.GetInstanceID() + ":" + slot;
            if (!_values.TryGetValue(key, out float v)) { v = live; _values[key] = v; }
            return v;
        }

        internal static int Of(UnityEngine.Object owner, string slot, int live) => (int)Of(owner, slot, (float)live);

        internal static float Want(UnityEngine.Object owner, string slot, float live, bool active, float desired)
        {
            float van = Of(owner, slot, live);
            return active ? desired : van;
        }

        internal static int Want(UnityEngine.Object owner, string slot, int live, bool active, int desired)
        {
            int van = Of(owner, slot, live);
            return active ? desired : van;
        }

        internal static bool Active(FloatOption o) => Math.Abs(o.Value - o.Default) > 1e-4f;
        internal static bool Active(IntOption o) => o.Value != o.Default;
    }

    /// <summary>Typed snapshots of the hired employees, rebuilt twice a second.</summary>
    internal static class Roster
    {
        internal static readonly List<Botanist> Botanists = new List<Botanist>();
        internal static readonly List<Chemist> Chemists = new List<Chemist>();
        internal static readonly List<Packager> Packagers = new List<Packager>();
        internal static readonly List<Cleaner> Cleaners = new List<Cleaner>();
        private static float _next;

        internal static void Refresh()
        {
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + 0.5f;

            Botanists.Clear(); Chemists.Clear(); Packagers.Clear(); Cleaners.Clear();
            EmployeeManager mgr = NetworkSingleton<EmployeeManager>.Instance;
            if (mgr == null) { Throttle.Warn("balance: roster", "EmployeeManager does not exist yet; no employees are balanced."); return; }
            var all = mgr.AllEmployees;
            if (all == null) { Throttle.Warn("balance: roster list", "EmployeeManager.AllEmployees is null; no employees are balanced."); return; }
            for (int i = 0; i < all.Count; i++)
            {
                Employee e = all[i];
                if (e == null) continue;
                Botanist b = e.TryCast<Botanist>();
                if (b != null) { Botanists.Add(b); continue; }
                Chemist c = e.TryCast<Chemist>();
                if (c != null) { Chemists.Add(c); continue; }
                Packager p = e.TryCast<Packager>();
                if (p != null) { Packagers.Add(p); continue; }
                Cleaner cl = e.TryCast<Cleaner>();
                if (cl != null) { Cleaners.Add(cl); continue; }
                Throttle.Warn("balance: roster type", "Employee " + e.name + " is none of botanist, chemist, packager or cleaner.");
            }
        }

        internal static void Clear()
        {
            Botanists.Clear(); Chemists.Clear(); Packagers.Clear(); Cleaners.Clear();
            _next = 0f;
        }
    }

    /// <summary>The employee actions whose duration is a literal inside a coroutine and is divided by the work speed.</summary>
    internal enum Act { None, LabOvenStart, LabOvenFinish, Cauldron, Chemistry, Mixing, DryingStart, SpawnStation, Packaging, BrickPress, MoveItem }

    internal static class ActionKinds
    {
        private static readonly Dictionary<IntPtr, Act> _cache = new Dictionary<IntPtr, Act>();

        internal static Act Of(NpcBehaviour b)
        {
            if (b == null) return Act.None;
            IntPtr key = b.Pointer;
            if (_cache.TryGetValue(key, out Act a)) return a;
            a = Classify(b);
            _cache[key] = a;
            return a;
        }

        private static Act Classify(NpcBehaviour b)
        {
            if (b.TryCast<StartLabOvenBehaviour>() != null) return Act.LabOvenStart;
            if (b.TryCast<FinishLabOvenBehaviour>() != null) return Act.LabOvenFinish;
            if (b.TryCast<StartCauldronBehaviour>() != null) return Act.Cauldron;
            if (b.TryCast<StartChemistryStationBehaviour>() != null) return Act.Chemistry;
            if (b.TryCast<StartMixingStationBehaviour>() != null) return Act.Mixing;
            if (b.TryCast<StartDryingRackBehaviour>() != null) return Act.DryingStart;
            if (b.TryCast<UseSpawnStationBehaviour>() != null) return Act.SpawnStation;
            if (b.TryCast<PackagingStationBehaviour>() != null) return Act.Packaging;
            if (b.TryCast<BrickPressBehaviour>() != null) return Act.BrickPress;
            if (b.TryCast<MoveItemBehaviour>() != null) return Act.MoveItem;
            return Act.None;
        }

        internal static void Clear() { _cache.Clear(); }
    }

    /// <summary>One timed action: its option in seconds and the game's own seconds.</summary>
    internal sealed class ActSpec
    {
        internal readonly FloatOption Option;
        internal readonly float VanillaSeconds;
        internal ActSpec(FloatOption option, float vanillaSeconds) { Option = option; VanillaSeconds = vanillaSeconds; }
    }

    /// <summary>
    /// Writes one multiplicative entry into an employee's WorkSpeedController. The entry is the work-speed option times,
    /// while a stack-driven action is active, vanilla seconds divided by the wanted seconds. The game divides the
    /// action literal by CurrentWorkSpeed when the wait starts, so setting the entry while the behaviour is active is
    /// enough.
    /// </summary>
    internal static class WorkSpeed
    {
        internal const string Label = "AWE.balance";
        private static readonly Dictionary<int, float> _applied = new Dictionary<int, float>();
        private static readonly Dictionary<string, string> _live = new Dictionary<string, string>();

        /// <summary>Last seconds the stack gave an action, per option key, for the dev command.</summary>
        internal static string LiveSeconds(string optionKey) => _live.TryGetValue(optionKey, out string s) ? s : "not seen yet";

        internal static void Drive(Employee e, float baseSpeed, Dictionary<Act, ActSpec> specs)
        {
            NpcBehaviour active = null;
            if (e.Behaviour != null) active = e.Behaviour.activeBehaviour;
            Act act = ActionKinds.Of(active);

            float factor = baseSpeed;
            if (act != Act.None && specs.TryGetValue(act, out ActSpec spec))
            {
                float want = spec.Option.Value;
                if (Math.Abs(want - spec.VanillaSeconds) > 1e-4f && want > 0f)
                {
                    factor = baseSpeed * (spec.VanillaSeconds / want);
                    _live[spec.Option.Key] = (want / Math.Max(baseSpeed, 0.0001f)).ToString("0.###", CultureInfo.InvariantCulture) + " s at work speed " + baseSpeed.ToString("0.###", CultureInfo.InvariantCulture);
                }
            }
            Apply(e, factor);
        }

        private static void Apply(Employee e, float factor)
        {
            FloatStack stack = e.WorkSpeedController;
            if (stack == null) { Throttle.Warn("balance: work speed", "Employee " + e.name + " has no WorkSpeedController."); return; }
            int id = e.GetInstanceID();
            bool has = _applied.TryGetValue(id, out float cur);
            if (Math.Abs(factor - 1f) < 0.0005f)
            {
                if (has) { stack.Remove(Label); _applied.Remove(id); }
                return;
            }
            if (has && Math.Abs(cur - factor) < 0.0005f) return;
            stack.Add(new FloatStack.StackEntry(Label, factor, FloatStack.EStackMode.Multiplicative, 100));
            _applied[id] = factor;
        }

        /// <summary>The entry value currently applied to an employee, or 1.</summary>
        internal static float AppliedFactor(Employee e) => _applied.TryGetValue(e.GetInstanceID(), out float f) ? f : 1f;

        internal static void Clear() { _applied.Clear(); _live.Clear(); }
    }

    /// <summary>
    /// The dev command <c>awe eff &lt;option&gt;</c>: what the option is set to and what the game holds now.
    /// Compiled out of Release.
    /// </summary>
    internal static class Eff
    {
#if DEBUG
        private static readonly Dictionary<string, Func<string>> _live = new Dictionary<string, Func<string>>(StringComparer.OrdinalIgnoreCase);
        private static bool _registered;
#endif

        internal static void Add(Option option, Func<string> live)
        {
#if DEBUG
            _live[option.Key] = live;
            if (_registered) return;
            _registered = true;
            ActuallyWorkingEmployees.Debugging.DevConsole.Register("eff", "awe eff <option>", Command);
#endif
        }

#if DEBUG
        private static void Command(string[] args)
        {
            if (args.Length < 1) { Core.Log?.Warning("[Dev] awe eff <option>"); return; }
            Option o = Options.Find(args[0]);
            if (o == null) { Core.Log?.Warning("[Dev] awe eff: no option '" + args[0] + "'."); return; }
            if (!_live.TryGetValue(o.Key, out Func<string> live)) { Core.Log?.Warning("[Dev] awe eff: " + o.Key + " has no live reading."); return; }
            string now;
            try { now = live(); }
            catch (Exception e) { Core.Caught("eff live " + o.Key, e); now = "error: " + e.Message; }
            Core.Log?.Msg("AWE eff " + o.Key + " configured=" + Configured(o) + " live=" + now);
        }

        private static string Configured(Option o)
        {
            switch (o)
            {
                case BoolOption b: return b.Value ? "true" : "false";
                case IntOption i: return i.Value == -1 && i.VanillaSentinel ? "vanilla" : i.Value.ToString(CultureInfo.InvariantCulture);
                case FloatOption f: return f.Value == -1f && f.VanillaSentinel ? "vanilla" : f.Value.ToString("0.###", CultureInfo.InvariantCulture);
                case ChoiceOption c: return c.Value;
                default: return o.ValueText;
            }
        }
#endif

        internal static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Base of the balance features: re-applies the options once a second and at once after an option changed, on the
    /// server only. The sync is idempotent, so a missed change heals on the next pass.
    /// </summary>
    internal abstract class BalanceFeature : IFeature
    {
        public abstract string Name { get; }
        protected abstract IEnumerable<Option> Watched { get; }
        protected abstract void Sync();
        protected virtual void PerFrame() { }
        protected abstract string Details();
        protected virtual void OnInit() { }
        protected virtual void OnReset() { }

        private float _next;
        private bool _dirty = true;
        private int _syncs;
        private bool _serverNoticeLogged;

        public void Init()
        {
            foreach (Option o in Watched) o.Changed += _ => _dirty = true;
            OnInit();
        }

        public void Tick(float dt)
        {
            if (!Net.IsServer)
            {
                if (!_serverNoticeLogged)
                {
                    _serverNoticeLogged = true;
                    Core.Log?.Msg("[" + Name + "] not the server, the host applies balance.");
                }
                return;
            }
            _serverNoticeLogged = false;
            Roster.Refresh();
            if (_dirty || Time.unscaledTime >= _next)
            {
                _dirty = false;
                _next = Time.unscaledTime + 1f;
                Sync();
                _syncs++;
            }
            PerFrame();
        }

        public void Reset()
        {
            _dirty = true;
            _next = 0f;
            OnReset();
        }

        public string Status => "syncs=" + _syncs + " " + Details();

        /// <summary>Runs one item of a sync; a failure is logged once and does not stop the others.</summary>
        protected void Guarded(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { Core.Failed(Name + ": " + what, e); }
        }
    }

    /// <summary>Settings shared by the four employee types: walking speed, wage and signing fee.</summary>
    internal static class CommonEmployee
    {
        internal static void Walk(Employee e, FloatOption opt)
        {
            if (e.Movement == null) { Throttle.Warn("balance: movement", "Employee " + e.name + " has no Movement."); return; }
            var sc = e.Movement.SpeedController;
            if (sc == null) { Throttle.Warn("balance: speed controller", "Employee " + e.name + " has no SpeedController."); return; }
            float live = sc.DefaultSpeed;
            float want = Vanilla.Want(e, "walk", live, opt.Value > 0f, opt.Value);
            if (Math.Abs(live - want) > 0.001f) sc.SetDefaultSpeed(want);
        }

        internal static void Money(Employee e, IntOption wage, IntOption fee)
        {
            float liveWage = e.DailyWage;
            float wantWage = Vanilla.Want(e, "wage", liveWage, wage.Value >= 0, wage.Value);
            if (Math.Abs(liveWage - wantWage) > 0.01f) e.DailyWage = wantWage;
            if (fee == null) return;
            float liveFee = e.SigningFee;
            float wantFee = Vanilla.Want(e, "fee", liveFee, fee.Value >= 0, fee.Value);
            if (Math.Abs(liveFee - wantFee) > 0.01f) e.SigningFee = wantFee;
        }

        /// <summary>New hires copy the prefab, and the hire dialogue reads the signing fee from it.</summary>
        internal static void Prefab(Employee prefab, string what, IntOption wage, IntOption fee)
        {
            if (prefab == null) { Throttle.Warn("balance: prefab " + what, "EmployeeManager has no " + what + " prefab; wage and fee of new hires stay vanilla."); return; }
            Money(prefab, wage, fee);
        }

        internal static void SetMax(Il2CppScheduleOne.Management.ObjectListField field, UnityEngine.Object owner, string slot, IntOption opt)
        {
            int live = field.MaxItems;
            int want = Vanilla.Want(owner, slot, live, Vanilla.Active(opt), opt.Value);
            if (live != want) field.MaxItems = want;
        }

        internal static string LiveWalk(Employee e)
        {
            if (e == null) return "no employee";
            return e.Movement != null && e.Movement.SpeedController != null ? Eff.F(e.Movement.SpeedController.DefaultSpeed) : "no controller";
        }
    }
}
