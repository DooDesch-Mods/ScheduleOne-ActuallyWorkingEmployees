using System;
using Il2CppScheduleOne.PlayerScripts;   // ViewmodelAvatar, PlayerCamera

namespace ActuallyWorkingEmployees.Features.Whip
{
    /// <summary>
    /// The lash of the whip in hand, a chain of points in camera space.
    ///
    /// * At rest it hangs from the handle tip in a fixed curve and follows the hand with a short lag that grows towards
    ///   the tip, so it sways a little and slowly, but walking, running or the head bob never fling it.
    /// * A click lifts it into a loop and rolls the loop out towards the crosshair; the loop runs down the lash faster and
    ///   faster and reaches the tip exactly at <see cref="SnapDelay"/>, the moment of the crack. From there the chain moves
    ///   freely (Verlet integration with distance and bending constraints), falls under gravity and eases back into the
    ///   hanging curve.
    ///
    /// Purely local and visual: nothing here is networked, the hit test and its effects run in <see cref="Whip"/>.
    /// </summary>
    internal static class WhipLash
    {
        // Tuned live with 'awe whip lash <name> <value>' (Debug build).
        internal static float SnapDelay = 0.17f;     // s from the click to the crack
        internal static float ThrowAt = 0.07f;       // s from the click to the roll-out; before it the lash lifts into the loop
        internal static float LoopStart = 0.12f;     // model units, loop radius when the roll-out starts
        internal static float LoopEnd = 0.03f;       // model units, loop radius when it reaches the tip
        internal static float SnapKick = 5f;         // model units/s along the throw for the last points at the crack
        internal static float Stiffness = 0.12f;     // bending pull towards the previous segment, at the base, while the lash moves freely
        internal static float RestStiffness = 0.03f; // the same while it settles after a swing
        internal static float Damping = 0.95f;       // velocity kept per substep while it moves freely after the crack
        internal static float RestDamping = 0.9f;    // velocity kept per substep while it settles
        internal static float RestPull = 9f;         // 1/s, pull towards the hanging curve while it settles
        internal static float RestLag = 0.12f;       // s, how far the tip lags behind the hand at rest; the base follows at once
        internal static float DownLag = 0.3f;        // s, gravity seen from the camera is smoothed over this, against head bob
        internal static float Inertia = 0.5f;        // share of a camera turn the lash lags behind during a swing
        internal static float RestInertia = 0.1f;    // the same at rest
        internal static float Smooth = 0.2f;         // pull of each point towards the middle of its neighbours during a swing, against zigzag kinks
        private const float SwingTime = 0.4f;        // s after the click that count as the swing
        private const float RestFadeIn = 0.3f;       // s over which the lash settles after a swing before it follows the hand again

        // The roll-out aims at this point in camera space (m): a little below the crosshair, so the lash cracks where the player looks.
        private static readonly Vector3 AimPoint = new Vector3(0f, -0.1f, 1.6f);

        private const int Substeps = 3;
        private const int Iterations = 4;

        private static Transform _root;
        private static Transform _lash;
        private static Transform _frame;   // the player camera; the chain points are stored in its local space
        private static Mesh _mesh;
        private static Vector3[] _p, _prev, _local, _rest, _target;
        private static float[] _radii;
        private static Vector3 _lastAnchor;
        private static Vector3 _framePos;
        private static Quaternion _frameRot;
        private static bool _hasFrame;
        private static Vector3 _down;
        private static bool _hasDown;
        private static Vector3 _rollDir = Vector3.forward;
        private static float _scale = 1f;
        private static float _swingStart = -1f, _swingEndAt = -10f, _frozenUntil = -1f;
        private static bool _rolling, _snapped;
#if DEBUG
        private static float _measureEnd = -1f, _measureStart;
        private static readonly System.Collections.Generic.List<Vector3> _tips = new System.Collections.Generic.List<Vector3>();
        private static readonly System.Collections.Generic.List<Vector3> _rels = new System.Collections.Generic.List<Vector3>();
        private static readonly System.Collections.Generic.List<Vector3> _hands = new System.Collections.Generic.List<Vector3>();
        private static readonly System.Collections.Generic.List<Vector3> _hands2 = new System.Collections.Generic.List<Vector3>();
        private static readonly System.Collections.Generic.List<Vector3> _offs = new System.Collections.Generic.List<Vector3>();
        private static readonly System.Collections.Generic.List<float> _tipTimes = new System.Collections.Generic.List<float>();
#endif
        private static float _nextSearch;
        private static bool _reportedMissing;

