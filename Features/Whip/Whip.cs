using System;
using System.Collections.Generic;
using ActuallyWorkingEmployees.Config;
using ActuallyWorkingEmployees.Features.Diagnostics;
using ActuallyWorkingEmployees.Features.Reliability;
using GameInput = Il2CppScheduleOne.GameInput;
using ActuallyWorkingEmployees.Host;
using ActuallyWorkingEmployees.Networking;
using Il2CppScheduleOne.Combat;          // CombatManager.MeleeLayerMask
using Il2CppScheduleOne.PlayerScripts;   // PlayerCamera
using Il2CppScheduleOne.Tools;           // FloatStack
using Il2CppScheduleOne.UI;              // NotificationsManager
using NpcBehaviour = Il2CppScheduleOne.NPCs.Behaviour.Behaviour;
#if DEBUG
using ActuallyWorkingEmployees.Debugging;
using Il2CppScheduleOne.ItemFramework;
#endif

namespace ActuallyWorkingEmployees.Features.Whip
{
    /// <summary>
    /// The whip (AWE#63). A click with the whip in hand at an employee within reach:
    /// <list type="bullet">
    /// <item>working: one boost stack of WhipBoostPercent work and walk speed, up to WhipMaxStacks. Every hit refreshes
    /// the timer of all stacks to WhipBoostMinutes game minutes; when it runs out, all stacks end together. No cooldown.</item>
    /// <item>idle, or standing at its task without progress for <see cref="StuckAfterSeconds"/>: the task restart of
    /// Fix 7, the same one the watchdog does.</item>
    /// <item>WhipResetHits hits on the same employee within WhipResetSeconds real seconds: all stacks end and the task
    /// restarts, also while working.</item>
    /// </list>
    /// The host decides and applies every effect; a co-op client sends its click through <see cref="WhipNet"/> and shows
    /// the answer. The work boost is its own entry in the employee's WorkSpeedController, so it multiplies with the work
    /// speed option. The walk boost is the speed multiplier of the movement on top of the walk speed option, capped at
    /// <see cref="WalkCap"/> m/s, the highest walk speed at which employees reached their targets without overshoot.
    /// </summary>
    internal sealed class Whip : IFeature
    {
        internal const string FeatureName = "whip";
        public string Name => FeatureName;

        private const string StackLabel = "AWE.whip";
        private const float Reach = 3f;
        private const float AimRadius = 0.25f;
        /// <summary>Standing still this long at an active task counts as stuck for the whip (the watchdog waits longer).</summary>
        internal const float StuckAfterSeconds = 10f;

        /// <summary>Walk speed the boost never pushes above (m/s); the "fast" preset measured 6 as safe for every role.</summary>
        internal const float WalkCap = 6f;

        private sealed class Boost
        {
            internal Employee Emp;
            internal int Stacks;                 // 0 = no boost running
            internal int Ends;                   // game minute
            internal readonly List<float> Hits = new List<float>();   // real time of recent hits, for the reset rule
        }
        private static readonly Dictionary<int, Boost> _boosts = new Dictionary<int, Boost>();
        private static int _uses, _boosted, _restarted, _refused, _resets;
        private static float _nextClickAt, _nextSlow, _nextExpire;

        public void Init()
        {
            WhipNet.Patch();
            // Before the save data loads, so a whip in a saved inventory finds its definition.
            S1API.Lifecycle.GameLifecycle.OnPreLoad += () =>
            {
                try { WhipItem.EnsureRegistered(); }
                catch (Exception e) { Core.Failed("whip: register on load", e); }
            };
#if DEBUG
            DevConsole.Register("whip", "awe whip [give|hold|use [name]|hits n [name]|boost [name]|ask [name]|aim|swing [freeze s]|unfreeze|burst [n] [s]|face name|turn deg s|walk [s] [sprint]|closephone|lash [name value]|lash measure [s]|vm px py pz rx ry rz scale|vm aim [tilt] [yaw]|vm arm dx dy dz [rx ry rz]|vm leftarm [show]|vm grip [z]|icon path]  (whip state / put a whip in the inventory / take it in hand / whip the first or named employee on the host / whip it n times in one frame / boost it without the work check / ask the host to whip it from a client / whip what the camera looks at / swing like a click, optionally freeze the game s seconds later / time scale back to 1 / n clicks through the click gate, one every s seconds / a spot and yaw that face an employee / turn the view / walk forward s seconds / close the phone / list or set the lash values / log how far the lash tip moves in s seconds / move the model in hand / point the handle forward / move and turn the whole arm / put the grip on the hand bone / save the icon as PNG)", Dev);
#endif
        }

        public void Reset()
        {
            WhipItem.MarkUnchecked();
            WhipShop.ResetSession();
            WhipFx.ResetSession();
            foreach (Boost b in _boosts.Values) EndBoost(b);
            _boosts.Clear();
        }

        public string Status => "registered=" + (WhipItem.Available ? 1 : 0) + " held=" + (WhipEquippable.Ready ? 1 : 0)
            + " uses=" + _uses + " boosted=" + _boosted + " restarted=" + _restarted + " refused=" + _refused + " resets=" + _resets
            + " activeBoosts=" + ActiveBoosts() + " net sent=" + WhipNet.Sent + " received=" + WhipNet.Received + " answered=" + WhipNet.Answered;

