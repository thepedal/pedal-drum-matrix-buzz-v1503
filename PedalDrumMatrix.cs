using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;          // MessageBox (About dialog)
using Buzz.MachineInterface;   // IBuzzMachine, IBuzzMachineHost, MachineDecl, ParameterDecl, Sample, WorkModes
using BuzzGUI.Interfaces;      // IMachine, IBuzz, IMenuItem
using BuzzGUI.Common;          // MenuItemVM, SimpleCommand

namespace PedalDrumMatrix
{
    [MachineDecl(Name = "Pedal Drum Matrix", ShortName = "DrumMtx",
                 Author = "thepedal", MaxTracks = 0)]
    public class PedalDrumMatrixMachine : IBuzzMachine
    {
        internal const string Version = "1.3.6";
        const int SLOTS = 6;

        // Right-click "About..." entry in the Machine View context menu.
        public IEnumerable<IMenuItem> Commands
        {
            get
            {
                yield return new MenuItemVM()
                {
                    Text = "About...",
                    Command = new SimpleCommand()
                    {
                        CanExecuteDelegate = p => true,
                        ExecuteDelegate = p => MessageBox.Show(
                            $"Pedal Drum Matrix   v{Version}  (Buzz 1503)\n\n" +
                            "Drum-centric 6-slot serial multi-effect: ten effects per slot, " +
                            "global feedback loop, envelope + tempo-synced LFO modulation, " +
                            "tuned resonator, and a live scene morph.\n\n" +
                            "github.com/thepedal/pedal-drum-matrix-buzz-v1503\n" +
                            "GNU General Public License v3.0",
                            "About Pedal Drum Matrix")
                    }
                };
            }
        }

        readonly IBuzzMachineHost host;
        readonly Slot[] _slots = new Slot[SLOTS];
        readonly OutputLimiter _limiter = new OutputLimiter();
        readonly AutoGain _autoGain = new AutoGain();
        readonly GlobalFeedback _feedback = new GlobalFeedback();
        readonly Lfo _lfo = new Lfo();
        readonly float[] _envDepth = new float[SLOTS];
        readonly float[] _lfoDepth = new float[SLOTS];
        static readonly int[] LfoTicks = { 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64 };
        float _sampleRate = 0f, _spt = 11025f;

        // Envelope follower (tracks the dry input — the actual drum hits).
        float _env, _envRelCoef = 0.999f, _fbEnvDepth;
        const float EnvSense = 2.5f;   // so typical drums reach strong modulation

        // ── Scene morph ──────────────────────────────────────────────────────
        // Scene A is always the LIVE parameters (so the knobs and presets always
        // drive the sound). Store captures a single target snapshot; the Morph
        // knob blends live→target (continuous params interpolate, discrete ones
        // switch at the midpoint). The target persists via MachineState, keyed by
        // param name so future param additions never corrupt saved songs.
        PropertyInfo[] _sp;                 // scene params (all but Morph/Store), name-sorted
        Func<int>[] _getters;               // compiled getters (avoid per-block reflection)
        Dictionary<string, int> _spIdx;     // name → index
        bool[] _spDiscrete;
        int[] _scene, _eff, _live;
        bool _hasScene, _prevStore;
        bool _started;   // set by the first Work() (1503 notes 3.3)
        const uint StateMagic = 0x4D52_4450u;   // 'PDRM'
        const byte StateVer = 2;