        /// <summary>Starts the throw of the lash. Without a lash in hand it does nothing; the crack still follows on time.</summary>
        internal static void StartSwing()
        {
            _swingStart = Time.time;
            _rolling = false;
            _snapped = false;
        }

        /// <summary>Hit stop: the lash holds still for <paramref name="seconds"/>.</summary>
        internal static void Freeze(float seconds) => _frozenUntil = Time.time + seconds;

        /// <summary>The lash tip in camera space, the camera and the lash's layer, for the effects at the crack. False without a lash in hand.</summary>
        internal static bool Tip(out Transform frame, out Vector3 tip, out Vector3 throwDir, out int layer, out float scale)
        {
            frame = _frame;
            tip = Vector3.zero;
            throwDir = _rollDir;
            layer = 0;
            scale = _scale;
            if (_p == null || _frame == null || _lash == null) return false;
            tip = _p[_p.Length - 1];
            layer = _lash.gameObject.layer;
            return true;
        }

        internal static void Tick(float dt)
        {
            try
            {
                if (!Find()) return;
                _frame = PlayerSingleton<PlayerCamera>.Instance != null ? PlayerSingleton<PlayerCamera>.Instance.transform : _root.parent;
                if (_frame == null) return;
                float s = (_root.TransformPoint(Vector3.forward) - _root.position).magnitude;   // model units to world
                if (s <= 1e-5f) return;
                _scale = s;
                ApplyInertia();
                Vector3 anchor = _frame.InverseTransformPoint(_root.TransformPoint(new Vector3(0f, 0f, WhipModel.HandleTipZ)));
                Vector3 handleFwd = _frame.InverseTransformDirection(_root.TransformDirection(Vector3.forward)).normalized;
                float seg = WhipModel.LashLength / (WhipModel.LashPoints - 1) * s;

                // First frame in hand, or a jump of the hand: the chain starts from the resting pose instead of flying after it.
                if (_p == null || (anchor - _lastAnchor).sqrMagnitude > (seg * WhipModel.LashPoints) * (seg * WhipModel.LashPoints))
                    Reset();
                _lastAnchor = anchor;

                dt = Mathf.Min(dt, 1f / 30f);
                if (dt > 0f) UpdateDown(dt);
                if (dt > 0f && Time.time >= _frozenUntil) Simulate(dt, anchor, handleFwd, seg, s);
                _p[0] = anchor;   // also while the game is paused: the lash stays on the handle tip
                Write();
#if DEBUG
                RecordTip();
#endif
            }
            catch (Exception e)
            {
                Core.Caught("whip: move the lash", e);
                Clear();
            }
        }

        /// <summary>Gravity in camera space, smoothed, so the hanging curve does not swing with every bob of the head.</summary>
        private static void UpdateDown(float dt)
        {
            Vector3 now = _frame.InverseTransformDirection(Vector3.down);
            _down = _hasDown ? Vector3.Slerp(_down, now, 1f - Mathf.Exp(-dt / Mathf.Max(DownLag, 1e-3f))).normalized : now;
            _hasDown = true;
        }

        private static float RestWeight() => _swingStart >= 0f ? 0f : Mathf.Clamp01((Time.time - _swingEndAt) / RestFadeIn);

        private static void Simulate(float dt, Vector3 anchor, Vector3 handleFwd, float seg, float s)
        {
            float since = _swingStart >= 0f ? Time.time - _swingStart : -1f;
            if (since > SwingTime) { _swingStart = -1f; _swingEndAt = Time.time; since = -1f; }
            if (since >= 0f && since < SnapDelay) { Roll(dt, since, anchor, seg, s); return; }

            float h = dt / Substeps;
            if (since >= 0f && !_snapped)
            {
                // The loop has reached the tip: the last points fly on along the throw and the constraints snap them back.
                _snapped = true;
                int n0 = _p.Length;
                for (int i = n0 - 3; i < n0; i++)
                {
                    float w = (float)(i - (n0 - 4)) / 3f;
                    _prev[i] -= _rollDir * (SnapKick * s * w * h);
                }
            }
            float restWeight = RestWeight();
            if (restWeight >= 1f) { Follow(dt, anchor, handleFwd, seg); return; }
            Free(dt, h, anchor, handleFwd, seg, s, since >= 0f, restWeight);
        }

