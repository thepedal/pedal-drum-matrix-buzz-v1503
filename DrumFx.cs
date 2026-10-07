using System;

namespace PedalDrumMatrix
{
    // Fixed-order palette. ORDER IS A PRESET CONTRACT (Build §3.3): append only.
    public enum FxType
    {
        None = 0, Bitcrush, Drive, Lowpass, RingMod, Comb, Stutter, Delay, Reverb, Gate, Resonator, Highpass,
        Transient, Wavefolder, Phaser, SubOctave, Formant, Resampler, Chorus, Freeze, AutoWah, Exciter
    }

    // One effect occupying a slot. Stereo, per-sample.
    //   amount = smoothed 0..1 slot macro (amount 0 = clean pass-through)
    //   p1     = smoothed 0..1 "Char" rotary (effect-specific character)
    //   mode   = smoothed 0..1 "Mode" switch — effects CROSSFADE between their
    //            two modes across this value so toggling never clicks (the Slot
    //            ramps it 0↔1 over ~20 ms).
    public interface IDrumFx
    {
        void Prepare(float sampleRate, float samplesPerTick);
        void Reset();
        void Process(ref float l, ref float r, float amount, float p1, float mode);
        bool IsRinging { get; }
        // Only the tuned Resonator uses this; every other fx implements it as an
        // empty method. (No default interface body: .NET Framework 4.8 does not
        // support default interface implementations.)
        void SetMusicalContext(int key, int scale);
    }

    public static class FxFactory
    {
        public static IDrumFx Create(FxType t) => t switch
        {
            FxType.Bitcrush => new BitcrushFx(),
            FxType.Drive    => new DriveFx(),
            FxType.Lowpass  => new FilterFx(highpass: false),
            FxType.Highpass => new FilterFx(highpass: true),
            FxType.RingMod  => new RingModFx(),
            FxType.Comb     => new CombFx(),
            FxType.Stutter  => new StutterFx(),
            FxType.Delay    => new DelayFx(),
            FxType.Reverb   => new ReverbFx(),
            FxType.Gate     => new GateFx(),
            FxType.Resonator => new ResonatorFx(),
            FxType.Transient => new TransientFx(),
            FxType.Wavefolder => new WavefolderFx(),
            FxType.Phaser    => new PhaserFx(),
            FxType.SubOctave => new SubOctaveFx(),
            FxType.Formant   => new FormantFx(),
            FxType.Resampler => new ResamplerFx(),
            FxType.Chorus    => new ChorusFx(),
            FxType.Freeze    => new FreezeFx(),
            FxType.AutoWah   => new AutoWahFx(),
            FxType.Exciter   => new ExciterFx(),
            _ => new NoneFx()
        };
    }

    // Shared decaying-energy tail tracker for feedback effects.
    internal struct Tail
    {
        float _level, _decay;
        public void Prepare(float sr) => _decay = (float)Math.Exp(-1.0 / (0.060 * sr));
        public void Reset() => _level = 0f;
        public void Feed(float a, float b)
        {
            float m = MathF.Abs(a) + MathF.Abs(b);
            _level = MathF.Max(_level * _decay, m);
            if (_level < 1e-8f) _level = 0f;   // never let it decay into denormals
        }
        public bool Ringing => _level > 1e-4f;
    }

    internal static class Dsp
    {
        // Flush-to-zero: keeps decaying feedback states out of the subnormal
        // range, where some CPUs slow down ~10× and cause dropout spikes.
        public static float Ftz(float v) => (v > -1e-15f && v < 1e-15f) ? 0f : v;

        // Fast tanh — a higher-order rational (Padé) approximation. Max error
        // ~1e-4 over the useful range, monotonic, saturates to ±1. Used in the
        // per-sample signal path (Drive, Resonator, feedback) where a smooth
        // bounded nonlinearity is wanted. Several × cheaper than MathF.Tanh.
        public static float TanhFast(float x)
        {
            if (x < -4f) return -1f;
            if (x >  4f) return  1f;
            float x2 = x * x;
            float num = x * (135135f + x2 * (17325f + x2 * (378f + x2)));
            float den = 135135f + x2 * (62370f + x2 * (3150f + x2 * 28f));
            return num / den;
        }
    }

    // Zero-latency stereo-linked peak limiter + cubic soft-clip ceiling.
    public sealed class OutputLimiter
    {
        const float Ceiling = 0.95f;
        float _gain = 1f, _atk, _rel;
        public void Prepare(float sr)
        {
            sr = sr > 0 ? sr : 44100f;
            _atk = (float)Math.Exp(-1.0 / (0.001 * sr));   // 1 ms
            _rel = (float)Math.Exp(-1.0 / (0.100 * sr));   // 100 ms
        }
        public void Reset() => _gain = 1f;
        static float SoftClip(float x)
        {
            if (x < -1.5f) return -1f;
            if (x >  1.5f) return  1f;
            return x - (x * x * x) * (1f / 6.75f);          // ±1.5 → ±1.0, smooth knee
        }
        public void Process(ref float l, ref float r)
        {
            float peak = MathF.Max(MathF.Abs(l), MathF.Abs(r));
            float target = peak > Ceiling ? Ceiling / peak : 1f;
            float coef = target < _gain ? _atk : _rel;       // attack fast, release slow
            _gain = target + (_gain - target) * coef;
            l = SoftClip(l * _gain);
            r = SoftClip(r * _gain);
        }
    }

    // Auto-gain leveler (peak-targeting). Lifts quiet output toward a target just
    // under the limiter ceiling; targets peak not RMS so drum transients survive.
    // Dual detector: fast env gates adaptation (freeze on gaps), slow peak sets level.
    public sealed class AutoGain
    {
        const float TargetPk = 0.5f;
        const float PkFloor  = 0.012f;
        const float MaxGain  = 8f;
        const float MinGain  = 0.25f;
        float _fast, _slow, _gain = 1f, _fastRel, _slowRel, _rise, _fall;
        public void Prepare(float sr)
        {
            sr = sr > 0 ? sr : 44100f;
            _fastRel = (float)Math.Exp(-1.0 / (0.030 * sr));
            _slowRel = (float)Math.Exp(-1.0 / (0.300 * sr));
            _rise    = (float)Math.Exp(-1.0 / (0.700 * sr));
            _fall    = (float)Math.Exp(-1.0 / (0.120 * sr));
        }
        public void Reset() { _fast = _slow = 0f; _gain = 1f; }
        public void Process(ref float l, ref float r)
        {
            float a = MathF.Max(MathF.Abs(l), MathF.Abs(r));
            _fast = a > _fast ? a : a + (_fast - a) * _fastRel;
            _slow = a > _slow ? a : a + (_slow - a) * _slowRel;
            if (_fast < 1e-15f) _fast = 0f;
            if (_slow < 1e-15f) _slow = 0f;

            float desired = TargetPk / MathF.Max(_slow, 1e-6f);
            if (desired > MaxGain) desired = MaxGain;
            else if (desired < MinGain) desired = MinGain;
            if (_fast < PkFloor) desired = _gain;

            float coef = desired > _gain ? _rise : _fall;
            _gain = desired + (_gain - desired) * coef;
            l *= _gain; r *= _gain;
        }
    }

    // Global feedback loop: a portion of the rack output is delayed, tone-shaped
    // and softly saturated, then fed back into the rack input. The in-loop tanh
    // makes it self-limiting (it sings rather than runs away) and a DC blocker
    // keeps it stable. Short loop times ring/pitch (comb-like); longer ones
    // regenerate rhythmically. Tap() before the slots, Write() after them.
    public sealed class GlobalFeedback
    {
        float[] _bL, _bR; int _w, _n, _d;
        float _sr = 44100f, _amount, _aLP;
        float _dcxL, _dcyL, _lpL, _dcxR, _dcyR, _lpR;
        const float DcR = 0.999f;
        Tail _tail;

