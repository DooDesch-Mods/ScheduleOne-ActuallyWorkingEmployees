using System;

namespace ActuallyWorkingEmployees.Features.Whip
{
    /// <summary>
    /// The whip's two sounds, synthesized once per session from fixed seeds, so they sound the same every time and the
    /// mod ships no audio file.
    ///
    /// * Crack: the tip of a whip outruns sound and sends a sonic boom, an N-shaped pressure wave of about a
    ///   millisecond. After it come its reflections off the ground, a few sparks of crackle and a short outdoor tail.
    ///   No low thump and no metallic ring, which is what made the machete sounds read as a sword.
    /// * Air: the lash moving through the air, a soft band of noise that rises and falls in pitch with the swing.
    /// </summary>
    internal static class WhipSounds
    {
        private const int Rate = 44100;
        /// <summary>Recorded swing (swish, crack, tail), cut so the crack peak sits <see cref="WhipLash.SnapDelay"/> after the start.</summary>
        private const string RecordedResource = "ActuallyWorkingEmployees.Assets.whip.whip_crack.wav";
        private const string RecordedCredit = "Mixed Whip Crack 1 by peterbullmusic (freesound.org/s/351267), CC0";

        private static AudioClip _crack, _air, _recorded;
        private static bool _recordedChecked;

        internal static string CrackSource => Recorded() != null ? "recorded: " + RecordedCredit : "synthesized";

        /// <summary>The recorded swing from the mod's resources, or null when the build carries none.</summary>
        internal static AudioClip Recorded()
        {
            if (_recordedChecked) return _recorded;
            _recordedChecked = true;
            try
            {
                using (System.IO.Stream s = typeof(WhipSounds).Assembly.GetManifestResourceStream(RecordedResource))
                {
                    if (s == null) { Core.Log?.Msg("[Whip] no recorded crack in this build, the crack is synthesized."); return null; }
                    var bytes = new byte[s.Length];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int r = s.Read(bytes, read, bytes.Length - read);
                        if (r <= 0) break;
                        read += r;
                    }
                    _recorded = FromWav("AWEWhipRecorded", bytes);
                }
                if (_recorded != null) Core.Log?.Msg("[Whip] recorded crack loaded: " + _recorded.length.ToString("F2") + " s, " + RecordedCredit + ".");
            }
            catch (Exception e) { Core.Caught("whip: load the recorded crack", e); _recorded = null; }
            return _recorded;
        }