        /// <summary>
        /// The throw: before <see cref="ThrowAt"/> the lash lifts into a loop over the hand, then the loop rolls along the
        /// lash towards the aim point. Everything behind the loop lies straight along the throw; ahead of it the lash runs
        /// over the loop and back. The loop speeds up and shrinks on its way, as on a tapered whip, and reaches the tip at
        /// <see cref="SnapDelay"/>.
        /// </summary>
        private static void Roll(float dt, float since, Vector3 anchor, float seg, float s)
        {
            int n = _p.Length;
            Vector3 dir = (AimPoint - anchor).normalized;
            Vector3 up = Vector3.Cross(dir, Vector3.right).normalized;
            Vector3 right = Vector3.Cross(up, dir).normalized;
            Vector3 back = (-dir + 0.6f * right + 0.5f * up).normalized;   // the part not yet thrown trails over the shoulder
            _rollDir = dir;

            float u = Mathf.Clamp01((since - ThrowAt) / Mathf.Max(SnapDelay - ThrowAt, 1e-3f));
            float e = Mathf.Pow(u, 1.5f);
            float k = e * (n - 1);
            float r = Mathf.Lerp(LoopStart, LoopEnd, e) * s;
            Vector3 fold = anchor + dir * (seg * k);
            float arc = Mathf.PI * r;
            if (_target == null || _target.Length != n) { _target = new Vector3[n]; }
            _target[0] = anchor;
            for (int i = 1; i < n; i++)
            {
                if (i <= k) { _target[i] = anchor + dir * (seg * i); continue; }
                float d = (i - k) * seg;
                if (d <= arc)
                {
                    float th = d / r;
                    _target[i] = fold + dir * (r * Mathf.Sin(th)) + up * (r * (1f - Mathf.Cos(th)));
                }
                else _target[i] = fold + up * (2f * r) + back * (d - arc);
            }
            // The chain is rebuilt from the hand outwards, segment by segment, so every segment keeps its length and no
            // point can fold back against its neighbours. During the lift each segment turns from its direction at the
            // click into the direction of the pose; from the roll-out on it lies on the pose.
            if (!_rolling)
            {
                _rolling = true;
                if (_startDir == null || _startDir.Length != n) _startDir = new Vector3[n];
                for (int i = 1; i < n; i++)
                {
                    Vector3 d = _p[i] - _p[i - 1];
                    _startDir[i] = d.sqrMagnitude > 1e-12f ? d.normalized : dir;
                }
            }
            float w = Mathf.Clamp01(since / Mathf.Max(ThrowAt, 1e-3f));
            w = w * w * (3f - 2f * w);
            if (_old == null || _old.Length != n) _old = new Vector3[n];
            Array.Copy(_p, _old, n);
            _p[0] = anchor;
            for (int i = 1; i < n; i++)
            {
                Vector3 td = _target[i] - _target[i - 1];
                td = td.sqrMagnitude > 1e-12f ? td.normalized : dir;
                Vector3 d = w >= 1f ? td : Vector3.Slerp(_startDir[i], td, w).normalized;
                _p[i] = _p[i - 1] + d * seg;
            }
            // The speed of the pose stays in the chain, so the lash flies on with it once the roll-out ends.
            for (int i = 0; i < n; i++) _prev[i] = _p[i] - (_p[i] - _old[i]) / Substeps;
            _prev[0] = anchor;
        }
        private static Vector3[] _startDir, _old;

