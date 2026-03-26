using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;

#pragma warning disable CA1416 // System.Drawing cross-platform in Rhino context

namespace ScriptNodePlugin
{
    /// <summary>Alien canvas UI — dark card, green accent, light grey text.</summary>
    public class AlienNodeAttributes : GH_ComponentAttributes
    {
        private const float HEADER_H = 28f;
        private const float ROW_H = 22f;
        private const float PARAM_H = 20f;
        private const float W_MIN = 160f;
        private const float CORNER_R = 10f;
        private const float DOT_R = 6f;
        private const float PAD = 10f;

        private static readonly Color ColAccent = Color.FromArgb(0, 200, 83);
        private static readonly Color ColHeaderBg = Color.FromArgb(26, 26, 26);
        private static readonly Color ColHeaderText = Color.FromArgb(220, 220, 220);
        private static readonly Color ColRow = Color.FromArgb(30, 30, 30);
        private static readonly Color ColRowText = Color.FromArgb(200, 200, 200);
        private static readonly Color ColIoPanel = Color.FromArgb(36, 36, 36);
        private static readonly Color ColIoText = Color.FromArgb(190, 190, 190);
        private static readonly Color ColOutline = Color.FromArgb(45, 45, 45);
        private static readonly Color ColDotGreen = Color.FromArgb(0, 200, 83);
        private static readonly Color ColDotAmber = Color.FromArgb(224, 160, 32);
        private static readonly Color ColDotRed = Color.FromArgb(220, 60, 60);

        private static readonly string FontFamily = GetFontFamily();

        private RectangleF _loadFileRect;

        private new AlienNodeComponent Owner => (AlienNodeComponent)base.Owner;

        public AlienNodeAttributes(AlienNodeComponent owner) : base(owner) { }

        protected override void Layout()
        {
            var comp = Owner;
            int inputCount = Math.Max(0, comp.Params.Input.Count - 1);
            int outputCount = comp.Params.Output.Count;
            int rowCount = Math.Max(inputCount, outputCount);
            bool hasIO = rowCount > 0;

            float w = W_MIN;
            using (var bmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(bmp))
            using (var nameFont = new Font(FontFamily, 7.5f))
            {
                foreach (var p in comp.Params.Input.Skip(1))
                    w = Math.Max(w, g.MeasureString(p.Name, nameFont).Width * 2 + PAD * 4 + 20);
                foreach (var p in comp.Params.Output)
                    w = Math.Max(w, g.MeasureString(p.Name, nameFont).Width * 2 + PAD * 4 + 20);
            }
            w = Math.Max(w, W_MIN);

            float h = HEADER_H + ROW_H + ROW_H + (hasIO ? rowCount * PARAM_H + 2 : 0);
            float x = Pivot.X - w / 2f;
            float y = Pivot.Y - h / 2f;
            Bounds = new RectangleF(x, y, w, h);

            float loadY = y + HEADER_H + ROW_H;
            _loadFileRect = new RectangleF(x, loadY, w, ROW_H);

            float ioTop = y + HEADER_H + ROW_H * 2;
            float halfW = w / 2f;

            for (int i = 0; i < inputCount; i++)
            {
                var p = comp.Params.Input[i + 1];
                if (p.Attributes == null) p.CreateAttributes();
                float py = ioTop + i * PARAM_H;
                p.Attributes.Bounds = new RectangleF(x, py, halfW, PARAM_H);
                p.Attributes.Pivot = new PointF(x + 4f, py + PARAM_H / 2f);
            }

            for (int i = 0; i < outputCount; i++)
            {
                var p = comp.Params.Output[i];
                if (p.Attributes == null) p.CreateAttributes();
                float py = ioTop + i * PARAM_H;
                p.Attributes.Bounds = new RectangleF(x + halfW, py, halfW, PARAM_H);
                p.Attributes.Pivot = new PointF(x + w - 4f, py + PARAM_H / 2f);
            }
        }