        public void Tick(float dt)
        {
            WhipItem.EnsureRegistered();
            if (Time.time >= _nextSlow)
            {
                _nextSlow = Time.time + 2f;
                WhipShop.EnsureListed();
                WhipEquippable.VerifyPointer();
            }
            if (WhipItem.Available) WhipEquippable.EnsureInstalled(WhipItem.EnsureModelSource());
            WhipEquippable.TickLeftArm(WhipItem.IsEquipped());
            TickClick();
            TickSnap();
            WhipEquippable.TickEffects();
#if DEBUG
            if (_freezeAt >= 0f && Time.time >= _freezeAt)
            {
                Core.Log?.Msg("[Dev] [Whip] frozen " + (Time.time - _freezeAt + _freezeDelay).ToString("F3") + " s after the swing ('awe whip unfreeze').");
                _freezeAt = -1f;
                Time.timeScale = 0f;
            }
            if (_turnLeft > 0f)
            {
                // Same path as the mouse: PlayerCamera adds its look delta onto the player's rotation each frame.
                Player local = Player.Local;
                if (local == null) { Core.Log?.Warning("[Dev] [Whip] turn stopped: no local player."); _turnLeft = 0f; }
                else
                {
                    float step = Mathf.Min(dt, _turnLeft);
                    local.transform.Rotate(0f, _turnRate * step, 0f, Space.World);
                    _turnLeft -= step;
                    if (_turnLeft <= 0f) Core.Log?.Msg("[Dev] [Whip] turn done.");
                }
            }
            if (_walkLeft > 0f)
            {
                // GameInput.MotionAxis is only written by input events, so a value set here holds like a held key; the
                // player then walks through PlayerMovement with the game's own head bob and viewmodel sway.
                var move = PlayerSingleton<PlayerMovement>.Instance;
                _walkLeft -= dt;
                bool done = _walkLeft <= 0f || move == null;
                GameInput.MotionAxis = done ? Vector2.zero : new Vector2(0f, 1f);
                if (move != null) move.ForceSprint = !done && _walkSprint;
                if (move == null) Core.Log?.Warning("[Dev] [Whip] walk stopped: no PlayerMovement.");
                else if (done) Core.Log?.Msg("[Dev] [Whip] walk done.");
                if (done) _walkLeft = 0f;
            }
#endif
            if (Time.time >= _nextExpire)
            {
                _nextExpire = Time.time + 1f;
                if (Net.IsServer) ExpireBoosts();
            }
        }

        // ---- local player -----------------------------------------------------------------------------------------------

        private static void TickClick()
        {
            if (!WhipItem.IsEquipped()) return;
            bool click = GameInput.GetButtonDown(GameInput.ButtonCode.PrimaryClick);
#if DEBUG
            if (!click && _burstLeft > 0 && Time.unscaledTime >= _burstNext)
            {
                _burstLeft--;
                _burstNext = Time.unscaledTime + _burstInterval;
                Core.Log?.Msg("[Dev] [Whip] burst click, " + _burstLeft + " left.");
                click = true;
            }
#endif
            if (!click) return;
            // From here on a click happened with the whip in hand, so every refusal is logged.
            if (GameInput.IsTyping) { Core.Log?.Msg("[Whip] click ignored: typing."); return; }
            if (Time.unscaledTime < _nextClickAt) { Core.Log?.Msg("[Whip] click ignored: the last swing was under " + SwingInterval.ToString("F2") + " s ago."); return; }
            PlayerCamera cam = PlayerSingleton<PlayerCamera>.Instance;
            if (cam == null) { Core.Log?.Warning("[Whip] click ignored: no player camera."); return; }
            if (cam.activeUIElements.Count > 0) { Core.Log?.Msg("[Whip] click ignored: a menu is open (" + cam.activeUIElements.Count + " UI element(s))."); return; }
            Swing();
        }

        /// <summary>
        /// One swing of the held whip: arm, air sound and the throw of the lash now; the crack and the hit test follow
        /// when the lash snaps, <see cref="WhipLash.SnapDelay"/> later (see <see cref="TickSnap"/>).
        /// </summary>
        internal static string Swing()
        {
            // A click before the last snap: that snap lands now, so no hit of a fast series is lost.
            if (_snapAt >= 0f) { _snapAt = Time.time; TickSnap(); }
            _nextClickAt = Time.unscaledTime + SwingInterval;
            WhipEquippable.PlaySwing();
            _snapAt = Time.time + WhipLash.SnapDelay;
            return "swing, snap in " + WhipLash.SnapDelay.ToString("F2") + " s";
        }

        private static float _snapAt = -1f;
        /// <summary>Shortest time between two swings, against a double click counting twice. 5 hits fit into 1.2 s.</summary>
        private const float SwingInterval = 0.28f;
#if DEBUG
        private static int _burstLeft;
        private static float _burstNext, _burstInterval;
        private static float _turnLeft, _turnRate;
        private static float _walkLeft;
        private static bool _walkSprint;
        private static float _freezeAt = -1f, _freezeDelay;
#endif