        public void Prepare(float sr, float spt)
        {
            _sr = sr > 0 ? sr : 44100f;
            _n = Math.Max(8, (int)(1.0f * _sr));        // up to 1 s loop
            _bL = new float[_n]; _bR = new float[_n];
            _tail.Prepare(_sr);
            SetParams(0, 64, 64);
            Reset();
        }
        public void Reset()
        {
            Array.Clear(_bL, 0, _n); Array.Clear(_bR, 0, _n); _w = 0;
            _dcxL = _dcyL = _lpL = _dcxR = _dcyR = _lpR = 0f;
            _tail.Reset();
        }
        public void SetParams(int amt, int time, int tone)
        {
            _amount = (amt / 127f) * 0.2f;   // full knob ≈ 0.2 (was 1.0 — too hot)
            float ms = 1f * MathF.Pow(500f, time / 127f);          // 1 → 500 ms
            int d = (int)(ms * 0.001f * _sr);
            _d = Math.Min(_n - 1, Math.Max(1, d));
            float cutoff = 200f * MathF.Pow(60f, tone / 127f);     // 200 Hz → 12 kHz
            _aLP = 1f - MathF.Exp(-2f * MathF.PI * cutoff / _sr);
        }
        void FilterChan(ref float dcx, ref float dcy, ref float lp, ref float x)
        {
            float hp = x - dcx + DcR * dcy; dcx = Dsp.Ftz(x); dcy = Dsp.Ftz(hp);  // DC blocker
            lp += _aLP * (hp - lp); lp = Dsp.Ftz(lp);                             // tone lowpass
            x = lp;
        }
        public void Tap(out float fbL, out float fbR, float extraAmount)
        {
            float amt = _amount + extraAmount;
            if (amt > 0.5f) amt = 0.5f;                 // safety clamp
            if (amt <= 0f) { fbL = fbR = 0f; return; }
            int rp = _w - _d; if (rp < 0) rp += _n;
            float yL = _bL[rp], yR = _bR[rp];
            FilterChan(ref _dcxL, ref _dcyL, ref _lpL, ref yL);
            FilterChan(ref _dcxR, ref _dcyR, ref _lpR, ref yR);
            yL = Dsp.TanhFast(yL * 1.5f); yR = Dsp.TanhFast(yR * 1.5f); // self-limiting
            fbL = yL * amt; fbR = yR * amt;
        }
        public void Write(float l, float r)
        {
            _bL[_w] = Dsp.Ftz(l); _bR[_w] = Dsp.Ftz(r);
            _w++; if (_w >= _n) _w = 0;
            _tail.Feed(l, r);
        }
        public bool IsRinging => _amount > 0f && _tail.Ringing;
    }

    // Tempo-synced LFO modulation source. Rate is a cycle length in ticks
    // (rows), so it tracks tempo. Output is bipolar -1..+1. Steps is a fixed
    // 8-step pattern; Random is sample-and-hold, new value each cycle.
    public sealed class Lfo
    {
        float _phase, _inc, _hold;
        int _wave; uint _rng = 0x1234567u;
        static readonly float[] StepPat = { 0f, 0.6f, -0.4f, 1f, -0.7f, 0.3f, -1f, 0.5f };
        public void SetRate(int ticks, float samplesPerTick)
        {
            float period = MathF.Max(1f, ticks * samplesPerTick);
            _inc = 1f / period;
        }
        public void SetWave(int w) => _wave = w;
        public void Reset() { _phase = 0f; _hold = 0f; }
        public float Next()
        {
            float p = _phase;
            _phase += _inc;
            if (_phase >= 1f)
            {
                _phase -= 1f;
                _rng = _rng * 1664525u + 1013904223u;
                _hold = ((_rng >> 9) & 0xFFFF) / 32768f - 1f;   // new S&H value
            }
            switch (_wave)
            {
                case 0:  return MathF.Sin(2f * MathF.PI * p);     // sine
                case 1:  return 1f - 4f * MathF.Abs(p - 0.5f);    // triangle
                case 2:  return 2f * p - 1f;                      // saw
                case 3:  return p < 0.5f ? 1f : -1f;              // square
                case 4:  return StepPat[(int)(p * 8f) & 7];       // steps
                default: return _hold;                           // random S&H
            }
        }
    }

    // ── None ────────────────────────────────────────────────────────────────
    public sealed class NoneFx : IDrumFx
    {
        public void Prepare(float sr, float spt) { }
        public void Reset() { }
        public void Process(ref float l, ref float r, float amount, float p1, float mode) { }
        public bool IsRinging => false;
        public void SetMusicalContext(int key, int scale) { }
    }

    // ── Bitcrush ─ char: bits↔rate tilt · mode: raw → anti-alias filter ─────
    public sealed class BitcrushFx : IDrumFx
    {
        float _holdL, _holdR, _phase, _lpL, _lpR;
        int _cc; float _levels = 256f;
        public void Prepare(float sr, float spt) { Reset(); }
        public void Reset() { _holdL = _holdR = 0f; _phase = 1f; _lpL = _lpR = 0f; _cc = 0; _levels = 256f; }
        public void Process(ref float l, ref float r, float amount, float p1, float mode)
        {
            if (amount <= 0f) return;
            float rateAmt = amount * (0.5f + 0.5f * p1);
            float step = 1f - rateAmt * 0.975f; if (step < 0.025f) step = 0.025f;
            if (_cc == 0)                                   // control-rate: bit depth
            {
                float bitAmt = amount * (0.5f + 0.5f * (1f - p1));
                _levels = MathF.Pow(2f, 16f - bitAmt * 13f);
                _cc = 16;
            }
            _cc--;
            _phase += step;
            if (_phase >= 1f) { _phase -= 1f; _holdL = l; _holdR = r; }
            float ql = MathF.Round(_holdL * _levels) / _levels;
            float qr = MathF.Round(_holdR * _levels) / _levels;
            const float a = 0.5f;                         // gentle post lowpass, always updated
            _lpL = Dsp.Ftz(_lpL + a * (ql - _lpL)); _lpR = Dsp.Ftz(_lpR + a * (qr - _lpR));
            l = ql + (_lpL - ql) * mode;                  // crossfade raw → filtered
            r = qr + (_lpR - qr) * mode;
        }
        public bool IsRinging => false;
        public void SetMusicalContext(int key, int scale) { }
    }

    // ── Drive ─ char: bias/asymmetry · mode: soft → hard clip ───────────────
    public sealed class DriveFx : IDrumFx
    {
        int _cc; float _pre = 1f, _makeup = 1f, _bias = 0f, _dcS = 0f, _dcH = 0f;
        public void Prepare(float sr, float spt) { Reset(); }
        public void Reset() { _cc = 0; _pre = 1f; _makeup = 1f; _bias = _dcS = _dcH = 0f; }
        static float Clip(float x) => x < -1f ? -1f : (x > 1f ? 1f : x);
        public void Process(ref float l, ref float r, float amount, float p1, float mode)
        {
            if (amount <= 0f) return;
            if (_cc == 0)                                   // control-rate: gain/bias/DC
            {
                _pre = 1f + amount * 23f;
                _makeup = 1f / MathF.Sqrt(_pre);
                _bias = (p1 - 0.5f) * 0.8f;
                _dcS = Dsp.TanhFast(_pre * _bias); _dcH = Clip(_pre * _bias);
                _cc = 16;
            }
            _cc--;
            float dc = _dcS + (_dcH - _dcS) * mode;
            float ylS = Dsp.TanhFast(_pre * (l + _bias)), ylH = Clip(_pre * (l + _bias));
            float yrS = Dsp.TanhFast(_pre * (r + _bias)), yrH = Clip(_pre * (r + _bias));
            float yl = ylS + (ylH - ylS) * mode;
            float yr = yrS + (yrH - yrS) * mode;
            l = (yl - dc) * _makeup; r = (yr - dc) * _makeup;
        }
        public bool IsRinging => false;
        public void SetMusicalContext(int key, int scale) { }
    }