        protected override void Render(GH_Canvas canvas, Graphics g, GH_CanvasChannel channel)
        {
            if (channel == GH_CanvasChannel.Wires)
            {
                base.Render(canvas, g, channel);
                return;
            }

            if (channel != GH_CanvasChannel.Objects) return;

            var comp = Owner;
            var bounds = Bounds;
            float x = bounds.X, y = bounds.Y, w = bounds.Width;

            g.SmoothingMode = SmoothingMode.AntiAlias;

            using var outlinePath = RoundedRect(bounds, CORNER_R);
            using (var shadowBrush = new SolidBrush(Color.FromArgb(50, 0, 0, 0)))
            {
                var shadowRect = new RectangleF(bounds.X + 2, bounds.Y + 3, bounds.Width, bounds.Height);
                using var shadowPath = RoundedRect(shadowRect, CORNER_R);
                g.FillPath(shadowBrush, shadowPath);
            }
            using (var bgBrush = new SolidBrush(ColRow))
                g.FillPath(bgBrush, outlinePath);
            using (var accentPen = new Pen(ColAccent, 2f))
                g.DrawLine(accentPen, x + 2, y + 4, x + 2, y + bounds.Height - 4);
            using (var pen = new Pen(ColOutline, 1.2f))
                g.DrawPath(pen, outlinePath);

            float headerH = HEADER_H;
            using var headerPath = RoundedTop(new RectangleF(x, y, w, headerH), CORNER_R);
            using (var hBrush = new SolidBrush(ColHeaderBg))
                g.FillPath(hBrush, headerPath);

            string title = string.IsNullOrEmpty(comp.ScriptPath)
                ? "ALIEN"
                : Path.GetFileNameWithoutExtension(comp.ScriptPath);
            using var headerFont = new Font(FontFamily, 9f, FontStyle.Bold);
            using var headerBrush = new SolidBrush(ColHeaderText);
            var headerTextRect = new RectangleF(x + PAD, y, w - PAD * 2, headerH);
            var headerFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(title, headerFont, headerBrush, headerTextRect, headerFormat);

            float mcpY = y + headerH;
            DrawStatusRow(g, x, mcpY, w, "MCP",
                McpServer.Instance.IsRunning ? ColDotGreen : ColDotRed);

            float loadY = mcpY + ROW_H;
            Color loadDot;
            if (string.IsNullOrEmpty(comp.ScriptPath) || !File.Exists(comp.ScriptPath))
                loadDot = ColDotRed;
            else if (comp.IsReloading)
                loadDot = ColDotAmber;
            else
                loadDot = ColDotGreen;
            DrawStatusRow(g, x, loadY, w, "Load file", loadDot);

            int inputCount = Math.Max(0, comp.Params.Input.Count - 1);
            int outputCount = comp.Params.Output.Count;
            int rowCount = Math.Max(inputCount, outputCount);
            bool hasIO = rowCount > 0;

            if (hasIO)
            {
                float ioTop = loadY + ROW_H;
                float halfW = w / 2f;
                float ioH = rowCount * PARAM_H;

                var ioRect = new RectangleF(x, ioTop, w, ioH);
                using var ioPath = RoundedBottom(ioRect, CORNER_R);
                using (var ioBrush = new SolidBrush(ColIoPanel))
                    g.FillPath(ioBrush, ioPath);

                using var divPen = new Pen(Color.FromArgb(40, 255, 255, 255), 0.75f);
                g.DrawLine(divPen, x + halfW, ioTop + 2, x + halfW, ioTop + ioH - 2);

                using var paramFont = new Font(FontFamily, 7.5f);
                using var paramBrush = new SolidBrush(ColIoText);
                var leftFmt = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
                var rightFmt = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };

                for (int i = 0; i < rowCount; i++)
                {
                    float rowY = ioTop + i * PARAM_H;
                    float rowCY = rowY + PARAM_H / 2f;

                    if (i % 2 == 1)
                    {
                        using var tintBrush = new SolidBrush(Color.FromArgb(12, 255, 255, 255));
                        g.FillRectangle(tintBrush, x, rowY, w, PARAM_H);
                    }

                    if (i < inputCount)
                    {
                        var p = comp.Params.Input[i + 1];
                        bool wired = p.SourceCount > 0;
                        using var dotB = new SolidBrush(wired ? ColDotGreen : Color.FromArgb(80, 80, 80));
                        g.FillEllipse(dotB, x + 6f, rowCY - 3f, 6f, 6f);
                        var labelRect = new RectangleF(x + 15f, rowY, halfW - 18f, PARAM_H);
                        g.DrawString(p.Name, paramFont, paramBrush, labelRect, leftFmt);
                    }

                    if (i < outputCount)
                    {
                        var p = comp.Params.Output[i];
                        var labelRect = new RectangleF(x + halfW + 2f, rowY, halfW - 14f, PARAM_H);
                        g.DrawString(p.Name, paramFont, paramBrush, labelRect, rightFmt);
                    }
                }
            }

