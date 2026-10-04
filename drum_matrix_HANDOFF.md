# Pedal Drum Matrix (Buzz 1503) — Handoff (v1.3.5)

A drum-centric multi-effect machine, ported from ReBuzz to Jeskola Buzz 1503
(32-bit), tailored to a Behringer BCR2000
(6 dual-function rotary+push encoders). It replaces a former rig of ~15 machines
(6 effects through a patcher into 6 more plus mixers) with one consolidated
serial rack, plus a modulation layer that makes it respond to the drums and
resonate on its own. Author tag: thepedal.

This doc is the cold-start reference. It reflects the machine as shipped at
v1.3. Where it says `Core` / `Build` / `PedalComp` etc. it means the project
ReBuzz_ManagedMachine_Notes_*.md files (the ReBuzz original's references,
kept for traceability). For this port, `Buzz1503_ManagedMachine_Notes.md` is
authoritative on host behaviour; see §0.

---

## 0. Buzz 1503 port (separate repo: pedal-drum-matrix-buzz-1503)

Same DSP, parameter layout (49, unchanged order) and preset contents as the
ReBuzz v1.3.5.

**Port deltas against ReBuzz v1.3.5** (everything else is byte-identical to
the ReBuzz source, which adopted the v1.3.3 port's host-boundary fixes):

- `PedalDrumMatrix.NET.csproj` (net48 / x86, `BuzzDir`, Buzz paths).
- About box: "(Buzz 1503)" in the title, this repo's URL, GPL-3.0.

(v1.3.4 also carried a "12/24 dB per oct" Mode readout to avoid a `/`; v1.3.5's
"Low Q" / "High Q" readouts need no change, so that delta is gone.)

When porting a future ReBuzz release: diff it against the previous ReBuzz
release, take `DrumFx.cs`, `Slot.cs`, `MathF.cs` and the `.NET` preset bundle
as-is, take `PedalDrumMatrix.cs` and reapply the deltas above, then re-sweep
strings for non-ASCII and `/ < > &`.

**Original v1.3.3 port changes**, all at the host boundary:

- **Target** `net48` / `x86` (1503 notes 1.1). `MathF.cs` shims `System.MathF`,
  which .NET Framework lacks (1503 notes 1.3); DSP call sites untouched.
- **`IDrumFx.SetMusicalContext`** has no default body (.NET Framework has no
  default interface implementations); every effect implements it.
- **READ flag** (1503 notes 2.1): `hasInput = (mode & WM_READ) != 0 && input
  != null`. Without input the rack is fed zeros, renders tails, and returns
  false once nothing rings. READ without WRITE returns false.
- **Store latch** (1503 notes 3.3): `_prevStore` is set from Store on the first
  `Work()` (before the early returns), so a song saved with Store On does not
  re-capture over the restored MachineState target.
- **Strings** (1503 notes 3.2): descriptions and readouts ASCII-only, no `/ <
  > &`. Slot Type params declare MinValue 0 / MaxValue 10.
- **Preset bundle** renamed `Pedal Drum Matrix.NET.prs.xml` (1503 notes 3.4).
- **Licence** GPL-3.0 (1503 notes 6): `LICENSE` in the repo root, stated in the
  README and the About box. The ReBuzz original's About box says MIT.

Validation done in the dev container (v1.3.3; re-run for each release. v1.3.5
filter checks: LP at Char 30 (~465 Hz) passes 60 Hz at 0 dB and cuts 8 kHz by
51 dB; HP at Char 69 (~2 kHz) cuts 60 Hz by 61 dB; at cutoff Low Q reads
-3.0 dB and High Q +15.6 dB, i.e. Q 0.707 and 6; Amount 64 gives about -6 dB
in the stopband, confirming the dry/wet mix): compiled with Mono `mcs` against the
.NET Framework 4.8 reference assemblies and stub Buzz interfaces (C# 8 syntax
downlevelled in a throwaway copy, since mcs is C# 7); headless Mono harness
confirmed bit-exact pass-through (all None, Limiter off), muted-input silence,
reverb tail then sleep with a stale input buffer, delay tail under WM_NOIO,
READ-without-WRITE, MachineState round trip, and the Store latch. Presets
cross-checked against the declarations (names, indices, ranges).

Untested in Buzz 1503 itself: MachineState persistence, whether
`DescribeValue` is called, `MasterInfo.SamplesPerTick`, label rendering,
CPU on heavy chains.

Pre-existing behaviour found by the harness, deliberately left unchanged in the
port (same in the ReBuzz original):

- The limiter's soft clip `x - x^3/6.75` acts over its whole range, so with
  Limiter On even quiet signals are slightly bent (about 0.9% at -12 dBFS).
- Delay's tail tracker only sees echoes that have already emerged. If the
  input goes silent within one delay time (~6 ticks) of the last hit, the rack
  can sleep with an echo still in the buffer, and that echo is lost.

---

## 1. File manifest

All under the project folder; deployed file is the DLL plus the preset bank.

- `PedalDrumMatrix.cs` — machine class `PedalDrumMatrixMachine`, all 49
  params, `Work()`, the modulation/morph engine, `DescribeValue`, MachineState.
- `Slot.cs` — `Slot`: owns one pre-built instance of every effect type,
  click-free crossfade on Type change, smooths Amount/Char/Mode, applies a
  precomputed Char-modulation offset, forwards Key/Scale to the active effect.
- `DrumFx.cs` — `IDrumFx` interface, `FxType` enum, `FxFactory`, the `Tail`
  tracker, `Dsp.Ftz`, `OutputLimiter`, `AutoGain`, `GlobalFeedback`, `Lfo`, and
  all effect classes including `ResonatorFx`.
- `PedalDrumMatrix.NET.csproj` — build + deploy (see §3).
- `Pedal Drum Matrix.NET.prs.xml` — 30-preset bank (auto-loads; see §13).
- `MathF.cs` — `MathF` shim for .NET Framework 4.8.
- `LICENSE` — GNU General Public License v3.0.
- `README.md` — user-facing feature notes.
- `drum_matrix_HANDOFF.md` — this file.

Approx line counts: DrumFx ~647, PedalDrumMatrix ~503, Slot ~89.

---

## 2. What to read first if resuming work

1. This doc, sections 4 (signal flow) and 5 (DSP conventions) — the load-bearing
   invariants.
2. Section 14 (do-not-regress gotchas).
3. `Buzz1503_ManagedMachine_Notes.md` for host behaviour; for the original
   ReBuzz references, the relevant project note file (MachineState →
   Core 39; presets → Build 3 and the Presets addendum; perf → PedalProfiler2).

---

## 3. Build and deploy

- **Target `net48`, `x86`** (Buzz 1503 hosts managed machines on .NET Framework;
  .NET 10 will not load). `LangVersion latest` keeps modern C# syntax.
- **Mandatory deployment properties:** `DebugType=none`, `DebugSymbols=false`,
  `GenerateDependencyFile=false` (Buzz needs only the DLL). `NoWarn=MSB3277`.
- **AssemblyName `Pedal Drum Matrix.NET`.**
- **`Microsoft.NETFramework.ReferenceAssemblies`** (build-time only) so the SDK
  builds net48 without the Developer Pack.
- **References:** WPF framework assemblies (MessageBox); `BuzzGUI.Interfaces.dll`
  and `BuzzGUI.Common.dll` from `$(BuzzDir)` with `Private=false`. `BuzzDir`
  defaults to `C:\Program Files (x86)\Jeskola\Buzz`.
- **Post-build deploy** copies the DLL and `$(AssemblyName).prs.xml` to
  `$(BuzzDir)\Gear\Effects\` (ContinueOnError true; build elevated, Buzz closed).
- **No .NET Core-only APIs** (`MathF` comes from the shim, no `Span`,
  `Array.Fill`, etc.); keep it a single DLL.

---

## 4. Architecture and signal flow

One stereo EffectBlock: `bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)`.

Per sample, in order:

1. Read dry input (or `output` when input is null).
2. **Envelope follower** tracks the dry input (instant attack, param release).
3. **Feedback tap** — the delayed/tone-shaped/saturated loop signal (plus an
   envelope boost) is added to the input.
4. **Six serial slots**, slot 0 to slot 5. Each slot gets a precomputed Char
   modulation offset = `env*envDepth[s] + lfo*lfoDepth[s]`.
5. **Feedback write** — the post-slot rack output is written into the feedback
   loop (so the loop carries the full character of the rack).
6. **AutoGain** (if on), then **Limiter** (if on).
7. Denormalise to the output buffer.

Parameters are pushed once per block via `PushParamsToSlots()`, which reads
**morph-effective** values (see §11), not the live properties directly.

No input (mode without WM_READ, or null input; §0): the machine keeps rendering tails and sleeps (returns
false) only when nothing is ringing — `AnyTailRinging()` OR `_feedback.IsRinging`.

---

## 5. DSP conventions and the load-bearing invariants

- **Buzz audio is the +/-32768 domain, not +/-1.0** (1503 notes 2.3).
  The machine normalises at input (multiply by 1/32768) and denormalises at
  output (multiply by 32768). All DSP runs in the normalised +/-1 section, so
  any absolute-threshold maths (tanh drive, soft-clip ceilings, denormal flush)
  is correct.
- **Denormal flush** (`Dsp.Ftz`) is applied to every recirculating feedback
  state (Comb, Delay, Reverb, Resonator, GlobalFeedback) to avoid the subnormal
  CPU stall.
- **ValueDescriptions must be an inline array literal** in the attribute (no
  static field reference) or the host will not read them.
- **Parameter declaration order is the preset/song contract** (Build 3.3) —
  append only, never reorder or insert. New params go at the end.
- **No audio-thread allocation.** Every effect instance is pre-built in the Slot
  constructor; effect buffers are allocated in `Prepare`. Nothing in the
  per-sample path allocates.

---

## 6. Effect palette

`FxType` enum (index = preset contract, append only):
`None=0, Bitcrush, Drive, Lowpass, RingMod, Comb, Stutter, Delay, Reverb, Gate, Resonator(=10), Highpass(=11)`.
(Lowpass keeps value 3 — the former `Filter` — so old lowpass-mode instances are
bit-identical; Highpass is appended at 11.)

Per effect, the slot macros are Amount (wet/intensity, 0 = clean pass-through),
Char (the rotary character), and Mode (the switch). Meanings:

- **Bitcrush** — Char: bits-vs-rate tilt. Mode: raw -> anti-alias filter.
- **Drive** — Char: bias/asymmetry. Mode: soft (tanh) -> hard clip.
- **Lowpass / Highpass** — two effect types sharing one 12 dB per oct TPT
  state-variable filter (LP/HP fixed at construction). Amount blends dry ->
  filtered (how much filter is applied); Char sets cutoff (150 Hz -> 18 kHz);
  Mode selects one of two Q values (0.707 gentle / 6.0 resonant), crossfaded.
- **RingMod** — Char: carrier fine tune (+/-1 oct). Mode: ring mod -> AM.
  Amount sets carrier 30 Hz to 3 kHz.
- **Comb** — Char: feedback damping. Mode: +feedback -> -feedback (passes
  through zero at the midpoint, which is what makes the flip click-free).
- **Stutter** — Char: repeats (2 to 8). Mode: forward -> reverse slice.
  Beat-repeat latched ~1 tick.
- **Delay** — Char: feedback. Mode: mono -> ping-pong. Amount = mix, ~6 ticks.
- **Reverb** — Char: damping. Mode: normal -> bright tilt. Freeverb 8 comb + 4
  allpass.
- **Gate** — Char: duty cycle. Mode: straight -> triplet timing. Tempo-synced.
- **Resonator** — Char: pitch (scale degree, snapped to Key/Scale). Mode: short
  (mallet) -> long (bell) decay. See §10.

**Mode is click-free (v1.2).** Mode is not a hard boolean inside the DSP. The
slot smooths it to a 0..1 value over ~20 ms, and each effect crossfades between
its two modes across that value (Filter blends LP/HP from the same SVF state,
Drive blends soft/hard, Comb ramps the feedback coefficient through zero, etc.).
The displayed parameter is still a clean Off/On toggle.

---

## 7. Output stage

- **OutputLimiter** — zero-latency stereo-linked peak follower (1 ms attack,
  100 ms release) plus a cubic soft-clip ceiling at 0.95. On by default.
- **AutoGain** (default Off) — peak-targeting leveler (~-6 dBFS). Dual detector:
  a 30 ms fast envelope gates adaptation (freezes on gaps/tails so silence is
  never boosted), a 300 ms slow peak sets the level. Rise 700 ms, fall 120 ms,
  boost cap +18 dB. Targets peak not RMS so drum transients survive.

---

## 8. Modulation system

Two mod sources route into each slot's Char. The slot is **source-agnostic**:
the machine computes `charMod[s] = env*envDepth[s] + lfo*lfoDepth[s]` per sample
and passes a single offset; the slot adds it to its smoothed Char and clamps.
This is why more sources can be added later without touching slot code.

**Envelope follower.** Tracks the dry input (instant attack; release set by
`EnvRelease`, 20 ms to 2 s). Output scaled by `EnvSense=2.5` and clamped to
0..1 so typical drums reach strong modulation. Routes to per-slot Char via the
bipolar `Slot{N}EnvDepth` (64 = none) and to the feedback amount via `EnvToFb`
(up to +0.3 added feedback on a hit).

**Tempo-synced LFO.** `LfoRate` is a cycle length in ticks (index into
`{1,2,3,4,6,8,12,16,24,32,48,64}`), so it tracks tempo. `LfoWave`: Sine,
Triangle, Saw, Square, Steps (fixed 8-step pattern), Random (sample-and-hold,
new value each cycle). Output bipolar -1..+1. Routes to per-slot Char via the
bipolar `Slot{N}LfoDepth`.

Because Char means different things per effect, the same modulation does
different things: on a Resonator it moves pitch (melodies), on a Filter it moves
resonance, on a RingMod it moves the carrier, on a Delay it moves feedback.

---

## 9. Global feedback loop

A portion of the rack output is delayed, tone-shaped and softly saturated, then
fed back into slot 0's input — so drum transients excite the whole rack and it
rings and evolves on its own. Tapped post-slots / pre-limiter (carries the full
rack character), injected pre-slot 0.

- `Feedback` — amount, scaled so the full knob is about 0.2 (a deliberate cap;
  the original 1.0 range was too hot and took over the signal).
- `FbTime` — loop length, 1 ms to 500 ms (short = resonant/pitched, long =
  rhythmic regeneration).
- `FbTone` — loop lowpass cutoff, 200 Hz to 12 kHz.

Stability: an in-loop tanh makes it self-limiting (it sings rather than runs
away); a DC blocker prevents drift; the output limiter is the final net. The
loop energy is included in the sleep check so a sustained tail keeps the machine
awake.

---

## 10. Tuned resonator

`ResonatorFx` is a Karplus-style tuned comb (fractional delay + damped feedback,
self-limited by an in-loop tanh) excited by the input, so percussive hits ring
at musical pitches. It is a normal slot effect, so the LFO and envelope route to
its Char (pitch) — the LFO can sequence melodies in key, the envelope can punch
notes on hits, and several resonator slots stack into chords.

Two global params set the tuning, read by any resonator slot:

- `Key` — root, 0..11 (C..B).
- `Scale` — Major, Minor, Maj Pent, Min Pent, Dorian, Phrygian, Blues, Whole
  Tone.

The slot Char selects a degree across 3 octaves, snapped to the scale; base note
is C2 + key. `ResonatorFx.CharToFreq(key, scale, p1)` is the public static used
both by the DSP and by `DescribeValue` to show the note name. Pitch verified
accurate to within ~0.3 percent.

The machine forwards Key/Scale to the active effect through
`Slot.SetParams(..., key, scale)` -> `IDrumFx.SetMusicalContext(key, scale)`, a
interface member that is an empty method in every effect except `ResonatorFx`
(no default interface body on .NET Framework).

---

## 11. Scene morph (live-anchored)

One `Morph` knob blends the whole machine from the live sound toward a stored
target snapshot.

- **Scene A is always the live parameters** — so knobs and presets always drive
  the sound. There is no separate A to store.
- `Store` — snapshots the current settings as the morph target on an Off->On
  edge (latching switch; toggle Off then On to re-capture).
- `Morph` — blends live -> target. Continuous params interpolate; discrete
  params (Type, Mode, LfoWave, LfoRate, Key, Scale) switch at the midpoint,
  smoothed by the click-free Type/Mode crossfades. At Morph 0 you hear the
  live/preset sound exactly; loading a preset changes the sound at any Morph
  position because it updates the live A endpoint.

Implementation: `EnsureReflect()` reflects all int `[ParameterDecl]` properties
except Morph and Store into a name-sorted array (47 scene params), marking the
discrete ones. Each block `ComputeEffective(mf)` reads the live values, applies
a Store rising-edge snapshot, and fills `_eff[]` (live as A, stored-or-live as
B). `PushParamsToSlots` reads everything through `E(name) = _eff[idx[name]]`.

**Why this design:** an earlier two-fixed-scene A/B model had a bug — once a
scene was stored the engine read only the frozen snapshots and ignored live
params, so presets changed the visible values but not the sound, with no clean
way to disengage. The live-anchored model fixes it permanently.

**Persistence:** the target is saved with the song via a framed `byte[]
MachineState` (magic `PDRM`, version 2), written **name-keyed** (param name +
value) so future param additions never corrupt saved songs. Older-version state
is ignored (start fresh). MachineState setter runs before the GUI on load.

---

## 12. Full parameter table

49 params. Declaration order is the contract; the index column below is 0-based
(as written in the preset XML). The host UI may show 1-based positions.

```
idx  name            range     def   meaning
 0   Slot1Type       enum*     0     effect in slot 1 (None..Resonator)
 1   Slot1Amount     0..127    0     slot 1 wet/intensity
 2   Slot2Type       enum*     0
 3   Slot2Amount     0..127    0
 4   Slot3Type       enum*     0
 5   Slot3Amount     0..127    0
 6   Slot4Type       enum*     0
 7   Slot4Amount     0..127    0
 8   Slot5Type       enum*     0
 9   Slot5Amount     0..127    0
10   Slot6Type       enum*     0
11   Slot6Amount     0..127    0
12   Slot1Char       0..127    64    slot 1 character rotary
13   Slot1Mode       0..1      0     slot 1 mode switch
14   Slot2Char       0..127    64
15   Slot2Mode       0..1      0
16   Slot3Char       0..127    64
17   Slot3Mode       0..1      0
18   Slot4Char       0..127    64
19   Slot4Mode       0..1      0
20   Slot5Char       0..127    64
21   Slot5Mode       0..1      0
22   Slot6Char       0..127    64
23   Slot6Mode       0..1      0
24   Limiter         0..1      1     output limiter on/off
25   AutoGainOn      0..1      0     auto-gain leveler on/off
26   Feedback        0..127    0     global feedback amount (full knob ~0.2)
27   FbTime          0..127    64    feedback loop time 1..500 ms
28   FbTone          0..127    64    feedback lowpass 200 Hz..12 kHz
29   EnvRelease      0..127    64    envelope release 20 ms..2 s
30   EnvToFb         0..127    0     envelope -> feedback amount
31   Slot1EnvDepth   0..127    64    env -> slot1 Char (bipolar, 64=none)
32   Slot2EnvDepth   0..127    64
33   Slot3EnvDepth   0..127    64
34   Slot4EnvDepth   0..127    64
35   Slot5EnvDepth   0..127    64
36   Slot6EnvDepth   0..127    64
37   LfoRate         0..11     7     cycle in ticks {1,2,3,4,6,8,12,16,24,32,48,64}
38   LfoWave         0..5      0     Sine/Triangle/Saw/Square/Steps/Random
39   Slot1LfoDepth   0..127    64    LFO -> slot1 Char (bipolar, 64=none)
40   Slot2LfoDepth   0..127    64
41   Slot3LfoDepth   0..127    64
42   Slot4LfoDepth   0..127    64
43   Slot5LfoDepth   0..127    64
44   Slot6LfoDepth   0..127    64
45   Key             0..11     0     resonator root C..B
46   Scale           0..7      0     resonator scale (Major..Whole Tone)
47   Morph           0..127    0     blend live -> stored target
48   Store           0..1      0     snapshot target on Off->On
```

`enum*` = Slot Type, 0..10 via inline ValueDescriptions
(None,Bitcrush,Drive,Filter,RingMod,Comb,Stutter,Delay,Reverb,Gate,Resonator).

`DescribeValue` shows real per-effect function on hover: Filter Char = `Q 2.0`,
Delay Char = `Feedback 47%`, Resonator Char = the note (e.g. `E3`), depths =
signed percent, FbTime/EnvRelease = ms, FbTone = Hz, Morph = percent, Mode = the
named state per effect (`Lowpass`/`Highpass`, etc.).

---

## 13. Preset bank

- Ships as `Pedal Drum Matrix.NET.prs.xml` next to the DLL in `Gear\Effects`. Because
  the filename equals the DLL base name plus `.prs.xml`, Buzz 1503 auto-loads it as
  the active preset set (right-click the machine). UTF-8 **with BOM**.
- Format: `PresetDictionary` -> `Item Key=name` -> `Preset Machine=Pedal Drum
  Matrix` -> `Parameters` with one `Parameter Name=.. Group=1 Index=.. Track=0
  Value=..` per global, in declaration order. Index (0-based) is what the host
  binds on, so the bank must be regenerated if the param order ever changes.
- 30 presets, each using **all six slots** (a full signal chain: saturation/
  lo-fi early, tone-shaping mid, delay/reverb tails; headline effect louder,
  glue effects lower). Covers every effect, the resonator melodics (Pentatonic
  Arp, Random Melody, Chord Stack, tuned bells/marimba), modulation patches,
  feedback patches, and full signature racks (Industrial Kit, Melodic Machine,
  Evolving Texture).
- Presets set parameters only; Morph sits at 0. The morph target is a separate
  live layer (not part of presets).
- Maintained by a generator (sparse override dicts + a name->index map keyed off
  the source declaration order). Regenerate after any param change; the generator
  is dev-only and is not deployed.

---

## 14. Do-not-regress gotchas

- Sample scale is +/-32768; normalise at the I/O boundary (§5).
- Denormal-flush every feedback state (`Dsp.Ftz`).
- ValueDescriptions inline literals only.
- Append-only parameter order (preset/song contract).
- The six mandatory csproj properties; .NET AssemblyName suffix.
- Slot is source-agnostic: the machine computes the combined Char-mod offset;
  do not push raw mod sources into the slot.
- Tail-aware sleep: OR every slot IsRinging with feedback.IsRinging.
- Feedback tapped post-slots / pre-limiter, injected pre-slot 0; in-loop tanh +
  DC blocker keep it stable; amount capped at ~0.2.
- Resonator gets Key/Scale via SetMusicalContext (empty method in other fx).
- Check `WM_READ` before touching `input` (1503 notes 2.1); never read a
  stale buffer.
- No default interface bodies, no `System.MathF` or other .NET Core-only APIs
  (net48).
- Morph reads live params each block and drives the engine through `_eff[]`;
  scene A is always live (do not reintroduce a stored A — that was the bug).
- MachineState is name-keyed and version-gated; bump the version on layout
  change.
- Denormals are the cause of any stuck high-CPU state. Two rules: (1) smoothed
  values that ramp toward 0 must SNAP to the target within an epsilon (~1e-6),
  or the value asymptotes into the denormal range AND stays a hair above 0 so
  the effect's `amount <= 0` early-return never fires; (2) every recursive state
  that decays toward 0 (delay/comb/reverb/resonator feedback, filter states,
  envelope/AutoGain detectors, Tail level) must be flushed via `Dsp.Ftz` or a
  threshold-to-zero. Turning Amounts DOWN must reduce CPU, never raise it.
- Control-rate caching: heavy effects recompute coefficients every 16 samples
  via an `_cc` counter; `Reset()` must set `_cc = 0` so a re-activated effect
  recomputes immediately. `Dsp.TanhFast` is for the signal path only (it is an
  approximation); keep exact maths where pitch/coefficient accuracy matters.
- Do not write a param via its C# property directly from custom code paths
  (PedalTracker 13.1 trap — it updates the field but not values[], so it will
  not persist). Reading params and snapshotting into MachineState is safe, which
  is what the morph does.

---

## 15. Performance

Measure with Pedal Profiler2 reading the **ENGINE** column (not SOLO/MARGINAL,
which are dominated by ReBuzz's fixed ~5 ms per-chunk host overhead floor).
In Buzz 1503 use its CPU Monitor; expect a fixed ~37 CC/sample managed-call
cost per machine (1503 notes 2.2), and somewhat higher DSP cost than ReBuzz
(the shim computes in double; older 32-bit .NET Framework JIT).

The v1.0 all-None reading was ~3 percent flat, but that predated the v1.3
modulation features and the all-six-slots presets. Heavy six-slot chains
(several Filter/RingMod/Bitcrush/Drive plus feedback/resonators) raised ENGINE
materially, so an optimisation pass was done (v1.3.1):

- **Control-rate coefficients.** Bitcrush, Drive, Filter, RingMod and Resonator
  recompute their macro-derived coefficients (the per-sample `Pow`/`Tan`/`Sqrt`
  and the resonator pitch) only every 16 samples via an internal `_cc` counter,
  reusing the cached values in between. The macros are smoothed over ~20 ms, so
  16-sample granularity is inaudible; static settings are bit-identical, a moving
  knob lags by at most 16 samples.
- **Fast tanh.** `Dsp.TanhFast` (a Padé rational, max error ~7e-4) replaces
  exact tanh in the per-sample signal path (Drive, Resonator, global feedback).
- **Compiled morph getters.** `ComputeEffective` reads the live params through
  cached `Func<int>` delegates instead of `PropertyInfo.GetValue`, removing the
  per-block reflection cost.

The Resonator is the heaviest single effect (stereo fractional-delay comb with
damping + in-loop saturation) and is often stacked (chords), so it got an extra
pass: L and R share one write pointer and one delay, so the read indices are
computed once rather than per channel, and the integer/fractional delay split is
cached at control rate. Note a second, structural cost driver: a long-decay
resonator (feedback ~0.997) rings for seconds, and the machine cannot sleep
while any tail rings — so resonator-heavy patches keep the whole chain running
continuously. That is inherent to the sustain; short-decay mode or lower
feedback reduces how long the machine stays awake.

All transparent (verified by simulation). Re-measure a worst-case chain to
confirm the ENGINE drop on the target machine. Not done (lower priority): a
mode-settled branch to skip the inactive Mode (fast tanh already made the
both-modes compute cheap), and fast `Sin` for the RingMod/LFO oscillators.

---

## 16. Version history

- **v1.0** — full 6-slot serial rack, 10 effects, output limiter, auto-gain,
  scale fix.
- **v1.1** — `DescribeValue` labels each control with its real per-effect value.
- **v1.2** — Mode switches crossfade between their two modes (click-free toggle).
- **v1.3** — signature modulation set: global feedback loop, envelope follower,
  tempo-synced LFO, tuned Resonator slot type (+ Key/Scale), and the
  live-anchored scene morph. Plus the 30-preset bank. Parameters 26 -> 49, all
  appended.
- **v1.3.1** — efficiency pass (no behaviour/param change): control-rate
  coefficient caching in the heavy effects, fast tanh in the per-sample path,
  compiled getters for the morph, and a resonator inner-loop cleanup (single
  shared L/R write pointer + cached integer/fractional delay, computed once
  instead of per-channel). All transparent (verified); aimed at heavy six-slot
  and multi-resonator chains.
- **v1.3.2** — denormal hardening (fixes a stuck high-CPU state). Smoothed slot
  values snap to target instead of asymptoting into the denormal range, and
  every decaying recursive state is flushed (Tail level, envelope follower,
  AutoGain detectors, feedback-loop filters, reverb allpass + bright tilt,
  resonator damping, bitcrush/filter states). Symptom was CPU climbing to ~40%
  after a few seconds and sticking, made worse (not better) by turning Amounts
  down — the classic denormal signature.
- **v1.3.3** — right-click About window (AboutWindow pattern): a `Commands`
  property yields a MenuItemVM that pops a MessageBox with name/version/URL/
  license. Adds a `BuzzGUI.Common.dll` reference and a `Version` const
  (single source of truth, currently 1.3.3).

---

- **Buzz 1503 port (v1.3.3)** — separate repo; see §0.
- **v1.3.4** — split the combined Filter into two effect types: `Lowpass`
  (reuses enum value 3, the former Filter) and `Highpass` (new, value 11). Char
  stays resonance; Mode now sets slope (12/24 dB per oct via a cascaded
  Butterworth second stage). Lowpass at 12 dB is bit-identical to the old
  Filter lowpass; old Filter highpass-mode instances in saved songs become
  24 dB lowpass unless retyped to Highpass (the preset bank was migrated).
  Ported to Buzz 1503 the same release.
- **v1.3.5** — reworked the filter controls: Amount now blends dry -> filtered
  (how much filter), Char sets cutoff (150 Hz -> 18 kHz), Mode selects one of two
  Q values (gentle 0.707 / resonant 6.0). Dropped the 12/24 dB slope and the
  cascaded second stage; single 12 dB per oct stage. This remaps all three
  filter controls, so the preset bank was migrated (cutoff from the old amount,
  full wet, Q from the old resonance) and existing songs with filter slots need
  their filter controls reset. Ported to Buzz 1503 the same release.

## 17. Roadmap / declined

- **GUI: declined by the user.** Control is via mapped BCR2000 encoders and the
  parameter window; `DescribeValue` carries the readouts.
- The machine is feature-complete for v1.3. Future direction is driven by
  real-world playing feedback rather than a fixed backlog.
- If a future feature wants another mod source, the slot's source-agnostic Char
  offset and the morph's reflection-based scene set both extend cleanly.