        /// <summary>After the crack: the chain moves on its own under gravity and settles into the hanging curve.</summary>
        private static void Free(float dt, float h, Vector3 anchor, Vector3 handleFwd, float seg, float s, bool swinging, float restWeight)
        {
            int n = _p.Length;
            float damping = swinging ? Damping : RestDamping;
            float stiffness = Mathf.Lerp(Stiffness, RestStiffness, restWeight);
            Vector3 g = _frame.InverseTransformDirection(Vector3.down) * (9.81f * s) * (h * h);
            for (int sub = 0; sub < Substeps; sub++)
            {
                for (int i = 1; i < n; i++)
                {
                    Vector3 v = (_p[i] - _prev[i]) * damping;
                    _prev[i] = _p[i];
                    _p[i] += v + g;
                }
                if (swinging && Smooth > 0f)
                    for (int i = 1; i < n - 1; i++)
                        _p[i] = Vector3.Lerp(_p[i], (_p[i - 1] + _p[i + 1]) * 0.5f, Smooth);
                for (int it = 0; it < Iterations; it++)
                {
                    _p[0] = anchor;
                    // Bending: each segment leans towards the direction of the one before it, the first towards the
                    // handle, stronger at the thick base than at the thin tip.
                    for (int i = 1; i < n; i++)
                    {
                        Vector3 dir = i == 1 ? handleFwd : (_p[i - 1] - _p[i - 2]).normalized;
                        float u = (float)(i - 1) / (n - 1);
                        float k = stiffness * (1f - u) * (1f - u);
                        _p[i] = Vector3.Lerp(_p[i], _p[i - 1] + dir * seg, k);
                    }
                    Constrain(anchor, seg);
                }
                _p[0] = anchor;
                _prev[0] = anchor;
            }

            if (restWeight > 0f && RestPull > 0f)
            {
                HangingPose(anchor, handleFwd, seg);
                float a = (1f - Mathf.Exp(-RestPull * dt)) * restWeight;
                for (int i = 1; i < n; i++)
                {
                    _p[i] = Vector3.Lerp(_p[i], _rest[i], a);
                    _prev[i] = Vector3.Lerp(_prev[i], _rest[i], a);
                }
            }
        }

        /// <summary>
        /// At rest: each point eases towards the hanging curve of this frame's hand, the base at once, the tip over
        /// <see cref="RestLag"/>. No velocity is kept, so nothing can swing up: the hand's own bob reaches the tip only as a
        /// short, smooth lag.
        /// </summary>
        private static void Follow(float dt, Vector3 anchor, Vector3 handleFwd, float seg)
        {
            int n = _p.Length;
            HangingPose(anchor, handleFwd, seg);
            for (int i = 1; i < n; i++)
            {
                float lag = RestLag * i / (n - 1);
                float a = lag > 1e-4f ? 1f - Mathf.Exp(-dt / lag) : 1f;
                _p[i] = Vector3.Lerp(_p[i], _rest[i], a);
            }
            _p[0] = anchor;
            for (int i = 1; i < n; i++)   // keep the segment lengths, from the hand outwards
            {
                Vector3 d = _p[i] - _p[i - 1];
                float len = d.magnitude;
                if (len > 1e-7f) _p[i] = _p[i - 1] + d * (seg / len);
            }
            Array.Copy(_p, _prev, n);
        }

        /// <summary>Distance constraints: every segment back to its length, the first one moving only its outer point.</summary>
        private static void Constrain(Vector3 anchor, float seg)
        {
            _p[0] = anchor;
            for (int i = 1; i < _p.Length; i++)
            {
                Vector3 d = _p[i] - _p[i - 1];
                float len = d.magnitude;
                if (len < 1e-7f) continue;
                Vector3 corr = d * (1f - seg / len);
                if (i == 1) _p[i] -= corr;
                else { _p[i - 1] += corr * 0.5f; _p[i] -= corr * 0.5f; }
            }
        }

        /// <summary>
        /// The curve the lash hangs in, in camera space: it leaves the handle tip along the handle and bends over into
        /// the (smoothed) direction of gravity within its first 40 %, so it hangs forward and down.
        /// </summary>
        private static void HangingPose(Vector3 anchor, Vector3 handleFwd, float seg)
        {
            int n = _p.Length;
            if (_rest == null || _rest.Length != n) _rest = new Vector3[n];
            Vector3 down = _hasDown ? _down : _frame.InverseTransformDirection(Vector3.down);
            _rest[0] = anchor;
            for (int i = 1; i < n; i++)
            {
                float t = Mathf.Clamp01(((i - 0.5f) / (n - 1)) * 2.5f);
                t = t * t * (3f - 2f * t);
                _rest[i] = _rest[i - 1] + Vector3.Slerp(handleFwd, down, t).normalized * seg;
            }
        }

        /// <summary>
        /// Turns the chain back by part of the camera's turn since the last frame, so it trails a turn a little. Only the
        /// rotation counts: the camera's own movement (walking, running, falling, head bob) does not move the lash at all.
        /// A jump of more than 45 degrees in one frame is a cut and is skipped.
        /// </summary>
        private static void ApplyInertia()
        {
            Vector3 pos = _frame.position;
            Quaternion rot = _frame.rotation;
            float share = Mathf.Lerp(Inertia, RestInertia, RestWeight());
            if (_p != null && _hasFrame && share > 0f && Quaternion.Angle(rot, _frameRot) < 45f)
            {
                for (int i = 1; i < _p.Length; i++)
                {
                    Vector3 lagging = _frame.InverseTransformPoint(pos + _frameRot * _p[i]);
                    _p[i] = Vector3.Lerp(_p[i], lagging, share);
                }
            }
            _framePos = pos;
            _frameRot = rot;
            _hasFrame = true;
        }