            if (comp.Attributes.Selected)
            {
                using var selPen = new Pen(Color.FromArgb(180, ColAccent), 2f);
                using var selPath = RoundedRect(new RectangleF(x - 1, y - 1, w + 2, bounds.Height + 2), CORNER_R + 1);
                g.DrawPath(selPen, selPath);
            }

            if (comp.RuntimeMessageLevel == GH_RuntimeMessageLevel.Error)
            {
                using var errPen = new Pen(Color.FromArgb(200, 255, 60, 60), 2f);
                using var errPath = RoundedRect(new RectangleF(x - 1, y - 1, w + 2, bounds.Height + 2), CORNER_R + 1);
                g.DrawPath(errPen, errPath);
            }

            g.SmoothingMode = SmoothingMode.Default;
        }

        public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (e.Button == MouseButtons.Left && _loadFileRect.Contains(e.CanvasLocation))
            {
                var dlg = new Eto.Forms.OpenFileDialog
                {
                    Title = "Select Python Script",
                };
                dlg.Filters.Add(new Eto.Forms.FileFilter("Python files", ".py"));
                dlg.Filters.Add(new Eto.Forms.FileFilter("All files", ".*"));

                if (!string.IsNullOrEmpty(Owner.ScriptPath))
                    dlg.Directory = new Uri(Path.GetDirectoryName(Owner.ScriptPath));

                if (dlg.ShowDialog(Rhino.UI.RhinoEtoApp.MainWindow) == Eto.Forms.DialogResult.Ok)
                    Owner.SetScriptPath(dlg.FileName);

                return GH_ObjectResponse.Handled;
            }
            return base.RespondToMouseDown(sender, e);
        }

        public override GH_ObjectResponse RespondToMouseDoubleClick(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            Owner.OpenBrowserEditor();
            return GH_ObjectResponse.Handled;
        }

        private static void DrawStatusRow(Graphics g, float x, float y, float w, string label, Color dotColor)
        {
            using var sepPen = new Pen(Color.FromArgb(40, 255, 255, 255), 0.75f);
            g.DrawLine(sepPen, x + 4, y, x + w - 4, y);

            using var font = new Font(FontFamily, 7.5f);
            using var brush = new SolidBrush(ColRowText);
            var textRect = new RectangleF(x + PAD, y, w - PAD * 2 - DOT_R * 2 - 8, ROW_H);
            var fmt = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
            g.DrawString(label, font, brush, textRect, fmt);

            float dotX = x + w - PAD - DOT_R * 2;
            float dotY = y + (ROW_H - DOT_R * 2) / 2f;
            using var dotBrush = new SolidBrush(dotColor);
            using var dotPen = new Pen(Color.FromArgb(60, 0, 0, 0), 0.75f);
            g.FillEllipse(dotBrush, dotX, dotY, DOT_R * 2, DOT_R * 2);
            g.DrawEllipse(dotPen, dotX, dotY, DOT_R * 2, DOT_R * 2);
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static GraphicsPath RoundedTop(RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
            path.CloseFigure();
            return path;
        }

        private static GraphicsPath RoundedBottom(RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddLine(r.X, r.Y, r.Right, r.Y);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static string GetFontFamily()
        {
            if (Environment.OSVersion.Platform == PlatformID.Unix ||
                Environment.OSVersion.Platform == PlatformID.MacOSX)
                return "Helvetica Neue";
            return "Segoe UI";
        }
    }
}