    // ── Filter ─ char: resonance · mode: lowpass → highpass. amount = cutoff ─
    // ── Lowpass / Highpass ─ char: cutoff · mode: low/high Q. amount = mix ───
    // One 12 dB/oct TPT state-variable filter configured as LP or HP at
    // construction. Amount blends dry → filtered (how much filter is applied);
    // Char sets the cutoff (150 Hz → 18 kHz); Mode selects one of two Q values
    // (gentle Butterworth vs resonant), crossfaded so toggling never clicks.
    public sealed class FilterFx : IDrumFx
    {
        readonly bool _hp;
        float _sr = 44100f;
        int _cc;
        float _a1, _a2, _a3, _k = 2f;
        float _ic1L, _ic2L, _ic1R, _ic2R;
        const float LoQ = 0.707f, HiQ = 6f;                 // the two Q values

        public FilterFx(bool highpass) { _hp = highpass; }
        public void Prepare(float sr, float spt) { _sr = sr > 0 ? sr : 44100f; Reset(); }
        public void Reset() { _ic1L = _ic2L = _ic1R = _ic2R = 0f; _cc = 0; }
        public void Process(ref float l, ref float r, float amount, float p1, float mode)
        {
            if (amount <= 0f) return;
            if (_cc == 0)                                   // control-rate: cutoff (char) + Q (mode)
            {
                float fc = 150f * MathF.Pow(120f, p1);      // 150 Hz → 18 kHz
                float q  = LoQ + (HiQ - LoQ) * mode;        // two-Q morph
                float g  = MathF.Tan(MathF.PI * fc / _sr);
                _k = 1f / q;
                _a1 = 1f / (1f + g * (g + _k)); _a2 = g * _a1; _a3 = g * _a2;
                _cc = 16;
            }
            _cc--;

            float v0 = l, v3 = v0 - _ic2L;
            float v1 = _a1 * _ic1L + _a2 * v3;
            float v2 = _ic2L + _a2 * _ic1L + _a3 * v3;
            _ic1L = Dsp.Ftz(2f * v1 - _ic1L); _ic2L = Dsp.Ftz(2f * v2 - _ic2L);
            float fL = _hp ? (v0 - _k * v1 - v2) : v2;
            l = v0 + (fL - v0) * amount;                    // dry → filtered (wet mix)

            v0 = r; v3 = v0 - _ic2R;
            v1 = _a1 * _ic1R + _a2 * v3;
            v2 = _ic2R + _a2 * _ic1R + _a3 * v3;
            _ic1R = Dsp.Ftz(2f * v1 - _ic1R); _ic2R = Dsp.Ftz(2f * v2 - _ic2R);
            float fR = _hp ? (v0 - _k * v1 - v2) : v2;
            r = v0 + (fR - v0) * amount;                    // dry → filtered (wet mix)
        }
        public bool IsRinging => false;
        public void SetMusicalContext(int key, int scale) { }
    }

    // ── RingMod ─ char: carrier fine tune · mode: ring-mod → AM ─────────────
    public sealed class RingModFx : IDrumFx
    {
        float _sr = 44100f, _phase;
        int _cc; float _inc = 0.1f;
        public void Prepare(float sr, float spt) { _sr = sr > 0 ? sr : 44100f; Reset(); }
        public void Reset() { _phase = 0f; _cc = 0; }
        public void Process(ref float l, ref float r, float amount, float p1, float mode)
        {
            if (amount <= 0f) return;
            if (_cc == 0)                                   // control-rate: carrier freq
            {
                float f = 30f * MathF.Pow(100f, amount) * MathF.Pow(2f, (p1 - 0.5f) * 2f);
                _inc = 2f * MathF.PI * f / _sr;
                _cc = 16;
            }
            _cc--;
            _phase += _inc;
            if (_phase > 2f * MathF.PI) _phase -= 2f * MathF.PI;
            float c = MathF.Sin(_phase);
            float carrier = c + ((0.5f + 0.5f * c) - c) * mode;   // RM → AM
            float w = amount;
            l = (1f - w) * l + w * (l * carrier);
            r = (1f - w) * r + w * (r * carrier);
        }
        public bool IsRinging => false;
        public void SetMusicalContext(int key, int scale) { }
    }

    // ── Comb ─ char: feedback damping · mode: +feedback → −feedback (rings) ──
    public sealed class CombFx : IDrumFx
    {
        float[] _bL, _bR; int _w, _n;
        float _sr = 44100f, _dL, _dR; Tail _tail;
        public void Prepare(float sr, float spt)
        {
            _sr = sr > 0 ? sr : 44100f;
            _n = Math.Max(8, (int)(0.025f * _sr));
            _bL = new float[_n]; _bR = new float[_n];
            _tail.Prepare(_sr); Reset();
        }
        public void Reset() { Array.Clear(_bL,0,_n); Array.Clear(_bR,0,_n); _w=0; _dL=_dR=0f; _tail.Reset(); }
        public void Process(ref float l, ref float r, float amount, float p1, float mode)
        {
            if (amount <= 0f) { _tail.Reset(); return; }
            int d = Math.Max(1, (int)(_n * (1f - amount * 0.92f)));
            float fb = (0.5f + amount * 0.45f) * (1f - 2f * mode);   // +fb → −fb (0 at mode 0.5)
            float damp = p1 * 0.7f;
            int rp = _w - d; if (rp < 0) rp += _n;

            _dL = Dsp.Ftz(_bL[rp] * (1f - damp) + _dL * damp);
            _dR = Dsp.Ftz(_bR[rp] * (1f - damp) + _dR * damp);
            float yL = l + fb * _dL, yR = r + fb * _dR;
            _bL[_w] = Dsp.Ftz(yL); _bR[_w] = Dsp.Ftz(yR);
            _w++; if (_w >= _n) _w = 0;

            float w = amount;
            l = (1f - w) * l + w * yL; r = (1f - w) * r + w * yR;
            _tail.Feed(l, r);
        }
        public bool IsRinging => _tail.Ringing;
        public void SetMusicalContext(int key, int scale) { }
    }

    // ── Stutter ─ char: repeats (2-8) · mode: forward → reverse slice (rings) ─
    public sealed class StutterFx : IDrumFx
    {
        float[] _ringL, _ringR, _sliceL, _sliceR;
        int _n, _w, _slice, _hold, _play, _counter, _reps = 4;
        float _sr = 44100f, _spt = 11025f; Tail _tail;
        public void Prepare(float sr, float spt)
        {
            _sr = sr > 0 ? sr : 44100f;
            _spt = spt > 1f ? spt : _sr / 8f;
            _n = Math.Max(8, (int)(0.75f * _sr));
            _ringL = new float[_n]; _ringR = new float[_n];
            int maxSlice = Math.Max(64, (int)_spt);
            _sliceL = new float[maxSlice]; _sliceR = new float[maxSlice];
            _tail.Prepare(_sr); Reset();
        }
        public void Reset()
        {
            Array.Clear(_ringL,0,_n); Array.Clear(_ringR,0,_n);
            _w=0; _slice=0; _hold=0; _play=0; _counter=0; _tail.Reset();
        }
        void Latch()
        {
            _slice = Math.Min(_sliceL.Length, Math.Max(64, (int)(_spt)));
            int start = _w - _slice; if (start < 0) start += _n;
            for (int i = 0; i < _slice; i++)
            {
                int idx = start + i; if (idx >= _n) idx -= _n;
                _sliceL[i] = _ringL[idx]; _sliceR[i] = _ringR[idx];
            }
            _hold = _slice * Math.Max(1, _reps); _play = 0; _counter = 0;
        }
        public void Process(ref float l, ref float r, float amount, float p1, float mode)
        {
            _reps = 2 + (int)MathF.Round(p1 * 6f);
            _ringL[_w] = l; _ringR[_w] = r; _w++; if (_w >= _n) _w = 0;
            if (amount <= 0f) { _tail.Reset(); return; }
            if (_hold <= 0 || _slice <= 0) Latch();

            int piF = _play;
            int piR = _slice - 1 - _play; if (piR < 0) piR = 0;
            float sl  = _sliceL[piF] + (_sliceL[piR] - _sliceL[piF]) * mode;   // fwd → reverse
            float sr2 = _sliceR[piF] + (_sliceR[piR] - _sliceR[piF]) * mode;
            _play++; if (_play >= _slice) _play = 0;
            _counter++; if (_counter >= _hold) Latch();

            l = (1f - amount) * l + amount * sl;
            r = (1f - amount) * r + amount * sr2;
            _tail.Feed(amount * sl, amount * sr2);
        }
        public bool IsRinging => _tail.Ringing;
        public void SetMusicalContext(int key, int scale) { }
    }

