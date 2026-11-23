using System;
using UnityEngine.Rendering;   // ShadowCastingMode

namespace ActuallyWorkingEmployees.Features.Whip
{
    /// <summary>
    /// The crack seen at the tip: a small burst of low-poly dust cubes that fly apart and shrink, and a very short
    /// flash in the middle. Built from primitives in the game's flat look, pooled, and shown only on this player's screen
    /// (camera space, on the lash's layer), like the lash itself.
    /// </summary>
    internal static class WhipFx
    {
        private const int Bits = 7;
        internal static float PuffTime = 0.24f;     // s the dust stays
        internal static float FlashTime = 0.06f;    // s the flash stays
        internal static float PuffSize = 0.035f;    // model units, edge of one dust cube at the start
        internal static float PuffSpread = 0.16f;   // model units, how far the dust flies
        internal static float FlashSize = 0.06f;    // model units, flash diameter at the start

        private static readonly Color Dust = new Color(0.78f, 0.74f, 0.66f, 1f);
        private static readonly Color FlashColor = new Color(1f, 0.95f, 0.72f, 1f);

        private static GameObject _holder;
        private static Transform[] _bits;
        private static Vector3[] _dirs;
        private static float[] _spin;
        private static Transform _flash;
        private static Vector3 _origin;
        private static float _scale = 1f, _start = -1f;
        private static int _puffs;
        private static bool _failed;

        /// <summary>Starts the burst at the lash tip. Logs why when it cannot.</summary>
        internal static void Puff()
        {
            if (_failed) return;
            try
            {
                if (!WhipLash.Tip(out Transform frame, out Vector3 tip, out Vector3 dir, out int layer, out float s))
                {
                    Core.Log?.Msg("[Whip] puff: no lash in hand.");
                    return;
                }
                if (!Ensure(frame, layer)) return;
                _origin = tip;
                _scale = s;
                _start = Time.time;
                var rng = new System.Random(_puffs * 7919 + 17);
                for (int i = 0; i < Bits; i++)
                {
                    // Mostly sideways and back from the throw, a little up, never all in one line.
                    Vector3 r = new Vector3((float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 1.4f - 0.4f, (float)rng.NextDouble() * 2f - 1f);
                    _dirs[i] = (r.normalized * 0.8f + dir * 0.35f).normalized * (0.6f + 0.4f * (float)rng.NextDouble());
                    _spin[i] = (float)rng.NextDouble() * 360f;
                    _bits[i].gameObject.SetActive(true);
                }
                _flash.gameObject.SetActive(true);
                Place(0f);
                _puffs++;
                Core.Log?.Msg("[Whip] puff " + _puffs + " at the tip.");
            }
            catch (Exception e)
            {
                _failed = true;
                Core.Failed("whip: dust puff", e);
            }
        }

        internal static void Tick()
        {
            if (_start < 0f) return;
            try
            {
                if (_holder == null) { _start = -1f; return; }   // the camera went away with a scene change
                float t = Time.time - _start;
                if (t >= PuffTime) { Hide(); return; }
                Place(t);
            }
            catch (Exception e)
            {
                Core.Caught("whip: move the dust puff", e);
                _start = -1f;
            }
        }

        internal static void ResetSession()
        {
            try { if (_holder != null) UnityEngine.Object.Destroy(_holder); }
            catch (Exception e) { Core.Caught("whip: remove the dust puff", e); }
            _holder = null;
            _start = -1f;
        }

        private static void Place(float t)
        {
            float u = Mathf.Clamp01(t / PuffTime);
            float out_ = 1f - (1f - u) * (1f - u) * (1f - u);   // fast out, then slows down
            float size = PuffSize * _scale * (1f + 0.6f * u) * (1f - u * u);
            for (int i = 0; i < Bits; i++)
            {
                _bits[i].localPosition = _origin + _dirs[i] * (PuffSpread * _scale * out_) + Vector3.up * (0.02f * _scale * u);
                _bits[i].localRotation = Quaternion.Euler(_spin[i] + 200f * u, _spin[i] * 0.5f, _spin[i] * 0.3f + 120f * u);
                _bits[i].localScale = Vector3.one * Mathf.Max(size, 1e-4f);
            }
            float f = Mathf.Clamp01(t / FlashTime);
            _flash.localPosition = _origin;
            _flash.localScale = Vector3.one * Mathf.Max(FlashSize * _scale * (1f - f), 1e-4f);
            if (f >= 1f && _flash.gameObject.activeSelf) _flash.gameObject.SetActive(false);
        }

        private static void Hide()
        {
            _start = -1f;
            for (int i = 0; i < Bits; i++) _bits[i].gameObject.SetActive(false);
            _flash.gameObject.SetActive(false);
        }

        /// <summary>Builds the pooled cubes and the flash under the camera once. False (and logged) when it cannot.</summary>
        private static bool Ensure(Transform frame, int layer)
        {
            if (_holder != null && _holder.transform.parent == frame)
            {
                if (_holder.layer != layer) SetLayer(layer);
                return true;
            }
            if (_holder != null) UnityEngine.Object.Destroy(_holder);
            Material dust = MakeMaterial("Universal Render Pipeline/Lit", Dust, "AWEWhipDust");
            Material flash = MakeMaterial("Universal Render Pipeline/Unlit", FlashColor, "AWEWhipFlash");
            if (dust == null || flash == null)
            {
                Core.Log?.Warning("[Whip] puff: no material for the dust, the crack stays without it.");
                _failed = true;
                return false;
            }
            _holder = new GameObject("AWEWhipFx");
            _holder.transform.SetParent(frame, false);
            _bits = new Transform[Bits];
            _dirs = new Vector3[Bits];
            _spin = new float[Bits];
            for (int i = 0; i < Bits; i++) _bits[i] = Part(PrimitiveType.Cube, "AWEWhipDust" + i, dust);
            _flash = Part(PrimitiveType.Sphere, "AWEWhipFlash", flash);
            SetLayer(layer);
            Core.Log?.Msg("[Whip] dust puff built: " + Bits + " cubes and a flash under the camera.");
            return true;
        }

        private static Transform Part(PrimitiveType type, string name, Material mat)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            Collider c = go.GetComponent<Collider>();
            if (c != null) UnityEngine.Object.Destroy(c);
            MeshRenderer mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
            go.transform.SetParent(_holder.transform, false);
            go.SetActive(false);
            return go.transform;
        }

        private static void SetLayer(int layer)
        {
            _holder.layer = layer;
            for (int i = 0; i < _holder.transform.childCount; i++) _holder.transform.GetChild(i).gameObject.layer = layer;
        }

        private static Material MakeMaterial(string shaderName, Color color, string name)
        {
            Shader shader = null;
            try { shader = Shader.Find(shaderName); } catch (Exception e) { Core.Caught("whip: find shader " + shaderName, e); }
            if (shader == null)
            {
                Core.Log?.Warning("[Whip] shader '" + shaderName + "' not found, the puff uses Sprites/Default.");
                shader = Shader.Find("Sprites/Default");
            }
            if (shader == null) return null;
            var mat = new Material(shader) { name = name };
            mat.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            mat.color = color;
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.1f);
            return mat;
        }
    }
}
