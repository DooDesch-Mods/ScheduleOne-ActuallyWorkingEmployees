using System;
using Il2CppScheduleOne.Equipping;      // Equippable_AvatarViewmodel
using Il2CppScheduleOne.ItemFramework;  // ItemDefinition, StorableItemDefinition
using Il2CppScheduleOne.PlayerScripts;  // ViewmodelAvatar
using ActuallyWorkingEmployees.Features.Reliability;   // Rel

namespace ActuallyWorkingEmployees.Features.Whip
{
    /// <summary>
    /// The held first-person whip, built the way the game holds a weapon (same approach as Yoink's winch).
    ///
    /// A template GameObject carries a real <c>Equippable_AvatarViewmodel</c> with its fields copied from a vanilla held
    /// item, the whip model under it, and the registered definition points at it with <c>EquipMode = Legacy</c>.
    /// <c>HotbarSlot.Equip</c> then clones the template and the vanilla code parents it into the hand, sets the
    /// animator and raises the arm.
    ///
    /// * The template is active under an inactive holder, so it stays out of the scene and its clone comes out active.
    /// * EquipTrigger is never null: PlayEquipAnimation hands it to Animator.SetTrigger.
    /// * AvatarEquippable stays null: copying the donor's would show other players a pistol instead of a whip.
    /// * The template is a plain Equippable_AvatarViewmodel even when the donor is a melee weapon, so the vanilla melee
    ///   update never runs and a click does not damage the employee: the whip only raycasts.
    /// </summary>
    internal static class WhipEquippable
    {
        internal const string ModelName = "AWEWhipModel";
        private static GameObject _bank;
        private static GameObject _template;
        private static Equippable_AvatarViewmodel _eq;
        private static bool _failed;

        internal static bool Ready => _template != null;

        internal static void ResetSession()
        {
            try { if (_bank != null) UnityEngine.Object.Destroy(_bank); }
            catch (Exception e) { Core.Caught("whip: destroy the template holder", e); }
            _bank = null;
            _template = null;
            _eq = null;
            _failed = false;
        }

        private static Vector3 _donorArmOffset, _donorArmRotation;

        private const string LeftShoulderBone = "mixamorig:LeftShoulder";
        private static Transform _leftShoulder;
        private static Vector3 _leftShoulderScale;
        private static bool _leftHidden, _leftMissingLogged;
        /// <summary>Dev switch for 'awe whip vm leftarm'; the whip always hides the left arm otherwise.</summary>
        internal static bool HideLeftArm = true;