    // ── Delay ─ char: time (tempo-synced ticks) · mode: Low/High feedback ────
    // amount = mix. Char steps through tick-synced delay lengths (the machine
    // converts Char→ticks→samples per block so it tracks tempo); Mode picks a
    // low or high feedback value (number of repeats). No ping-pong.
    public sealed class DelayFx : IDrumFx
    {
        // Char → delay length in ticks. Stepped across the knob.
        public static readonly int[] TickVals = { 1, 2, 3, 4, 6, 8, 12, 16, 24, 32 };
        public static int CharToTicks(float p1)
        {
            int i = (int)(p1 * (TickVals.Length - 1) + 0.5f);
            if (i < 0) i = 0; else if (i >= TickVals.Length) i = TickVals.Length - 1;
            return TickVals[i];
        }

        float[] _bL, _bR; int _w, _n, _d;
        float _sr = 44100f, _spt = 11025f; Tail _tail;
        public void Prepare(float sr, float spt)
        {
            _sr = sr > 0 ? sr : 44100f;
            _spt = spt > 1f ? spt : _sr / 8f;
            _n = Math.Max(8, (int)(4.0f * _sr));          // up to ~4 s for long tick syncs
            _bL = new float[_n]; _bR = new float[_n];
            _tail.Prepare(_sr);
            _d = Math.Min(_n - 1, Math.Max(1, (int)(6f * _spt)));   // 6-tick fallback
            Reset();
        }
        // Delay length in samples, pushed per block by the machine (Char→ticks × spt).
        public void SetDelaySamples(int s) { _d = s < 1 ? 1 : (s > _n - 1 ? _n - 1 : s); }
        public void Reset() { Array.Clear(_bL,0,_n); Array.Clear(_bR,0,_n); _w=0; _tail.Reset(); }
        public void Process(ref float l, ref float r, float amount, float p1, float mode)
        {
            if (amount <= 0f) { _tail.Reset(); return; }
            float fb = 0.25f + mode * 0.5f, wet = amount;   // Mode: Low(0.25) → High(0.75)
            int rp = _w - _d; if (rp < 0) rp += _n;
            float dl = _bL[rp], dr = _bR[rp];
            _bL[_w] = Dsp.Ftz(l + fb * dl);                 // mono feedback (no ping-pong)
            _bR[_w] = Dsp.Ftz(r + fb * dr);
            _w++; if (_w >= _n) _w = 0;
            l = l + wet * dl; r = r + wet * dr;
            _tail.Feed(wet * dl, wet * dr);
        }
        public bool IsRinging => _tail.Ringing;
        public void SetMusicalContext(int key, int scale) { }
    }

    // ── Reverb ─ char: damping · mode: normal → bright tilt (rings) ─────────
    public sealed class ReverbFx : IDrumFx
    {
        static readonly int[] CombTune = { 1116,1188,1277,1356,1422,1491,1557,1617 };
        static readonly int[] ApTune   = { 556,441,341,225 };
        const int Spread = 23; const float FixedGain = 0.015f, ApFb = 0.5f;
        float[][] _cbL, _cbR, _apL, _apR;
        int[] _cbiL, _cbiR, _apiL, _apiR;
        float[] _cfL, _cfR;
        float _sr = 44100f, _feedback = 0.84f, _damp = 0.25f;
        float _hpL, _hpR, _hxL, _hxR; Tail _tail;

        static float[][] MakeBufs(int[] tune, float scale, int spread)
        {
            var a = new float[tune.Length][];
            for (int i = 0; i < tune.Length; i++)
                a[i] = new float[Math.Max(1, (int)((tune[i] + spread) * scale))];
            return a;
        }
        public void Prepare(float sr, float spt)
        {
            _sr = sr > 0 ? sr : 44100f; float s = _sr / 44100f;
            _cbL = MakeBufs(CombTune,s,0); _cbR = MakeBufs(CombTune,s,Spread);
            _apL = MakeBufs(ApTune,s,0);   _apR = MakeBufs(ApTune,s,Spread);
            _cbiL = new int[CombTune.Length]; _cbiR = new int[CombTune.Length];
            _apiL = new int[ApTune.Length];   _apiR = new int[ApTune.Length];
            _cfL = new float[CombTune.Length]; _cfR = new float[CombTune.Length];
            _tail.Prepare(_sr); Reset();
        }
        public void Reset()
        {
            foreach (var b in _cbL) Array.Clear(b,0,b.Length);
            foreach (var b in _cbR) Array.Clear(b,0,b.Length);
            foreach (var b in _apL) Array.Clear(b,0,b.Length);
            foreach (var b in _apR) Array.Clear(b,0,b.Length);
            Array.Clear(_cbiL,0,_cbiL.Length); Array.Clear(_cbiR,0,_cbiR.Length);
            Array.Clear(_apiL,0,_apiL.Length); Array.Clear(_apiR,0,_apiR.Length);
            Array.Clear(_cfL,0,_cfL.Length);   Array.Clear(_cfR,0,_cfR.Length);
            _hpL=_hpR=_hxL=_hxR=0f; _tail.Reset();
        }
        float Comb(float[] buf, ref int idx, ref float store, float input)
        {
            float o = buf[idx];
            store = Dsp.Ftz(o * (1f - _damp) + store * _damp);
            buf[idx] = Dsp.Ftz(input + store * _feedback);
            idx++; if (idx >= buf.Length) idx = 0;
            return o;
        }
        float Allpass(float[] buf, ref int idx, float input)
        {
            float bo = buf[idx];
            float o = -input + bo;
            buf[idx] = Dsp.Ftz(input + bo * ApFb);
            idx++; if (idx >= buf.Length) idx = 0;
            return o;
        }
        public void Process(ref float l, ref float r, float amount, float p1, float mode)
        {
            if (amount <= 0f) { _tail.Reset(); return; }
            _feedback = 0.70f + amount * 0.28f;
            _damp = p1 * 0.6f;
            float input = (l + r) * FixedGain;
            float wl = 0f, wr = 0f;
            for (int i = 0; i < CombTune.Length; i++)
            {
                wl += Comb(_cbL[i], ref _cbiL[i], ref _cfL[i], input);
                wr += Comb(_cbR[i], ref _cbiR[i], ref _cfR[i], input);
            }
            for (int i = 0; i < ApTune.Length; i++)
            {
                wl = Allpass(_apL[i], ref _apiL[i], wl);
                wr = Allpass(_apR[i], ref _apiR[i], wr);
            }
            // bright tilt = one-pole highpass on the wet; compute always, crossfade by mode
            const float a = 0.85f;
            float hl = a * (_hpL + wl - _hxL); _hxL = Dsp.Ftz(wl); _hpL = Dsp.Ftz(hl);
            float hr = a * (_hpR + wr - _hxR); _hxR = Dsp.Ftz(wr); _hpR = Dsp.Ftz(hr);
            float wlo = wl + (hl - wl) * mode;
            float wro = wr + (hr - wr) * mode;
            l = l + amount * wlo; r = r + amount * wro;
            _tail.Feed(amount * wlo, amount * wro);
        }
        public bool IsRinging => _tail.Ringing;
        public void SetMusicalContext(int key, int scale) { }
    }