        private static void TickSnap()
        {
            if (_snapAt < 0f || Time.time < _snapAt) return;
            _snapAt = -1f;
            string crack = WhipEquippable.PlayCrack();
            WhipFx.Puff();
            // A small kick of the view on every crack; a hit replaces it with the bigger shake of the impact.
            PlayerSingleton<PlayerCamera>.Instance?.StartCameraShake(0.05f, 0.08f, true);
            string result = Crack();
            Core.Log?.Msg("[Whip] snap: crack " + crack + ", click: " + result + ".");
        }

        /// <summary>The click: finds the employee under the crosshair and whips it, on the host directly, else through the host.</summary>
        internal static string Crack()
        {
            PlayerCamera cam = PlayerSingleton<PlayerCamera>.Instance;
            var combat = NetworkSingleton<CombatManager>.Instance;
            if (cam == null || combat == null) { Core.Log?.Warning("[Whip] crack: no player camera or combat manager yet."); return "no camera"; }
            if (!cam.LookRaycast(Reach, out RaycastHit hit, combat.MeleeLayerMask, true, AimRadius))
            {
                Core.Log?.Msg("[Whip] crack: nothing within " + Reach + " m.");
                return "nothing in reach";
            }
            Employee emp = hit.collider != null ? hit.collider.GetComponentInParent<Employee>() : null;
            if (emp == null)
            {
                Core.Log?.Msg("[Whip] crack: hit " + (hit.collider != null ? hit.collider.name : "?") + ", not an employee.");
                return "not an employee";
            }
            WhipEquippable.PlayImpact(emp, cam.transform.forward);
            if (Net.IsServer) return Rel.Name(emp) + " " + Apply(emp, -1, "local player");
            WhipNet.SendIntent(emp.GUID.ToString());
            return Rel.Name(emp) + " sent to host";
        }

        /// <summary>Client side: the host's answer to this player's click.</summary>
        internal static void ShowResult(string code, int minutes, string name, string reason)
        {
            string text;
            switch (code)
            {
                case "boost": text = Loc.Format("{0} works {1}% faster for {2} min.", name, reason, minutes); break;
                case "max": text = Loc.Format("{0} is at the highest boost, {1}% faster for {2} min.", name, reason, minutes); break;
                case "reset": text = Loc.Format("{0} was whipped too often: boost gone, the task restarts.", name); break;
                case "restart": text = Loc.Format("{0} restarts the stuck task.", name); break;
                case "idle":
                    text = string.IsNullOrEmpty(reason)
                        ? Loc.Format("{0} has nothing to do right now.", name)
                        : Loc.Format("{0} has nothing to do: {1}", name, Loc.Text(reason));   // the host sends the English reason, each peer translates it
                    break;
                default: text = Loc.Format("The whip did nothing to {0}.", name); break;
            }
            Core.Log?.Msg("[Whip] " + code + ": " + text);
            try
            {
                var nm = Singleton<NotificationsManager>.Instance;
                if (nm == null) { Core.Log?.Warning("[Whip] no notifications manager, the result stays in the log."); return; }
                nm.SendNotification(Loc.Text("Whip"), text, WhipItem.IconSprite, 5f, true);
                FitNotification(nm, text);
            }
            catch (Exception e) { Core.Caught("whip: show the result", e); }
        }

        // The game's notification box is about 200 px wide and cuts a longer subtitle with "...". After the game builds the
        // entry, the box grows to the right until the subtitle fits (same approach as Based's notices); the icon and the
        // slide-in from the left stay as they are.
        private const float NoticeTextInset = 52.5f, NoticePadding = 12f;

        private static void FitNotification(NotificationsManager nm, string subtitle)
        {
            var entries = nm.EntryContainer;
            if (entries == null || entries.childCount == 0) { Core.Log?.Warning("[Whip] notification not fitted: the entry list is empty."); return; }
            Transform entry = entries.GetChild(entries.childCount - 1);
            var container = entry.Find("Container")?.GetComponent<RectTransform>();
            var sub = container != null ? container.Find("Subtitle")?.GetComponent<Il2CppTMPro.TextMeshProUGUI>() : null;
            var box = entry.GetComponent<RectTransform>();
            if (container == null || sub == null || box == null) { Core.Log?.Warning("[Whip] notification not fitted: no Container/Subtitle, the game's layout changed."); return; }
            float needed = sub.GetPreferredValues(subtitle).x + NoticeTextInset + NoticePadding;
            float extra = needed - box.rect.width;
            if (extra <= 0f) return;
            container.pivot = new Vector2(0f, container.pivot.y);
            container.sizeDelta = new Vector2(extra, container.sizeDelta.y);
            Core.Log?.Msg("[Whip] notification widened by " + extra.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " px.");
        }

        // ---- host -------------------------------------------------------------------------------------------------------