        /// <summary>
        /// The machete pose keeps the left hand beside the right one. With the arm moved towards the screen middle that hand
        /// comes into view next to the whip, so the left shoulder bone is collapsed while the whip is in hand and restored as
        /// soon as anything else is held. The avatar is shared by every held item, which is why the restore must never be skipped.
        /// </summary>
        internal static void TickLeftArm(bool equipped)
        {
            try
            {
                if (_leftHidden && _leftShoulder == null)
                {
                    _leftHidden = false;   // avatar rebuilt (scene change): the new one has its own untouched scale
                    Core.Log?.Msg("[Whip] left arm: the avatar was rebuilt, nothing to restore.");
                }
                bool want = equipped && HideLeftArm;
                if (!want)
                {
                    if (!_leftHidden) return;
                    _leftShoulder.localScale = _leftShoulderScale;
                    _leftHidden = false;
                    Core.Log?.Msg("[Whip] left arm restored.");
                    return;
                }
                if (_leftShoulder == null)
                {
                    var avatar = Singleton<ViewmodelAvatar>.Instance;
                    _leftShoulder = avatar != null ? FindDeep(avatar.transform, LeftShoulderBone) : null;
                    if (_leftShoulder == null)
                    {
                        if (!_leftMissingLogged) Core.Log?.Warning("[Whip] left arm: no '" + LeftShoulderBone + "' under the viewmodel avatar; the left hand stays visible.");
                        _leftMissingLogged = true;
                        return;
                    }
                }
                if (!_leftHidden)
                {
                    _leftShoulderScale = _leftShoulder.localScale;
                    _leftHidden = true;
                    Core.Log?.Msg("[Whip] left arm hidden while the whip is in hand.");
                }
                _leftShoulder.localScale = new Vector3(0.0001f, 0.0001f, 0.0001f);
            }
            catch (Exception e) { Core.Caught("whip: hide or restore the left arm", e); }
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform hit = FindDeep(root.GetChild(i), name);
                if (hit != null) return hit;
            }
            return null;
        }

#if DEBUG
        /// <summary>'awe whip vm arm': moves and turns the whole first-person arm against the donor's placement, live and for the next equip.</summary>
        internal static string SetArm(Vector3 offset, Vector3 rotation)
        {
            WhipItem.ArmOffset = offset;
            WhipItem.ArmRotation = rotation;
            if (_eq != null)
            {
                _eq.ViewmodelAvatarOffset = _donorArmOffset + offset;
                _eq.ViewmodelAvatarRotationOffset = _donorArmRotation + rotation;
            }
            var avatar = Singleton<ViewmodelAvatar>.Instance;
            if (avatar == null) return "no viewmodel avatar.";
            if (!WhipItem.IsEquipped()) return "arm offset " + offset.ToString("F3") + " rot " + rotation.ToString("F1") + " stored, the whip is not in hand.";
            avatar.SetOffset(_donorArmOffset + offset);
            avatar.SetRotationOffset(_donorArmRotation + rotation);
            return "arm offset " + offset.ToString("F3") + " rot " + rotation.ToString("F1") + " (donor " + _donorArmOffset.ToString("F3") + " / " + _donorArmRotation.ToString("F1") + ").";
        }
#endif