    // ── Gate ─ char: duty cycle · mode: straight → triplet timing (tail-free) ─
    public sealed class GateFx : IDrumFx
    {
        float _sr = 44100f, _spt = 11025f, _pos, _env, _coef;
        public void Prepare(float sr, float spt)
        {
            _sr = sr > 0 ? sr : 44100f;
            _spt = spt > 1f ? spt : _sr / 8f;
            _coef = (float)Math.Exp(-1.0 / (0.003 * _sr));
            Reset();
        }
        public void Reset() { _pos = 0f; _env = 1f; }
        public void Process(ref float l, ref float r, float amount, float p1, float mode)
        {
            if (amount <= 0f) { _env = 1f; return; }
            float cyc = (8f - amount * 7f) * (1f - mode / 3f);   // straight → triplet (×2/3)
            float period = cyc * _spt; if (period < 2f) period = 2f;
            float duty = 0.05f + p1 * 0.9f;
            _pos += 1f; if (_pos >= period) _pos -= period;
            float target = (_pos < period * duty) ? 1f : 0f;
            _env = target + (_env - target) * _coef;
            float g = 1f - amount * (1f - _env);
            l *= g; r *= g;
        }
        public bool IsRinging => false;
        public void SetMusicalContext(int key, int scale) { }
    }

    // ── Resonator ─ char: pitch (snapped to Key/Scale) · mode: short→long decay
    // A tuned comb resonator (Karplus-style) excited by the input, so percussive
    // hits ring at musical pitches — drums become melodic. Char selects a scale
    // degree; modulate it with the LFO/envelope to play patterns in the key.
    // The in-loop tanh keeps it self-limiting. Rings.
    public sealed class ResonatorFx : IDrumFx
    {
        static readonly int[][] Scales =
        {
            new[]{0,2,4,5,7,9,11},   // major
            new[]{0,2,3,5,7,8,10},   // minor
            new[]{0,2,4,7,9},        // major pentatonic
            new[]{0,3,5,7,10},       // minor pentatonic
            new[]{0,2,3,5,7,9,10},   // dorian
            new[]{0,1,3,5,7,8,10},   // phrygian
            new[]{0,3,5,6,7,10},     // blues
            new[]{0,2,4,6,8,10},     // whole tone
        };
        float[] _bL, _bR; int _n, _w;
        float _sr = 44100f, _dsL, _dsR;
        int _key, _scale, _cc, _di = 100; float _frac; Tail _tail;

        public void SetMusicalContext(int key, int scale) { _key = key; _scale = scale; }

        public static float CharToFreq(int key, int scale, float p1)
        {
            int[] sc = Scales[scale % Scales.Length];
            int degrees = sc.Length * 3;                  // 3 octaves of the scale
            int d = (int)(p1 * (degrees - 1) + 0.5f);
            if (d < 0) d = 0; else if (d >= degrees) d = degrees - 1;
            int semitone = (d / sc.Length) * 12 + sc[d % sc.Length];
            int note = 36 + key + semitone;               // base C2 + key
            return 440f * MathF.Pow(2f, (note - 69) / 12f);
        }

        public void Prepare(float sr, float spt)
        {
            _sr = sr > 0 ? sr : 44100f;
            _n = Math.Max(8, (int)(_sr / 20f));           // down to 20 Hz
            _bL = new float[_n]; _bR = new float[_n];
            _tail.Prepare(_sr); Reset();
        }
        public void Reset()
        {
            Array.Clear(_bL, 0, _n); Array.Clear(_bR, 0, _n);
            _w = 0; _dsL = _dsR = 0f; _cc = 0; _tail.Reset();
        }
        public void Process(ref float l, ref float r, float amount, float p1, float mode)
        {
            if (amount <= 0f) { _tail.Reset(); return; }
            if (_cc == 0)                                   // control-rate: pitch → delay
            {
                float dsc = _sr / CharToFreq(_key, _scale, p1);
                if (dsc < 2f) dsc = 2f; else if (dsc > _n - 2) dsc = _n - 2;
                _di = (int)dsc; _frac = dsc - _di;          // integer + fractional delay
                _cc = 16;
            }
            _cc--;
            float fb = 0.980f + mode * 0.017f;            // short → long decay
            const float damp = 0.3f;

            // L/R share one write pointer and one delay, so read indices once
            int i0 = _w - _di; if (i0 < 0) i0 += _n;
            int i1 = i0 - 1;   if (i1 < 0) i1 += _n;
            float dL = _bL[i0] + (_bL[i1] - _bL[i0]) * _frac;
            float dR = _bR[i0] + (_bR[i1] - _bR[i0]) * _frac;
            _dsL = Dsp.Ftz(dL + (_dsL - dL) * damp);
            _dsR = Dsp.Ftz(dR + (_dsR - dR) * damp);
            _bL[_w] = Dsp.Ftz(Dsp.TanhFast(l + fb * _dsL));
            _bR[_w] = Dsp.Ftz(Dsp.TanhFast(r + fb * _dsR));
            _w++; if (_w >= _n) _w = 0;

            l = (1f - amount) * l + amount * dL;
            r = (1f - amount) * r + amount * dR;
            _tail.Feed(amount * dL, amount * dR);
        }
        public bool IsRinging => _tail.Ringing;
    }

    // ── Transient ─ char: attack↔sustain · mode: fast/slow detector ──────────
    // Differential-envelope transient designer. A fast and a slow follower of the
    // (stereo-linked) level; their difference marks attack (fast>slow) vs sustain
    // (fast<slow). Char tilts the gain toward sharpening the hit or fattening the
    // body; Amount scales the whole effect; Mode sets detector speed. Tail-free.
    public sealed class TransientFx : IDrumFx
    {
        float _sr = 44100f, _fast, _slow, _fAtk, _fRel, _sAtk, _sRel;
        public void Prepare(float sr, float spt) { _sr = sr > 0 ? sr : 44100f; Reset(); }
        public void Reset() { _fast = _slow = 0f; SetTimes(0f); }
        void SetTimes(float mode)
        {
            // mode 0 = fast detector, mode 1 = slower/broader
            float fa = 0.5f + mode * 1.5f, fr = 20f + mode * 40f;
            float sa = 15f + mode * 35f,  sr = 150f + mode * 250f;
            _fAtk = Co(fa); _fRel = Co(fr); _sAtk = Co(sa); _sRel = Co(sr);
        }
        float Co(float ms) => (float)Math.Exp(-1.0 / (ms * 0.001 * _sr));
        public void Process(ref float l, ref float r, float amount, float p1, float mode)
        {
            if (amount <= 0f) return;
            SetTimes(mode);
            float a = MathF.Max(MathF.Abs(l), MathF.Abs(r));          // stereo-linked detector
            _fast = a > _fast ? a + (_fast - a) * _fAtk : a + (_fast - a) * _fRel;
            _slow = a > _slow ? a + (_slow - a) * _sAtk : a + (_slow - a) * _sRel;
            _fast = Dsp.Ftz(_fast); _slow = Dsp.Ftz(_slow);

            float s = (p1 - 0.5f) * 2f;                               // -1 sharpen .. +1 fatten
            float d = _fast - _slow;                                  // >0 attack, <0 sustain
            float g = 1f;
            if (d > 0f) g += (-s) * d * 8f;                           // attack boost/cut
            else        g += ( s) * (-d) * 8f;                        // sustain boost/cut
            if (g < 0.1f) g = 0.1f; else if (g > 4f) g = 4f;
            g = 1f + (g - 1f) * amount;                               // Amount = intensity
            l *= g; r *= g;
        }
        public bool IsRinging => false;
        public void SetMusicalContext(int key, int scale) { }
    }

