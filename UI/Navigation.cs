using System.ComponentModel;

namespace FilesBackup.UI
{
    // ================= 左端のアイコンバー =================

    public class NavRail : PaintedControl
    {
        private sealed record Item(string Key, string Glyph, string Tip, bool Bottom);

        private readonly List<Item> _items = new();
        private readonly ToolTip _tip = new() { InitialDelay = 400 };
        private string _selected = "";
        private int _hover = -1;

        public event EventHandler<string>? ItemClicked;

        public NavRail()
        {
            Width = 76;
        }

        public void Add(string key, string glyph, string tip, bool bottom = false)
        {
            _items.Add(new Item(key, glyph, tip, bottom));
            Invalidate();
        }

        public void SetGlyph(string key, string glyph, string tip)
        {
            int i = _items.FindIndex(x => x.Key == key);
            if (i < 0) return;
            _items[i] = _items[i] with { Glyph = glyph, Tip = tip };
            if (_hover == i) _tip.SetToolTip(this, tip);
            Invalidate();
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Selected { get => _selected; set { _selected = value; Invalidate(); } }

        private Rectangle ItemRect(int index)
        {
            int size = S(50), step = S(60);
            int x = (Width - size) / 2;
            var item = _items[index];

            if (!item.Bottom)
            {
                int order = _items.Take(index).Count(i => !i.Bottom);
                return new Rectangle(x, S(132) + order * step, size, size);
            }

            int fromBottom = _items.Skip(index + 1).Count(i => i.Bottom);
            return new Rectangle(x, Height - S(24) - size - fromBottom * step, size, size);
        }

        private int HitTest(Point p)
        {
            for (int i = 0; i < _items.Count; i++)
                if (ItemRect(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int hit = HitTest(e.Location);
            if (hit != _hover)
            {
                _hover = hit;
                Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
                _tip.SetToolTip(this, hit >= 0 ? _items[hit].Tip : "");
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            int hit = HitTest(e.Location);
            if (hit >= 0) ItemClicked?.Invoke(this, _items[hit].Key);
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Rail);

            int logo = S(40);
            Theme.DrawLogo(g, new RectangleF((Width - logo) / 2f, S(40), logo, logo), Theme.RailIcon);

            for (int i = 0; i < _items.Count; i++)
            {
                var r = ItemRect(i);
                bool selected = _items[i].Key == _selected;
                if (selected)
                    Theme.FillRound(g, Theme.RailSelected, new RectangleF(r.X, r.Y, r.Width, r.Height), Sf(12),
                        Color.FromArgb(40, 255, 255, 255));
                else if (i == _hover)
                    Theme.FillRound(g, Theme.RailHover, new RectangleF(r.X, r.Y, r.Width, r.Height), Sf(12));

                Theme.DrawGlyph(g, _items[i].Glyph, 15f, selected || i == _hover ? Theme.RailIcon : Theme.RailIconMuted, r);
            }
        }
    }

    // ================= 2列目の一覧 =================

    public enum SideEntryKind { Item, Header, Separator, Add, Info }

    public class SideEntry
    {
        public SideEntryKind Kind { get; init; } = SideEntryKind.Item;
        public string Key { get; init; } = "";
        public string Glyph { get; init; } = "";
        public string Text { get; init; } = "";
        public string Count { get; init; } = "";
        public Color? GlyphColor { get; init; }
        public Color? CountColor { get; init; }
        public string Tooltip { get; init; } = "";

        public static SideEntry Item(string key, string glyph, string text, string count = "", Color? glyphColor = null, Color? countColor = null) =>
            new() { Kind = SideEntryKind.Item, Key = key, Glyph = glyph, Text = text, Count = count, GlyphColor = glyphColor, CountColor = countColor };

        public static SideEntry Header(string text) => new() { Kind = SideEntryKind.Header, Text = text };
        public static SideEntry Separator() => new() { Kind = SideEntryKind.Separator };
        public static SideEntry Add(string key, string text) => new() { Kind = SideEntryKind.Add, Key = key, Glyph = UI.Glyph.Add, Text = text };

        public static SideEntry Info(string glyph, string text, string value, Color? glyphColor = null) =>
            new() { Kind = SideEntryKind.Info, Glyph = glyph, Text = text, Count = value, GlyphColor = glyphColor };
    }

    public class SideList : PaintedControl
    {
        private readonly List<SideEntry> _entries = new();
        private readonly ToolTip _tip = new() { InitialDelay = 500 };
        private string _title = "";
        private string? _selected;
        private int _hover = -1;
        private int _scroll;

        public event EventHandler<string>? EntryClicked;

        public SideList()
        {
            Width = 280;
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Title { get => _title; set { _title = value; Invalidate(); } }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? SelectedKey { get => _selected; set { _selected = value; Invalidate(); } }

        public void SetEntries(string title, IEnumerable<SideEntry> entries, string? selected)
        {
            _title = title;
            _entries.Clear();
            _entries.AddRange(entries);
            _selected = selected;
            _hover = -1;
            _scroll = Math.Min(_scroll, MaxScroll());
            Invalidate();
        }

        private int EntryHeight(SideEntry e) => e.Kind switch
        {
            SideEntryKind.Header => S(44),
            SideEntryKind.Separator => S(25),
            _ => S(52),
        };

        private int ContentTop => S(100);

        private int ContentHeight() => ContentTop + _entries.Sum(EntryHeight) + S(24);

        private int MaxScroll() => Math.Max(0, ContentHeight() - Height);

        private Rectangle EntryRect(int index)
        {
            int y = ContentTop - _scroll;
            for (int i = 0; i < index; i++) y += EntryHeight(_entries[i]);
            return new Rectangle(S(14), y, Width - S(28), EntryHeight(_entries[index]));
        }

        private static bool Clickable(SideEntry e) => e.Kind is SideEntryKind.Item or SideEntryKind.Add;

        private int HitTest(Point p)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (!Clickable(_entries[i])) continue;
                var r = EntryRect(i);
                r.Inflate(0, -S(3));
                if (r.Contains(p)) return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int hit = HitTest(e.Location);
            if (hit != _hover)
            {
                _hover = hit;
                Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
                _tip.SetToolTip(this, hit >= 0 ? (_entries[hit].Tooltip is { Length: > 0 } t ? t : _entries[hit].Text) : "");
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            _scroll = Math.Clamp(_scroll - e.Delta / 2, 0, MaxScroll());
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override void OnResize(EventArgs e)
        {
            _scroll = Math.Min(_scroll, MaxScroll());
            base.OnResize(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            int hit = HitTest(e.Location);
            if (hit >= 0)
            {
                var entry = _entries[hit];
                if (entry.Kind == SideEntryKind.Item) _selected = entry.Key;
                Invalidate();
                EntryClicked?.Invoke(this, entry.Key);
            }
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Side);

            TextRenderer.DrawText(g, _title, Theme.SideTitle, new Rectangle(S(34), S(40) - _scroll, Width - S(48), S(36)), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                var r = EntryRect(i);
                if (r.Bottom < 0 || r.Top > Height) continue;

                switch (entry.Kind)
                {
                    case SideEntryKind.Header:
                        TextRenderer.DrawText(g, entry.Text, Theme.Section, new Rectangle(r.X + S(20), r.Y, r.Width - S(20), r.Height - S(4)),
                            Theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.SingleLine);
                        break;

                    case SideEntryKind.Separator:
                        using (var pen = new Pen(Theme.Separator))
                            g.DrawLine(pen, r.X + S(20), r.Y + r.Height / 2, r.Right - S(20), r.Y + r.Height / 2);
                        break;

                    default:
                        DrawRow(g, entry, r, i);
                        break;
                }
            }
        }

        private void DrawRow(Graphics g, SideEntry entry, Rectangle r, int index)
        {
            var row = new RectangleF(r.X + 0.5f, r.Y + S(3) + 0.5f, r.Width - 1, r.Height - S(6) - 1);
            bool selected = entry.Kind == SideEntryKind.Item && entry.Key == _selected;

            if (selected)
                Theme.FillRound(g, Theme.SideSelected, row, Sf(10), Theme.SideSelectedBorder);
            else if (index == _hover)
                Theme.FillRound(g, Theme.SideHover, row, Sf(10));

            bool add = entry.Kind == SideEntryKind.Add;
            Color textColor = add ? Theme.TextMuted : Theme.Text;
            Color glyphColor = entry.GlyphColor ?? (add ? Theme.TextMuted : Theme.Text);

            int iconX = r.X + S(18);
            if (!string.IsNullOrEmpty(entry.Glyph))
                Theme.DrawGlyph(g, entry.Glyph, 13f, glyphColor, new Rectangle(iconX, (int)row.Y, S(24), (int)row.Height));

            int countWidth = string.IsNullOrEmpty(entry.Count) ? 0 : TextRenderer.MeasureText(entry.Count, Theme.Count).Width;
            int textX = iconX + S(40);
            int textRight = r.Right - S(18) - (countWidth > 0 ? countWidth + S(10) : 0);

            TextRenderer.DrawText(g, entry.Text, Theme.Body, new Rectangle(textX, (int)row.Y, Math.Max(0, textRight - textX), (int)row.Height),
                textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            if (countWidth > 0)
            {
                TextRenderer.DrawText(g, entry.Count, Theme.Count, new Rectangle(r.Right - S(18) - countWidth, (int)row.Y, countWidth, (int)row.Height),
                    entry.CountColor ?? Theme.TextMuted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }
    }
}