        /// <summary>Builds the template once the avatar exists. Does nothing after success or after a failure that will not change this load.</summary>
        internal static void EnsureInstalled(GameObject model)
        {
            if (_failed || _template != null || model == null) return;
            try
            {
                var avatar = Singleton<ViewmodelAvatar>.Instance;
                if (avatar == null || avatar.Animator == null || avatar.RightHandContainer == null) return;   // not up yet

                Equippable_AvatarViewmodel donor = FindDonor();
                if (donor == null) { _failed = true; return; }

                _bank = new GameObject("AWEEquippableTemplates");
                _bank.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_bank);

                _template = new GameObject("AWEWhipEquippable");
                _template.transform.SetParent(_bank.transform, false);
                _template.SetActive(true);

                Equippable_AvatarViewmodel eq = _template.AddComponent<Equippable_AvatarViewmodel>();
                eq.AnimatorController = donor.AnimatorController;
                _donorArmOffset = donor.ViewmodelAvatarOffset;
                _donorArmRotation = donor.ViewmodelAvatarRotationOffset;
                eq.ViewmodelAvatarOffset = _donorArmOffset + WhipItem.ArmOffset;
                eq.ViewmodelAvatarRotationOffset = _donorArmRotation + WhipItem.ArmRotation;
                eq.EquipTrigger = donor.EquipTrigger ?? string.Empty;
                eq.EquipTime = donor.EquipTime;
                eq.localPosition = donor.localPosition;
                eq.localEulerAngles = donor.localEulerAngles;
                eq.localScale = donor.localScale;
                eq.AvatarEquippable = null;
                eq.CanInteractWhenEquipped = true;
                eq.CanPickUpWhenEquipped = true;

                GameObject visual = UnityEngine.Object.Instantiate(model, _template.transform);
                visual.name = ModelName;
                Place(visual.transform);
                visual.SetActive(true);
                BuildSounds(donor);

                if (!PointDefinitionAtTemplate(eq)) { _failed = true; return; }
                _eq = eq;
                Core.Log?.Msg("[Whip] held model installed.");
            }
            catch (Exception e)
            {
                _failed = true;
                Core.Failed("whip: install the held model", e);
            }
        }

        internal const string AirName = "AWEWhipAir";
        internal const string CrackName = "AWEWhipCrack";
        internal const string ThumpName = "AWEWhipThump";
        private const float AirVolume = 0.5f;

        /// <summary>
        /// Two sound sources under the template, so every held whip carries its own: a soft air whoosh for the swing and
        /// the crack for the snap. Both are copies of the donor's whoosh source, so they go through the game's mixer
        /// group and volume setting; only their clips are replaced. None of the donor's own clips is kept.
        /// </summary>
        private static void BuildSounds(Equippable_AvatarViewmodel donor)
        {
            try
            {
                var melee = donor.TryCast<Equippable_MeleeWeapon>();
                if (melee == null || melee.WhooshSound == null) { Core.Log?.Warning("[Whip] the held-item donor has no swing sound source; the whip stays silent."); return; }
                // The recorded clip holds swish, crack and tail in one, cut so its crack lands at the snap: it plays on the
                // click and the snap plays nothing. Without it, the synthesized air and crack play at click and snap.
                AudioClip recorded = WhipSounds.Recorded();
                _recorded = recorded != null;
                // The synthesized crack is always built: with the recording it is layered quietly on the snap for a
                // sharper attack, without it it is the crack.
                bool air = CloneWithClip(melee.WhooshSound.gameObject, AirName, recorded ?? WhipSounds.Air());
                bool crack = CloneWithClip(melee.WhooshSound.gameObject, CrackName, WhipSounds.Crack());
                bool thump = CloneWithClip(melee.WhooshSound.gameObject, ThumpName, WhipSounds.Thump());
                Core.Log?.Msg("[Whip] sounds ready: swing " + (air ? "yes" : "no") + ", crack " + (crack ? "yes" : "no") + ", thump " + (thump ? "yes" : "no")
                    + " (" + WhipSounds.CrackSource + ").");
            }
            catch (Exception e) { Core.Caught("whip: build the sounds", e); }
        }

        private static bool CloneWithClip(GameObject source, string name, AudioClip clip)
        {
            if (clip == null) { Core.Log?.Warning("[Whip] no '" + name + "' sound: the clip was not built."); return false; }
            var go = UnityEngine.Object.Instantiate(source, _template.transform);
            go.name = name;
            var src = go.GetComponent<AudioSource>();
            if (src == null) { Core.Log?.Warning("[Whip] no '" + name + "' sound: the donor's sound object has no AudioSource."); UnityEngine.Object.Destroy(go); return false; }
            src.clip = clip;
            return true;
        }

        /// <summary>The swing: the donor's light release animation on the arm and the camera, the air whoosh and the throw of the lash.</summary>
        internal static void PlaySwing()
        {
            try
            {
                var avatar = Singleton<ViewmodelAvatar>.Instance;
                if (avatar != null && avatar.Animator != null) avatar.Animator.SetTrigger("Release_Light");
                var cam = PlayerSingleton<PlayerCamera>.Instance;
                if (cam != null && cam.Animator != null) cam.Animator.SetTrigger("Release_Light");
                bool air = _recorded
                    ? PlayHeld(AirName, UnityEngine.Random.Range(0.95f, 1.05f), RecordedVolume)   // moves the recorded crack by at most 8 ms
                    : PlayHeld(AirName, UnityEngine.Random.Range(0.9f, 1.1f), AirVolume);
                WhipLash.StartSwing();
                _swings++;
                Core.Log?.Msg("[Whip] swing " + _swings + ": arm animation, " + (_recorded ? "recorded crack " : "air ") + (air ? "played" : "missing") + ", lash thrown, snap in " + WhipLash.SnapDelay.ToString("F2") + " s.");
            }
            catch (Exception e) { Core.Caught("whip: swing", e); }
        }
        private static int _swings;
        private static bool _recorded;
        private const float RecordedVolume = 1f;

        /// <summary>The snap at the end of the swing: the crack, whether or not it hits anyone. Returns what happened, for the log.</summary>
        internal static string PlayCrack()
        {
            try
            {
                // With the recording, its own crack carries the sound and the synthesized one only sharpens the attack.
                bool played = _recorded
                    ? PlayHeld(CrackName, UnityEngine.Random.Range(0.94f, 1.06f), LayeredCrackVolume)
                    : PlayHeld(CrackName, UnityEngine.Random.Range(0.94f, 1.06f), 1f);
                return (_recorded ? "recorded, layered " : "") + (played ? "played" : "missing");
            }
            catch (Exception e) { Core.Caught("whip: crack", e); return "failed"; }
        }
        internal static float LayeredCrackVolume = 0.45f;

        // Hit stop and FOV punch, run out by TickEffects.
        internal static float HitStop = 0.07f;      // s the arm, the lash and the employee hold still on a hit
        internal static float FovPunch = 4f;        // degrees the view widens on a hit
        private static float _hitStopEnd = -1f, _fovBackAt = -1f;
        private static Animator _stopArm, _stopEmp;
        private static float _stopArmSpeed = 1f, _stopEmpSpeed = 1f;

        /// <summary>
        /// The crack lands on an employee: the thump, a heavy flinch, a short hit stop of arm, lash and employee, a camera
        /// shake and a small widening of the view.
        /// </summary>
        internal static void PlayImpact(Employee emp, Vector3 direction)
        {
            try
            {
                string who = emp != null ? Rel.Name(emp) : "?";
                var cam = PlayerSingleton<PlayerCamera>.Instance;
                cam?.StartCameraShake(0.18f, 0.18f, true);   // replaces the small shake of the snap
                bool thump = PlayHeld(ThumpName, UnityEngine.Random.Range(0.9f, 1.1f), 0.8f);
                bool flinch = false;
                var anim = emp != null && emp.Avatar != null ? emp.Avatar.Animation : null;
                if (anim == null) Core.Log?.Msg("[Whip] impact: " + who + " has no avatar animation, no flinch.");
                else if (anim.IsSeated) Core.Log?.Msg("[Whip] impact: " + who + " is seated, no flinch.");
                else
                {
                    anim.Flinch(direction, Il2CppScheduleOne.AvatarFramework.Animation.AvatarAnimation.EFlinchType.Heavy);
                    flinch = true;
                }
                string stop = StartHitStop(anim);
                string fov = "skipped";
                if (cam == null) fov = "no camera";
                else if (cam.fovOverriden) fov = "skipped, the view is already overridden";
                else if (cam.Camera == null) fov = "no camera component";
                else
                {
                    cam.OverrideFOV(cam.Camera.fieldOfView + FovPunch, 0.04f);
                    _fovBackAt = Time.time + 0.06f;
                    fov = "+" + FovPunch.ToString("F0") + " deg";
                }
                Core.Log?.Msg("[Whip] impact on " + who + ": thump " + (thump ? "played" : "missing") + ", heavy flinch " + (flinch ? "played" : "skipped")
                    + ", hit stop " + stop + ", view " + fov + ", camera shake.");
            }
            catch (Exception e) { Core.Caught("whip: impact", e); }
        }

        private static string StartHitStop(Il2CppScheduleOne.AvatarFramework.Animation.AvatarAnimation emp)
        {
            if (HitStop <= 0f) return "off";
            EndHitStop();
            var avatar = Singleton<ViewmodelAvatar>.Instance;
            _stopArm = avatar != null ? avatar.Animator : null;
            _stopEmp = emp != null ? emp._animator : null;
            if (_stopArm != null) { _stopArmSpeed = _stopArm.speed; _stopArm.speed = 0f; }
            if (_stopEmp != null) { _stopEmpSpeed = _stopEmp.speed; _stopEmp.speed = 0f; }
            WhipLash.Freeze(HitStop);
            _hitStopEnd = Time.time + HitStop;
            return (HitStop * 1000f).ToString("F0") + " ms (arm " + (_stopArm != null ? "yes" : "no") + ", employee " + (_stopEmp != null ? "yes" : "no") + ")";
        }

        /// <summary>Gives the animators back their speed; only where it is still the hit stop's 0, so nothing set since is undone.</summary>
        private static void EndHitStop()
        {
            if (_hitStopEnd < 0f) return;
            _hitStopEnd = -1f;
            if (_stopArm != null && _stopArm.speed == 0f) _stopArm.speed = _stopArmSpeed > 0f ? _stopArmSpeed : 1f;
            if (_stopEmp != null && _stopEmp.speed == 0f) _stopEmp.speed = _stopEmpSpeed > 0f ? _stopEmpSpeed : 1f;
            _stopArm = null;
            _stopEmp = null;
        }

        /// <summary>Ends the hit stop and the FOV punch on time. Called every frame by <see cref="Whip"/>.</summary>
        internal static void TickEffects()
        {
            try
            {
                if (_hitStopEnd >= 0f && Time.time >= _hitStopEnd) EndHitStop();
                if (_fovBackAt >= 0f && Time.time >= _fovBackAt)
                {
                    _fovBackAt = -1f;
                    var cam = PlayerSingleton<PlayerCamera>.Instance;
                    if (cam == null) Core.Log?.Msg("[Whip] view punch: the camera is gone, nothing to give back.");
                    else cam.StopFOVOverride(0.18f);
                }
            }
            catch (Exception e)
            {
                _hitStopEnd = -1f;
                _fovBackAt = -1f;
                Core.Caught("whip: end the hit effects", e);
            }
        }

        private static bool PlayHeld(string name, float pitch, float volume)
        {
            var avatar = Singleton<ViewmodelAvatar>.Instance;
            if (avatar == null || avatar.RightHandContainer == null) { Core.Log?.Msg("[Whip] sound '" + name + "': no viewmodel avatar."); return false; }
            foreach (Transform t in avatar.RightHandContainer.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t.name != name) continue;
                var src = t.GetComponent<Il2CppScheduleOne.Audio.AudioSourceController>();
                if (src == null) continue;
                src.PitchMultiplier = pitch;
                src.VolumeMultiplier = volume;
                src.Play();
                return true;
            }
            Core.InfoOnce("whip sound " + name, "[Whip] no '" + name + "' in hand; the donor had no sound source.");
            return false;
        }

        internal static void Place(Transform t)
        {
            t.localPosition = WhipItem.HeldPosition;
            t.localRotation = Quaternion.Euler(WhipItem.HeldRotation);
            t.localScale = Vector3.one * WhipItem.HeldScale;
        }