        /// <summary>Host: a client's whip click on the employee with this GUID.</summary>
        internal static void HandleIntent(int client, string employeeGuid)
        {
            Employee emp = null;
            foreach (Employee e in Rel.Employees())
                if (e != null && e.GUID.ToString() == employeeGuid) { emp = e; break; }
            if (emp == null)
            {
                Core.Log?.Warning("[Whip] client " + client + " whipped employee " + employeeGuid + ", which the host does not know.");
                WhipNet.SendResult(client, "fail", 0, "?", "");
                return;
            }
            Apply(emp, client, "client " + client);
        }

        /// <summary>
        /// Host: decides and applies the whip on one employee, then tells the player (client -1 = the host's own player).
        /// Returns the result code.
        /// </summary>
        internal static string Apply(Employee emp, int client, string who)
        {
            _uses++;
            string name = Rel.Name(emp);
            string code; int minutes = 0; string reason = "";
            try
            {
                NpcBehaviour active = emp.Behaviour != null ? emp.Behaviour.activeBehaviour : null;
                bool working = active != null && active.Active && Rel.IsWork(active);
                float still = Fix7StuckWork.StillSeconds(emp);

                if (CountHit(emp))
                {
                    // Continuous whipping: everything off, start over (AWE#63).
                    Boost b = _boosts[emp.GetInstanceID()];
                    b.Hits.Clear();
                    EndBoost(b);
                    _resets++;
                    Core.Log?.Msg("[Whip] " + name + " was hit " + Options.WhipResetHits.Value + " times within "
                        + Options.WhipResetSeconds.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s: boost dropped, task restarted.");
                    if (Fix7StuckWork.Restart(emp, "whip reset from " + who, true)) { _restarted++; code = "reset"; }
                    else { _refused++; code = "fail"; }
                }
                else if (!working || still >= StuckAfterSeconds)
                {
                    string why = "whip from " + who + (working ? ", " + Rel.TypeName(active) + " " + still.ToString("F0") + " s without progress" : ", no work task");
                    if (!working) WorkIssueInfo.TryFirstIssue(emp, out reason, out _);
                    reason = reason ?? "";   // TryFirstIssue leaves null when it finds no issue
                    if (Fix7StuckWork.Restart(emp, why, true)) { _restarted++; code = working ? "restart" : "idle"; }
                    else { _refused++; code = "fail"; }
                }
                else code = TryBoost(emp, out minutes, out reason);
            }
            catch (Exception e)
            {
                Core.Failed("whip: apply to " + name, e);
                code = "fail";
            }
            Core.Log?.Msg("[Whip] " + who + " whipped " + name + ": " + code + (minutes > 0 ? ", " + minutes + " min" : "") + (reason.Length > 0 ? ", " + reason : "") + ".");
            if (client < 0) ShowResult(code, minutes, name, reason);
            else WhipNet.SendResult(client, code, minutes, name, reason);
            return code;
        }

        private static Boost BoostOf(Employee emp)
        {
            int id = emp.GetInstanceID();
            if (!_boosts.TryGetValue(id, out Boost b)) { b = new Boost(); _boosts[id] = b; }
            b.Emp = emp;
            return b;
        }

        /// <summary>Records a hit; true when it completes WhipResetHits hits within WhipResetSeconds real seconds.</summary>
        private static bool CountHit(Employee emp)
        {
            Boost b = BoostOf(emp);
            float now = Time.unscaledTime;
            float window = Options.WhipResetSeconds.Value;
            b.Hits.Add(now);
            b.Hits.RemoveAll(t => now - t > window);
            return b.Hits.Count >= Options.WhipResetHits.Value;
        }

        /// <summary>Total extra speed in percent for a number of stacks.</summary>
        private static int Percent(int stacks) => stacks * Options.WhipBoostPercent.Value;

        /// <summary>Adds one stack up to WhipMaxStacks and refreshes the timer of all stacks. No cooldown.</summary>
        private static string TryBoost(Employee emp, out int minutes, out string reason)
        {
            Boost b = BoostOf(emp);
            minutes = Options.WhipBoostMinutes.Value;
            if (emp.WorkSpeedController == null)
            {
                Core.Log?.Warning("[Whip] " + Rel.Name(emp) + " has no WorkSpeedController.");
                reason = ""; _refused++;
                return "fail";
            }
            int max = Math.Max(1, Options.WhipMaxStacks.Value);
            bool atMax = b.Stacks >= max;
            if (!atMax) b.Stacks++;
            b.Ends = Now() + minutes;
            Push(b);
            if (atMax) _refused++; else _boosted++;
            reason = Percent(b.Stacks).ToString();
            Core.Log?.Msg("[Whip] boost on " + Rel.Name(emp) + ": " + b.Stacks + " of " + max + " stack(s), +" + reason + "% until game minute " + b.Ends
                + ", work speed " + emp.WorkSpeedController.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                + ", walk " + WalkText(emp) + ".");
            return atMax ? "max" : "boost";
        }

        /// <summary>Writes the current stacks into the work speed stack and the walk speed multiplier.</summary>
        private static void Push(Boost b)
        {
            Employee e = b.Emp;
            if (e == null) return;
            float factor = 1f + Percent(b.Stacks) / 100f;
            e.WorkSpeedController?.Add(new FloatStack.StackEntry(StackLabel, factor, FloatStack.EStackMode.Multiplicative, 101));
            var sc = e.Movement != null ? e.Movement.SpeedController : null;
            if (sc == null) { Core.WarnOnce("whip walk " + e.GetInstanceID(), "[Whip] " + Rel.Name(e) + " has no movement speed controller; only the work speed is boosted."); return; }
            float walk = factor;
            if (sc.DefaultSpeed > 0f) walk = Mathf.Clamp(factor, 1f, Math.Max(1f, WalkCap / sc.DefaultSpeed));
            sc.SetSpeedMultiplier(walk);
        }

        /// <summary>Drops every stack of one employee: work speed entry removed, walk multiplier back to 1.</summary>
        private static void EndBoost(Boost b)
        {
            bool had = b.Stacks > 0;
            b.Stacks = 0;
            if (!had) return;
            try
            {
                if (b.Emp == null) { Core.Log?.Msg("[Whip] a boost ended on an employee that no longer exists."); return; }
                b.Emp.WorkSpeedController?.Remove(StackLabel);
                b.Emp.Movement?.SpeedController?.SetSpeedMultiplier(1f);
            }
            catch (Exception e) { Core.Caught("whip: end a boost", e); }
        }

        private static string WalkText(Employee e)
        {
            var sc = e.Movement != null ? e.Movement.SpeedController : null;
            return sc == null ? "?" : sc.MoveSpeed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " m/s";
        }

        /// <summary>Host, once a second: ends boosts whose time ran out and re-applies the rest, so a walk speed option
        /// change or a game effect that reset the multiplier does not lose the boost.</summary>
        private static void ExpireBoosts()
        {
            if (_boosts.Count == 0) return;
            int now = Now();
            foreach (Boost b in _boosts.Values)
            {
                if (b.Stacks == 0) continue;
                if (now < b.Ends)
                {
                    try { Push(b); }
                    catch (Exception e) { Core.Caught("whip: keep a boost", e); }
                    continue;
                }
                string name = b.Emp != null ? Rel.Name(b.Emp) : "?";
                int stacks = b.Stacks;
                EndBoost(b);
                Core.Log?.Msg("[Whip] boost on " + name + " ended at game minute " + now + " (" + stacks + " stack(s)).");
            }
        }

        private static int ActiveBoosts()
        {
            int n = 0;
            foreach (Boost b in _boosts.Values) if (b.Stacks > 0) n++;
            return n;
        }

        private static int Now()
        {
            try
            {
                var tm = NetworkSingleton<Il2CppScheduleOne.GameTime.TimeManager>.Instance;
                return tm != null ? tm.GetTotalMinSum() : 0;
            }
            catch (Exception e) { Core.Caught("whip: read the game time", e); return 0; }
        }

#if DEBUG
        private static void Dev(string[] args)
        {
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            switch (mode)
            {
                case "":
                    Core.Log?.Msg("[Dev] [Whip] " + new Whip().Status + " equipped=" + WhipItem.IsEquipped() + " role=" + Net.RoleText() + " now=" + Now());
                    foreach (Employee e in Rel.Employees())
                    {
                        NpcBehaviour act = e.Behaviour != null ? e.Behaviour.activeBehaviour : null;
                        bool working = act != null && act.Active && Rel.IsWork(act);
                        _boosts.TryGetValue(e.GetInstanceID(), out Boost b);
                        var esc = e.Movement != null ? e.Movement.SpeedController : null;
                        Core.Log?.Msg("[Dev] [Whip] employee " + Rel.Name(e) + " working=" + (working ? 1 : 0) + " still=" + Fix7StuckWork.StillSeconds(e).ToString("F0")
                            + "s stacks=" + (b != null ? b.Stacks : 0) + " ends=" + (b != null && b.Stacks > 0 ? b.Ends : 0)
                            + " workSpeed=" + (e.WorkSpeedController != null ? e.WorkSpeedController.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "?")
                            + " walkDefault=" + (esc != null ? esc.DefaultSpeed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "?")
                            + " walk=" + (esc != null ? esc.MoveSpeed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "?")
                            + " behaviour=" + (act != null ? Rel.TypeName(act) : "none")
                            + " pos=" + e.transform.position.x.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                            + "," + e.transform.position.y.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                            + "," + e.transform.position.z.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    return;
                case "hold":
                {
                    var inv = PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerInventory>.Instance;
                    if (inv == null) { Core.Log?.Warning("[Dev] [Whip] hold: no player inventory."); return; }
                    foreach (var slot in inv.hotbarSlots)
                    {
                        if (slot == null || slot.ItemInstance == null || slot.ItemInstance.Definition == null || slot.ItemInstance.Definition.ID != WhipItem.Id) continue;
                        if (inv.equippedSlot != null) inv.equippedSlot.Deselect();
                        inv.Equip(slot);
                        Core.Log?.Msg("[Dev] [Whip] hold: the whip is in hand.");
                        return;
                    }
                    Core.Log?.Warning("[Dev] [Whip] hold: no whip on the hotbar; 'awe whip give' first.");
                    return;
                }
                case "ask":
                {
                    string want = args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : "";
                    foreach (Employee e in Rel.Employees())
                    {
                        if (want.Length > 0 && Rel.Name(e).IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (Net.IsServer) { Core.Log?.Msg("[Dev] [Whip] ask on the host applies directly: " + Apply(e, -1, "dev command")); return; }
                        WhipNet.SendIntent(e.GUID.ToString());
                        Core.Log?.Msg("[Dev] [Whip] ask: request for " + Rel.Name(e) + " sent to the host.");
                        return;
                    }
                    Core.Log?.Warning("[Dev] [Whip] ask: no employee" + (want.Length > 0 ? " named '" + want + "'" : "") + ".");
                    return;
                }
                case "give":
                {
                    var def = Il2CppScheduleOne.Registry.GetItem(WhipItem.Id);
                    var inv = PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerInventory>.Instance;
                    if (def == null || inv == null) { Core.Log?.Warning("[Dev] [Whip] give: item registered=" + (def != null) + ", inventory=" + (inv != null) + "."); return; }
                    inv.AddItemToInventory(def.GetDefaultInstance(1));
                    Core.Log?.Msg("[Dev] [Whip] a whip was put into the inventory.");
                    return;
                }
                case "use":
                {
                    if (!DevConsole.RequireHost("whip use")) return;
                    string want = args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : "";
                    foreach (Employee e in Rel.Employees())
                    {
                        if (want.Length > 0 && Rel.Name(e).IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        Core.Log?.Msg("[Dev] [Whip] use on " + Rel.Name(e) + ": " + Apply(e, -1, "dev command"));
                        return;
                    }
                    Core.Log?.Warning("[Dev] [Whip] use: no employee" + (want.Length > 0 ? " named '" + want + "'" : "") + ".");
                    return;
                }
                case "hits":
                {
                    // n whips in one frame, so the reset rule can be tested without the console round trip in the window.
                    if (!DevConsole.RequireHost("whip hits")) return;
                    if (args.Length < 2 || !int.TryParse(args[1], out int n) || n < 1 || n > 50)
                    {
                        Core.Log?.Warning("[Dev] [Whip] hits: usage 'awe whip hits <1-50> [name]'.");
                        return;
                    }
                    string want = args.Length > 2 ? string.Join(" ", args, 2, args.Length - 2) : "";
                    foreach (Employee e in Rel.Employees())
                    {
                        if (want.Length > 0 && Rel.Name(e).IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        string codes = "";
                        for (int i = 0; i < n; i++) codes += (i > 0 ? "," : "") + Apply(e, -1, "dev command");
                        Core.Log?.Msg("[Dev] [Whip] hits on " + Rel.Name(e) + ": " + codes);
                        return;
                    }
                    Core.Log?.Warning("[Dev] [Whip] hits: no employee" + (want.Length > 0 ? " named '" + want + "'" : "") + ".");
                    return;
                }
                case "boost":
                {
                    // The boost bookkeeping alone, without the working/idle/stuck decision: a chemist works only for a
                    // few seconds per cook, too short to test stacking, the cap and the expiry one after another.
                    if (!DevConsole.RequireHost("whip boost")) return;
                    string want = args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : "";
                    foreach (Employee e in Rel.Employees())
                    {
                        if (want.Length > 0 && Rel.Name(e).IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        string code = TryBoost(e, out int m, out _);
                        Core.Log?.Msg("[Dev] [Whip] boost on " + Rel.Name(e) + ": " + code + " (" + m + " min)");
                        return;
                    }
                    Core.Log?.Warning("[Dev] [Whip] boost: no employee" + (want.Length > 0 ? " named '" + want + "'" : "") + ".");
                    return;
                }
                case "aim":
                    Core.Log?.Msg("[Dev] [Whip] aim: " + Crack());
                    return;
                case "swing":
                    if (!WhipItem.IsEquipped()) { Core.Log?.Warning("[Dev] [Whip] swing: the whip is not in hand ('awe whip hold')."); return; }
                    Core.Log?.Msg("[Dev] [Whip] swing: " + Swing());
                    if (args.Length >= 3 && args[1].Equals("freeze", StringComparison.OrdinalIgnoreCase)
                        && float.TryParse(args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float freeze))
                    {
                        _freezeDelay = freeze;
                        _freezeAt = Time.time + freeze;
                        Core.Log?.Msg("[Dev] [Whip] swing: the game freezes " + freeze.ToString("F3") + " s from now.");
                    }
                    return;
                case "unfreeze":
                    Time.timeScale = 1f;
                    _freezeAt = -1f;
                    Core.Log?.Msg("[Dev] [Whip] unfreeze: time scale 1.");
                    return;
                case "closephone":
                {
                    // A stray Tab (Alt+Tab into the window) opens the phone over every test shot.
                    var phone = PlayerSingleton<Il2CppScheduleOne.UI.Phone.Phone>.Instance;
                    if (phone == null) { Core.Log?.Warning("[Dev] [Whip] closephone: no phone."); return; }
                    bool was = phone.IsOpen;
                    if (was) phone.SetIsOpen(false);
                    Core.Log?.Msg("[Dev] [Whip] closephone: " + (was ? "phone closed." : "the phone was not open."));
                    return;
                }
                case "face":
                {
                    // Where to stand to look at an employee from 1.4 m: 'awe whip face <name>' prints a spot and a yaw for s1.py tp.
                    // Without a name it takes the first working employee with a free view.
                    string want = args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : "";
                    foreach (Employee e in Rel.Employees())
                    {
                        if (want.Length > 0 && Rel.Name(e).IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (want.Length == 0)
                        {
                            NpcBehaviour act = e.Behaviour != null ? e.Behaviour.activeBehaviour : null;
                            if (act == null || !act.Active || !Rel.IsWork(act)) continue;
                        }
                        Vector3 at = e.transform.position;
                        Vector3 fwd = e.transform.forward; fwd.y = 0f;
                        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
                        // Employees often work facing a wall or a table: try eight directions around them, starting in
                        // front, and take the first spot where the crack's own probe (a sphere cast from eye height on the
                        // melee layers, as Crack runs it) meets nothing but this employee.
                        Vector3 spot = at + fwd.normalized * 1.4f;
                        string view = "no free view, standing in front";
                        PlayerCamera pc = PlayerSingleton<PlayerCamera>.Instance;
                        Player me = Player.Local;
                        // After the teleport the camera keeps its height above the floor and its pitch: the player
                        // transform sits at the capsule centre, so the floor comes from a ray down, and the crack ray
                        // tilts with the camera.
                        float eyeHeight = 1.75f, pitch = 0f;
                        if (pc != null && me != null)
                        {
                            if (Physics.Raycast(me.transform.position, Vector3.down, out RaycastHit floor, 3f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                                eyeHeight = pc.transform.position.y - floor.point.y;
                            else Core.Log?.Warning("[Dev] [Whip] face: no floor under the player, eye height 1.75 m assumed.");
                            pitch = pc.transform.eulerAngles.x;
                        }
                        var cm = NetworkSingleton<CombatManager>.Instance;
                        int mask = cm != null ? (int)cm.MeleeLayerMask : Physics.DefaultRaycastLayers;
                        var blockers = new List<string>();
                        bool free = false;
                        for (int k = 0; k < 8; k++)
                        {
                            Vector3 dir = Quaternion.Euler(0f, 45f * k, 0f) * fwd.normalized;
                            Vector3 cand = at + dir * 1.4f;
                            Vector3 eye = cand + Vector3.up * eyeHeight;
                            Vector3 aim = Quaternion.Euler(pitch, Quaternion.LookRotation(-dir).eulerAngles.y, 0f) * Vector3.forward;
                            // The game turns off the colliders of far NPCs, so the employee itself may not answer yet; what
                            // counts is that nothing else sits on the first 1.2 m of the crack ray.
                            // A sphere cast skips colliders that already overlap its start sphere, so the spot itself is
                            // checked first: standing inside the trigger of the pot or station the employee works at, the
                            // crack hits that trigger.
                            string inside = null;
                            foreach (Collider c in Physics.OverlapSphere(eye, AimRadius, mask, QueryTriggerInteraction.Collide))
                                if (c != null && c.GetComponentInParent<Employee>() != e && c.GetComponentInParent<Player>() == null) { inside = c.name; break; }
                            if (inside != null) { blockers.Add((45 * k) + ": inside " + inside); continue; }
                            if (Physics.SphereCast(eye, AimRadius, aim, out RaycastHit probe, 1.2f, mask, QueryTriggerInteraction.Collide)
                                && probe.collider != null && probe.collider.GetComponentInParent<Employee>() != e)
                            { blockers.Add((45 * k) + ": " + probe.collider.name); continue; }
                            spot = cand;
                            view = "free view at " + (45 * k) + " deg";
                            free = true;
                            break;
                        }
                        if (!free)
                        {
                            Core.Log?.Msg("[Dev] [Whip] face " + Rel.Name(e) + ": the crack probe (eye " + eyeHeight.ToString("F2") + " m, pitch "
                                + pitch.ToString("F0") + ") is blocked from every side (" + string.Join(", ", blockers) + ").");
                            if (want.Length == 0) continue;
                        }
                        Vector3 look = at - spot;
                        float yaw = Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg;
                        Core.Log?.Msg(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "[Dev] [Whip] face {0}: spot={1:F2},{2:F2},{3:F2} yaw={4:F1} ({5})", Rel.Name(e), spot.x, at.y, spot.z, yaw, view));
                        return;
                    }
                    Core.Log?.Warning(want.Length > 0 ? "[Dev] [Whip] face: no employee matches '" + want + "'."
                        : "[Dev] [Whip] face: no working employee with a free view.");
                    return;
                }
                case "burst":
                {
                    // Clicks through the same gate as the mouse: 'awe whip burst 5 0.3'.
                    int count = args.Length >= 2 && int.TryParse(args[1], out int c) ? Mathf.Clamp(c, 1, 20) : 5;
                    float every = args.Length >= 3 && float.TryParse(args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float e) ? Mathf.Max(0.02f, e) : 0.3f;
                    if (!WhipItem.IsEquipped()) { Core.Log?.Warning("[Dev] [Whip] burst: the whip is not in hand ('awe whip hold')."); return; }
                    _burstLeft = count;
                    _burstInterval = every;
                    _burstNext = Time.unscaledTime;
                    Core.Log?.Msg("[Dev] [Whip] burst: " + count + " clicks, one every " + every.ToString("F2") + " s.");
                    return;
                }
                case "turn":
                {
                    // Turns the player smoothly, like the mouse: 'awe whip turn 90 1' = 90 degrees over 1 s.
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    float deg = args.Length >= 2 && float.TryParse(args[1], System.Globalization.NumberStyles.Float, inv, out float d) ? d : 90f;
                    float secs = args.Length >= 3 && float.TryParse(args[2], System.Globalization.NumberStyles.Float, inv, out float t) ? Mathf.Max(0.05f, t) : 1f;
                    _turnRate = deg / secs;
                    _turnLeft = secs;
                    Core.Log?.Msg("[Dev] [Whip] turn: " + deg.ToString("F0", inv) + " deg over " + secs.ToString("F2", inv) + " s.");
                    return;
                }
                case "walk":
                {
                    // Walks forward like a held W key: 'awe whip walk 4' or 'awe whip walk 4 sprint'. With 'turn' at the
                    // same time the player walks a circle.
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    float secs = args.Length >= 2 && float.TryParse(args[1], System.Globalization.NumberStyles.Float, inv, out float t) ? Mathf.Clamp(t, 0.1f, 30f) : 3f;
                    _walkSprint = args.Length >= 3 && args[2].Equals("sprint", StringComparison.OrdinalIgnoreCase);
                    _walkLeft = secs;
                    Core.Log?.Msg("[Dev] [Whip] walk: " + secs.ToString("F1", inv) + " s" + (_walkSprint ? ", sprinting." : "."));
                    return;
                }
                case "lash":
                    Core.Log?.Msg("[Dev] [Whip] lash: " + WhipLash.Tune(args.Length >= 2 ? args[1] : null, args.Length >= 3 ? args[2] : null));
                    return;
                case "vm":
                    if (args.Length >= 2 && args[1].Equals("aim", StringComparison.OrdinalIgnoreCase))
                    {
                        float tilt = 20f, yaw = 0f;
                        if (args.Length >= 3) float.TryParse(args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out tilt);
                        if (args.Length >= 4) float.TryParse(args[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out yaw);
                        Core.Log?.Msg("[Dev] [Whip] vm aim: " + WhipEquippable.AimRotation(tilt, yaw));
                    }
                    else if (args.Length >= 2 && args[1].Equals("arm", StringComparison.OrdinalIgnoreCase))
                    {
                        // 'awe whip vm arm dx dy dz [rx ry rz]': shift of the arm in the camera's frame, metres and degrees.
                        var v = new float[6];
                        for (int i = 0; i < 6 && i + 2 < args.Length; i++)
                            float.TryParse(args[i + 2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v[i]);
                        Core.Log?.Msg("[Dev] [Whip] vm arm: " + WhipEquippable.SetArm(new Vector3(v[0], v[1], v[2]), new Vector3(v[3], v[4], v[5])));
                    }
                    else if (args.Length >= 2 && args[1].Equals("leftarm", StringComparison.OrdinalIgnoreCase))
                    {
                        WhipEquippable.HideLeftArm = !(args.Length >= 3 && args[2].Equals("show", StringComparison.OrdinalIgnoreCase));
                        Core.Log?.Msg("[Dev] [Whip] vm leftarm: " + (WhipEquippable.HideLeftArm ? "hidden while the whip is in hand." : "shown."));
                        return;
                    }
                    else if (args.Length >= 2 && args[1].Equals("grip", StringComparison.OrdinalIgnoreCase))
                    {
                        float along = 0f;
                        if (args.Length >= 3) float.TryParse(args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out along);
                        Core.Log?.Msg("[Dev] [Whip] vm grip: " + WhipEquippable.GripPosition(along));
                    }
                    else if (args.Length >= 8 && float.TryParse(args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float px)
                        && float.TryParse(args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float py)
                        && float.TryParse(args[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float pz)
                        && float.TryParse(args[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rx)
                        && float.TryParse(args[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ry)
                        && float.TryParse(args[6], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rz)
                        && float.TryParse(args[7], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sc))
                    {
                        WhipItem.HeldPosition = new Vector3(px, py, pz);
                        WhipItem.HeldRotation = new Vector3(rx, ry, rz);
                        WhipItem.HeldScale = sc;
                    }
                    Core.Log?.Msg("[Dev] [Whip] vm pos=" + WhipItem.HeldPosition.ToString("F3") + " rot=" + WhipItem.HeldRotation.ToString("F1") + " scale=" + WhipItem.HeldScale
                        + ", moved " + WhipEquippable.Replace() + " model(s).");
                    return;
                case "icon":
                {
                    GameObject iconModel = WhipItem.BuildIconModel();
                    Core.Log?.Msg("[Dev] [Whip] " + IconRenderer.SaveToDisk(iconModel, args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : "awe_whip_icon.png"));
                    UnityEngine.Object.Destroy(iconModel);
                    return;
                }
                default:
                    Core.Log?.Warning("[Dev] awe whip: unknown mode '" + mode + "'.");
                    return;
            }
        }
#endif
    }
}