        private static void Reset()
        {
            Vector3[] rest = WhipModel.Hanging();
            int n = rest.Length;
            _p = new Vector3[n];
            _prev = new Vector3[n];
            _local = new Vector3[n];
            for (int i = 0; i < n; i++) { _p[i] = _frame.InverseTransformPoint(_root.TransformPoint(rest[i])); _prev[i] = _p[i]; }
            if (_radii == null) _radii = WhipModel.LashRadii();
        }

        private static void Write()
        {
            for (int i = 0; i < _p.Length; i++) _local[i] = _lash.InverseTransformPoint(_frame.TransformPoint(_p[i]));
            WhipModel.WriteTube(_mesh, _local, _radii, WhipModel.LashSides, false, false);
        }

        /// <summary>Finds the lash of the whip in hand. False while no whip is held; the cached chain is dropped then.</summary>
        private static bool Find()
        {
            if (!WhipItem.IsEquipped()) { Clear(); return false; }
            if (_root != null && _lash != null && _mesh != null) return true;
            if (Time.time < _nextSearch) return false;
            _nextSearch = Time.time + 0.5f;

            var avatar = Singleton<ViewmodelAvatar>.Instance;
            if (avatar == null || avatar.RightHandContainer == null) return false;
            foreach (Transform t in avatar.RightHandContainer.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t.name != WhipEquippable.ModelName) continue;
                Transform lash = t.Find(WhipModel.LashName);
                MeshFilter mf = lash != null ? lash.GetComponent<MeshFilter>() : null;
                if (mf == null) continue;
                _root = t;
                _lash = lash;
                _mesh = mf.mesh;   // its own copy, so the template and the next equip start from the resting pose
                _mesh.MarkDynamic();
                _p = null;
                _reportedMissing = false;
                Core.Log?.Msg("[Whip] lash in hand found, " + WhipModel.LashPoints + " points.");
                return true;
            }
            if (!_reportedMissing)
            {
                _reportedMissing = true;
                Core.Log?.Warning("[Whip] the whip is in hand but has no lash to move.");
            }
            return false;
        }

        private static void Clear()
        {
            _root = null;
            _lash = null;
            _frame = null;
            _hasFrame = false;
            _hasDown = false;
            _mesh = null;
            _p = null;
            _swingStart = -1f;
            _rolling = false;
        }

#if DEBUG
        /// <summary>
        /// 'awe whip lash measure s': samples the tip in camera space every frame and then logs how far it strayed from
        /// its mean, and from what time on it stayed within 1 cm of its last position.
        /// </summary>
        private static void RecordTip()
        {
            if (_measureEnd < 0f || _p == null) return;
            _tips.Add(_p[_p.Length - 1]);
            _rels.Add(_p[_p.Length - 1] - _p[0]);
            _hands.Add(_frame.InverseTransformPoint(_root.position));
            _hands2.Add(_p[0]);
            HangingPose(_p[0], _frame.InverseTransformDirection(_root.TransformDirection(Vector3.forward)).normalized,
                (_p[1] - _p[0]).magnitude);
            _offs.Add(_p[_p.Length - 1] - _rest[_p.Length - 1]);
            _tipTimes.Add(Time.time - _measureStart);
            if (Time.time < _measureEnd) return;
            _measureEnd = -1f;
            int count = _tips.Count;
            Vector3 mean = Vector3.zero;
            for (int i = 0; i < count; i++) mean += _tips[i];
            mean /= Mathf.Max(1, count);
            float maxDev = 0f;
            for (int i = 0; i < count; i++) maxDev = Mathf.Max(maxDev, (_tips[i] - mean).magnitude);
            Vector3 last = _tips[count - 1];
            float settled = 0f;
            for (int i = count - 1; i >= 0; i--)
                if ((_tips[i] - last).magnitude > 0.01f) { settled = i + 1 < count ? _tipTimes[i + 1] : _tipTimes[i]; break; }
            // The same against the handle tip: the hand's own sway (vanilla viewmodel lag after a turn) is not lash wobble.
            Vector3 lastRel = _rels[count - 1];
            float relMax = 0f, relSettled = 0f;
            for (int i = 0; i < count; i++) relMax = Mathf.Max(relMax, (_rels[i] - lastRel).magnitude);
            for (int i = count - 1; i >= 0; i--)
                if ((_rels[i] - lastRel).magnitude > 0.01f) { relSettled = i + 1 < count ? _tipTimes[i + 1] : _tipTimes[i]; break; }
            // The hand (handle base and tip) still within 3 mm, and the lash's own motion: the tip against the hanging pose
            // of this frame's hand, which takes out the arm's recovery after a swing.
            float handSettled = 0f;
            for (int i = count - 1; i >= 0; i--)
                if ((_hands[i] - _hands[count - 1]).magnitude > 0.003f || (_hands2[i] - _hands2[count - 1]).magnitude > 0.003f)
                { handSettled = i + 1 < count ? _tipTimes[i + 1] : _tipTimes[i]; break; }
            float offMax = 0f, offSettled = 0f;
            for (int i = 0; i < count; i++) offMax = Mathf.Max(offMax, _offs[i].magnitude);
            for (int i = count - 1; i >= 0; i--)
                if ((_offs[i] - _offs[count - 1]).magnitude > 0.01f) { offSettled = i + 1 < count ? _tipTimes[i + 1] : _tipTimes[i]; break; }
            Core.Log?.Msg("[Whip] lash measure: " + count + " frames over " + _tipTimes[count - 1].ToString("F2") + " s, tip max deviation "
                + (maxDev * 100f).ToString("F2") + " cm from its mean, within 1 cm of its last position from " + settled.ToString("F2")
                + " s; against the hand " + (relMax * 100f).ToString("F2") + " cm at most, still from " + relSettled.ToString("F2")
                + " s; hand still from " + handSettled.ToString("F2") + " s; off its hanging pose " + (offMax * 100f).ToString("F2")
                + " cm at most, settled from " + offSettled.ToString("F2") + " s.");
        }

