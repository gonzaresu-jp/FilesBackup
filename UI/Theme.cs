using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace FilesBackup.UI
{
    /// <summary>配色・フォント・アイコン。ライト/ダークの2種類</summary>
    public static class Theme
    {
        public static bool IsDark { get; private set; }

        public static event EventHandler? Changed;

        public static void DetectSystem()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                IsDark = key?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch
            {
                IsDark = false;
            }
        }

        public static void Set(bool dark)
        {
            if (IsDark == dark) return;
            IsDark = dark;
            Changed?.Invoke(null, EventArgs.Empty);
        }

        private static Color Hex(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

        // ---- 左端のアイコンバー（ライトでも黒）----
        public static Color Rail => IsDark ? Hex(0x0C0C0D) : Hex(0x141414);
        public static Color RailSelected => IsDark ? Hex(0x2C2D31) : Hex(0x3B3B3E);
        public static Color RailHover => IsDark ? Hex(0x1C1D20) : Hex(0x252527);
        public static Color RailIcon => Hex(0xF2F2F2);
        public static Color RailIconMuted => Hex(0xA4A6AB);

        // ---- 2列目の一覧 ----
        public static Color Side => IsDark ? Hex(0x1A1B1E) : Hex(0xF3F4F6);
        public static Color SideSelected => IsDark ? Hex(0x2A2C31) : Hex(0xE4E5E8);
        public static Color SideSelectedBorder => IsDark ? Hex(0x383B42) : Hex(0xD7D9DE);
        public static Color SideHover => IsDark ? Hex(0x222428) : Hex(0xEAEBEE);

        // ---- 右側の内容 ----
        public static Color Surface => IsDark ? Hex(0x212226) : Hex(0xFFFFFF);
        public static Color SurfaceBorder => IsDark ? Hex(0x2D2F34) : Hex(0xE3E5E9);
        public static Color Card => IsDark ? Hex(0x28292E) : Hex(0xF7F8FA);
        public static Color CardBorder => IsDark ? Hex(0x33353B) : Hex(0xECEEF1);
        public static Color Input => IsDark ? Hex(0x1C1D21) : Hex(0xFFFFFF);
        public static Color InputBorder => IsDark ? Hex(0x3A3C43) : Hex(0xD9DCE1);

        public static Color Text => IsDark ? Hex(0xE7E8EA) : Hex(0x1F2328);
        public static Color TextMuted => IsDark ? Hex(0x8D929A) : Hex(0x8B9099);
        public static Color Separator => IsDark ? Hex(0x2E3035) : Hex(0xE3E5E8);

        public static Color Accent => Hex(0x2563EB);
        public static Color AccentHover => Hex(0x3B74F0);
        public static Color AccentSoft => IsDark ? Hex(0x1E2A44) : Hex(0xE8EFFD);
        public static Color Danger => Hex(0xDC2626);
        public static Color DangerSoft => IsDark ? Hex(0x3A1E1E) : Hex(0xFDECEC);
        public static Color Success => Hex(0x16A34A);
        public static Color Warning => Hex(0xD97706);

        public static Color ButtonBg => IsDark ? Hex(0x2E3035) : Hex(0xFFFFFF);
        public static Color ButtonHover => IsDark ? Hex(0x373940) : Hex(0xF1F2F4);
        public static Color ButtonPressed => IsDark ? Hex(0x41444B) : Hex(0xE6E8EB);
        public static Color ButtonBorder => IsDark ? Hex(0x3D4047) : Hex(0xD9DCE1);
        public static Color ToggleOff => IsDark ? Hex(0x4A4D55) : Hex(0xCFD3D9);

        // ---- フォント ----
        public static readonly string UiFontName = PickFont("Yu Gothic UI", "Meiryo UI", "Segoe UI");
        public static readonly string NumberFontName = PickFont("Segoe UI", "Yu Gothic UI");
        public static readonly string IconFontName = PickFont("Segoe Fluent Icons", "Segoe MDL2 Assets");

        public static readonly Font Body = new(UiFontName, 10f);
        public static readonly Font BodyBold = new(UiFontName, 10f, FontStyle.Bold);
        public static readonly Font Small = new(UiFontName, 9f);
        public static readonly Font SideTitle = new(UiFontName, 13f);
        public static readonly Font Section = new(UiFontName, 9.5f);
        public static readonly Font PageTitle = new(UiFontName, 16f, FontStyle.Bold);
        public static readonly Font CardTitle = new(UiFontName, 10.5f, FontStyle.Bold);
        public static readonly Font Count = new(NumberFontName, 10.5f);
        public static readonly Font StatValue = new(NumberFontName, 14f, FontStyle.Bold);

        private static readonly Dictionary<float, Font> IconFonts = new();

        public static Font IconFont(float size)
        {
            lock (IconFonts)
            {
                if (!IconFonts.TryGetValue(size, out var font))
                {
                    font = new Font(IconFontName, size);
                    IconFonts[size] = font;
                }
                return font;
            }
        }

        private static string PickFont(params string[] candidates)
        {
            try
            {
                using var installed = new InstalledFontCollection();
                var names = new HashSet<string>(installed.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var c in candidates)
                    if (names.Contains(c)) return c;
            }
            catch { }
            return candidates[^1];
        }

        // ---- 描画の補助 ----

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRound(Graphics g, Color fill, RectangleF r, float radius, Color? border = null)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundRect(r, radius);
            using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
            if (border is Color b && b.A > 0)
            {
                using var pen = new Pen(b, 1f);
                g.DrawPath(pen, path);
            }
        }

        public static void DrawGlyph(Graphics g, string glyph, float size, Color color, Rectangle bounds)
        {
            TextRenderer.DrawText(g, glyph, IconFont(size), bounds, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        /// <summary>アプリのロゴ（2×2 の幾何学模様）</summary>
        public static void DrawLogo(Graphics g, RectangleF box, Color color)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float gap = box.Width * 0.06f;
            float cell = (box.Width - gap) / 2f;
            using var brush = new SolidBrush(color);

            // 左上：四角、右上：三角（左下半分）、左下：三角（右上半分）、右下：四角
            g.FillRectangle(brush, box.X, box.Y, cell, cell);
            g.FillPolygon(brush, new[]
            {
                new PointF(box.X + cell + gap, box.Y),
                new PointF(box.X + cell + gap, box.Y + cell),
                new PointF(box.Right, box.Y + cell),
            });
            g.FillPolygon(brush, new[]
            {
                new PointF(box.X, box.Y + cell + gap),
                new PointF(box.X + cell, box.Y + cell + gap),
                new PointF(box.X + cell, box.Bottom),
            });
            g.FillRectangle(brush, box.X + cell + gap, box.Y + cell + gap, cell, cell);
        }

        /// <summary>アイコンの絵（黒い角丸四角に白いロゴ）</summary>
        private static Bitmap RenderAppIcon(int size)
        {
            var bmp = new Bitmap(size, size);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            FillRound(g, Hex(0x141414), new RectangleF(0, 0, size - 1, size - 1), size * 0.22f);
            float pad = size * 0.24f;
            DrawLogo(g, new RectangleF(pad, pad, size - pad * 2, size - pad * 2), Color.White);
            return bmp;
        }

        /// <summary>ウィンドウ・トレイ用のアイコン</summary>
        public static Icon CreateAppIcon(int size = 64)
        {
            // exe に埋め込んだアイコンがあればそれを使う（無ければ描いて作る）
            try
            {
                var embedded = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (embedded != null && embedded.Width >= 32) return new Icon(embedded, size, size);
            }
            catch { }

            using var bmp = RenderAppIcon(size);
            return Icon.FromHandle(bmp.GetHicon());
        }

        /// <summary>
        /// exe 用のアイコンファイル（.ico）を書き出す（各サイズを PNG で格納）。
        /// 作り直すとき: FilesBackup.exe --make-icon Assets\app.ico
        /// </summary>
        public static void SaveIconFile(string path)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            var images = new List<byte[]>();
            foreach (int s in sizes)
            {
                using var bmp = RenderAppIcon(s);
                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                images.Add(ms.ToArray());
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var fs = File.Create(path);
            using var w = new BinaryWriter(fs);
            w.Write((ushort)0);
            w.Write((ushort)1);
            w.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                w.Write(dim);
                w.Write(dim);
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((ushort)1);
                w.Write((ushort)32);
                w.Write(images[i].Length);
                w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) w.Write(img);
        }
    }

    /// <summary>Segoe Fluent Icons / Segoe MDL2 Assets の文字コード</summary>
    public static class Glyph
    {
        public const string Home = "";
        public const string History = "";
        public const string Calendar = "";
        public const string Clock = "";
        public const string Filter = "";
        public const string Settings = "";
        public const string Add = "";
        public const string Folder = "";
        public const string OpenFile = "";
        public const string Document = "";
        public const string Warning = "";
        public const string Error = "";
        public const string Completed = "";
        public const string CheckMark = "";
        public const string Circle = "";
        public const string RadioOn = "";
        public const string Play = "";
        public const string Pause = "";
        public const string Stop = "";
        public const string Delete = "";
        public const string Up = "";
        public const string Down = "";
        public const string Sync = "";
        public const string Preview = "";
        public const string CheckList = "";
        public const string Shield = "";
        public const string Admin = "";
        public const string Import = "";
        public const string Export = "";
        public const string Save = "";
        public const string Info = "";
        public const string Sun = "";
        public const string Moon = "";
        public const string Tag = "";
        public const string Link = "";
        public const string Drive = "";
        public const string Lightning = "";
        public const string Person = "";
        public const string Cancel = "";
        public const string ArrowRight = "";
        public const string Repeat = "";
        public const string Power = "";
        public const string Brush = "";
        public const string Storage = "";
        public const string Download = "";
        public const string Refresh = "";
    }
}