        void EnsureReflect()
        {
            if (_sp != null) return;
            _sp = GetType().GetProperties()
                .Where(p => p.PropertyType == typeof(int)
                            && p.GetCustomAttribute(typeof(ParameterDecl)) != null
                            && p.Name != "Morph" && p.Name != "Store")
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToArray();
            int n = _sp.Length;
            _spIdx = new Dictionary<string, int>(n);
            _spDiscrete = new bool[n];
            for (int i = 0; i < n; i++)
            {
                string nm = _sp[i].Name;
                _spIdx[nm] = i;
                _spDiscrete[i] = nm.EndsWith("Type") || nm.EndsWith("Mode")
                              || nm == "LfoWave" || nm == "LfoRate" || nm == "Key" || nm == "Scale";
            }
            _scene = new int[n]; _eff = new int[n]; _live = new int[n];
            _getters = new Func<int>[n];
            for (int i = 0; i < n; i++)
                _getters[i] = (Func<int>)_sp[i].GetGetMethod().CreateDelegate(typeof(Func<int>), this);
        }

        int E(string name) => _eff[_spIdx[name]];

        void ComputeEffective(float mf)
        {
            for (int i = 0; i < _sp.Length; i++) _live[i] = _getters[i]();

            bool st = Store != 0;
            if (st && !_prevStore) { Array.Copy(_live, _scene, _live.Length); _hasScene = true; }
            _prevStore = st;

            // A endpoint = live (always), B endpoint = stored target (or live if none).
            for (int i = 0; i < _sp.Length; i++)
            {
                int av = _live[i];
                int bv = _hasScene ? _scene[i] : _live[i];
                if (mf <= 0f) _eff[i] = av;
                else if (mf >= 1f) _eff[i] = bv;
                else if (_spDiscrete[i]) _eff[i] = mf < 0.5f ? av : bv;
                else _eff[i] = (int)MathF.Round(av + (bv - av) * mf);
            }
        }

        public byte[] MachineState
        {
            get
            {
                try
                {
                    EnsureReflect();
                    using var ms = new MemoryStream();
                    using var bw = new BinaryWriter(ms, Encoding.UTF8);
                    bw.Write(StateMagic); bw.Write(StateVer);
                    bw.Write(_hasScene);
                    bw.Write(_hasScene ? _sp.Length : 0);
                    if (_hasScene)
                        for (int i = 0; i < _sp.Length; i++) { bw.Write(_sp[i].Name); bw.Write(_scene[i]); }
                    return ms.ToArray();
                }
                catch { return Array.Empty<byte>(); }
            }
            set
            {
                if (value == null || value.Length < 5) return;
                try
                {
                    EnsureReflect();
                    using var ms = new MemoryStream(value);
                    using var br = new BinaryReader(ms, Encoding.UTF8);
                    if (br.ReadUInt32() != StateMagic) return;
                    if (br.ReadByte() != StateVer) return;   // older layout → start fresh
                    bool has = br.ReadBoolean();
                    int count = br.ReadInt32();
                    var map = new Dictionary<string, int>(count);
                    for (int i = 0; i < count; i++) { string nm = br.ReadString(); int v = br.ReadInt32(); map[nm] = v; }
                    if (has)
                        for (int i = 0; i < _sp.Length; i++)   // name-keyed restore
                            _scene[i] = map.TryGetValue(_sp[i].Name, out int v) ? v : (int)_sp[i].GetValue(this);
                    _hasScene = has;
                }
                catch { _hasScene = false; }
            }
        }

        public PedalDrumMatrixMachine(IBuzzMachineHost host)
        {
            this.host = host;
            for (int i = 0; i < SLOTS; i++) _slots[i] = new Slot();
        }

        public IMachine Machine { get; set; }

        // ── Parameters ───────────────────────────────────────────────────────
        // Declaration ORDER is the preset contract (Build §3.3) — append only.
        // Per slot: a Type selector (enum, click-free swappable, peer/MIDI
        // mappable like any param) followed by its Amount macro (the six
        // mappable performance controls). The ValueDescriptions literal must be
        // inline per attribute (a static-field reference won't compile) and its
        // order must match the FxType enum.

