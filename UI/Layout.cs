using System.ComponentModel;

namespace FilesBackup.UI
{
    /// <summary>子を上から順に、横幅いっぱいに並べる</summary>
    public class StackPanel : Panel
    {
        private bool _inLayout;

        public StackPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        /// <summary>子の間隔（96dpi 基準）</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Gap { get; set; } = 16;

        /// <summary>中身に合わせて自分の高さを変える（カードの中身など）</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool AutoHeight { get; set; }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (_inLayout) return;
            _inLayout = true;
            try
            {
                int gap = (int)Math.Round(Gap * DeviceDpi / 96f);
                int y = Padding.Top + (AutoScroll ? AutoScrollPosition.Y : 0);
                int width = ClientSize.Width - Padding.Horizontal;
                bool first = true;

                foreach (Control c in Controls)
                {
                    if (!c.Visible) continue;
                    if (!first) y += gap;
                    first = false;
                    c.SetBounds(Padding.Left, y, Math.Max(10, width), c.Height);
                    y += c.Height;
                }

                int total = y - (AutoScroll ? AutoScrollPosition.Y : 0) + Padding.Bottom;
                if (AutoHeight)
                    Height = total;
                else if (AutoScroll)
                    AutoScrollMinSize = new Size(0, total);
            }
            finally
            {
                _inLayout = false;
            }
        }
    }

    /// <summary>子を横一列に並べる（左寄せ・右寄せ・残り幅いっぱい）</summary>
    public class RowPanel : Panel
    {
        private enum Align { Left, Right, Fill }
        private readonly List<(Control Control, Align Align)> _items = new();

        public RowPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            Height = 38;
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Gap { get; set; } = 10;

        public RowPanel AddLeft(Control c) { _items.Add((c, Align.Left)); Controls.Add(c); return this; }
        public RowPanel AddRight(Control c) { _items.Add((c, Align.Right)); Controls.Add(c); return this; }
        public RowPanel AddFill(Control c) { _items.Add((c, Align.Fill)); Controls.Add(c); return this; }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int gap = (int)Math.Round(Gap * DeviceDpi / 96f);

            int left = 0;
            foreach (var (c, align) in _items)
            {
                if (align != Align.Left || !c.Visible) continue;
                c.SetBounds(left, (Height - c.Height) / 2, c.Width, c.Height);
                left += c.Width + gap;
            }

            int right = ClientSize.Width;
            foreach (var (c, align) in Enumerable.Reverse(_items))
            {
                if (align != Align.Right || !c.Visible) continue;
                right -= c.Width;
                c.SetBounds(right, (Height - c.Height) / 2, c.Width, c.Height);
                right -= gap;
            }

            foreach (var (c, align) in _items)
            {
                if (align != Align.Fill || !c.Visible) continue;
                c.SetBounds(left, (Height - c.Height) / 2, Math.Max(20, right - left), c.Height);
            }
        }
    }

    /// <summary>子を同じ幅で横に並べる（数値のタイルなど）</summary>
    public class ColumnsPanel : Panel
    {
        public ColumnsPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Gap { get; set; } = 14;

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            var visible = Controls.Cast<Control>().Where(c => c.Visible).ToList();
            if (visible.Count == 0) return;

            int gap = (int)Math.Round(Gap * DeviceDpi / 96f);
            int width = (ClientSize.Width - gap * (visible.Count - 1)) / visible.Count;
            int x = 0;
            foreach (var c in visible)
            {
                c.SetBounds(x, 0, width, Height);
                x += width + gap;
            }
        }
    }

    /// <summary>見出し付きの角丸カード。中身は Body に上から並べる</summary>
    public class Card : RoundedPanel
    {
        private readonly ThemedLabel? _title;
        private readonly ThemedLabel? _description;

        public StackPanel Body { get; }

        public Card(string? title = null, string? description = null)
        {
            FillColor = Theme.Card;
            BorderColor = Theme.CardBorder;
            Radius = 14;
            Padding = new Padding(22, 18, 22, 20);

            if (!string.IsNullOrEmpty(title))
            {
                _title = new ThemedLabel(ThemedLabel.Style.CardTitle) { Text = title, Height = 24 };
                Controls.Add(_title);
            }

            if (!string.IsNullOrEmpty(description))
            {
                _description = new ThemedLabel(ThemedLabel.Style.Small) { Text = description, Height = 20 };
                Controls.Add(_description);
            }

            Body = new StackPanel { AutoHeight = true, Gap = 12, BackColor = Theme.Card };
            Controls.Add(Body);
        }

        public void SetTitle(string title)
        {
            if (_title != null) _title.Text = title;
        }

        public Card Add(params Control[] controls)
        {
            foreach (var c in controls) Body.Controls.Add(c);
            return this;
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (Body is null) return;   // コンストラクタ内（Padding 設定時）に呼ばれた場合
            int y = Padding.Top;
            int width = Width - Padding.Horizontal;

            if (_title != null)
            {
                _title.SetBounds(Padding.Left, y, width, _title.Height);
                y += _title.Height + 2;
            }
            if (_description != null)
            {
                _description.SetBounds(Padding.Left, y, width, _description.Height);
                y += _description.Height;
            }
            if (_title != null || _description != null)
                y += 12;

            Body.SetBounds(Padding.Left, y, width, Body.Height);
            int height = y + Body.Height + Padding.Bottom;
            if (Height != height) Height = height;
        }
    }
}