    // ── Wavefolder ─ char: fold amount · mode: symmetric/asymmetric ──────────
    // Sine wavefolder (West-Coast style): as level/fold rises the signal reflects
    // through the sine repeatedly, adding bright inharmonic partials unlike the
    // squaring of Drive. Amount drives level into the fold; Char sets fold
    // density; Mode adds a bias for asymmetric (even-harmonic) folding. Tail-free.
    public sealed class WavefolderFx : IDrumFx
    {
        float _sr = 44100f; int _cc;
        float _pre = 1f, _foldK = 1f, _bias, _dc;
        float _envL, _envR, _envRel;
        const float Makeup = 0.75f;
        public void Prepare(float sr, float spt)
        {
            _sr = sr > 0 ? sr : 44100f;
            _envRel = (float)Math.Exp(-1.0 / (0.006 * _sr));         // 6 ms envelope release
            Reset();
        }
        public void Reset() { _cc = 0; _envL = _envR = 0f; }
        public void Process(ref float l, ref float r, float amount, float p1, float mode)
        {
            if (amount <= 0f) return;
            if (_cc == 0)
            {
                _pre   = 1f + amount * 3f;                            // drive into the fold
                _foldK = (0.5f + p1 * 2f) * MathF.PI;                 // fold density (brightness)
                _bias  = mode * 0.5f;                                 // asymmetric → even harmonics
                _dc    = MathF.Sin(_bias * _foldK);
                _cc = 16;
            }
            _cc--;
            // Fold brightness rises with input level (louder = more folds); the
            // output amplitude follows the input envelope, so quiet tails stay
            // quiet and clean (bell/FM-like, and level-matched to the dry).
            float aL = MathF.Abs(l); _envL = aL > _envL ? aL : aL + (_envL - aL) * _envRel;
            float aR = MathF.Abs(r); _envR = aR > _envR ? aR : aR + (_envR - aR) * _envRel;
            _envL = Dsp.Ftz(_envL); _envR = Dsp.Ftz(_envR);
            l = (MathF.Sin((l * _pre + _bias) * _foldK) - _dc) * _envL * Makeup;
            r = (MathF.Sin((r * _pre + _bias) * _foldK) - _dc) * _envR * Makeup;
        }
        public bool IsRinging => false;
        public void SetMusicalContext(int key, int scale) { }
    }

    // ── Phaser ─ char: sweep position · mode: 4/8 stages. amount = depth/mix ──
    // Cascaded first-order allpasses with light feedback, mixed with the dry to
    // form moving notches. Char sets the allpass frequency (the sweep position) —
    // point the LFO/envelope at Char for classic phasing. Mode taps 4 or 8
    // stages (subtle vs deep). Rings only briefly via feedback; treated tail-free.
    public sealed class PhaserFx : IDrumFx
    {
        float _sr = 44100f; int _cc; float _a;
        readonly float[] _sL = new float[8];
        readonly float[] _sR = new float[8];
        float _fbL, _fbR;
        public void Prepare(float sr, float spt) { _sr = sr > 0 ? sr : 44100f; Reset(); }
        public void Reset() { Array.Clear(_sL, 0, 8); Array.Clear(_sR, 0, 8); _fbL = _fbR = 0f; _cc = 0; }
        public void Process(ref float l, ref float r, float amount, float p1, float mode)
        {
            if (amount <= 0f) return;
            if (_cc == 0)
            {
                float f = 200f * MathF.Pow(40f, p1);                 // 200 Hz → 8 kHz
                float t = MathF.Tan(MathF.PI * f / _sr);
                _a = (t - 1f) / (t + 1f);                            // 1st-order allpass coef
                _cc = 16;
            }
            _cc--;
            l = RunChannel(l, _sL, ref _fbL, amount, mode);
            r = RunChannel(r, _sR, ref _fbR, amount, mode);
        }
        float RunChannel(float x, float[] s, ref float fbState, float amount, float mode)
        {
            const float fb = 0.5f;
            float v = x + fbState * fb;
            float out4 = 0f;
            for (int i = 0; i < 8; i++)
            {
                float y = _a * v + s[i];                             // allpass: y = a*v + s
                s[i] = Dsp.Ftz(v - _a * y);                          // s = v - a*y
                v = y;
                if (i == 3) out4 = v;                                // 4-stage tap
            }
            float stageOut = (out4 + (v - out4) * mode) * 0.87f;     // 4 → 8 stages, level-match
            fbState = Dsp.Ftz(stageOut);
            return x + (stageOut - x) * amount;                      // dry → phased (depth/mix)
        }
        public bool IsRinging => false;
        public void SetMusicalContext(int key, int scale) { }
    }

    // ── SubOctave ─ char: sub tone · mode: -1/-2 octaves. amount = sub level ─
    // Analog-style octave divider: a flip-flop toggled by rising zero-crossings of
    // the (lowpassed, mono) input makes a square an octave down (every 2nd crossing
    // = two down). The square follows the input envelope and is tone-shaped, then
    // mixed under the dry. Best on monophonic hits (kicks, toms). Tail-free.
    public sealed class SubOctaveFx : IDrumFx
    {
        float _sr = 44100f, _lp, _env, _sub, _tone; bool _state, _prevPos; int _div;
        int _cc; float _toneCoef = 0.2f, _envRel;
        public void Prepare(float sr, float spt) { _sr = sr > 0 ? sr : 44100f; _envRel = (float)Math.Exp(-1.0/(0.08*_sr)); Reset(); }
        public void Reset() { _lp = _env = _sub = _tone = 0f; _state = _prevPos = false; _div = 0; _cc = 0; }
        public void Process(ref float l, ref float r, float amount, float p1, float mode)
        {
            if (amount <= 0f) return;
            if (_cc == 0)
            {
                float fc = 150f * MathF.Pow(40f, p1);                // sub tone 150 Hz → 6 kHz
                _toneCoef = 1f - MathF.Exp(-2f * MathF.PI * fc / _sr);
                _cc = 16;
            }
            _cc--;
            float mono = (l + r) * 0.5f;
            _lp += 0.05f * (mono - _lp);                             // stabilise crossing detection
            bool pos = _lp > 0f;
            if (pos && !_prevPos)                                    // rising zero-crossing
            {
                if (mode < 0.5f) _state = !_state;                  // -1 oct: toggle each crossing
                else { _div ^= 1; if (_div == 0) _state = !_state; }// -2 oct: every 2nd
            }
            _prevPos = pos;
            float am = MathF.Abs(mono);
            _env = am > _env ? am : am + (_env - am) * _envRel;      // follow dynamics
            float sq = _state ? 1f : -1f;
            _tone += _toneCoef * (sq * _env - _tone);               // tone lowpass on the sub
            _tone = Dsp.Ftz(_tone);
            float sub = _tone * amount * 0.6f;        // level-match (was +6 dB)
            l += sub; r += sub;
        }
        public bool IsRinging => false;
        public void SetMusicalContext(int key, int scale) { }
    }