#if DEBUG
        /// <summary>Applies the held placement to the template and to the whip in hand right now. Returns how many were moved.</summary>
        internal static int Replace()
        {
            int moved = 0;
            if (_template != null)
            {
                Transform t = _template.transform.Find(ModelName);
                if (t != null) { Place(t); moved++; }
            }
            var avatar = Singleton<ViewmodelAvatar>.Instance;
            if (avatar != null && avatar.RightHandContainer != null)
            {
                foreach (Transform t in avatar.RightHandContainer.GetComponentsInChildren<Transform>(true))
                    if (t != null && t.name == ModelName) { Place(t); moved++; }
            }
            return moved;
        }

        private static Transform HeldModel(out string error)
        {
            error = null;
            var avatar = Singleton<ViewmodelAvatar>.Instance;
            if (avatar == null || avatar.RightHandContainer == null) { error = "no viewmodel avatar."; return null; }
            foreach (Transform t in avatar.RightHandContainer.GetComponentsInChildren<Transform>(true))
                if (t != null && t.name == ModelName && t.parent != null) return t;
            error = "the whip is not in hand.";
            return null;
        }

        /// <summary>
        /// Sets the held rotation from the camera: the handle (model +Z) points forward, raised by <paramref name="tilt"/>
        /// degrees and turned <paramref name="yaw"/> degrees to the right, model +Y stays up.
        /// </summary>
        internal static string AimRotation(float tilt, float yaw)
        {
            Transform model = HeldModel(out string error);
            if (model == null) return error;
            Camera cam = Camera.main;
            if (cam == null) return "no main camera.";
            Quaternion inCam = Quaternion.Euler(-tilt, yaw, 0f);
            Quaternion world = cam.transform.rotation * inCam;
            WhipItem.HeldRotation = (Quaternion.Inverse(model.parent.rotation) * world).eulerAngles;
            int moved = Replace();
            return "rot=" + WhipItem.HeldRotation.ToString("F1") + " (tilt " + tilt.ToString("F0") + ", yaw " + yaw.ToString("F0") + "), moved " + moved + " model(s).";
        }

        /// <summary>
        /// Sets the held position so the grip (model origin, moved <paramref name="alongHandle"/> model units along the
        /// handle) sits on the right hand container. Keeps the current rotation, so run 'vm aim' first.
        /// </summary>
        internal static string GripPosition(float alongHandle)
        {
            Transform model = HeldModel(out string error);
            if (model == null) return error;
            var avatar = Singleton<ViewmodelAvatar>.Instance;
            Vector3 handLocal = model.parent.InverseTransformPoint(avatar.RightHandContainer.position);
            Vector3 grip = new Vector3(0f, 0f, alongHandle);
            WhipItem.HeldPosition = handLocal - Quaternion.Euler(WhipItem.HeldRotation) * (grip * WhipItem.HeldScale);
            int moved = Replace();
            return "pos=" + WhipItem.HeldPosition.ToString("F4") + " (grip z " + alongHandle.ToString("F3") + "), moved " + moved + " model(s).";
        }
