using System;

namespace PedalDrumMatrix
{
    // One rack slot. Owns a pre-built instance of every FxType (no audio-thread
    // allocation on swap). Type changes run a short equal-power crossfade so
    // they're click-free. Amount and Char are smoothed; Mode is a plain switch.
    public sealed class Slot
    {
        readonly IDrumFx[] _fx;
        FxType _active = FxType.None;
        FxType _previous = FxType.None;

        float _amount, _amountTarget;
        float _p1, _p1Target;
        float _mode, _modeTarget;
        float _smoothCoef;

        int _xfadeRemain, _xfadeLen = 1;

        public Slot()
        {
            _fx = new IDrumFx[Enum.GetValues(typeof(FxType)).Length];
            for (int i = 0; i < _fx.Length; i++) _fx[i] = FxFactory.Create((FxType)i);
        }

        public void Prepare(float sampleRate, float samplesPerTick)
        {
            foreach (var f in _fx) f.Prepare(sampleRate, samplesPerTick);
            _smoothCoef = (float)Math.Exp(-1.0 / (0.020 * sampleRate)); // 20 ms
            _xfadeLen   = Math.Max(1, (int)(0.010f * sampleRate));      // 10 ms swap
        }

        // Called once per Work block, before the sample loop.
        public void SetParams(int typeRaw, int amountRaw, int charRaw, bool mode, int key, int scale)
        {
            _amountTarget = amountRaw / 127f;
            _p1Target     = charRaw   / 127f;
            _modeTarget   = mode ? 1f : 0f;
            _fx[(int)_active].SetMusicalContext(key, scale);

            var t = (FxType)typeRaw;
            if (t != _active)
            {
                _previous    = _active;
                _active      = t;
                _xfadeRemain = _xfadeLen;
                _fx[(int)_active].Reset();
            }
        }

        public void Process(ref float l, ref float r, float charMod)
        {
            _amount = _amountTarget + (_amount - _amountTarget) * _smoothCoef;
            _p1     = _p1Target     + (_p1     - _p1Target)     * _smoothCoef;
            _mode   = _modeTarget   + (_mode   - _modeTarget)   * _smoothCoef;
            // Snap to target once within an inaudible epsilon. Without this the
            // smoothed value asymptotes toward (but never reaches) the target and
            // slides into the denormal range — which both pins CPU at ~100× cost
            // and keeps Amount a hair above 0 so the effect never early-returns.
            if (MathF.Abs(_amount - _amountTarget) < 1e-6f) _amount = _amountTarget;
            if (MathF.Abs(_p1     - _p1Target)     < 1e-6f) _p1     = _p1Target;
            if (MathF.Abs(_mode   - _modeTarget)   < 1e-6f) _mode   = _modeTarget;

            // modulated Char (envelope + LFO offset from the machine), clamped
            float p1e = _p1 + charMod;
            if (p1e < 0f) p1e = 0f; else if (p1e > 1f) p1e = 1f;

            if (_xfadeRemain > 0)
            {
                float t  = 1f - (_xfadeRemain / (float)_xfadeLen);
                float gN = MathF.Sin(t * (MathF.PI * 0.5f));
                float gO = MathF.Cos(t * (MathF.PI * 0.5f));

                float oL = l, oR = r, nL = l, nR = r;
                _fx[(int)_previous].Process(ref oL, ref oR, _amount, p1e, _mode);
                _fx[(int)_active  ].Process(ref nL, ref nR, _amount, p1e, _mode);

                l = oL * gO + nL * gN;
                r = oR * gO + nR * gN;

                _xfadeRemain--;
                if (_xfadeRemain == 0) _fx[(int)_previous].Reset();
            }
            else
            {
                _fx[(int)_active].Process(ref l, ref r, _amount, p1e, _mode);
            }
        }

        public bool IsRinging =>
            _xfadeRemain > 0
                ? (_fx[(int)_active].IsRinging || _fx[(int)_previous].IsRinging)
                : _fx[(int)_active].IsRinging;
    }
}