    // ── Formant ─ char: vowel (A-E-I-O-U) · mode: dark/bright. amount = mix ───
    // Three band-pass resonators at vowel formant frequencies, summed. Char morphs
    // the formants through A-E-I-O-U; Mode tilts toward the upper formants (bright)
    // or lower (dark). Sweep Char with the LFO/envelope for a talking filter.
    public sealed class FormantFx : IDrumFx
    {
        // F1,F2,F3 per vowel (A,E,I,O,U), approx male voice
        static readonly float[][] V =
        {
            new[]{ 700f, 1220f, 2600f },  // A
            new[]{ 530f, 1840f, 2480f },  // E
            new[]{ 270f, 2290f, 3010f },  // I
            new[]{ 570f,  840f, 2410f },  // O
            new[]{ 300f,  870f, 2240f },  // U
        };
        float _sr = 44100f; int _cc;
        readonly float[] _k = new float[3];
        readonly float[] _a1 = new float[3], _a2 = new float[3], _a3 = new float[3];
        readonly float[] _icL1 = new float[3], _icL2 = new float[3];
        readonly float[] _icR1 = new float[3], _icR2 = new float[3];
        readonly float[] _gain = new float[3];
        public void Prepare(float sr, float spt) { _sr = sr > 0 ? sr : 44100f; Reset(); }
        public void Reset()
        {
            Array.Clear(_icL1,0,3); Array.Clear(_icL2,0,3);
            Array.Clear(_icR1,0,3); Array.Clear(_icR2,0,3); _cc = 0;
        }
        public void Process(ref float l, ref float r, float amount, float p1, float mode)
        {
            if (amount <= 0f) return;
            if (_cc == 0)
            {
                float fpos = p1 * (V.Length - 1);                    // vowel morph position
                int i0 = (int)fpos; if (i0 > V.Length - 2) i0 = V.Length - 2;
                float fr = fpos - i0;
                for (int b = 0; b < 3; b++)
                {
                    float f = V[i0][b] + (V[i0 + 1][b] - V[i0][b]) * fr;
                    float g = MathF.Tan(MathF.PI * f / _sr);
                    float q = 5f;                                    // formant sharpness
                    _k[b] = 1f / q;
                    _a1[b] = 1f / (1f + g * (g + _k[b])); _a2[b] = g * _a1[b]; _a3[b] = g * _a2[b];
                    // bright (mode 1) tilts gain to upper formants, dark to lower
                    float tilt = (mode - 0.5f) * 2f;                 // -1 dark .. +1 bright
                    _gain[b] = 1f + tilt * (b - 1) * 0.6f;           // b=0 down, b=2 up
                    if (_gain[b] < 0f) _gain[b] = 0f;
                    _gain[b] *= 3.5f;                               // level-match (was -11 dB)
                }
                _cc = 16;
            }
            _cc--;
            l = Band(l, _icL1, _icL2, amount);
            r = Band(r, _icR1, _icR2, amount);
        }
        float Band(float x, float[] ic1, float[] ic2, float amount)
        {
            float wet = 0f;
            for (int b = 0; b < 3; b++)
            {
                float v3 = x - ic2[b];
                float v1 = _a1[b] * ic1[b] + _a2[b] * v3;
                float v2 = ic2[b] + _a2[b] * ic1[b] + _a3[b] * v3;
                ic1[b] = Dsp.Ftz(2f * v1 - ic1[b]); ic2[b] = Dsp.Ftz(2f * v2 - ic2[b]);
                float bp = _k[b] * v1;                               // band-pass tap
                wet += bp * _gain[b];
            }
            return x + (wet - x) * amount;                          // dry → vowel (mix)
        }
        public bool IsRinging => false;
        public void SetMusicalContext(int key, int scale) { }
    }

    // ── Resampler ─ char: sample rate (full → very low) · mode: bits (full/8) ─
    // Classic decimator/downsampler: Char lowers the effective sample rate via
    // sample-and-hold (full rate → ~1/100), Mode crossfades to 8-bit depth,
    // Amount blends dry → resampled. Separate, explicit lo-fi vs Bitcrush's tilt.
    public sealed class ResamplerFx : IDrumFx
    {
        float _holdL, _holdR, _phase;
        public void Prepare(float sr, float spt) { Reset(); }
        public void Reset() { _holdL = _holdR = 0f; _phase = 1f; }
        public void Process(ref float l, ref float r, float amount, float p1, float mode)
        {
            if (amount <= 0f) return;
            float step = 1f - p1 * 0.99f;                 // Char: full rate (1) → ~1/100
            if (step < 0.008f) step = 0.008f;
            _phase += step;
            if (_phase >= 1f) { _phase -= 1f; _holdL = l; _holdR = r; }   // sample & hold

            float xl = _holdL, xr = _holdR;
            float ql = MathF.Round(xl * 128f) * (1f / 128f);   // 8-bit (256 levels)
            float qr = MathF.Round(xr * 128f) * (1f / 128f);
            xl += (ql - xl) * mode;                        // crossfade full → 8-bit
            xr += (qr - xr) * mode;

            l += (xl - l) * amount;                        // dry → resampled (mix)
            r += (xr - r) * amount;
        }
        public bool IsRinging => false;
        public void SetMusicalContext(int key, int scale) { }
    }

    // ── Chorus / Ensemble ─ char: rate · mode: chorus/flanger. amount = mix ──
    // Short modulated delay lines detuned by LFOs, summed with dry. Two voices
    // in stereo-opposed phase for width. Mode shortens the line and adds feedback
    // for a flanger sweep. Char sets the modulation rate; depth is fixed-musical.
    public sealed class ChorusFx : IDrumFx
    {
        // Char → LFO cycle length in ticks. Starts at sub-tick (fast shimmer).
        public static readonly float[] Divs = { 0.125f, 0.25f, 0.5f, 0.75f, 1f, 2f, 3f, 4f, 6f, 8f, 12f, 16f };
        public static readonly string[] DivLabels = { "1/8", "1/4", "1/2", "3/4", "1", "2", "3", "4", "6", "8", "12", "16" };
        public static int CharToIdx(float p1)
        {
            int i = (int)(p1 * (Divs.Length - 1) + 0.5f);
            return i < 0 ? 0 : (i >= Divs.Length ? Divs.Length - 1 : i);
        }