        /// <summary>A 16-bit PCM WAV, mono or stereo (mixed down), as a clip. Null with a warning for any other format.</summary>
        private static AudioClip FromWav(string name, byte[] b)
        {
            if (b.Length < 12 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E')
            { Core.Log?.Warning("[Whip] recorded crack: not a RIFF/WAVE file, the crack is synthesized."); return null; }
            int pos = 12, channels = 0, rate = 0, bits = 0, format = 0, dataAt = -1, dataLen = 0;
            while (pos + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
                int len = BitConverter.ToInt32(b, pos + 4);
                int body = pos + 8;
                if (id == "fmt ")
                {
                    format = BitConverter.ToInt16(b, body);
                    channels = BitConverter.ToInt16(b, body + 2);
                    rate = BitConverter.ToInt32(b, body + 4);
                    bits = BitConverter.ToInt16(b, body + 14);
                }
                else if (id == "data") { dataAt = body; dataLen = Math.Min(len, b.Length - body); break; }
                pos = body + len + (len & 1);
            }
            if (format != 1 || bits != 16 || (channels != 1 && channels != 2) || rate <= 0 || dataAt < 0)
            {
                Core.Log?.Warning("[Whip] recorded crack: format " + format + ", " + bits + " bit, " + channels + " channels, " + rate + " Hz is not 16-bit PCM mono or stereo; the crack is synthesized.");
                return null;
            }
            int frames = dataLen / (2 * channels);
            var d = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++) sum += BitConverter.ToInt16(b, dataAt + (i * channels + c) * 2) / 32768f;
                d[i] = sum / channels;
            }
            AudioClip clip = AudioClip.Create(name, frames, 1, rate, false);
            clip.SetData(d, 0);
            clip.hideFlags = HideFlags.HideAndDontSave;
            return clip;
        }

        internal static AudioClip Crack()
        {
            if (_crack != null) return _crack;
            try
            {
                int n = (int)(Rate * 0.5f);
                var d = new float[n];
                var rng = new System.Random(63);

                AddNWave(d, 0, 1.1e-3f, 1f);
                AddNWave(d, (int)(Rate * 0.0055f), 1.3e-3f, 0.42f);   // ground
                AddNWave(d, (int)(Rate * 0.0130f), 1.6e-3f, 0.18f);   // a wall or a second bounce

                // Crackle: the frayed tip, a handful of very short clicks in the first 20 ms.
                for (int c = 0; c < 9; c++)
                {
                    int at = (int)(Rate * (0.0015 + rng.NextDouble() * 0.018));
                    float amp = (float)(0.12 + rng.NextDouble() * 0.22) * (rng.Next(2) == 0 ? 1f : -1f);
                    for (int k = 0; k < 6 && at + k < n; k++) d[at + k] += amp * (1f - k / 6f);
                }

                // Burst and tail: high noise right after the boom, then a quiet lower tail that dies within a third of a second.
                float lp = 0f, prev = 0f;
                for (int i = 0; i < n; i++)
                {
                    float t = (float)i / Rate;
                    float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                    float hi = noise - prev;   // first difference: only the highs
                    prev = noise;
                    lp += 0.18f * (noise - lp);  // one-pole low-pass for the tail
                    d[i] += 0.30f * hi * Mathf.Exp(-t / 0.0035f) + 0.12f * lp * Mathf.Exp(-t / 0.05f) * Mathf.Clamp01(t / 0.004f);
                }

                d = Reverb(d, rng);
                HighPass(d, 700f);   // bright: the movie crack has no body below the boom

                _crack = Make("AWEWhipCrack", d, 0.98f);
                Core.Log?.Msg("[Whip] crack sound built (" + n + " samples).");
                return _crack;
            }
            catch (Exception e) { Core.Caught("whip: build the crack sound", e); return null; }
        }

        internal static AudioClip Air()
        {
            if (_air != null) return _air;
            try
            {
                const float length = 0.3f;
                int n = (int)(Rate * length);
                var d = new float[n];
                var rng = new System.Random(64);
                float low = 0f, band = 0f;
                for (int i = 0; i < n; i++)
                {
                    float u = (float)i / n;
                    float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                    float shape = Mathf.Sin(Mathf.PI * Mathf.Pow(u, 0.75f));
                    float fc = 350f + 1300f * shape;                        // the swell rises in pitch, then falls
                    float f = 2f * Mathf.Sin(Mathf.PI * fc / Rate);         // state-variable band-pass
                    float high = noise - low - 0.9f * band;
                    band += f * high;
                    low += f * band;
                    d[i] = band * shape * shape;
                }
                _air = Make("AWEWhipAir", d, 0.9f);
                Core.Log?.Msg("[Whip] air sound built (" + n + " samples).");
                return _air;
            }
            catch (Exception e) { Core.Caught("whip: build the air sound", e); return null; }
        }

        /// <summary>
        /// The lash landing on someone: a dull body thump (a sine falling from 150 to 70 Hz) under a short slap of
        /// low-passed noise, softly clipped. It plays on a hit only, so a hit sounds different from a miss.
        /// </summary>
        internal static AudioClip Thump()
        {
            if (_thump != null) return _thump;
            try
            {
                const float length = 0.18f;
                int n = (int)(Rate * length);
                var d = new float[n];
                var rng = new System.Random(91);
                float phase = 0f, lp = 0f;
                for (int i = 0; i < n; i++)
                {
                    float t = (float)i / Rate;
                    float f = 70f + 80f * Mathf.Exp(-t / 0.03f);
                    phase += 2f * Mathf.PI * f / Rate;
                    float body = Mathf.Sin(phase) * Mathf.Exp(-t / 0.05f) * Mathf.Clamp01(t / 0.002f);
                    float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                    lp += 0.25f * (noise - lp);
                    float slap = lp * Mathf.Exp(-t / 0.012f);
                    float x = 1.1f * body + 1.6f * slap;
                    d[i] = x / (1f + Mathf.Abs(x));
                }
                _thump = Make("AWEWhipThump", d, 0.9f);
                Core.Log?.Msg("[Whip] thump sound built (" + n + " samples).");
                return _thump;
            }
            catch (Exception e) { Core.Caught("whip: build the thump sound", e); return null; }
        }
        private static AudioClip _thump;

        /// <summary>
        /// A short outdoor reverb: eight early echoes off ground and walls within 0.1 s, then a quiet diffuse tail of
        /// about 0.15 s. Kept low, so the crack stays dry and sharp in front of it.
        /// </summary>
        private static float[] Reverb(float[] dry, System.Random rng)
        {
            int n = dry.Length;
            var wet = new float[n];
            float[] delays = { 0.011f, 0.017f, 0.023f, 0.031f, 0.043f, 0.059f, 0.079f, 0.101f };
            for (int k = 0; k < delays.Length; k++)
            {
                int off = (int)(Rate * delays[k]);
                float gain = 0.28f * Mathf.Exp(-delays[k] / 0.05f) * (k % 2 == 0 ? 1f : -1f);
                for (int i = off; i < n; i++) wet[i] += dry[i - off] * gain;
            }
            float lp = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate;
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                lp += 0.35f * (noise - lp);
                wet[i] += 0.05f * lp * Mathf.Exp(-t / 0.15f) * Mathf.Clamp01((t - 0.005f) / 0.01f);
                wet[i] += dry[i];
            }
            return wet;
        }

        /// <summary>One-pole high-pass at <paramref name="cutoff"/> Hz, in place.</summary>
        private static void HighPass(float[] d, float cutoff)
        {
            float rc = 1f / (2f * Mathf.PI * cutoff);
            float a = rc / (rc + 1f / Rate);
            float prevIn = 0f, prevOut = 0f;
            for (int i = 0; i < d.Length; i++)
            {
                float x = d[i];
                prevOut = a * (prevOut + x - prevIn);
                prevIn = x;
                d[i] = prevOut;
            }
        }

        /// <summary>A sonic boom: the pressure jumps up, falls in a straight line below zero and snaps back.</summary>
        private static void AddNWave(float[] d, int start, float duration, float amp)
        {
            int len = Mathf.Max(2, (int)(Rate * duration));
            for (int k = 0; k <= len && start + k < d.Length; k++) d[start + k] += amp * (1f - 2f * k / len);
        }

        private static AudioClip Make(string name, float[] data, float target)
        {
            float peak = 0f;
            for (int i = 0; i < data.Length; i++) peak = Mathf.Max(peak, Mathf.Abs(data[i]));
            float gain = peak > 0f ? target / peak : 1f;
            for (int i = 0; i < data.Length; i++) data[i] *= gain;
            AudioClip clip = AudioClip.Create(name, data.Length, 1, Rate, false);
            clip.SetData(data, 0);
            clip.hideFlags = HideFlags.HideAndDontSave;
            return clip;
        }
    }
}
