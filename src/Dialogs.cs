using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Media;
using System.Windows.Forms;

namespace DriveFolderSync
{
    public enum MessageKind
    {
        Info,
        Question,
        Warning,
        Error,
        Success
    }

    static class Dialogs
    {
        public static void Info(IWin32Window owner, string heading, string body)
        {
            Show(owner, heading, body, null, "OK", null, MessageKind.Info);
        }

        public static void Success(IWin32Window owner, string heading, string body)
        {
            Show(owner, heading, body, null, "OK", null, MessageKind.Success);
        }

        public static void Error(IWin32Window owner, string heading, string body)
        {
            Show(owner, heading, body, null, "OK", null, MessageKind.Error);
        }

        public static bool Confirm(IWin32Window owner, string heading, string body, IList<string> details, string action, MessageKind kind)
        {
            return Show(owner, heading, body, details, action, "Cancel", kind) == DialogResult.OK;
        }

        static DialogResult Show(IWin32Window owner, string heading, string body, IList<string> details, string primary, string secondary, MessageKind kind)
        {
            using (MessageDialog dialog = new MessageDialog(heading, body, details, primary, secondary, kind))
            {
                if (owner == null)
                {
                    dialog.StartPosition = FormStartPosition.CenterScreen;
                    dialog.ShowInTaskbar = true;
                    dialog.ShowIcon = true;
                }
                return dialog.ShowDialog(owner);
            }
        }
    }

    // A message box with a heading, body, optional list of details, and up to two buttons.
    class MessageDialog : Form
    {
        readonly MessageKind kind;
        readonly int pad = Theme.S(24);
        readonly int badge = Theme.S(32);

        public MessageDialog(string heading, string body, IList<string> details, string primaryText, string secondaryText, MessageKind kind)
        {
            Theme.Init();
            this.kind = kind;
            Text = "Drive Folder Sync";
            Icon = Theme.AppIcon;
            ShowIcon = false;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.Body;
            BackColor = Theme.Window;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

            int width = Theme.S(500);
            int left = pad + badge + Theme.S(16);
            int textWidth = width - left - pad;
            int y = pad + Theme.S(4);

            Label head = MakeLabel(heading, Theme.Heading, Theme.Text, textWidth);
            head.Location = new Point(left, y);
            Controls.Add(head);
            y += head.Height;

            if (!string.IsNullOrEmpty(body))
            {
                Label text = MakeLabel(body, Theme.Body, Theme.TextMuted, textWidth);
                text.Location = new Point(left, y + Theme.S(8));
                Controls.Add(text);
                y += Theme.S(8) + text.Height;
            }

            if (details != null && details.Count > 0)
            {
                Panel frame = new Panel();
                frame.BackColor = Theme.Window;
                int lineHeight = Theme.Body.Height + Theme.S(2);
                int listHeight = Math.Min(details.Count, 7) * lineHeight;
                frame.SetBounds(left, y + Theme.S(14), textWidth, listHeight + Theme.S(20));
                frame.Paint += new PaintEventHandler(delegate(object sender, PaintEventArgs e)
                {
                    Theme.FillRound(e.Graphics, Theme.Chrome, new Rectangle(0, 0, frame.Width, frame.Height), Theme.S(6));
                    Theme.DrawRound(e.Graphics, Theme.Border, new Rectangle(0, 0, frame.Width, frame.Height), Theme.S(6), 1f);
                });
                TextBox list = new TextBox();
                list.Multiline = true;
                list.ReadOnly = true;
                list.TabStop = false;
                list.WordWrap = false;
                list.BorderStyle = BorderStyle.None;
                list.BackColor = Theme.Chrome;
                list.ForeColor = Theme.Text;
                list.Font = Theme.Body;
                list.ScrollBars = details.Count > 7 ? ScrollBars.Vertical : ScrollBars.None;
                list.Text = string.Join("\r\n", new List<string>(details).ToArray());
                list.SetBounds(Theme.S(12), Theme.S(10), frame.Width - Theme.S(18), listHeight);
                frame.Controls.Add(list);
                Controls.Add(frame);
                y += Theme.S(14) + frame.Height;
            }

            y = Math.Max(y, pad + badge) + pad + Theme.S(4);

            Panel footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = Theme.S(64);
            footer.BackColor = Theme.Chrome;
            footer.Paint += new PaintEventHandler(delegate(object sender, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            });
            FlatButton primary = new FlatButton(primaryText ?? "OK", true);
            primary.DialogResult = DialogResult.OK;
            int x = width - pad - primary.Width;
            primary.Location = new Point(x, (footer.Height - primary.Height) / 2);
            footer.Controls.Add(primary);
            AcceptButton = primary;
            CancelButton = primary;
            if (!string.IsNullOrEmpty(secondaryText))
            {
                FlatButton secondary = new FlatButton(secondaryText, false);
                secondary.DialogResult = DialogResult.Cancel;
                secondary.Location = new Point(x - Theme.S(8) - secondary.Width, primary.Top);
                footer.Controls.Add(secondary);
                CancelButton = secondary;
            }
            Controls.Add(footer);
            ClientSize = new Size(width, y + footer.Height);
            ActiveControl = primary;
        }

        static Label MakeLabel(string text, Font font, Color color, int width)
        {
            Label label = new Label();
            label.Font = font;
            label.ForeColor = color;
            label.BackColor = Theme.Window;
            label.UseMnemonic = false;
            label.AutoSize = false;
            label.Text = text ?? "";
            Size size = TextRenderer.MeasureText(label.Text, font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            label.Size = new Size(width, size.Height + Theme.S(2));
            return label;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (kind == MessageKind.Error) SystemSounds.Hand.Play();
            else if (kind == MessageKind.Warning) SystemSounds.Exclamation.Play();
            else if (kind == MessageKind.Question) SystemSounds.Question.Play();
            else SystemSounds.Asterisk.Play();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Color color = Theme.Accent;
            if (kind == MessageKind.Warning) color = Color.FromArgb(214, 136, 0);
            else if (kind == MessageKind.Error) color = Theme.CloudText;
            else if (kind == MessageKind.Success) color = Theme.Success;
            Rectangle circle = new Rectangle(pad, pad, badge, badge);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush brush = new SolidBrush(color)) g.FillEllipse(brush, circle);
            if (kind == MessageKind.Error) Theme.DrawGlyphCentered(g, Glyphs.Cancel, Theme.S(14), Color.White, circle);
            else if (kind == MessageKind.Success) Theme.DrawGlyphCentered(g, Glyphs.Check, Theme.S(16), Color.White, circle);
            else
            {
                string mark = kind == MessageKind.Warning ? "!" : kind == MessageKind.Question ? "?" : "i";
                using (Font font = new Font("Segoe UI", badge * 0.56f, FontStyle.Bold, GraphicsUnit.Pixel))
                    TextRenderer.DrawText(g, mark, font, circle, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }
}