        float[] _bL, _bR; int _n, _w;
        float _sr = 44100f, _ph0, _ph1, _rate = 1.5f, _fbL, _fbR, _spt = 11025f;
        int _cc; float _depthS, _baseS, _fb;
        public void Prepare(float sr, float spt)
        {
            _sr = sr > 0 ? sr : 44100f;
            _spt = spt > 1f ? spt : _sr / 8f;
            _n = Math.Max(8, (int)(0.05f * _sr));         // 50 ms line
            _bL = new float[_n]; _bR = new float[_n];
            Reset();
        }
        // Tempo-synced LFO rate: the machine pushes samples-per-tick per block.
        public void SetSpt(float spt) { if (spt > 1f) _spt = spt; }
        public void Reset() { Array.Clear(_bL,0,_n); Array.Clear(_bR,0,_n); _w=0; _ph0=0f; _ph1=0.25f; _fbL=_fbR=0f; _cc=0; }
        public void Process(ref float l, ref float r, float amount, float p1, float mode)
        {
            if (amount <= 0f) return;
            if (_cc == 0)
            {
                float ticks = Divs[CharToIdx(p1)];         // Char → tempo division (ticks/cycle, sub-tick ok)
                float period = ticks * _spt; if (period < 2f) period = 2f;
                _rate = _sr / period;                      // one LFO cycle per division
                _baseS = (mode < 0.5f ? 0.011f : 0.0012f) * _sr;   // chorus ~11ms / flanger ~1.2ms
                _depthS = (mode < 0.5f ? 0.004f : 0.0010f) * _sr;
                _fb = mode * 0.6f;                         // feedback only in flanger range
                _cc = 16;
            }
            _cc--;
            _w++; if (_w >= _n) _w = 0;
            float inc = _rate / _sr;
            _ph0 += inc; if (_ph0 >= 1f) _ph0 -= 1f;
            _ph1 += inc; if (_ph1 >= 1f) _ph1 -= 1f;
            float m0 = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * _ph0);
            float m1 = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * _ph1);
            float dL = _baseS + _depthS * m0;              // stereo-opposed voices
            float dR = _baseS + _depthS * m1;
            _bL[_w] = Dsp.Ftz(l + _fb * _fbL);
            _bR[_w] = Dsp.Ftz(r + _fb * _fbR);
            float wetL = ReadFrac(_bL, _w, dL);
            float wetR = ReadFrac(_bR, _w, dR);
            _fbL = wetL; _fbR = wetR;
            l += (wetL - l) * amount * 0.9f;
            r += (wetR - r) * amount * 0.9f;
        }
        float ReadFrac(float[] b, int w, float d)
        {
            float rp = w - d; if (rp < 0) rp += _n;
            int i0 = (int)rp; float fr = rp - i0; int i1 = i0 + 1; if (i1 >= _n) i1 -= _n;
            return b[i0] + (b[i1] - b[i0]) * fr;
        }
        public bool IsRinging => false;
        public void SetMusicalContext(int key, int scale) { }
    }

    // ── Freeze / Granular hold ─ char: grain size · mode: capture mode ───────
    // Captures a short slice and loops it into a sustained pad under the dry.
    // Char sets grain/loop length; Mode: one-shot (freeze the first slice while
    // amount>0) vs continuous (re-capture on each loop wrap, a smearing texture).
    // Loop is windowed and crossfaded at the wrap so it sustains without a click.
    public sealed class FreezeFx : IDrumFx
    {
        float[] _ring, _grain; int _rn, _rw, _gn, _play; bool _have;
        float _sr = 44100f; int _cc; bool _continuous;
        float _fast, _slow, _fastRel, _slowRel; int _armWait, _pending;
        Tail _tail;
        public void Prepare(float sr, float spt)
        {
            _sr = sr > 0 ? sr : 44100f;
            _rn = Math.Max(8, (int)(0.5f * _sr));          // 500 ms capture ring
            _ring = new float[_rn]; _grain = new float[_rn];
            _fastRel = (float)Math.Exp(-1.0 / (0.003 * _sr));
            _slowRel = (float)Math.Exp(-1.0 / (0.100 * _sr));
            _tail.Prepare(_sr); Reset();
        }
        public void Reset() { Array.Clear(_ring,0,_rn); _rw=0; _gn=0; _play=0; _have=false; _cc=0; _fast=_slow=0f; _armWait=0; _pending=-1; _tail.Reset(); }
        public void Process(ref float l, ref float r, float amount, float p1, float mode)
        {
            float mono = (l + r) * 0.5f;
            _ring[_rw] = mono; _rw++; if (_rw >= _rn) _rw = 0;   // always record dry

            // transient detector on the dry input (for one-shot capture triggering)
            float a = MathF.Abs(mono);
            _fast = a > _fast ? a : a + (_fast - a) * _fastRel;
            _slow = a > _slow ? a : a + (_slow - a) * _slowRel;
            bool transient = _fast > _slow * 1.8f && _fast > 0.02f;
            if (_armWait > 0) _armWait--;

            if (amount <= 0f) { _have = false; _tail.Reset(); return; }
            if (_cc == 0)
            {
                _gn = Math.Max(64, (int)((0.38f - p1 * 0.35f) * _sr));  // 380→30 ms grain (Char reversed)
                if (_gn > _rn) _gn = _rn;
                _continuous = mode >= 0.5f;
                _cc = 16;
            }
            _cc--;

            // One-shot: (re)capture on a fresh transient so a hit is frozen into a
            // sustained grain (never captures the opening silence). Continuous:
            // re-capture on each loop wrap for a smearing texture.
            if (!_continuous)
            {
                // On a transient (or if we have nothing yet), schedule a capture one
                // grain-length later so the ring fills with the hit body, not its
                // leading edge. _pending counts down to the capture.
                if ((!_have || transient) && _armWait == 0 && _pending < 0)
                {
                    _pending = _gn; _armWait = (int)(0.08f * _sr);
                }
                if (_pending == 0) { Capture(); _pending = -1; }
                else if (_pending > 0) _pending--;
            }
            else if (!_have || _play == 0) Capture();

            if (!_have) { return; }
            float g = _grain[_play];
            float wpos = _play / (float)_gn;
            float win = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * wpos);   // raised-cosine loop window
            float wet = g * win * 1.6f;                     // window halves RMS; compensate
            _play++; if (_play >= _gn) _play = 0;
            _tail.Feed(wet * amount, wet * amount);
            l += (wet - l) * amount; r += (wet - r) * amount;
        }
        void Capture()
        {
            int start = _rw - _gn; if (start < 0) start += _rn;
            for (int i = 0; i < _gn; i++) { int idx = start + i; if (idx >= _rn) idx -= _rn; _grain[i] = _ring[idx]; }
            _play = 0; _have = true;
        }
        public bool IsRinging => _tail.Ringing;
        public void SetMusicalContext(int key, int scale) { }
    }

    // ── Auto-wah / Envelope filter ─ char: sensitivity · mode: up/down ───────
    // A resonant bandpass whose cutoff tracks the input envelope — louder input
    // sweeps it open (up) or closed (down). Reuses the TPT SVF and an envelope
    // follower. Amount = wet mix.
    public sealed class AutoWahFx : IDrumFx
    {
        float _sr = 44100f, _env, _envRel, _ic1L, _ic2L, _ic1R, _ic2R;
        int _cc; float _sens = 3f; bool _down;
        public void Prepare(float sr, float spt)
        {
            _sr = sr > 0 ? sr : 44100f;
            _envRel = (float)Math.Exp(-1.0 / (0.040 * _sr));   // 40 ms release
            Reset();
        }
        public void Reset() { _env = 0f; _ic1L=_ic2L=_ic1R=_ic2R=0f; _cc=0; }
        public void Process(ref float l, ref float r, float amount, float p1, float mode)
        {
            if (amount <= 0f) return;
            if (_cc == 0) { _sens = 1f + p1 * 9f; _down = mode >= 0.5f; _cc = 8; }
            _cc--;
            float a = MathF.Max(MathF.Abs(l), MathF.Abs(r));
            _env = a > _env ? a : a + (_env - a) * _envRel;
            if (_env < 1e-15f) _env = 0f;
            float e = _env * _sens; if (e > 1f) e = 1f;
            float sweep = _down ? (1f - e) : e;
            float fc = 180f * MathF.Pow(10000f / 180f, sweep);   // 180 Hz → 18 kHz
            float g = MathF.Tan(MathF.PI * fc / _sr), k = 1f / 3.5f;   // Q ~3.5
            float a1 = 1f / (1f + g * (g + k)), a2 = g * a1, a3 = g * a2;

            float v3 = l - _ic2L, v1 = a1 * _ic1L + a2 * v3, v2 = _ic2L + a2 * _ic1L + a3 * v3;
            _ic1L = Dsp.Ftz(2f*v1 - _ic1L); _ic2L = Dsp.Ftz(2f*v2 - _ic2L);
            float bpL = k * v1;
            l += (bpL * 2f - l) * amount;
            v3 = r - _ic2R; v1 = a1 * _ic1R + a2 * v3; v2 = _ic2R + a2 * _ic1R + a3 * v3;
            _ic1R = Dsp.Ftz(2f*v1 - _ic1R); _ic2R = Dsp.Ftz(2f*v2 - _ic2R);
            float bpR = k * v1;
            r += (bpR * 2f - r) * amount;
        }
        public bool IsRinging => false;
        public void SetMusicalContext(int key, int scale) { }
    }

    // ── Exciter / Enhancer ─ char: frequency · mode: tube/bright. amount=drive
    // Highpasses the signal, generates harmonics on that high band (soft or hard),
    // and adds the result back for air/presence. Band-limited, unlike Drive.
    public sealed class ExciterFx : IDrumFx
    {
        float _sr = 44100f, _hpL, _hxL, _hpR, _hxR; int _cc; float _aHP = 0.8f; bool _bright;
        public void Prepare(float sr, float spt) { _sr = sr > 0 ? sr : 44100f; Reset(); }
        public void Reset() { _hpL=_hxL=_hpR=_hxR=0f; _cc=0; }
        public void Process(ref float l, ref float r, float amount, float p1, float mode)
        {
            if (amount <= 0f) return;
            if (_cc == 0)
            {
                float fc = 1500f * MathF.Pow(8f, p1);      // 1.5 kHz → 12 kHz band start
                _aHP = MathF.Exp(-2f * MathF.PI * fc / _sr);
                _bright = mode >= 0.5f;
                _cc = 16;
            }
            _cc--;
            float drive = 1f + amount * 6f;
            float hl = _aHP * (_hpL + l - _hxL); _hxL = l; _hpL = Dsp.Ftz(hl);
            float hr = _aHP * (_hpR + r - _hxR); _hxR = r; _hpR = Dsp.Ftz(hr);
            float exL = _bright ? Dsp.TanhFast(hl * drive * 1.5f) : (hl * drive - (hl*drive)*(hl*drive)*(hl*drive) * (1f/3f));
            float exR = _bright ? Dsp.TanhFast(hr * drive * 1.5f) : (hr * drive - (hr*drive)*(hr*drive)*(hr*drive) * (1f/3f));
            l += exL * amount * 0.22f;                      // add sparkle on top of dry (level-matched)
            r += exR * amount * 0.22f;
        }
        public bool IsRinging => false;
        public void SetMusicalContext(int key, int scale) { }
    }
}