        [ParameterDecl(MinValue = 0, MaxValue = 16, DefValue = 0, ValueDescriptions = new[] {
            "None","Bitcrush","Drive","Lowpass","RingMod","Comb","Stutter","Delay","Reverb","Gate","Resonator","Highpass","Transient","Wavefolder","Phaser","SubOctave","Formant" },
            Description = "Slot 1 - effect type")]
        public int Slot1Type { get; set; } = 0;
        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 0, Description = "Slot 1 - amount")]
        public int Slot1Amount { get; set; } = 0;

        [ParameterDecl(MinValue = 0, MaxValue = 16, DefValue = 0, ValueDescriptions = new[] {
            "None","Bitcrush","Drive","Lowpass","RingMod","Comb","Stutter","Delay","Reverb","Gate","Resonator","Highpass","Transient","Wavefolder","Phaser","SubOctave","Formant" },
            Description = "Slot 2 - effect type")]
        public int Slot2Type { get; set; } = 0;
        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 0, Description = "Slot 2 - amount")]
        public int Slot2Amount { get; set; } = 0;

        [ParameterDecl(MinValue = 0, MaxValue = 16, DefValue = 0, ValueDescriptions = new[] {
            "None","Bitcrush","Drive","Lowpass","RingMod","Comb","Stutter","Delay","Reverb","Gate","Resonator","Highpass","Transient","Wavefolder","Phaser","SubOctave","Formant" },
            Description = "Slot 3 - effect type")]
        public int Slot3Type { get; set; } = 0;
        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 0, Description = "Slot 3 - amount")]
        public int Slot3Amount { get; set; } = 0;

        [ParameterDecl(MinValue = 0, MaxValue = 16, DefValue = 0, ValueDescriptions = new[] {
            "None","Bitcrush","Drive","Lowpass","RingMod","Comb","Stutter","Delay","Reverb","Gate","Resonator","Highpass","Transient","Wavefolder","Phaser","SubOctave","Formant" },
            Description = "Slot 4 - effect type")]
        public int Slot4Type { get; set; } = 0;
        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 0, Description = "Slot 4 - amount")]
        public int Slot4Amount { get; set; } = 0;

        [ParameterDecl(MinValue = 0, MaxValue = 16, DefValue = 0, ValueDescriptions = new[] {
            "None","Bitcrush","Drive","Lowpass","RingMod","Comb","Stutter","Delay","Reverb","Gate","Resonator","Highpass","Transient","Wavefolder","Phaser","SubOctave","Formant" },
            Description = "Slot 5 - effect type")]
        public int Slot5Type { get; set; } = 0;
        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 0, Description = "Slot 5 - amount")]
        public int Slot5Amount { get; set; } = 0;

        [ParameterDecl(MinValue = 0, MaxValue = 16, DefValue = 0, ValueDescriptions = new[] {
            "None","Bitcrush","Drive","Lowpass","RingMod","Comb","Stutter","Delay","Reverb","Gate","Resonator","Highpass","Transient","Wavefolder","Phaser","SubOctave","Formant" },
            Description = "Slot 6 - effect type")]
        public int Slot6Type { get; set; } = 0;
        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 0, Description = "Slot 6 - amount")]
        public int Slot6Amount { get; set; } = 0;

        // ── Character controls (appended — Build §3.3) ───────────────────────
        // Per slot: Char (continuous rotary, effect-specific) + Mode (switch).
        // Maps to a BCR2000 dual-function encoder: rotation → Char, push → Mode.
        // Appended after the original 12 params so existing songs keep their
        // Type/Amount values; they group at the bottom of the rack.

        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 64, Description = "Slot 1 - char")]
        public int Slot1Char { get; set; } = 64;
        [ParameterDecl(MinValue = 0, MaxValue = 1, DefValue = 0, ValueDescriptions = new[] { "Off", "On" }, Description = "Slot 1 - mode")]
        public int Slot1Mode { get; set; } = 0;

        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 64, Description = "Slot 2 - char")]
        public int Slot2Char { get; set; } = 64;
        [ParameterDecl(MinValue = 0, MaxValue = 1, DefValue = 0, ValueDescriptions = new[] { "Off", "On" }, Description = "Slot 2 - mode")]
        public int Slot2Mode { get; set; } = 0;

        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 64, Description = "Slot 3 - char")]
        public int Slot3Char { get; set; } = 64;
        [ParameterDecl(MinValue = 0, MaxValue = 1, DefValue = 0, ValueDescriptions = new[] { "Off", "On" }, Description = "Slot 3 - mode")]
        public int Slot3Mode { get; set; } = 0;

        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 64, Description = "Slot 4 - char")]
        public int Slot4Char { get; set; } = 64;
        [ParameterDecl(MinValue = 0, MaxValue = 1, DefValue = 0, ValueDescriptions = new[] { "Off", "On" }, Description = "Slot 4 - mode")]
        public int Slot4Mode { get; set; } = 0;

        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 64, Description = "Slot 5 - char")]
        public int Slot5Char { get; set; } = 64;
        [ParameterDecl(MinValue = 0, MaxValue = 1, DefValue = 0, ValueDescriptions = new[] { "Off", "On" }, Description = "Slot 5 - mode")]
        public int Slot5Mode { get; set; } = 0;

        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 64, Description = "Slot 6 - char")]
        public int Slot6Char { get; set; } = 64;
        [ParameterDecl(MinValue = 0, MaxValue = 1, DefValue = 0, ValueDescriptions = new[] { "Off", "On" }, Description = "Slot 6 - mode")]
        public int Slot6Mode { get; set; } = 0;

        // Output safety limiter (zero-latency peak limiter + soft-clip ceiling).
        // On by default; appended last (Build §3.3).
        [ParameterDecl(MinValue = 0, MaxValue = 1, DefValue = 1, ValueDescriptions = new[] { "Off", "On" }, Description = "Output limiter")]
        public int Limiter { get; set; } = 1;

        // Auto-gain leveler — brings quiet output up toward a target, holds on
        // silence. Pair with the limiter for "set and forget" output level.
        // Default Off so existing songs are unchanged; appended (Build §3.3).
        [ParameterDecl(MinValue = 0, MaxValue = 1, DefValue = 0, ValueDescriptions = new[] { "Off", "On" }, Description = "Auto gain (leveler)")]
        public int AutoGainOn { get; set; } = 0;

        // Global feedback loop — a portion of the rack output is delayed,
        // tone-shaped and saturated, then fed back into the input. Turns the
        // rack into a self-resonating instrument driven by the drums. Appended.
        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 0, Description = "Feedback amount")]
        public int Feedback { get; set; } = 0;
        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 64, Description = "Feedback time")]
        public int FbTime { get; set; } = 64;
        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 64, Description = "Feedback tone")]
        public int FbTone { get; set; } = 64;

        // Envelope follower (tracks dry-input drum hits) as a modulation source.
        // EnvRelease shapes how long each hit's modulation lasts; per-slot
        // EnvDepth (bipolar, 64 = none) routes the envelope to that slot's Char;
        // EnvToFb routes it to the feedback amount (hits bloom the loop). Appended.
        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 64, Description = "Envelope release")]
        public int EnvRelease { get; set; } = 64;
        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 0, Description = "Envelope to feedback")]
        public int EnvToFb { get; set; } = 0;

        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 64, Description = "Slot 1 - env depth")]
        public int Slot1EnvDepth { get; set; } = 64;
        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 64, Description = "Slot 2 - env depth")]
        public int Slot2EnvDepth { get; set; } = 64;
        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 64, Description = "Slot 3 - env depth")]
        public int Slot3EnvDepth { get; set; } = 64;
        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 64, Description = "Slot 4 - env depth")]
        public int Slot4EnvDepth { get; set; } = 64;
        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 64, Description = "Slot 5 - env depth")]
        public int Slot5EnvDepth { get; set; } = 64;
        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 64, Description = "Slot 6 - env depth")]
        public int Slot6EnvDepth { get; set; } = 64;

        // Tempo-synced LFO modulation source. Rate is a cycle length in ticks;
        // per-slot LfoDepth (bipolar, 64 = none) routes it to that slot's Char.
        [ParameterDecl(MinValue = 0, MaxValue = 11, DefValue = 7,
            ValueDescriptions = new[] { "1", "2", "3", "4", "6", "8", "12", "16", "24", "32", "48", "64" },
            Description = "LFO rate (ticks per cycle)")]
        public int LfoRate { get; set; } = 7;
        [ParameterDecl(MinValue = 0, MaxValue = 5, DefValue = 0,
            ValueDescriptions = new[] { "Sine", "Triangle", "Saw", "Square", "Steps", "Random" },
            Description = "LFO wave")]
        public int LfoWave { get; set; } = 0;

        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 64, Description = "Slot 1 - LFO depth")]
        public int Slot1LfoDepth { get; set; } = 64;
        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 64, Description = "Slot 2 - LFO depth")]
        public int Slot2LfoDepth { get; set; } = 64;
        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 64, Description = "Slot 3 - LFO depth")]
        public int Slot3LfoDepth { get; set; } = 64;
        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 64, Description = "Slot 4 - LFO depth")]
        public int Slot4LfoDepth { get; set; } = 64;
        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 64, Description = "Slot 5 - LFO depth")]
        public int Slot5LfoDepth { get; set; } = 64;
        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 64, Description = "Slot 6 - LFO depth")]
        public int Slot6LfoDepth { get; set; } = 64;

        // Musical tuning for any Resonator slots: Key (root) + Scale. A
        // resonator's Char selects a degree of this scale. Appended.
        [ParameterDecl(MinValue = 0, MaxValue = 11, DefValue = 0,
            ValueDescriptions = new[] { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" },
            Description = "Key (resonator root)")]
        public int Key { get; set; } = 0;
        [ParameterDecl(MinValue = 0, MaxValue = 7, DefValue = 0,
            ValueDescriptions = new[] { "Major", "Minor", "Maj Pent", "Min Pent", "Dorian", "Phrygian", "Blues", "Whole Tone" },
            Description = "Scale (resonator)")]
        public int Scale { get; set; } = 0;

        // Scene morph (excluded from the scene snapshot). Scene A is the live
        // params; Store snapshots the current settings as the morph target on
        // Off→On; Morph blends live→target.
        [ParameterDecl(MinValue = 0, MaxValue = 127, DefValue = 0, Description = "Morph (live to target)")]
        public int Morph { get; set; } = 0;
        [ParameterDecl(MinValue = 0, MaxValue = 1, DefValue = 0, ValueDescriptions = new[] { "...", "Store" }, Description = "Store morph target (Off to On)")]
        public int Store { get; set; } = 0;

        void PushParamsToSlots()
        {
            _slots[0].SetParams(E("Slot1Type"), E("Slot1Amount"), E("Slot1Char"), E("Slot1Mode") != 0, E("Key"), E("Scale"));
            _slots[1].SetParams(E("Slot2Type"), E("Slot2Amount"), E("Slot2Char"), E("Slot2Mode") != 0, E("Key"), E("Scale"));
            _slots[2].SetParams(E("Slot3Type"), E("Slot3Amount"), E("Slot3Char"), E("Slot3Mode") != 0, E("Key"), E("Scale"));
            _slots[3].SetParams(E("Slot4Type"), E("Slot4Amount"), E("Slot4Char"), E("Slot4Mode") != 0, E("Key"), E("Scale"));
            _slots[4].SetParams(E("Slot5Type"), E("Slot5Amount"), E("Slot5Char"), E("Slot5Mode") != 0, E("Key"), E("Scale"));
            _slots[5].SetParams(E("Slot6Type"), E("Slot6Amount"), E("Slot6Char"), E("Slot6Mode") != 0, E("Key"), E("Scale"));
            _feedback.SetParams(E("Feedback"), E("FbTime"), E("FbTone"));

            _envDepth[0] = (E("Slot1EnvDepth") - 64) / 64f; _lfoDepth[0] = (E("Slot1LfoDepth") - 64) / 64f;
            _envDepth[1] = (E("Slot2EnvDepth") - 64) / 64f; _lfoDepth[1] = (E("Slot2LfoDepth") - 64) / 64f;
            _envDepth[2] = (E("Slot3EnvDepth") - 64) / 64f; _lfoDepth[2] = (E("Slot3LfoDepth") - 64) / 64f;
            _envDepth[3] = (E("Slot4EnvDepth") - 64) / 64f; _lfoDepth[3] = (E("Slot4LfoDepth") - 64) / 64f;
            _envDepth[4] = (E("Slot5EnvDepth") - 64) / 64f; _lfoDepth[4] = (E("Slot5LfoDepth") - 64) / 64f;
            _envDepth[5] = (E("Slot6EnvDepth") - 64) / 64f; _lfoDepth[5] = (E("Slot6LfoDepth") - 64) / 64f;

            int rate = E("LfoRate"); _lfo.SetRate(LfoTicks[rate < 0 ? 0 : (rate > 11 ? 11 : rate)], _spt);
            _lfo.SetWave(E("LfoWave"));

            float relMs = 20f * MathF.Pow(100f, E("EnvRelease") / 127f);   // 20 ms → 2 s
            _envRelCoef = (float)Math.Exp(-1.0 / (relMs * 0.001 * (_sampleRate > 0 ? _sampleRate : 44100f)));
            _fbEnvDepth = (E("EnvToFb") / 127f) * 0.3f;                    // up to +0.3 feedback on a hit
        }

        bool AnyTailRinging()
        {
            for (int s = 0; s < SLOTS; s++) if (_slots[s].IsRinging) return true;
            return false;
        }

        // ── Work — single stereo in/out EffectBlock ─────────────────────────
        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            int sr = (int)host.MasterInfo.SamplesPerSec;
            float spt = (float)host.MasterInfo.SamplesPerTick;
            _spt = spt > 1f ? spt : (_sampleRate > 0 ? _sampleRate : 44100f) / 8f;
            if (sr > 0 && sr != _sampleRate)
            {
                _sampleRate = sr;
                for (int s = 0; s < SLOTS; s++) _slots[s].Prepare(_sampleRate, spt);
                _limiter.Prepare(_sampleRate);
                _autoGain.Prepare(_sampleRate);
                _feedback.Prepare(_sampleRate, spt);
            }

            // Song load runs the parameter setters and the MachineState setter
            // before the first Work() (1503 notes 3.3). Latch the saved Store
            // state here so a song saved with Store On is not read as an
            // Off-to-On edge, which would overwrite the restored morph target.
            if (!_started) { _started = true; _prevStore = Store != 0; }

            // Buzz 1503 (1503 notes 2.1): input is only valid when the READ flag
            // is set. A muted upstream machine does NOT arrive as WM_NOIO; Work()
            // is called without READ while `input` still holds the last block
            // from before the mute. Reading it would loop that stale block
            // through the rack (a constant buzz) and keep exciting the envelope
            // follower and the feedback loop, so the machine would never sleep.
            bool hasInput = (mode & WorkModes.WM_READ) != 0 && input != null;

            // No input: keep rendering tails from silence (a Reverb/Delay/
            // Resonator or the feedback loop may still be ringing); sleep only
            // once the whole rack is quiet.
            if (!hasInput && !AnyTailRinging() && !_feedback.IsRinging) return false;

            // READ without WRITE: the host wants the input seen but no output.
            if (hasInput && (mode & WorkModes.WM_WRITE) == 0) return false;

            EnsureReflect();

            ComputeEffective(Morph / 127f);
            PushParamsToSlots();

            // Buzz audio is ±32768, not ±1.0 (1503 notes 2.3).
            // Normalise to ±1.0 for the DSP, denormalise on output — otherwise
            // any absolute-threshold stage (limiter, drive, soft-clip) is wildly
            // mis-scaled (the limiter crushes full-scale audio to silence).
            const float INV = 1f / 32768f, SCALE = 32768f;
            for (int i = 0; i < n; i++)
            {
                float inL = hasInput ? input[i].L * INV : 0f;
                float inR = hasInput ? input[i].R * INV : 0f;

                // envelope follows the dry input (instant attack, param release)
                float a = MathF.Max(MathF.Abs(inL), MathF.Abs(inR));
                _env = a > _env ? a : a + (_env - a) * _envRelCoef;
                if (_env < 1e-15f) _env = 0f;   // keep the follower out of denormals
                float envMod = _env * EnvSense; if (envMod > 1f) envMod = 1f;

                _feedback.Tap(out float fbL, out float fbR, envMod * _fbEnvDepth);
                float l = inL + fbL;
                float r = inR + fbR;

                // serial rack: slot 0 → slot 5; Char modulated per slot by env + LFO
                float lfoVal = _lfo.Next();
                for (int s = 0; s < SLOTS; s++)
                {
                    float charMod = envMod * _envDepth[s] + lfoVal * _lfoDepth[s];
                    _slots[s].Process(ref l, ref r, charMod);
                }

                _feedback.Write(l, r);   // rack output (pre-AGC/limiter) feeds the loop

                if (AutoGainOn != 0) _autoGain.Process(ref l, ref r);
                if (Limiter != 0) _limiter.Process(ref l, ref r);

                output[i].L = l * SCALE;
                output[i].R = r * SCALE;
            }

            return true;
        }

        // ── Value labelling (DescribeValue) ─────────────────────────────────
        // Called only on cursor hover (UI thread, never the audio thread), so it
        // can be as descriptive as we like. Char/Mode read the slot's current
        // Type, so the readout reflects whichever effect is loaded — e.g. a
        // slot's Char shows "Q 2.0" under Filter but "Feedback 47%" under Delay,
        // and its Mode shows "Lowpass" / "Ping-pong" accordingly. Returning null
        // falls back to ValueDescriptions (Type, Limiter, Auto Gain).
        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
        static string Pct(int v) => ((int)MathF.Round(v * 100f / 127f)).ToString(Inv) + "%";

        int SlotTypeOf(int slot) => slot switch
        {
            1 => Slot1Type, 2 => Slot2Type, 3 => Slot3Type,
            4 => Slot4Type, 5 => Slot5Type, 6 => Slot6Type, _ => 0
        };

        public string DescribeValue(IParameter param, int value)
        {
            string n = param?.Name ?? "";
            if (n.StartsWith("Slot") && n.Length > 5 && char.IsDigit(n[4]))
            {
                FxType t = (FxType)SlotTypeOf(n[4] - '0');
                if (n.EndsWith("Amount")) return Pct(value);
                if (n.EndsWith("Char"))   return t == FxType.Resonator ? NoteName(value) : CharLabel(t, value);
                if (n.EndsWith("Mode"))     return ModeLabel(t, value != 0);
                if (n.EndsWith("Depth"))    return SignedPct(value);
            }
            if (n == "Feedback")   return Pct(value);
            if (n == "FbTime")     return FormatMs(1f * MathF.Pow(500f, value / 127f));
            if (n == "FbTone")     return FormatHz(200f * MathF.Pow(60f, value / 127f));
            if (n == "EnvRelease") return FormatMs(20f * MathF.Pow(100f, value / 127f));
            if (n == "EnvToFb")    return Pct(value);
            if (n == "Morph")      return Pct(value);
            return null;   // Type / Limiter / Auto Gain → ValueDescriptions
        }

        static string SignedPct(int v)
        {
            int p = (int)MathF.Round((v - 64) * 100f / 64f);
            return (p > 0 ? "+" : "") + p.ToString(Inv) + "%";
        }

        static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        string NoteName(int charValue)
        {
            float freq = ResonatorFx.CharToFreq(Key, Scale, charValue / 127f);
            int midi = (int)MathF.Round(69f + 12f * MathF.Log2(freq / 440f));
            return NoteNames[((midi % 12) + 12) % 12] + (midi / 12 - 1).ToString(Inv);
        }

        static string FormatMs(float ms)
            => ms < 100f ? ms.ToString("0.0", Inv) + " ms"
                         : ((int)MathF.Round(ms)).ToString(Inv) + " ms";
        static string FormatHz(float hz)
            => hz < 1000f ? ((int)MathF.Round(hz)).ToString(Inv) + " Hz"
                          : (hz / 1000f).ToString("0.0", Inv) + " kHz";

        static string CharLabel(FxType t, int v)
        {
            float p = v / 127f;
            switch (t)
            {
                case FxType.Bitcrush: return "Crush-Rate " + Pct(v);
                case FxType.Drive:    return "Bias " + ((p - 0.5f) * 0.8f).ToString("+0.00;-0.00;0.00", Inv);
                case FxType.Lowpass:
                case FxType.Highpass: return FormatHz(150f * MathF.Pow(120f, p));
                case FxType.RingMod:  return "Tune " + ((p - 0.5f) * 2f).ToString("+0.00;-0.00;0.00", Inv) + " oct";
                case FxType.Comb:     return "Damp " + Pct(v);
                case FxType.Stutter:  return "Repeats " + (2 + (int)MathF.Round(p * 6f)).ToString(Inv);
                case FxType.Delay:    return "Feedback " + ((int)MathF.Round(p * 95f)).ToString(Inv) + "%";
                case FxType.Reverb:   return "Damping " + Pct(v);
                case FxType.Gate:     return "Duty " + ((int)MathF.Round((0.05f + p * 0.9f) * 100f)).ToString(Inv) + "%";
                case FxType.Transient: return v < 60 ? "Attack " + Pct(128 - v) : (v > 68 ? "Sustain " + Pct(v) : "Neutral");
                case FxType.Wavefolder: return "Fold " + Pct(v);
                case FxType.Phaser:   return "Sweep " + Pct(v);
                case FxType.SubOctave: return "Tone " + Pct(v);
                case FxType.Formant:  return "Vowel " + "AEIOU"[(int)MathF.Round(p * 4f)].ToString();
                default:              return "-";
            }
        }

        static string ModeLabel(FxType t, bool on)
        {
            switch (t)
            {
                case FxType.Bitcrush: return on ? "Anti-alias"   : "Raw";
                case FxType.Drive:    return on ? "Hard clip"    : "Soft";
                case FxType.Lowpass:
                case FxType.Highpass: return on ? "High Q"       : "Low Q";
                case FxType.RingMod:  return on ? "AM"           : "Ring mod";
                case FxType.Comb:     return on ? "Neg feedback" : "Pos feedback";
                case FxType.Stutter:  return on ? "Reverse"      : "Forward";
                case FxType.Delay:    return on ? "Ping-pong"    : "Mono";
                case FxType.Reverb:   return on ? "Bright"       : "Normal";
                case FxType.Gate:     return on ? "Triplet"      : "Straight";
                case FxType.Resonator: return on ? "Long decay"  : "Short decay";
                case FxType.Transient: return on ? "Slow"        : "Fast";
                case FxType.Wavefolder: return on ? "Asymmetric" : "Symmetric";
                case FxType.Phaser:    return on ? "8 stages"    : "4 stages";
                case FxType.SubOctave: return on ? "-2 oct"      : "-1 oct";
                case FxType.Formant:   return on ? "Bright"      : "Dark";
                default:              return "-";
            }
        }
    }
}
