using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;

namespace PedalDrumMatrix
{
    // ── Buzz 1503 only: readout panel ────────────────────────────────────────
    // Buzz 1503's managed bridge never calls DescribeValue (it arrived in the
    // managed interface's "Update 1", after build 1503), so the parameter window
    // shows raw 0-127 values. This panel, embedded at the top of the parameter
    // window (1503 notes 4.1), shows the same labels ReBuzz shows, by calling the
    // machine's own DescribeValue. Display only: all editing stays on Buzz's
    // sliders. It shows parameter values, not the LFO/envelope-modulated ones.
    [MachineGUIFactoryDecl(PreferWindowedGUI = false, IsGUIResizable = false, UseThemeStyles = false)]
    public class ReadoutGuiFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new ReadoutGui();
    }

    // ── Binding: parameter values and labels by name ─────────────────────────
    // Values come straight from the machine's int properties (reads on the GUI
    // thread; int reads are atomic, 1503 notes 1.4). Labels come from the
    // machine's DescribeValue, which needs Buzz's IParameter object for the
    // name (1503 notes 3.5); list parameters fall back to ValueDescriptions.
    internal sealed class MachineBinding
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        readonly PedalDrumMatrixMachine _m;
        readonly IMachine _im;
        readonly Dictionary<string, Func<int>> _get = new Dictionary<string, Func<int>>();
        readonly Dictionary<string, string[]> _vd = new Dictionary<string, string[]>();
        readonly Dictionary<string, int> _max = new Dictionary<string, int>();
        Dictionary<string, IParameter> _params;

        public MachineBinding(PedalDrumMatrixMachine m, IMachine im)
        {
            _m = m; _im = im;
            foreach (var pi in typeof(PedalDrumMatrixMachine).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var decl = pi.GetCustomAttribute<ParameterDecl>();
                if (decl == null || pi.PropertyType != typeof(int) || pi.GetGetMethod() == null) continue;
                _get[pi.Name] = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), m, pi.GetGetMethod());
                if (decl.ValueDescriptions != null) _vd[pi.Name] = decl.ValueDescriptions;
                _max[pi.Name] = decl.MaxValue;
            }
        }

        public int Get(string name) => _get.TryGetValue(name, out var g) ? g() : 0;
        public int Max(string name) => _max.TryGetValue(name, out var m) ? m : 0;

        public string Describe(string name, int value)
        {
            EnsureParams();
            string s = null;
            IParameter p = null;
            if (_params != null && _params.TryGetValue(name, out p))
            {
                try { s = _m.DescribeValue(p, value); } catch { s = null; }
            }
            if (s == null && _vd.TryGetValue(name, out var vd) && value >= 0 && value < vd.Length) s = vd[value];
            return s ?? value.ToString(Inv);
        }

        // Parameter groups are not ready at construction (1503 notes 2.5), so the
        // name map is built lazily and retried until Buzz's parameters appear.
        void EnsureParams()
        {
            if (_params != null || _im == null) return;
            try
            {
                var map = new Dictionary<string, IParameter>();
                foreach (var g in _im.ParameterGroups)
                    foreach (var p in g.Parameters)
                        if (p != null && p.Name != null && _get.ContainsKey(p.Name) && !map.ContainsKey(p.Name))
                            map[p.Name] = p;
                if (map.Count > 0) _params = map;
            }
            catch { /* not ready yet; try again next refresh */ }
        }
    }

    // ── Text model (no WPF, so it can be tested headless) ────────────────────
    internal sealed class ReadoutModel
    {
        public sealed class SlotRow
        {
            public string Index, Type, Amount, Char, Mode, Env, Lfo;
            public bool Off, AmountZero, EnvZero, LfoZero;
            // The widest labels this slot's effect can show for Char and Mode
            // (a few longest candidates; the GUI measures them). Columns are
            // sized from these, so they fit the effects in use but don't jump
            // while a slider moves.
            public string[] CharWidest = new string[0], ModeWidest = new string[0];
        }
        public sealed class Pair { public string Label, Value; public string[] Widest = new string[0]; public bool Dim; }

        // Global settings: three rows. Each Pair carries the widest values it can
        // show (Widest, found by sweeping the parameter), and the GUI spaces a row
        // by those, so nothing shifts as values change. Rows: feedback loop; LFO
        // and tuning; the rest.
        public Pair[][] Globals = new Pair[0][];

        public readonly SlotRow[] Slots = new SlotRow[6];
        public string Signature = "";
        readonly Dictionary<string, string[][]> _widestCache = new Dictionary<string, string[][]>();

        readonly Func<string, int> _get, _max;
        readonly Func<string, int, string> _describe;
        readonly Dictionary<string, string[]> _paramWidest = new Dictionary<string, string[]>();

        public ReadoutModel(Func<string, int> get, Func<string, int> max, Func<string, int, string> describe)
        {
            _get = get; _max = max; _describe = describe;
            for (int i = 0; i < 6; i++) Slots[i] = new SlotRow();
        }

        string D(string name) => _describe(name, _get(name));

        // The longest labels a parameter can show (its value alone decides them,
        // for every global setting). Cached per parameter.
        string[] W(string name)
        {
            if (_paramWidest.TryGetValue(name, out var hit)) return hit;
            var all = new List<string>();
            int max = Math.Max(0, _max(name));
            for (int v = 0; v <= max; v++) all.Add(_describe(name, v));
            return _paramWidest[name] = Longest(all, 3);
        }

        // LFO shows "<rate> ticks <wave>": combine the widest of each.
        string[] LfoWidest()
        {
            var r = new List<string>();
            foreach (var a in W("LfoRate")) foreach (var b in W("LfoWave")) r.Add(a + " ticks " + b);
            return Longest(r, 3);
        }

        public void Refresh()
        {
            var sig = new StringBuilder(512);
            for (int s = 1; s <= 6; s++)
            {
                var r = Slots[s - 1];
                string p = "Slot" + s;
                int type = _get(p + "Type");
                r.Index = s.ToString(CultureInfo.InvariantCulture);
                r.Type = D(p + "Type");
                r.Off = type == 0;
                if (r.Off)
                {
                    r.CharWidest = r.ModeWidest = new string[0];
                    r.Amount = r.Char = r.Mode = r.Env = r.Lfo = "";
                    r.AmountZero = r.EnvZero = r.LfoZero = true;
                }
                else
                {
                    var w = Widest(s, type);
                    r.CharWidest = w[0]; r.ModeWidest = w[1];
                    r.Amount = D(p + "Amount"); r.AmountZero = _get(p + "Amount") == 0;
                    r.Char   = D(p + "Char");
                    r.Mode   = D(p + "Mode");
                    r.Env    = D(p + "EnvDepth"); r.EnvZero = _get(p + "EnvDepth") == 64;
                    r.Lfo    = D(p + "LfoDepth"); r.LfoZero = _get(p + "LfoDepth") == 64;
                }
                sig.Append(r.Type).Append('|').Append(r.Amount).Append('|').Append(r.Char).Append('|')
                   .Append(r.Mode).Append('|').Append(r.Env).Append('|').Append(r.Lfo).Append('|')
                   .Append(r.AmountZero ? '0' : '1').Append(';');
            }

            bool fbOff = _get("Feedback") == 0;
            Globals = new[]
            {
                new[]
                {
                    new Pair { Label = "Feedback",  Value = D("Feedback"),   Widest = W("Feedback"),   Dim = fbOff },
                    new Pair { Label = "Time",      Value = D("FbTime"),     Widest = W("FbTime"),     Dim = fbOff },
                    new Pair { Label = "Tone",      Value = D("FbTone"),     Widest = W("FbTone"),     Dim = fbOff },
                    new Pair { Label = "Env to Fb", Value = D("EnvToFb"),    Widest = W("EnvToFb"),    Dim = _get("EnvToFb") == 0 },
                },
                new[]
                {
                    new Pair { Label = "LFO",       Value = D("LfoRate") + " ticks " + D("LfoWave"), Widest = LfoWidest() },
                    new Pair { Label = "Key",       Value = D("Key"),        Widest = W("Key") },
                    new Pair { Label = "Scale",     Value = D("Scale"),      Widest = W("Scale") },
                },
                new[]
                {
                    new Pair { Label = "Release",   Value = D("EnvRelease"), Widest = W("EnvRelease") },
                    new Pair { Label = "Morph",     Value = D("Morph"),      Widest = W("Morph"),      Dim = _get("Morph") == 0 },
                    new Pair { Label = "Limiter",   Value = D("Limiter"),    Widest = W("Limiter") },
                    new Pair { Label = "Auto gain", Value = D("AutoGainOn"), Widest = W("AutoGainOn") },
                },
            };
            foreach (var row in Globals) foreach (var g in row) sig.Append(g.Value).Append(g.Dim ? '0' : '1').Append(';');
            Signature = sig.ToString();
        }

        // The longest Char and Mode labels slot `slot` can show with effect
        // `type`, found by sweeping DescribeValue once per effect (the machine
        // labels by the slot's current type, which is `type`). Resonator note
        // names also depend on Key and Scale, so those are part of its cache key.
        // Cached; a few candidates are kept because width isn't exactly length.
        string[][] Widest(int slot, int type)
        {
            string key = type == (int)FxType.Resonator
                ? type + "|" + _get("Key") + "|" + _get("Scale") : type.ToString(CultureInfo.InvariantCulture);
            if (_widestCache.TryGetValue(key, out var hit)) return hit;
            string p = "Slot" + slot;
            var chars = new List<string>();
            for (int v = 0; v <= 127; v++) chars.Add(_describe(p + "Char", v));
            var modes = new List<string> { _describe(p + "Mode", 0), _describe(p + "Mode", 1) };
            var result = new[] { Longest(chars, 4), Longest(modes, 2) };
            if (_get(p + "Type") == type) _widestCache[key] = result;   // type changed mid-sweep: don't cache
            return result;
        }

        static string[] Longest(List<string> items, int keep)
        {
            var distinct = new List<string>();
            foreach (var x in items) if (x != null && !distinct.Contains(x)) distinct.Add(x);
            distinct.Sort((a, b) => b.Length.CompareTo(a.Length));
            if (distinct.Count > keep) distinct.RemoveRange(keep, distinct.Count - keep);
            return distinct.ToArray();
        }
    }

    // ── The panel ────────────────────────────────────────────────────────────
    // OnRender FrameworkElement (1503 notes 4.1: a UserControl's template would
    // paint over OnRender output). Width follows the parameter window, which
    // clips rather than widens (1503 notes 4.2); height is fixed.
    public sealed class ReadoutGui : FrameworkElement, IMachineGUI
    {
        const double DefaultW = 540, MinW = 300, MaxW = 1600;
        const double FontPx = 12, RowH = 17, Pad = 6, ColGap = 12, PairGap = 16;
        const double H = Pad + RowH * 7 + 5 + RowH * 3 + Pad;   // header + 6 slots, gap, 3 global rows
        double W = DefaultW;

        IMachine _im;
        ReadoutModel _model;
        DispatcherTimer _timer;
        string _lastSig;

        static readonly Typeface Face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        static readonly Typeface Bold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        // Column titles, and the widest text the fixed-content columns can hold.
        // Effect, Char and Mode are sized from the slots' current effects.
        static readonly string[] ColTitle  = { "",  "Effect", "Amount", "Char", "Mode", "Env",   "LFO"   };
        static readonly string[] ColSample = { "6", "None",   "100%",   "",     "",     "-100%", "-100%" };

        public ReadoutGui()
        {
            Height = H;
            MinWidth = MinW;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            Loaded += (s, e) =>
            {
                if (_timer != null) return;
                _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
                _timer.Tick += OnTick;
                _timer.Start();
                OnTick(null, EventArgs.Empty);
            };
            Unloaded += (s, e) => { if (_timer != null) { _timer.Stop(); _timer = null; } };
        }

        public IMachine Machine
        {
            get => _im;
            set
            {
                _im = value;
                var m = value?.ManagedMachine as PedalDrumMatrixMachine;
                if (m == null) { _model = null; }
                else
                {
                    var bind = new MachineBinding(m, value);
                    _model = new ReadoutModel(bind.Get, bind.Max, bind.Describe);
                }
                _lastSig = null;
                InvalidateVisual();
            }
        }

        void OnTick(object sender, EventArgs e)
        {
            if (_model == null) return;
            try { _model.Refresh(); } catch { return; }
            if (_model.Signature != _lastSig) { _lastSig = _model.Signature; InvalidateVisual(); }
        }

        protected override Size MeasureOverride(Size available)
        {
            double w = double.IsInfinity(available.Width) || double.IsNaN(available.Width)
                ? DefaultW : Math.Min(Math.Max(available.Width, MinW), MaxW);
            return new Size(w, H);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double w = Math.Min(Math.Max(finalSize.Width, MinW), MaxW);
            if (Math.Abs(w - W) > 0.5) { W = w; InvalidateVisual(); }
            return new Size(W, H);
        }

        double _ppd = 1.0;

        FormattedText T(string s, Brush b, bool bold = false, double maxW = 0)
        {
            var ft = new FormattedText(s ?? "", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                       bold ? Bold : Face, FontPx, b, _ppd);
            if (maxW > 0)
            {
                ft.MaxTextWidth = Math.Max(1.0, maxW);
                ft.MaxLineCount = 1;
                ft.Trimming = TextTrimming.CharacterEllipsis;
            }
            return ft;
        }

        protected override void OnRender(DrawingContext dc)
        {
            _ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            Brush bg = SystemColors.ControlBrush, fg = SystemColors.ControlTextBrush,
                  dim = SystemColors.GrayTextBrush, rule = SystemColors.ControlDarkBrush;

            dc.DrawRectangle(bg, null, new Rect(0, 0, W, H));          // background first (hit testing, 4.1)
            dc.DrawRectangle(rule, null, new Rect(0, H - 1, W, 1));    // divider above Buzz's sliders

            if (_model == null)
            {
                dc.DrawText(T("Pedal Drum Matrix", dim), new Point(Pad, Pad));
                return;
            }

            // Column widths: titles, fixed samples, and for Effect / Char / Mode
            // the widest labels the loaded effects can show. When the window is
            // too narrow, drop LFO, then Env, then trim Char with an ellipsis.
            int n = ColTitle.Length;
            var cw = new double[n];
            for (int c = 0; c < n; c++)
                cw[c] = Math.Max(T(ColTitle[c], fg, true).WidthIncludingTrailingWhitespace,
                                 T(ColSample[c], fg).WidthIncludingTrailingWhitespace);
            foreach (var r in _model.Slots)
            {
                cw[1] = Math.Max(cw[1], T(r.Type, fg, !r.Off).WidthIncludingTrailingWhitespace);
                foreach (var t in r.CharWidest) cw[3] = Math.Max(cw[3], T(t, fg).WidthIncludingTrailingWhitespace);
                foreach (var t in r.ModeWidest) cw[4] = Math.Max(cw[4], T(t, fg).WidthIncludingTrailingWhitespace);
            }
            double avail = W - 2 * Pad;
            int cols = n;
            Func<int, double> total = k => { double t = 0; for (int c = 0; c < k; c++) t += cw[c] + (c > 0 ? ColGap : 0); return t; };
            while (cols > 5 && total(cols) > avail) cols--;
            if (total(cols) > avail) cw[3] = Math.Max(40, cw[3] - (total(cols) - avail));

            var cx = new double[n];
            double x = Pad;
            for (int c = 0; c < cols; c++) { cx[c] = x; x += cw[c] + ColGap; }

            double y = Pad;
            for (int c = 1; c < cols; c++) dc.DrawText(T(ColTitle[c], dim, true, cw[c]), new Point(cx[c], y));
            y += RowH;

            foreach (var r in _model.Slots)
            {
                Brush rowB = r.Off ? dim : fg;
                dc.DrawText(T(r.Index, dim, false, cw[0]), new Point(cx[0], y));
                dc.DrawText(T(r.Type, rowB, !r.Off, cw[1]), new Point(cx[1], y));
                if (!r.Off)
                {
                    string[] v = { null, null, r.Amount, r.Char, r.Mode, r.Env, r.Lfo };
                    bool[] z = { false, false, r.AmountZero, r.AmountZero, r.AmountZero, r.EnvZero, r.LfoZero };
                    for (int c = 2; c < cols; c++)
                        dc.DrawText(T(v[c], z[c] ? dim : fg, false, cw[c]), new Point(cx[c], y));
                }
                y += RowH;
            }

            y += 5;
            DrawGlobals(dc, y, fg, dim);
        }

        // Global settings, one row at a time. Each pair takes the width of its
        // widest possible value, so nothing shifts as values change; a row that
        // doesn't fit the window is squeezed evenly and its texts trimmed.
        void DrawGlobals(DrawingContext dc, double y, Brush fg, Brush dim)
        {
            double avail = W - 2 * Pad;
            foreach (var row in _model.Globals)
            {
                var lw = new double[row.Length];
                var pw = new double[row.Length];
                double sum = (row.Length - 1) * PairGap;
                for (int c = 0; c < row.Length; c++)
                {
                    lw[c] = T(row[c].Label + " ", dim).WidthIncludingTrailingWhitespace;
                    double vw = T(row[c].Value, fg, true).WidthIncludingTrailingWhitespace;
                    foreach (var t in row[c].Widest) vw = Math.Max(vw, T(t, fg, true).WidthIncludingTrailingWhitespace);
                    pw[c] = lw[c] + vw;
                    sum += pw[c];
                }
                double scale = sum > avail
                    ? Math.Max(0.3, (avail - (row.Length - 1) * PairGap) / (sum - (row.Length - 1) * PairGap)) : 1.0;
                double x = Pad;
                for (int c = 0; c < row.Length; c++)
                {
                    double w = pw[c] * scale;
                    var p = row[c];
                    dc.DrawText(T(p.Label + " ", dim, false, w), new Point(x, y));
                    double l = Math.Min(lw[c], w);
                    if (w - l > 8)
                        dc.DrawText(T(p.Value, p.Dim ? dim : fg, true, w - l), new Point(x + l, y));
                    x += w + PairGap;
                }
                y += RowH;
            }
        }
    }
}
