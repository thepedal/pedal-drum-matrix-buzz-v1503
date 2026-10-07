# Pedal Drum Matrix (Buzz 1503)

Port of [pedal-drum-matrix](https://github.com/thepedal/pedal-drum-matrix) to
**Jeskola Buzz 1503, 32-bit**. Same DSP, parameters and presets as the ReBuzz
version (v1.4.0); only the host boundary changed. See *Buzz 1503 port* below.

**Versioning.** This repo's releases are numbered independently: release
**v1.0** is the port of ReBuzz v1.3.5, **v1.1** the port of ReBuzz v1.3.6,
**v1.2** adds the readout panel (below) to the v1.3.6 code, **v1.3** is the
port of ReBuzz v1.3.8 (reworked Delay, new Resampler), and **v1.4** the port of
ReBuzz v1.4.0 (Chorus, Freeze, AutoWah, Exciter), both with the panel. The
About box and assembly version show the ReBuzz version the code tracks
(currently 1.4.0).

**Readout panel (1503 only).** Buzz 1503's parameter window shows the sliders
as raw 0-127 numbers: the per-effect labels ReBuzz shows come from
`DescribeValue`, which Buzz's managed-machine interface only gained after
build 1503. So the port embeds a small display-only panel at the top of the
parameter window. One row per slot shows the effect and its Amount, Char,
Mode, Env and LFO depth with the same labels as ReBuzz (the Resonator's Char
as a note name, Delay's as feedback, and so on); three rows below show the
feedback loop (Feedback, Time, Tone, Env to Fb), the LFO and tuning (LFO, Key,
Scale) and the rest (Release, Morph, Limiter, Auto gain). Editing still happens
on Buzz's sliders, or by MIDI; the panel follows either within a tenth of a
second, including a slot's Type change. It shows parameter values, not the
values the LFO and envelope modulate them to. Columns are sized for the
effects currently loaded and every setting is spaced for its widest possible
value, so nothing moves while you adjust a slider; only changing an effect can
resize the columns. In narrow windows the LFO and Env columns drop out first.

A single stereo-in / stereo-out drum-fx **rack**: six serial slots,
each one a swappable effect. Aimed at serious textural transformation of drum
sounds.

## Buzz 1503 port

**Build.** SDK-style project targeting `net48` / `x86` (Buzz 1503 hosts
managed machines on .NET Framework; a .NET 10 assembly will not load). Build
from an elevated prompt with Buzz closed:

```
dotnet build -c Release
```

Buzz is assumed at `C:\Program Files (x86)\Jeskola\Buzz`; override with
`-p:BuzzDir="D:\path\to\Buzz"`. The build references `BuzzGUI.Interfaces.dll`
and `BuzzGUI.Common.dll` from there (not copied) and deploys two files to
`Gear\Effects`:

- `Pedal Drum Matrix.NET.dll`
- `Pedal Drum Matrix.NET.prs.xml` (the 30-preset bundle; Buzz 1503 pairs a
  bundle with the DLL by base name)

No `.pdb` or `.deps.json` is produced. Look for the `Deploying ...` line in the
build output; the copy is non-fatal if Buzz has the DLL locked.

**What differs from the ReBuzz version.** Since ReBuzz v1.3.4 the source is
shared almost entirely: the ReBuzz repo adopted the `MathF` shim, the explicit
`SetMusicalContext`, the `WM_READ` handling, the Store latch and the ASCII
strings. What remains specific to this repo is the csproj (`net48` / `x86`,
Buzz paths), the About box (title, repo URL, GPL-3.0) and `ReadoutGui.cs`.

ReBuzz v1.4.0 added its own `ReadoutGui.cs`, modelled on this one but tuned
for ReBuzz: a fixed 560 px width (ReBuzz sizes the parameter window to its
GUI) and hard-coded dark colours (ReBuzz's theme). Buzz 1503 clips the window
instead (about 440 px wide at 125% scaling) and uses the light system colours,
so this repo keeps its own `ReadoutGui.cs` in place of the ReBuzz one. Both
panels get their labels from the shared `DescribeValue`.

**Original port changes (v1.3.3), for reference:**

- csproj: `net48` / `x86`, explicit WPF framework references, Buzz install
  paths, preset bundle renamed to the DLL's base name.
- `MathF.cs`: a small shim, since `System.MathF` does not exist on .NET
  Framework. All DSP call sites are unchanged.
- `IDrumFx.SetMusicalContext` is a plain interface member (no default body,
  which .NET Framework does not support); each effect implements it, a no-op
  except in the Resonator.
- `Work()` checks the `WM_READ` flag. In Buzz 1503 a muted upstream machine
  calls `Work()` without READ while `input` still holds the last block; the
  rack now treats that as silence (tails ring out, then it sleeps) instead of
  looping the stale block.
- The Store switch's state is latched on the first `Work()`, so loading a song
  saved with Store On no longer overwrites the restored morph target.
- Parameter descriptions and value readouts are plain ASCII, with no `/`, `<`,
  `>` or `&`; the six Slot Type params declare `MinValue = 0, MaxValue = 10`.

**First-load checklist in Buzz 1503:** appears under Effects; audio passes
unchanged with all slots None and Limiter off; muting the input silences the
output (after any tail); presets appear in the preset menu; About works;
hover readouts show labels (if not, Buzz 1503 does not call `DescribeValue`
and you see raw numbers); a stored morph target survives save and reload
(depends on `MachineState`, not yet confirmed in 1503); Delay, Gate, Stutter
and the LFO follow tempo (depends on `MasterInfo.SamplesPerTick`).

## How it's controlled

Per slot:
- **Slot N Type** — picks the effect (None / Bitcrush / Drive / Lowpass / Highpass / RingMod
  / Comb / Stutter / Delay / Reverb / Gate). Automatable and peer/MIDI mappable.
- **Slot N Amount** — overall intensity / wet (amount 0 = clean).
- **Slot N Char** — continuous 0–100 character control (effect-specific). Maps
  to a BCR2000 encoder's rotation.
- **Slot N Mode** — switch (effect-specific). Maps to the encoder's push.

The Char + Mode pair per slot is designed for the BCR2000's six dual-function
(rotary + push) encoders — one encoder per slot. Per-effect meanings:

| Effect | Char (rotary) | Mode (switch) |
|---|---|---|
| Bitcrush | bit-crush ↔ decimation tilt | anti-alias filter |
| Drive | bias / asymmetry | hard clip vs soft |
| Lowpass | cutoff | Low vs High Q |
| Highpass | cutoff | Low vs High Q |
| Transient | attack↔sustain | fast vs slow detector |
| Wavefolder | fold amount | symmetric vs asymmetric |
| Phaser | sweep position | 4 vs 8 stages |
| SubOctave | sub tone | -1 vs -2 octaves |
| Formant | vowel (A-E-I-O-U) | dark vs bright |
| Resampler | sample rate | full vs 8-bit |
| Chorus | rate (tempo-synced ticks) | chorus vs flanger |
| Freeze | grain size | one-shot vs continuous |
| AutoWah | sensitivity | up vs down sweep |
| Exciter | frequency | tube vs bright |
| RingMod | carrier fine tune (±1 oct) | ring-mod vs AM |
| Comb | feedback damping | +/− feedback sign |
| Stutter | repeats (2–8) | reverse slice |
| Delay | time (tempo-synced ticks) | Low vs High feedback |
| Reverb | damping (bright↔dark) | bright tilt |
| Gate | duty cycle | triplet timing |

Char/Mode were appended after the original Type/Amount params (Build §3.3), so
older test songs keep their values; they appear at the bottom of the rack.
Note the new defaults (Char 64, Mode off) re-voice some effects slightly versus
v0.3 — e.g. Filter resonance and Delay feedback are now their own controls.

Signal flow is serial: slot 1 → 2 → 3 → 4 → 5 → 6 → out.

## Architecture

- Each `Slot` pre-builds one instance of **every** effect type, so changing a
  slot's type at runtime is an index change, never an audio-thread allocation
  (no GC). Swaps run a 10 ms equal-power crossfade → click-free.
- `IDrumFx` exposes `IsRinging`; the machine ORs it across all slots so a Reverb
  or Delay in any slot keeps its tail after the drums stop (tail-aware sleep:
  input without the READ flag is treated as silence).
- Amount is smoothed per sample (Core §32).
- The `FxType` enum order is the preset contract (Build §3.3): **append only**;
  never reorder. The `ValueDescriptions` literal on each Slot Type param must
  match it.

## Status

v1.3.3 — signature modulation set (feedback, envelope, LFO, tuned resonator, scene morph) plus a right-click About window.
Bitcrush, Drive, Filter, RingMod, Comb, Stutter, Delay, Reverb, Gate. Each of
the six slots selects independently, so any slot can hold any effect (and the
same effect can sit in more than one slot — each slot owns its own instances).

Effect notes:
- Bitcrush — decimation + bit reduction. Tail-free.
- Drive — tanh saturation with makeup gain. Tail-free.
- Lowpass / Highpass — TPT state-variable, separate effect types; amount blends
  dry → filtered (how much filter is applied), char sets cutoff (150 Hz → 18 kHz),
  mode selects one of two Q values (gentle Butterworth vs resonant).
- RingMod — sine carrier 30 Hz → 3 kHz, wet scales with amount. Tail-free.
- Comb — short feedback resonator (metallic); amount raises pitch + feedback. Rings.
- Delay — tempo-synced; Char sets the time in ticks {1,2,3,4,6,8,12,16,24,32}
  (from `host.MasterInfo.SamplesPerTick`, up to 4 s), Mode sets Low or High
  feedback (0.25 / 0.75), Amount is the mix. No ping-pong. Rings.
- Resampler — sample-and-hold decimator; Char lowers the effective sample rate
  (full down to 1/100), Mode crossfades to 8-bit, Amount is the mix. Tail-free.
- Gate — tempo-synced rhythmic gate, 8→1 ticks/cycle, 50% duty. Tail-free.
- Stutter — tempo-synced beat-repeat: latches a slice and loops it 4× before
  grabbing a new one; amount shortens the slice + raises wet. Rings, then
  self-silences once it latches silence.
- Reverb — Freeverb-style (8 lowpass-comb + 4 allpass per channel); amount
  sets tail length + wet. Rings.

Comb and Delay report `IsRinging` via a decaying-energy tracker so their tails
survive the input going silent and the rack sleeps only once everything is quiet.

## DSP to lift from existing repos

Drive → pedal-shaper · Filter → pedal-zplane · Reverb → pedal-converb ·
Comb/movement → pedal-chorus · bipolar-param offset → pedal-comp §2.

## Files

- `PedalDrumMatrix.NET.csproj` — net48 / x86 build, deploy to Buzz's `Gear\Effects`.
- `PedalDrumMatrix.cs` — machine: 49 params, serial Work, modulation, morph, MachineState.
- `Slot.cs` — slot host, swap crossfade, amount smoothing, tail flag.
- `DrumFx.cs` — `IDrumFx`, `FxType`, factory, all effects, limiter, auto gain, feedback, LFO.
- `MathF.cs` — `MathF` shim for .NET Framework 4.8.
- `ReadoutGui.cs` — Buzz 1503 only: the embedded readout panel.
- `Pedal Drum Matrix.NET.prs.xml` — 30-preset bundle.
- `LICENSE` — GNU General Public License v3.0.

## Next steps

1. Build against local Buzz 1503; run the first-load checklist above.
2. Fill effects in order of value for e-drums: Drive, Filter, RingMod, then the
   time-based ones (Delay, Reverb) which exercise the tail logic.
3. Consider a GUI later to expose per-effect detail params + visual rack.


## Preset bank (30 presets)

Ships as `Pedal Drum Matrix.NET.prs.xml` alongside the DLL in `Gear\Effects`;
Buzz 1503 loads it automatically (the machine's preset menu). Covers single-effect
showcases (each of the ten effects), tuned-resonator melodics (Pentatonic Arp,
Random Melody, Chord Stack, tuned bells/marimba), modulation patches (Vowel
Wobble, Siren Ring, Snap Comb, Swell Echo), feedback patches (Dub Chamber,
Resonant Drone, Howl Wash) and full signature racks (Industrial Kit, Melodic
Machine, Evolving Texture). Presets set parameters only; the morph target is a
separate live layer on top.

## Scene morph (v1.3)

A Morph knob blends the whole machine from the live sound toward a stored
target snapshot — the performance capstone.

- **Scene A is always the live parameters**, so the knobs and presets always
  drive the sound. There is no separate A to store.
- **Store** — captures the current settings as the morph target on an Off→On
  toggle.
- **Morph** — blends live→target. Continuous params (amounts, chars, depths,
  feedback, tone, release) interpolate; discrete params (slot Type, Mode, LFO
  wave/rate, Key, Scale) switch at the midpoint, smoothed by the existing
  click-free Type and Mode crossfades.
- At Morph 0 you hear the live/preset sound exactly; loading a preset changes
  the sound at any Morph position (it updates the live A endpoint). The target
  persists with the song via MachineState, keyed by param name so future param
  additions never corrupt saved songs.

Map Morph to a BCR encoder to sweep the whole texture from your current sound
toward a stored extreme with one hand.

## Tuned resonator (v1.3)

A new **Resonator** slot type: a tuned comb resonator (Karplus-style) excited by
the input, so percussive hits ring at musical pitches — drums become melodic.

- Two global params, **Key** (root) and **Scale** (Major, Minor, Maj/Min Pent,
  Dorian, Phrygian, Blues, Whole Tone), set the tuning.
- A resonator slot's **Char** selects a degree of that scale (snapped to pitch
  across 3 octaves); the value readout shows the actual note (e.g. E3). **Mode**
  sets short (mallet) vs long (bell) decay. **Amount** is wet mix.
- Because it's a normal slot, the LFO and envelope route to its Char like any
  other — so the **LFO can sequence its pitch through the scale** (arpeggios /
  melodies locked to tempo) and the **envelope can punch notes on hits**. Stack
  several resonator slots for chords.

An in-loop `tanh` keeps it self-limiting; pitch verified accurate to the scale.

## Tempo-synced LFO (v1.3)

A tempo-locked LFO as a second modulation source, routed into the slots like the
envelope. Params (all mappable):

- **LFO rate** — cycle length in ticks (1 → 64), so it tracks song tempo.
- **LFO wave** — Sine, Triangle, Saw, Square, Steps (fixed 8-step pattern), or
  Random (sample-and-hold, new value each cycle).
- **Slot N LFO depth** — bipolar (64 = none); routes the LFO to that slot's Char.

Each slot's Char modulation is the sum of envelope and LFO contributions
(env·envDepth + lfo·lfoDepth), computed in the machine and applied on top of the
smoothed Char. The slot itself is source-agnostic — it just receives one Char
offset — so further mod sources can be added without touching it. Output is
bipolar -1..+1; Steps and Random give sequencer-like rhythmic motion.

## Envelope / transient follower (v1.3)

An envelope follower tracks the dry input — the actual drum hits, before effects
and feedback — and uses it as a modulation source so the rack responds to each
hit. Params (all mappable):

- **Envelope release** — how long each hit's modulation lasts (20 ms → 2 s).
  Short = snappy per-hit blips; long = sustained swells.
- **Slot N env depth** — bipolar (64 = none); routes the envelope to that slot's
  Char. So a hit can open a Filter, jump a RingMod's pitch, swell a Reverb's
  damping, etc. Negative depth inverts (a hit closes instead of opens).
- **Envelope → feedback** — routes the envelope to the feedback amount, so hits
  bloom the feedback loop and settle as the envelope decays.

Detector: instant attack, release per the param; output scaled so typical drums
reach strong modulation, clamped to 0..1. Per-slot Char modulation is applied on
top of the (smoothed) Char setting and re-clamped to range.

## Global feedback loop (v1.3)

A portion of the rack output is delayed, tone-shaped and softly saturated, then
fed back into the rack input — so drum transients excite the whole rack and it
rings, drones and evolves on its own. Three global params (all mappable):

- **Feedback** — amount returned (0 = off). Scaled so the full knob tops out
  around 0.2; musical regeneration across the range without runaway.
- **Feedback time** — loop length, 1 ms → 500 ms. Short = resonant / pitched
  (comb-like); long = rhythmic regeneration.
- **Feedback tone** — lowpass cutoff in the loop, 200 Hz → 12 kHz (dark → bright).

Stability: an in-loop `tanh` makes it self-limiting (it sings rather than runs
away) and a DC blocker keeps it from drifting; the output limiter is the final
net. The loop is tapped post-slots / pre-limiter, so it carries the full
character of whatever effects are in the rack, and injected ahead of slot 1.
Its energy is included in the tail/sleep check, so a sustained drone keeps the
machine awake (pull Feedback down to let it decay).

## Value labelling

Hovering a control shows its real current function via `DescribeValue`, read
live from the slot's Type. A slot's **Char** reads e.g. `Q 2.0` under Filter,
`8 ticks` under Delay, `Repeats 5` under Stutter; its **Mode** reads
`Low Q`/`High Q`, `Low fb`/`High fb`, etc. Amount reads as a percentage.
The static control names stay generic (the per-knob labels are a GUI job); the
value readout carries the meaning.

## Sample scale

Buzz audio is ±32768, not ±1.0. The machine
normalises to ±1.0 at the input, runs all DSP there, and denormalises (×32768)
at the output. Without this the output limiter crushed full-scale drums to
near-silence, Drive saturated everything to ±1, and Bitcrush's bit-reduction
did almost nothing. Linear/ratio effects (Filter, Delay, Comb, Reverb, RingMod,
Gate, Stutter) were unaffected.

## Output stage & CPU hygiene

- **Output limiter** (global `Limiter`, On by default): zero-latency
  stereo-linked peak limiter (1 ms attack / 100 ms release) into a cubic
  soft-clip ceiling at ~0.95. Catches level swings from Drive, resonant
  Filter, and the feedback effects without a separate machine/connection.
- **Denormal flush-to-zero** on all feedback states (Comb, Delay, Reverb).
  Decaying tails otherwise drift into subnormal floats, which spike CPU on
  some hardware and cause dropouts. `Dsp.Ftz` keeps them normal.
- Building both in-machine (vs. external comp/limiter machines) avoids the
  per-machine + per-connection host overhead (in Buzz 1503, each managed
  machine also pays a fixed per-call cost for the .NET boundary).

## Licence

GNU General Public License v3.0. See `LICENSE`.