        /// <summary>'awe whip lash [name value]': lists the lash values or sets one.</summary>
        internal static string Tune(string name, string value)
        {
            if (name != null && name.Equals("measure", StringComparison.OrdinalIgnoreCase))
            {
                float secs = 3f;
                if (value != null && !float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out secs))
                    return "'" + value + "' is not a number.";
                if (_root == null) return "no lash in hand ('awe whip hold').";
                _tips.Clear();
                _rels.Clear();
                _hands.Clear();
                _hands2.Clear();
                _offs.Clear();
                _tipTimes.Clear();
                _measureStart = Time.time;
                _measureEnd = Time.time + Mathf.Clamp(secs, 0.2f, 30f);
                return "measuring the tip for " + secs.ToString("F1") + " s.";
            }
            if (name != null && value != null)
            {
                if (!float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v))
                    return "'" + value + "' is not a number.";
                switch (name.ToLowerInvariant())
                {
                    case "snap": SnapDelay = v; break;
                    case "throwat": ThrowAt = v; break;
                    case "loop": LoopStart = v; break;
                    case "loopend": LoopEnd = v; break;
                    case "kick": SnapKick = v; break;
                    case "stiff": Stiffness = v; break;
                    case "rstiff": RestStiffness = v; break;
                    case "damp": Damping = v; break;
                    case "rdamp": RestDamping = v; break;
                    case "smooth": Smooth = v; break;
                    case "pull": RestPull = v; break;
                    case "rlag": RestLag = v; break;
                    case "dlag": DownLag = v; break;
                    case "inertia": Inertia = v; break;
                    case "rinertia": RestInertia = v; break;
                    default: return "unknown value '" + name + "' (snap, throwat, loop, loopend, kick, stiff, rstiff, damp, rdamp, smooth, pull, rlag, dlag, inertia, rinertia, measure).";
                }
            }
            return "snap=" + SnapDelay + " throwat=" + ThrowAt + " loop=" + LoopStart + " loopend=" + LoopEnd + " kick=" + SnapKick
                + " stiff=" + Stiffness + " rstiff=" + RestStiffness + " damp=" + Damping + " rdamp=" + RestDamping + " smooth=" + Smooth + " pull=" + RestPull
                + " rlag=" + RestLag + " dlag=" + DownLag + " inertia=" + Inertia + " rinertia=" + RestInertia + (_root != null ? " (lash in hand)" : " (no lash in hand)");
        }
#endif
    }
}
