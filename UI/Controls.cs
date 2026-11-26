using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace FilesBackup.UI
{
    /// <summary>自前描画するコントロールの共通部分</summary>
    public abstract class PaintedControl : Control
    {
        protected PaintedControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Font = Theme.Body;
        }

        protected int S(int v) => (int)Math.Round(v * DeviceDpi / 96f);
        protected float Sf(float v) => v * DeviceDpi / 96f;

        protected Color ParentBack => Parent switch
        {
            RoundedPanel rp => rp.FillColor,
            null => BackColor,
            var p => p.BackColor,
        };

        protected static Color Blend(Color c, float alpha) => Color.FromArgb((int)(255 * alpha), c);
    }

    // ================= 角丸パネル =================

    public class RoundedPanel : Panel
    {
        private Color _fill = Color.White;
        private Color _border = Color.Transparent;
        private int _radius = 12;

        public RoundedPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color FillColor { get => _fill; set { _fill = value; BackColor = value; Invalidate(); } }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color BorderColor { get => _border; set { _border = value; Invalidate(); } }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Radius { get => _radius; set { _radius = value; Invalidate(); } }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Color outside = Parent switch
            {
                RoundedPanel rp => rp.FillColor,
                null => _fill,
                var p => p.BackColor,
            };
            e.Graphics.Clear(outside);
            float r = _radius * DeviceDpi / 96f;
            Theme.FillRound(e.Graphics, _fill, new RectangleF(0, 0, Width - 1, Height - 1), r, _border);
        }
    }

    // ================= ボタン =================

    public enum ButtonKind { Secondary, Primary, Danger, Ghost }

    public class ModernButton : PaintedControl, IButtonControl
    {
        private bool _hover, _pressed, _highlighted;
        private ButtonKind _kind = ButtonKind.Secondary;
        private string _glyph = "";

        public ModernButton()
        {
            Cursor = Cursors.Hand;
            TabStop = true;
            Height = 36;
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public ButtonKind Kind { get => _kind; set { _kind = value; Invalidate(); } }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Glyph { get => _glyph; set { _glyph = value; Invalidate(); } }

        /// <summary>未保存の変更があることを示す（青い枠）</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Highlighted { get => _highlighted; set { _highlighted = value; Invalidate(); } }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public DialogResult DialogResult { get; set; }

        public void NotifyDefault(bool value) { }
        public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }

        /// <summary>文字に合わせた幅</summary>
        public int PreferredWidth()
        {
            int text = TextRenderer.MeasureText(Text, Theme.Body).Width;
            int icon = string.IsNullOrEmpty(_glyph) ? 0 : S(22);
            return text + icon + S(30);
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; Focus(); Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Enter or Keys.Space) { PerformClick(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(ParentBack);

            Color fill, border, fore;
            switch (_kind)
            {
                case ButtonKind.Primary:
                    fill = _pressed ? Theme.Accent : _hover ? Theme.AccentHover : Theme.Accent;
                    border = fill;
                    fore = Color.White;
                    break;
                case ButtonKind.Danger:
                    fill = _pressed ? Theme.ButtonPressed : _hover ? Theme.DangerSoft : Theme.ButtonBg;
                    border = Theme.ButtonBorder;
                    fore = Theme.Danger;
                    break;
                case ButtonKind.Ghost:
                    fill = _pressed ? Theme.ButtonPressed : _hover ? Theme.ButtonHover : ParentBack;
                    border = Color.Transparent;
                    fore = Theme.Text;
                    break;
                default:
                    fill = _pressed ? Theme.ButtonPressed : _hover ? Theme.ButtonHover : Theme.ButtonBg;
                    border = Theme.ButtonBorder;
                    fore = Theme.Text;
                    break;
            }

            if (_highlighted && _kind != ButtonKind.Primary)
            {
                border = Theme.Accent;
                fore = Theme.Accent;
            }

            if (!Enabled)
            {
                fill = Blend(fill, 0.55f);
                border = Blend(border, 0.55f);
                fore = Blend(fore, 0.45f);
            }

            var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.FillRound(g, fill, rect, Sf(8), border);

            if (Focused && ShowFocusCues)
            {
                using var pen = new Pen(Blend(Theme.Accent, 0.6f), 1.5f);
                using var path = Theme.RoundRect(RectangleF.Inflate(rect, -2.5f, -2.5f), Sf(6));
                g.DrawPath(pen, path);
            }

            int textWidth = TextRenderer.MeasureText(Text, Theme.Body).Width;
            int iconWidth = string.IsNullOrEmpty(_glyph) ? 0 : S(20);
            int x = (Width - textWidth - iconWidth) / 2;
            if (iconWidth > 0)
            {
                Theme.DrawGlyph(g, _glyph, 10.5f, fore, new Rectangle(x, 0, S(16), Height));
                x += iconWidth;
            }
            TextRenderer.DrawText(g, Text, Theme.Body, new Rectangle(x, 0, textWidth + S(4), Height), fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }

    // ================= トグルスイッチ =================

    public class ToggleSwitch : PaintedControl
    {
        private bool _checked;
        private bool _hover;
        private string _description = "";

        public event EventHandler? CheckedChanged;

        public ToggleSwitch()
        {
            Cursor = Cursors.Hand;
            TabStop = true;
            Height = 30;
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>2行目の補足説明（あれば高さを広げる）</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Description
        {
            get => _description;
            set { _description = value; Height = S(string.IsNullOrEmpty(value) ? 30 : 48); Invalidate(); }
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnClick(EventArgs e)
        {
            Focus();
            if (Enabled) Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space && Enabled) { Checked = !Checked; e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(ParentBack);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int trackW = S(38), trackH = S(22);
            int trackY = string.IsNullOrEmpty(_description) ? (Height - trackH) / 2 : S(4);
            var track = new RectangleF(0.5f, trackY + 0.5f, trackW - 1, trackH - 1);

            Color trackColor = _checked ? (_hover ? Theme.AccentHover : Theme.Accent) : Theme.ToggleOff;
            if (!Enabled) trackColor = Blend(trackColor, 0.5f);
            Theme.FillRound(g, trackColor, track, trackH / 2f);

            float knob = trackH - S(6);
            float knobX = _checked ? track.Right - knob - S(3) : track.X + S(3);
            using (var brush = new SolidBrush(Color.White))
                g.FillEllipse(brush, knobX, track.Y + S(3), knob, knob);

            Color text = Enabled ? Theme.Text : Blend(Theme.Text, 0.5f);
            int textX = trackW + S(12);
            TextRenderer.DrawText(g, Text, Theme.Body, new Rectangle(textX, trackY - S(2), Width - textX, trackH + S(4)), text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            if (!string.IsNullOrEmpty(_description))
            {
                TextRenderer.DrawText(g, _description, Theme.Small, new Rectangle(textX, trackY + trackH, Width - textX, Height - trackY - trackH),
                    Theme.TextMuted, TextFormatFlags.Top | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }

            if (Focused && ShowFocusCues)
            {
                using var pen = new Pen(Blend(Theme.Accent, 0.6f), 1.5f);
                using var path = Theme.RoundRect(RectangleF.Inflate(track, 2, 2), trackH / 2f + 2);
                g.DrawPath(pen, path);
            }
        }
    }

    // ================= 入力欄 =================

    public class ModernInput : RoundedPanel
    {
        public TextBox Box { get; }

        public event EventHandler? ValueChanged;

        public ModernInput()
        {
            Radius = 8;
            Box = new TextBox { BorderStyle = BorderStyle.None, Font = Theme.Body };
            Box.TextChanged += (s, e) => ValueChanged?.Invoke(this, EventArgs.Empty);
            Box.Enter += (s, e) => BorderColor = Theme.Accent;
            Box.Leave += (s, e) => BorderColor = Theme.InputBorder;
            Controls.Add(Box);
            Height = 36;
            Cursor = Cursors.IBeam;
            ApplyTheme();
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Value { get => Box.Text; set => Box.Text = value; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Placeholder { get => Box.PlaceholderText; set => Box.PlaceholderText = value; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool ReadOnly { get => Box.ReadOnly; set { Box.ReadOnly = value; ApplyTheme(); } }

        public void ApplyTheme()
        {
            Color back = Box.ReadOnly ? Theme.Card : Theme.Input;
            FillColor = back;
            BorderColor = Box.Focused ? Theme.Accent : Theme.InputBorder;
            Box.BackColor = back;
            Box.ForeColor = Theme.Text;
        }

        protected override void OnClick(EventArgs e) { Box.Focus(); base.OnClick(e); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int pad = (int)(10 * DeviceDpi / 96f);
            Box.Location = new Point(pad, (Height - Box.Height) / 2);
            Box.Width = Math.Max(10, Width - pad * 2);
        }
    }

    // ================= 進捗バー =================

    public class ThinProgress : PaintedControl
    {
        private float _value;
        private bool _indeterminate;
        private float _phase;
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };

        public ThinProgress()
        {
            Height = 6;
            _timer.Tick += (s, e) => { _phase = (_phase + 0.012f) % 1.4f; Invalidate(); };
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public float Value { get => _value; set { _value = Math.Clamp(value, 0f, 1f); Invalidate(); } }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Indeterminate
        {
            get => _indeterminate;
            set
            {
                if (_indeterminate == value) return;
                _indeterminate = value;
                _timer.Enabled = value && Visible;
                Invalidate();
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            _timer.Enabled = _indeterminate && Visible;
            base.OnVisibleChanged(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(ParentBack);
            var track = new RectangleF(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, Theme.Separator, track, Height / 2f);

            RectangleF bar;
            if (_indeterminate)
            {
                float w = Width * 0.3f;
                float x = (_phase - 0.3f) * Width;
                bar = RectangleF.Intersect(new RectangleF(x, 0, w, Height - 1), track);
            }
            else
            {
                bar = new RectangleF(0, 0, (Width - 1) * _value, Height - 1);
            }

            if (bar.Width > 1)
                Theme.FillRound(g, Theme.Accent, bar, Height / 2f);
        }
    }

    // ================= 曜日の選択 =================

    public class DayChips : PaintedControl
    {
        private static readonly string[] Labels = { "日", "月", "火", "水", "木", "金", "土" };
        private readonly HashSet<DayOfWeek> _selected = new();
        private int _hover = -1;

        public event EventHandler? SelectionChanged;

        public DayChips()
        {
            Cursor = Cursors.Hand;
            Height = 40;
        }

        public IReadOnlyCollection<DayOfWeek> Selected => _selected;

        public void SetSelected(IEnumerable<DayOfWeek> days)
        {
            _selected.Clear();
            foreach (var d in days) _selected.Add(d);
            Invalidate();
        }

        private Rectangle ChipRect(int i)
        {
            int size = Height - S(2);
            return new Rectangle(i * (size + S(8)), 0, size, size);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int hit = Enumerable.Range(0, 7).FirstOrDefault(i => ChipRect(i).Contains(e.Location), -1);
            if (hit != _hover) { _hover = hit; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            for (int i = 0; i < 7; i++)
            {
                if (!ChipRect(i).Contains(e.Location)) continue;
                var day = (DayOfWeek)i;
                if (!_selected.Remove(day)) _selected.Add(day);
                Invalidate();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                break;
            }
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(ParentBack);
            for (int i = 0; i < 7; i++)
            {
                var r = ChipRect(i);
                bool on = _selected.Contains((DayOfWeek)i);
                Color fill = on ? Theme.Accent : (_hover == i ? Theme.ButtonHover : Theme.ButtonBg);
                Color border = on ? Theme.Accent : Theme.ButtonBorder;
                Theme.FillRound(g, fill, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), r.Height / 2f, border);
                Color fore = on ? Color.White : (i == 0 ? Theme.Danger : i == 6 ? Theme.Accent : Theme.Text);
                TextRenderer.DrawText(g, Labels[i], Theme.Body, r, fore,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }

    // ================= 数値のタイル =================

    public class StatTile : PaintedControl
    {
        private string _caption = "", _value = "", _sub = "", _glyph = "";
        private Color? _valueColor;

        public StatTile() { Height = 96; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Caption { get => _caption; set { _caption = value; Invalidate(); } }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Value { get => _value; set { _value = value; Invalidate(); } }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Sub { get => _sub; set { _sub = value; Invalidate(); } }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Glyph { get => _glyph; set { _glyph = value; Invalidate(); } }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color? ValueColor { get => _valueColor; set { _valueColor = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(ParentBack);
            Theme.FillRound(g, Theme.Card, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Sf(12), Theme.CardBorder);

            int pad = S(16);
            if (!string.IsNullOrEmpty(_glyph))
                Theme.DrawGlyph(g, _glyph, 11f, Theme.TextMuted, new Rectangle(Width - pad - S(20), pad - S(2), S(20), S(20)));

            TextRenderer.DrawText(g, _caption, Theme.Small, new Rectangle(pad, pad - S(2), Width - pad * 2 - S(24), S(20)), Theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, _value, Theme.StatValue, new Rectangle(pad, pad + S(18), Width - pad * 2, S(32)), _valueColor ?? Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, _sub, Theme.Small, new Rectangle(pad, pad + S(52), Width - pad * 2, S(20)), Theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
    }

    // ================= 見出し・説明 =================

    /// <summary>背景を親に合わせるラベル（テーマ切替時に色を付け直す）</summary>
    public class ThemedLabel : Label
    {
        public enum Style { Body, Muted, PageTitle, CardTitle, Small }

        private Style _style;

        public ThemedLabel(Style style = Style.Body)
        {
            _style = style;
            AutoSize = false;
            UseMnemonic = false;
            AutoEllipsis = true;
            ApplyTheme();
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Style TextStyle { get => _style; set { _style = value; ApplyTheme(); } }

        /// <summary>null 以外なら、この色で表示する（状態表示など）</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color? OverrideColor { get; set; }

        public void ApplyTheme()
        {
            Font = _style switch
            {
                Style.PageTitle => Theme.PageTitle,
                Style.CardTitle => Theme.CardTitle,
                Style.Small or Style.Muted => _style == Style.Small ? Theme.Small : Theme.Body,
                _ => Theme.Body,
            };
            ForeColor = OverrideColor ?? (_style is Style.Muted or Style.Small ? Theme.TextMuted : Theme.Text);
            BackColor = Color.Transparent;
        }
    }
}