#endif

        /// <summary>
        /// Re-points the registered definition when something replaced its equippable (a reload that kept our template
        /// but rebuilt the definition). Cheap; called every two seconds.
        /// </summary>
        internal static void VerifyPointer()
        {
            if (_eq == null) return;
            try
            {
                var storable = Il2CppScheduleOne.Registry.GetItem(WhipItem.Id)?.TryCast<StorableItemDefinition>();
                if (storable == null) return;
                if (storable.Equippable != null && storable.Equippable.Pointer == _eq.Pointer) return;
                if (PointDefinitionAtTemplate(_eq)) Core.Log?.Msg("[Whip] the whip definition was replaced; pointed it at the held model again.");
            }
            catch (Exception e) { Core.Caught("whip: verify the held model pointer", e); }
        }

        private static bool PointDefinitionAtTemplate(Equippable_AvatarViewmodel eq)
        {
            var native = Il2CppScheduleOne.Registry.GetItem(WhipItem.Id);
            if (native == null) { Core.Log?.Warning("[Whip] the whip is not in the registry yet."); return false; }
            var storable = native.TryCast<StorableItemDefinition>();
            if (storable == null) { Core.Log?.Warning("[Whip] the whip definition is not storable."); return false; }
            storable.Equippable = eq;
            storable.EquipMode = ItemDefinition.EEquipMode.Legacy;
            return true;
        }

        /// <summary>A vanilla held item to copy the first-person setup from.</summary>
        private static Equippable_AvatarViewmodel FindDonor()
        {
            // Melee first: their animator has the swing ("Release_Light") and a sound source to clone. The machete is a
            // one-handed slash, closest to a whip. The pistols only keep the whip visible in hand.
            string[] donors = { "machete", "baseballbat", "fryingpan", "m1911", "revolver" };
            for (int i = 0; i < donors.Length; i++)
            {
                try
                {
                    var def = Il2CppScheduleOne.Registry.GetItem(donors[i]);
                    if (def == null || def.Equippable == null) continue;
                    var view = def.Equippable.GetComponent<Equippable_AvatarViewmodel>();
                    if (view == null || view.AnimatorController == null) continue;
                    Core.Log?.Msg("[Whip] copying the held-item setup from '" + donors[i] + "'.");
                    return view;
                }
                catch (Exception e) { Core.Caught("whip: donor " + donors[i], e); }
            }
            Core.Log?.Warning("[Whip] no vanilla held item to copy from; the whip has no model in hand.");
            return null;
        }
    }
}
