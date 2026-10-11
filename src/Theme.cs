using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DriveFolderSync
{
    static class Glyphs
    {
        public const char Back = '';
        public const char Forward = '';
        public const char Up = '';
        public const char Refresh = '';
        public const char Search = '';
        public const char Download = '';
        public const char Upload = '';
        public const char SaveLocal = '';
        public const char Link = '';
        public const char MapDrive = '';
        public const char Sync = '';
        public const char More = '';
        public const char Cancel = '';
        public const char FolderOpen = '';
        public const char Folder = '';
        public const char OpenLocal = '';
        public const char Delete = '';
        public const char Info = '';
        public const char Copy = '';
        public const char OpenFile = '';
        public const char ChevronRight = '';
        public const char ChevronDown = '';
        public const char Check = '';
        public const char Warning = '';
        public const char Cloud = '';
        public const char Pc = '';
        public const char Error = '';
        public const char Clear = '';
    }

    static class Theme
    {
        public static readonly Color Accent = Color.FromArgb(0, 103, 192);
        public static readonly Color AccentHover = Color.FromArgb(0, 92, 175);
        public static readonly Color AccentPressed = Color.FromArgb(0, 78, 150);
        public static readonly Color Window = Color.White;
        public static readonly Color Chrome = Color.FromArgb(243, 245, 248);
        public static readonly Color Pane = Color.FromArgb(249, 250, 251);
        public static readonly Color Border = Color.FromArgb(225, 228, 233);
        public static readonly Color BorderStrong = Color.FromArgb(196, 201, 209);
        public static readonly Color Text = Color.FromArgb(28, 28, 32);
        public static readonly Color TextMuted = Color.FromArgb(94, 100, 110);
        public static readonly Color TextFaint = Color.FromArgb(150, 155, 164);
        public static readonly Color Hover = Color.FromArgb(230, 233, 238);
        public static readonly Color Pressed = Color.FromArgb(218, 222, 229);
        public static readonly Color RowHover = Color.FromArgb(240, 246, 253);
        public static readonly Color RowSelected = Color.FromArgb(214, 233, 251);
        public static readonly Color RowSelectedIdle = Color.FromArgb(232, 236, 241);
        public static readonly Color LocalText = Color.FromArgb(14, 122, 13);
        public static readonly Color LocalFill = Color.FromArgb(221, 245, 219);
        public static readonly Color CloudText = Color.FromArgb(192, 36, 26);
        public static readonly Color CloudFill = Color.FromArgb(253, 229, 231);
        public static readonly Color WarnText = Color.FromArgb(138, 82, 0);
        public static readonly Color WarnFill = Color.FromArgb(255, 241, 199);
        public static readonly Color InfoFill = Color.FromArgb(224, 238, 251);
        public static readonly Color Success = Color.FromArgb(15, 123, 15);

        public static readonly Font Body = new Font("Segoe UI", 9f);
        public static readonly Font Strong = new Font("Segoe UI Semibold", 9f);
        public static readonly Font Small = new Font("Segoe UI", 8.25f);
        public static readonly Font SmallStrong = new Font("Segoe UI Semibold", 8.25f);
        public static readonly Font Heading = new Font("Segoe UI Semibold", 11.25f);
        public static readonly Font Title = new Font("Segoe UI Semibold", 15f);
        public static readonly Font Display = new Font("Segoe UI Semibold", 19f);

        public static readonly ToolStripRenderer Renderer = new FlatRenderer();

        static float scale;
        static string iconFamily;
        static Icon appIcon;
        static readonly Dictionary<string, Bitmap> glyphs = new Dictionary<string, Bitmap>();

        public static void Init()
        {
            if (ToolStripManager.Renderer != Renderer) ToolStripManager.Renderer = Renderer;
        }

        public static float Scale
        {
            get
            {
                if (scale <= 0)
                {
                    using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
                    if (scale <= 0) scale = 1f;
                }
                return scale;
            }
        }

        public static int S(int value)
        {
            return (int)Math.Round(value * Scale);
        }

        public static Size S(int width, int height)
        {
            return new Size(S(width), S(height));
        }

        public static Padding Pad(int left, int top, int right, int bottom)
        {
            return new Padding(S(left), S(top), S(right), S(bottom));
        }

        public static Icon AppIcon
        {
            get
            {
                if (appIcon != null) return appIcon;
                try
                {
                    using (Stream stream = typeof(Theme).Assembly.GetManifestResourceStream("DriveFolderSync.app.ico"))
                    {
                        if (stream != null) appIcon = new Icon(stream);
                    }
                }
                catch { }
                if (appIcon == null)
                {
                    try { appIcon = Icon.ExtractAssociatedIcon(typeof(Theme).Assembly.Location); }
                    catch { }
                }
                if (appIcon == null) appIcon = SystemIcons.Application;
                return appIcon;
            }
        }

        public static Icon AppIconSized(int size)
        {
            try { return new Icon(AppIcon, size, size); }
            catch { return AppIcon; }
        }

        public static string IconFamily
        {
            get
            {
                if (iconFamily != null) return iconFamily;
                iconFamily = "Segoe MDL2 Assets";
                foreach (FontFamily family in FontFamily.Families)
                {
                    if (string.Equals(family.Name, "Segoe Fluent Icons", StringComparison.OrdinalIgnoreCase))
                    {
                        iconFamily = family.Name;
                        break;
                    }
                }
                return iconFamily;
            }
        }

        public static Bitmap Glyph(char code, int size, Color color)
        {
            string key = ((int)code).ToString() + ":" + size + ":" + color.ToArgb();
            Bitmap bitmap;
            if (glyphs.TryGetValue(key, out bitmap)) return bitmap;
            bitmap = new Bitmap(Math.Max(1, size), Math.Max(1, size), PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            using (Font font = new Font(IconFamily, Math.Max(1, size), GraphicsUnit.Pixel))
            using (SolidBrush brush = new SolidBrush(color))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.DrawString(code.ToString(), font, brush, 0, 0, StringFormat.GenericTypographic);
            }
            glyphs[key] = bitmap;
            return bitmap;
        }

        public static void DrawGlyph(Graphics g, char code, int size, Color color, int x, int y)
        {
            g.DrawImage(Glyph(code, size, color), x, y, size, size);
        }

        public static void DrawGlyphCentered(Graphics g, char code, int size, Color color, Rectangle box)
        {
            DrawGlyph(g, code, size, color, box.X + (box.Width - size) / 2, box.Y + (box.Height - size) / 2);
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 0)
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

        public static void FillRound(Graphics g, Color color, Rectangle r, int radius)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Round(r, radius))
            using (SolidBrush brush = new SolidBrush(color))
                g.FillPath(brush, path);
            g.SmoothingMode = old;
        }

        public static void DrawRound(Graphics g, Color color, Rectangle r, int radius, float width)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF inner = new RectangleF(r.X + width / 2f, r.Y + width / 2f, r.Width - width, r.Height - width);
            using (GraphicsPath path = Round(inner, radius))
            using (Pen pen = new Pen(color, width))
                g.DrawPath(pen, path);
            g.SmoothingMode = old;
        }

        public static void DrawText(Graphics g, string text, Font font, Rectangle bounds, Color color, TextFormatFlags extra)
        {
            if ((extra & TextFormatFlags.PathEllipsis) != 0)
            {
                text = FitPath(text, font, bounds.Width);
                extra &= ~TextFormatFlags.PathEllipsis;
            }
            TextRenderer.DrawText(g, text ?? "", font, bounds, color,
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | extra);
        }

        // Shortens C:\a\b\c\d to C:\a\…\d so the folder name stays readable.
        public static string FitPath(string path, Font font, int width)
        {
            if (string.IsNullOrEmpty(path) || Measure(path, font) <= width) return path;
            string[] parts = path.Split('\\');
            if (parts.Length < 3) return path;
            for (int keep = parts.Length - 2; keep >= 1; keep--)
            {
                for (int head = Math.Min(keep, 2); head >= 1; head--)
                {
                    int tail = keep - head + 1;
                    if (tail < 1) continue;
                    List<string> pieces = new List<string>();
                    for (int i = 0; i < head; i++) pieces.Add(parts[i]);
                    pieces.Add("\u2026");
                    for (int i = parts.Length - tail; i < parts.Length; i++) pieces.Add(parts[i]);
                    string candidate = string.Join("\\", pieces.ToArray());
                    if (Measure(candidate, font) <= width) return candidate;
                }
            }
            return parts[0] + "\\\u2026\\" + parts[parts.Length - 1];
        }

        public static int Measure(string text, Font font)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
        }

        public static Color KindText(string kind)
        {
            if (kind == "local") return LocalText;
            if (kind == "cloud") return CloudText;
            if (kind == "warn") return WarnText;
            if (kind == "info") return AccentPressed;
            return TextMuted;
        }

        static Color KindFill(string kind)
        {
            if (kind == "local") return LocalFill;
            if (kind == "cloud") return CloudFill;
            if (kind == "warn") return WarnFill;
            if (kind == "info") return InfoFill;
            return Color.Empty;
        }

        static char KindGlyph(string kind, string text)
        {
            string lower = (text ?? "").ToLowerInvariant();
            if (kind == "warn") return Glyphs.Warning;
            if (kind == "cloud" && (lower.Contains("both") || lower.Contains("review") || lower.Contains("error"))) return Glyphs.Warning;
            if (kind == "cloud") return Glyphs.Cloud;
            if (kind == "local") return Glyphs.Pc;
            if (kind == "same" || kind == "info") return Glyphs.Check;
            return '\0';
        }

        public static int StatusWidth(string text, string kind)
        {
            if (KindGlyph(kind, text) == '\0') return Measure(text, Body);
            Font font = KindFill(kind).IsEmpty ? Body : SmallStrong;
            return S(8) + S(12) + S(6) + Measure(text, font) + S(10);
        }

        // Status shown as a tinted pill for changes, a check for in-sync, or plain muted text.
        public static void DrawStatus(Graphics g, Rectangle cell, string text, string kind)
        {
            if (string.IsNullOrEmpty(text)) return;
            char glyph = KindGlyph(kind, text);
            if (glyph == '\0')
            {
                DrawText(g, text, Body, cell, TextMuted, TextFormatFlags.Left);
                return;
            }
            Color fill = KindFill(kind);
            Color fore = KindText(kind);
            Color glyphColor = kind == "same" || kind == "info" ? Accent : fore;
            int height = Math.Min(cell.Height, S(22));
            int width = Math.Min(StatusWidth(text, kind), cell.Width);
            Rectangle pill = new Rectangle(cell.X, cell.Y + (cell.Height - height) / 2, width, height);
            if (!fill.IsEmpty) FillRound(g, fill, pill, height / 2);
            int glyphSize = S(12);
            DrawGlyph(g, glyph, glyphSize, glyphColor, pill.X + S(8), pill.Y + (pill.Height - glyphSize) / 2);
            Rectangle textBox = new Rectangle(pill.X + S(8) + glyphSize + S(6), pill.Y, Math.Max(0, pill.Right - (pill.X + S(8) + glyphSize + S(6)) - S(8)), pill.Height);
            DrawText(g, text, fill.IsEmpty ? Body : SmallStrong, textBox, fore, TextFormatFlags.Left);
        }
    }

    static class Native
    {
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, string lParam);

        public static void ExplorerTheme(IntPtr handle, string name)
        {
            try { SetWindowTheme(handle, name, null); }
            catch { }
        }

        public static void Caption(Form form, Color color)
        {
            int value = color.R | (color.G << 8) | (color.B << 16);
            try { DwmSetWindowAttribute(form.Handle, 35, ref value, 4); }
            catch { }
        }

        public static void RoundCorners(IntPtr handle, bool small)
        {
            int value = small ? 3 : 2;
            try { DwmSetWindowAttribute(handle, 33, ref value, 4); }
            catch { }
        }

        public static void Cue(TextBox box, string text)
        {
            if (!box.IsHandleCreated) return;
            SendMessage(box.Handle, 0x1501, (IntPtr)1, text ?? "");
        }
    }

    class FlatRenderer : ToolStripProfessionalRenderer
    {
        public FlatRenderer()
        {
            RoundedEdges = false;
        }

        protected override void Initialize(ToolStrip toolStrip)
        {
            base.Initialize(toolStrip);
            ToolStripDropDown drop = toolStrip as ToolStripDropDown;
            if (drop == null) return;
            drop.Padding = new Padding(Theme.S(4), Theme.S(4), Theme.S(4), Theme.S(4));
            drop.DropShadowEnabled = false;
            if (drop.IsHandleCreated) Native.RoundCorners(drop.Handle, false);
            drop.HandleCreated += new EventHandler(delegate { Native.RoundCorners(drop.Handle, false); });
        }

        protected override void InitializeItem(ToolStripItem item)
        {
            base.InitializeItem(item);
            if (item is ToolStripMenuItem && item.IsOnDropDown) item.Padding = new Padding(0, Theme.S(4), 0, Theme.S(4));
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            Color back = e.ToolStrip is ToolStripDropDown ? Theme.Window : e.ToolStrip.BackColor;
            using (SolidBrush brush = new SolidBrush(back)) e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (Pen pen = new Pen(Theme.Border))
            {
                if (e.ToolStrip is ToolStripDropDown)
                    e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
                else if (e.ToolStrip is StatusStrip)
                    e.Graphics.DrawLine(pen, 0, 0, e.ToolStrip.Width, 0);
            }
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
        }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            ToolStripButton button = e.Item as ToolStripButton;
            Item(e.Graphics, e.Item, button != null && button.Checked);
        }

        protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e)
        {
            Item(e.Graphics, e.Item, false);
        }

        protected override void OnRenderSplitButtonBackground(ToolStripItemRenderEventArgs e)
        {
            Item(e.Graphics, e.Item, false);
            ToolStripSplitButton split = e.Item as ToolStripSplitButton;
            if (split != null) DrawArrow(new ToolStripArrowRenderEventArgs(e.Graphics, split, split.DropDownButtonBounds, Theme.TextMuted, ArrowDirection.Down));
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Enabled || !(e.Item.Selected || e.Item.Pressed)) return;
            Rectangle r = new Rectangle(0, 0, e.Item.Width, e.Item.Height);
            if (e.Item.IsOnDropDown) r = new Rectangle(Theme.S(2), 0, e.Item.Width - Theme.S(4), e.Item.Height);
            Theme.FillRound(e.Graphics, e.Item.Pressed && !e.Item.IsOnDropDown ? Theme.Pressed : Theme.Hover, r, Theme.S(4));
        }

        void Item(Graphics g, ToolStripItem item, bool on)
        {
            bool hot = item.Selected || item.Pressed;
            if (!item.Enabled || (!hot && !on)) return;
            Theme.FillRound(g, item.Pressed ? Theme.Pressed : Theme.Hover, new Rectangle(0, 0, item.Width, item.Height), Theme.S(4));
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using (Pen pen = new Pen(Theme.Border))
            {
                if (e.Vertical)
                {
                    int x = e.Item.Width / 2;
                    e.Graphics.DrawLine(pen, x, Theme.S(8), x, e.Item.Height - Theme.S(8));
                }
                else
                {
                    int y = e.Item.Height / 2;
                    e.Graphics.DrawLine(pen, Theme.S(4), y, e.Item.Width - Theme.S(4), y);
                }
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.TextFaint;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            int size = Theme.S(10);
            Color color = e.Item != null && !e.Item.Enabled ? Theme.TextFaint : Theme.TextMuted;
            char glyph = e.Direction == ArrowDirection.Down ? Glyphs.ChevronDown : Glyphs.ChevronRight;
            Theme.DrawGlyphCentered(e.Graphics, glyph, size, color, e.ArrowRectangle);
        }

        protected override void OnRenderStatusStripSizingGrip(ToolStripRenderEventArgs e)
        {
        }
    }

    // A borderless glyph button for navigation (Back, Forward, Up, Refresh).
    class IconButton : Control
    {
        readonly char glyph;
        bool hot;
        bool down;

        public IconButton(char glyph, string tip, ToolTip tips)
        {
            this.glyph = glyph;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Size = Theme.S(34, 32);
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = tip;
            tips.SetToolTip(this, tip);
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { hot = false; down = false; Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            if (Enabled && (hot || down)) Theme.FillRound(e.Graphics, down ? Theme.Pressed : Theme.Hover, ClientRectangle, Theme.S(4));
            Theme.DrawGlyphCentered(e.Graphics, glyph, Theme.S(16), Enabled ? Theme.Text : Theme.TextFaint, ClientRectangle);
        }
    }

    // A rounded push button. Primary buttons use the accent fill.
    class FlatButton : Button
    {
        bool hot;
        bool down;
        public bool Primary;

        public FlatButton(string text, bool primary)
        {
            Text = text;
            Primary = primary;
            UseMnemonic = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            Font = Theme.Body;
            Height = Theme.S(32);
            Width = Math.Max(Theme.S(96), Theme.Measure(text, Theme.Body) + Theme.S(36));
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Default;
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color parent = Parent != null ? Parent.BackColor : Theme.Window;
            g.Clear(parent);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill;
            Color border;
            Color fore;
            if (Primary)
            {
                fill = !Enabled ? Color.FromArgb(160, 190, 222) : down ? Theme.AccentPressed : hot ? Theme.AccentHover : Theme.Accent;
                border = fill;
                fore = Color.White;
            }
            else
            {
                fill = !Enabled ? Theme.Chrome : down ? Theme.Pressed : hot ? Color.FromArgb(246, 247, 249) : Theme.Window;
                border = Theme.BorderStrong;
                fore = Enabled ? Theme.Text : Theme.TextFaint;
            }
            Theme.FillRound(g, fill, new Rectangle(0, 0, Width, Height), Theme.S(4));
            Theme.DrawRound(g, border, new Rectangle(0, 0, Width, Height), Theme.S(4), 1f);
            if (Focused && ShowFocusCues)
                Theme.DrawRound(g, Primary ? Color.White : Theme.Accent, Rectangle.Inflate(new Rectangle(0, 0, Width, Height), -Theme.S(2), -Theme.S(2)), Theme.S(3), 1f);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    // Explorer-style breadcrumb. Click a segment to open it, or click empty space to type a path.
    class AddressBar : Control
    {
        public class Crumb
        {
            public string Text;
            public string Key;
            public Rectangle Bounds;
            public bool Visible;
        }

        readonly TextBox edit = new TextBox();
        List<Crumb> crumbs = new List<Crumb>();
        Crumb overflow;
        int hot = -2;
        string editText = "";
        char glyph = Glyphs.Pc;

        public event Action<string> Navigate;
        public event Action<string> Submit;
        public event EventHandler EditEnded;

        public AddressBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Chrome;
            Height = Theme.S(32);
            edit.BorderStyle = BorderStyle.None;
            edit.Font = Theme.Body;
            edit.BackColor = Theme.Window;
            edit.ForeColor = Theme.Text;
            edit.Visible = false;
            edit.KeyDown += new KeyEventHandler(Edit_KeyDown);
            edit.LostFocus += new EventHandler(delegate { EndEdit(); });
            Controls.Add(edit);
            AccessibleRole = AccessibleRole.ToolBar;
            AccessibleName = "Address";
        }

        public bool Editing { get { return edit.Visible; } }

        public void SetPath(List<Crumb> list, string text, char icon)
        {
            crumbs = list ?? new List<Crumb>();
            editText = text ?? "";
            glyph = icon;
            hot = -2;
            if (Editing) EndEdit();
            LayoutCrumbs();
            Invalidate();
        }

        public void BeginEdit()
        {
            edit.Text = editText;
            int left = Theme.S(38);
            edit.SetBounds(left, (Height - edit.Height) / 2, Math.Max(10, Width - left - Theme.S(10)), edit.Height);
            edit.Visible = true;
            edit.Focus();
            edit.SelectAll();
            Invalidate();
        }

        public void EndEdit()
        {
            if (!edit.Visible) return;
            edit.Visible = false;
            Invalidate();
        }

        void Edit_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                string text = edit.Text;
                EndEdit();
                if (EditEnded != null) EditEnded(this, EventArgs.Empty);
                if (Submit != null) Submit(text);
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                EndEdit();
                if (EditEnded != null) EditEnded(this, EventArgs.Empty);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutCrumbs();
            if (Editing)
            {
                int left = Theme.S(38);
                edit.SetBounds(left, (Height - edit.Height) / 2, Math.Max(10, Width - left - Theme.S(10)), edit.Height);
            }
        }

        void LayoutCrumbs()
        {
            int left = Theme.S(38);
            int right = Width - Theme.S(8);
            int chevron = Theme.S(18);
            int pad = Theme.S(16);
            int[] widths = new int[crumbs.Count];
            for (int i = 0; i < crumbs.Count; i++) widths[i] = Theme.Measure(crumbs[i].Text, Theme.Body) + pad;
            int overflowWidth = Theme.Measure("…", Theme.Body) + pad + chevron;
            int start = 0;
            while (start < crumbs.Count - 1)
            {
                int total = start > 0 ? overflowWidth : 0;
                for (int i = start; i < crumbs.Count; i++) total += widths[i] + (i < crumbs.Count - 1 ? chevron : 0);
                if (left + total <= right) break;
                start++;
            }
            overflow = null;
            int x = left;
            int top = Theme.S(4);
            int height = Height - Theme.S(8);
            if (start > 0)
            {
                overflow = new Crumb();
                overflow.Text = "…";
                overflow.Key = crumbs[start - 1].Key;
                overflow.Visible = true;
                overflow.Bounds = new Rectangle(x, top, overflowWidth - chevron, height);
                x += overflowWidth;
            }
            for (int i = 0; i < crumbs.Count; i++)
            {
                crumbs[i].Visible = i >= start;
                if (!crumbs[i].Visible) continue;
                int width = Math.Min(widths[i], Math.Max(Theme.S(40), right - x));
                crumbs[i].Bounds = new Rectangle(x, top, width, height);
                x += width + chevron;
            }
        }

        int HitTest(Point p)
        {
            if (overflow != null && overflow.Bounds.Contains(p)) return -1;
            for (int i = 0; i < crumbs.Count; i++)
            {
                if (crumbs[i].Visible && crumbs[i].Bounds.Contains(p)) return i;
            }
            return -2;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (Editing) return;
            int hit = HitTest(e.Location);
            if (hit != hot)
            {
                hot = hit;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hot != -2)
            {
                hot = -2;
                Invalidate();
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left || Editing) return;
            int hit = HitTest(e.Location);
            string key = null;
            if (hit == -1) key = overflow.Key;
            else if (hit >= 0) key = crumbs[hit].Key;
            if (key != null)
            {
                if (Navigate != null) Navigate(key);
            }
            else BeginEdit();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            Rectangle box = new Rectangle(0, 0, Width, Height);
            Theme.FillRound(g, Theme.Window, box, Theme.S(5));
            Theme.DrawRound(g, Editing ? Theme.Accent : Theme.Border, box, Theme.S(5), Editing ? 1.5f : 1f);
            Theme.DrawGlyphCentered(g, glyph, Theme.S(16), Theme.TextMuted, new Rectangle(Theme.S(10), 0, Theme.S(18), Height));
            if (Editing) return;
            int chevron = Theme.S(18);
            if (overflow != null)
            {
                if (hot == -1) Theme.FillRound(g, Theme.Hover, overflow.Bounds, Theme.S(4));
                Theme.DrawText(g, overflow.Text, Theme.Body, overflow.Bounds, Theme.Text, TextFormatFlags.HorizontalCenter);
                Theme.DrawGlyphCentered(g, Glyphs.ChevronRight, Theme.S(10), Theme.TextFaint, new Rectangle(overflow.Bounds.Right, 0, chevron, Height));
            }
            for (int i = 0; i < crumbs.Count; i++)
            {
                Crumb crumb = crumbs[i];
                if (!crumb.Visible) continue;
                if (hot == i) Theme.FillRound(g, Theme.Hover, crumb.Bounds, Theme.S(4));
                Rectangle text = Rectangle.Inflate(crumb.Bounds, -Theme.S(8), 0);
                Theme.DrawText(g, crumb.Text, Theme.Body, text, Theme.Text, TextFormatFlags.HorizontalCenter);
                if (i < crumbs.Count - 1)
                    Theme.DrawGlyphCentered(g, Glyphs.ChevronRight, Theme.S(10), Theme.TextFaint, new Rectangle(crumb.Bounds.Right, 0, chevron, Height));
            }
        }
    }

    // A rounded text field with an optional leading glyph and clear button.
    class TextField : Control
    {
        public readonly TextBox Box = new TextBox();
        readonly char glyph;
        readonly bool clearable;
        string cue = "";
        bool clearHot;

        public event EventHandler QueryChanged;

        public TextField(char glyph, bool clearable, string name)
        {
            this.glyph = glyph;
            this.clearable = clearable;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Window;
            Height = Theme.S(32);
            Cursor = Cursors.IBeam;
            Box.AccessibleName = name;
            Box.BorderStyle = BorderStyle.None;
            Box.Font = Theme.Body;
            Box.BackColor = Theme.Window;
            Box.ForeColor = Theme.Text;
            Box.TextChanged += new EventHandler(delegate
            {
                Invalidate();
                if (QueryChanged != null) QueryChanged(this, EventArgs.Empty);
            });
            Box.KeyDown += new KeyEventHandler(delegate(object sender, KeyEventArgs e)
            {
                if (this.clearable && e.KeyCode == Keys.Escape && Box.Text.Length > 0)
                {
                    e.SuppressKeyPress = true;
                    Box.Text = "";
                }
            });
            Box.GotFocus += new EventHandler(delegate { Invalidate(); });
            Box.LostFocus += new EventHandler(delegate { Invalidate(); });
            Box.HandleCreated += new EventHandler(delegate { Native.Cue(Box, cue); });
            Controls.Add(Box);
            AccessibleRole = AccessibleRole.Text;
            AccessibleName = name;
        }

        int TextLeft
        {
            get { return glyph == '\0' ? Theme.S(10) : Theme.S(34); }
        }

        public string Query
        {
            get { return Box.Text; }
            set { Box.Text = value ?? ""; }
        }

        public void SetCue(string text)
        {
            cue = text ?? "";
            Native.Cue(Box, cue);
        }

        Rectangle ClearBounds
        {
            get { return new Rectangle(Width - Theme.S(30), Theme.S(4), Theme.S(26), Height - Theme.S(8)); }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int left = TextLeft;
            Box.SetBounds(left, (Height - Box.Height) / 2, Math.Max(10, Width - left - (clearable ? Theme.S(32) : Theme.S(10))), Box.Height);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Box.BackColor = Enabled ? Theme.Window : Theme.Chrome;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool over = clearable && Box.Text.Length > 0 && ClearBounds.Contains(e.Location);
            if (over != clearHot)
            {
                clearHot = over;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (clearHot)
            {
                clearHot = false;
                Invalidate();
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (clearable && Box.Text.Length > 0 && ClearBounds.Contains(e.Location)) Box.Text = "";
            Box.Focus();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            Rectangle box = new Rectangle(0, 0, Width, Height);
            Theme.FillRound(g, Enabled ? Theme.Window : Theme.Chrome, box, Theme.S(5));
            Theme.DrawRound(g, Box.Focused ? Theme.Accent : Theme.BorderStrong, box, Theme.S(5), Box.Focused ? 1.5f : 1f);
            if (glyph != '\0')
                Theme.DrawGlyphCentered(g, glyph, Theme.S(14), Theme.TextMuted, new Rectangle(Theme.S(10), 0, Theme.S(16), Height));
            if (clearable && Box.Text.Length > 0)
            {
                Rectangle clear = ClearBounds;
                if (clearHot) Theme.FillRound(g, Theme.Hover, clear, Theme.S(4));
                Theme.DrawGlyphCentered(g, Glyphs.Cancel, Theme.S(10), Theme.TextMuted, clear);
            }
        }
    }

    // Title, link status, and details for the open folder.
    class ViewHeader : Control
    {
        string title = "";
        string detail = "";
        string badge = "";
        string badgeKind = "none";
        bool legend;
        readonly ToolTip tips;

        public ViewHeader(ToolTip tips)
        {
            this.tips = tips;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Window;
            Height = Theme.S(70);
        }

        public void Set(string title, string detail, string tip, string badge, string badgeKind, bool legend)
        {
            this.title = title ?? "";
            this.detail = detail ?? "";
            this.badge = badge ?? "";
            this.badgeKind = string.IsNullOrEmpty(badgeKind) ? "none" : badgeKind;
            this.legend = legend;
            tips.SetToolTip(this, string.IsNullOrEmpty(tip) ? this.detail : tip);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            int left = Theme.S(20);
            int right = Width - Theme.S(20);
            int legendWidth = 0;
            string localText = "Changed on this PC";
            string cloudText = "Changed in the cloud";
            int dot = Theme.S(8);
            int gap = Theme.S(6);
            int spacing = Theme.S(18);
            if (legend)
            {
                legendWidth = dot + gap + Theme.Measure(localText, Theme.Small) + spacing + dot + gap + Theme.Measure(cloudText, Theme.Small);
                if (right - legendWidth - left < Theme.S(320)) legendWidth = 0;
            }
            int titleHeight = Theme.Title.Height;
            int top = Theme.S(12);
            int badgeWidth = string.IsNullOrEmpty(badge) ? 0 : badgeKind == "none" ? Theme.Measure(badge, Theme.SmallStrong) + Theme.S(24) : Theme.StatusWidth(badge, badgeKind) + Theme.S(4);
            int titleRoom = right - left - (legendWidth > 0 ? legendWidth + Theme.S(24) : 0) - (badgeWidth > 0 ? badgeWidth + Theme.S(12) : 0);
            int titleWidth = Math.Min(Theme.Measure(title, Theme.Title) + Theme.S(2), Math.Max(Theme.S(60), titleRoom));
            Theme.DrawText(g, title, Theme.Title, new Rectangle(left, top, titleWidth, titleHeight), Theme.Text, TextFormatFlags.Left);
            if (badgeWidth > 0)
            {
                Rectangle cell = new Rectangle(left + titleWidth + Theme.S(12), top, badgeWidth, titleHeight);
                if (badgeKind == "none")
                {
                    int w = Theme.Measure(badge, Theme.SmallStrong) + Theme.S(20);
                    Rectangle pill = new Rectangle(cell.X, cell.Y + (cell.Height - Theme.S(22)) / 2, w, Theme.S(22));
                    Theme.FillRound(g, Theme.Chrome, pill, pill.Height / 2);
                    Theme.DrawRound(g, Theme.Border, pill, pill.Height / 2, 1f);
                    Theme.DrawText(g, badge, Theme.SmallStrong, pill, Theme.TextMuted, TextFormatFlags.HorizontalCenter);
                }
                else Theme.DrawStatus(g, cell, badge, badgeKind);
            }
            int detailTop = top + titleHeight + Theme.S(2);
            Rectangle detailBox = new Rectangle(left, detailTop, Math.Max(0, right - left - (legendWidth > 0 ? legendWidth + Theme.S(24) : 0)), Theme.Body.Height + Theme.S(4));
            Theme.DrawText(g, detail, Theme.Body, detailBox, Theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.PathEllipsis);
            if (legendWidth > 0)
            {
                int x = right - legendWidth;
                int y = top + (titleHeight - dot) / 2;
                SmoothingModeFill(g, Theme.LocalText, new Rectangle(x, y, dot, dot));
                x += dot + gap;
                int w = Theme.Measure(localText, Theme.Small);
                Theme.DrawText(g, localText, Theme.Small, new Rectangle(x, top, w + Theme.S(2), titleHeight), Theme.TextMuted, TextFormatFlags.Left);
                x += w + spacing;
                SmoothingModeFill(g, Theme.CloudText, new Rectangle(x, y, dot, dot));
                x += dot + gap;
                Theme.DrawText(g, cloudText, Theme.Small, new Rectangle(x, top, Theme.Measure(cloudText, Theme.Small) + Theme.S(2), titleHeight), Theme.TextMuted, TextFormatFlags.Left);
            }
        }

        static void SmoothingModeFill(Graphics g, Color color, Rectangle r)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush brush = new SolidBrush(color)) g.FillEllipse(brush, r);
            g.SmoothingMode = old;
        }
    }

    // Centered message shown when a list has nothing to show.
    class EmptyState : Control
    {
        char glyph = Glyphs.Folder;
        string title = "";
        string body = "";

        public EmptyState()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Window;
            Size = Theme.S(440, 170);
            Visible = false;
        }

        public void Set(char glyph, string title, string body)
        {
            this.glyph = glyph;
            this.title = title ?? "";
            this.body = body ?? "";
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            int icon = Theme.S(40);
            int y = Theme.S(8);
            Theme.DrawGlyph(g, glyph, icon, Theme.TextFaint, (Width - icon) / 2, y);
            y += icon + Theme.S(14);
            TextRenderer.DrawText(g, title, Theme.Heading, new Rectangle(0, y, Width, Theme.Heading.Height + Theme.S(4)), Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            y += Theme.Heading.Height + Theme.S(8);
            TextRenderer.DrawText(g, body, Theme.Body, new Rectangle(Theme.S(10), y, Width - Theme.S(20), Height - y), Theme.TextMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
        }
    }

    // Tree view with no horizontal scroll bar and Explorer styling.
    class NavTree : TreeView
    {
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= 0x8000;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.ExplorerTheme(Handle, "Explorer");
            Native.SendMessage(Handle, 0x112C, (IntPtr)0x0044, (IntPtr)0x0044);
        }
    }

    // Owner-drawn details list with flicker-free painting.
    class FileList : ListView
    {
        public FileList()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            IntPtr header = Native.SendMessage(Handle, 0x101F, IntPtr.Zero, IntPtr.Zero);
            if (header != IntPtr.Zero) Native.ExplorerTheme(header, "ItemsView");
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct HdItem
        {
            public int mask;
            public int cxy;
            public IntPtr pszText;
            public IntPtr hbm;
            public int cchTextMax;
            public int fmt;
            public IntPtr lParam;
            public int iImage;
            public int iOrder;
            public uint type;
            public IntPtr pvFilter;
            public uint state;
        }

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        static extern IntPtr SendHeader(IntPtr hwnd, int msg, IntPtr wParam, ref HdItem item);

        public void SetSortArrow(int column, bool ascending)
        {
            if (!IsHandleCreated) return;
            IntPtr header = Native.SendMessage(Handle, 0x101F, IntPtr.Zero, IntPtr.Zero);
            if (header == IntPtr.Zero) return;
            for (int i = 0; i < Columns.Count; i++)
            {
                HdItem item = new HdItem();
                item.mask = 0x0004;
                SendHeader(header, 0x120B, (IntPtr)i, ref item);
                item.fmt &= ~(0x0400 | 0x0200);
                if (i == column) item.fmt |= ascending ? 0x0400 : 0x0200;
                SendHeader(header, 0x120C, (IntPtr)i, ref item);
            }
        }
    }
}
