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
    /// <summary>
    /// Full custom card-style attributes for ScriptNode.
    ///
    /// Layout (top → bottom):
    ///   ┌─────────────────────────────┐  ← HEADER_H  (red, rounded top)
    ///   │        Script Name          │
    ///   ├─────────────────────────────┤  ← ROW_H     (MCP status)
    ///   │  MCP Status            [●]  │
    ///   ├─────────────────────────────┤  ← ROW_H     (load file)
    ///   │  Load file             [●]  │
    ///   ├──────────────┬──────────────┤  ← PARAM_H × n   (IO panel, optional)
    ///   │ ● input 1    │  output 1 ●  │
    ///   │ ● input 2    │  output 2 ●  │
    ///   └──────────────┴──────────────┘
    /// </summary>
    public class ScriptNodeAttributes : GH_ComponentAttributes
    {
        // ── Layout constants ──────────────────────────────────
        private const float HEADER_H = 28f;
        private const float ROW_H    = 22f;
        private const float PARAM_H  = 20f;
        private const float W_MIN    = 160f;
        private const float CORNER_R = 10f;
        private const float DOT_R    = 6f;
        private const float PAD      = 10f;

        // ── Colours ───────────────────────────────────────────
        private static readonly Color ColHeader     = Color.FromArgb(204, 34, 34);
        private static readonly Color ColHeaderText = Color.White;
        private static readonly Color ColRow        = Color.FromArgb(58, 58, 58);
        private static readonly Color ColRowText    = Color.FromArgb(224, 224, 224);
        private static readonly Color ColIoPanel    = Color.FromArgb(72, 72, 72);
        private static readonly Color ColIoText     = Color.FromArgb(200, 200, 200);
        private static readonly Color ColOutline    = Color.FromArgb(30, 30, 30);
        private static readonly Color ColDotGreen   = Color.FromArgb(80, 200, 120);
        private static readonly Color ColDotAmber   = Color.FromArgb(224, 160, 32);
        private static readonly Color ColDotRed     = Color.FromArgb(220, 60, 60);

        // ── Cached hit-test rects (in canvas-space) ───────────
        private RectangleF _loadFileRect;

        private ScriptNodeComponent Owner => (ScriptNodeComponent)base.Owner;

        public ScriptNodeAttributes(ScriptNodeComponent owner) : base(owner) { }

        // ── Layout ────────────────────────────────────────────
        protected override void Layout()
        {
            var comp = Owner;

            // Count real IO (skip script_path at index 0)
            int inputCount  = Math.Max(0, comp.Params.Input.Count - 1);
            int outputCount = comp.Params.Output.Count;
            int rowCount    = Math.Max(inputCount, outputCount);
            bool hasIO      = rowCount > 0;

            // Compute width from longest param name
            float w = W_MIN;
            using (var g = Graphics.FromHwnd(IntPtr.Zero))
            using (var nameFont = new Font("Segoe UI", 7.5f))
            {
                foreach (var p in comp.Params.Input.Skip(1))
                    w = Math.Max(w, g.MeasureString(p.Name, nameFont).Width * 2 + PAD * 4 + 20);
                foreach (var p in comp.Params.Output)
                    w = Math.Max(w, g.MeasureString(p.Name, nameFont).Width * 2 + PAD * 4 + 20);
            }
            w = Math.Max(w, W_MIN);

            // Total height
            float h = HEADER_H + ROW_H + ROW_H + (hasIO ? rowCount * PARAM_H + 2 : 0);

            // Anchor to pivot
            float x = Pivot.X - w / 2f;
            float y = Pivot.Y - h / 2f;
            Bounds = new RectangleF(x, y, w, h);

            // Cache load-file click rect
            float loadY = y + HEADER_H + ROW_H;
            _loadFileRect = new RectangleF(x, loadY, w, ROW_H);

            // ── Lay out param attributes ──────────────────────
            float ioTop = y + HEADER_H + ROW_H * 2;
            float halfW = w / 2f;

            for (int i = 0; i < inputCount; i++)
            {
                var p = comp.Params.Input[i + 1];
                if (p.Attributes == null) p.CreateAttributes();
                float py = ioTop + i * PARAM_H;
                // Grip = left edge of component, centred in row
                p.Attributes.Bounds = new RectangleF(x, py, halfW, PARAM_H);
                p.Attributes.Pivot  = new PointF(x + 4f, py + PARAM_H / 2f);
            }

            for (int i = 0; i < outputCount; i++)
            {
                var p = comp.Params.Output[i];
                if (p.Attributes == null) p.CreateAttributes();
                float py = ioTop + i * PARAM_H;
                p.Attributes.Bounds = new RectangleF(x + halfW, py, halfW, PARAM_H);
                p.Attributes.Pivot  = new PointF(x + w - 4f, py + PARAM_H / 2f);
            }
        }

        // ── Render ────────────────────────────────────────────
        protected override void Render(GH_Canvas canvas, Graphics g, GH_CanvasChannel channel)
        {
            if (channel == GH_CanvasChannel.Wires)
            {
                base.Render(canvas, g, channel);
                return;
            }

            if (channel != GH_CanvasChannel.Objects) return;

            var comp   = Owner;
            var bounds = Bounds;
            float x = bounds.X, y = bounds.Y, w = bounds.Width;

            g.SmoothingMode = SmoothingMode.AntiAlias;

            // ── 1. Full outline background (rounded rect) ──────
            using var outlinePath = RoundedRect(bounds, CORNER_R);
            using (var shadowBrush = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
            {
                var shadowRect = new RectangleF(bounds.X + 2, bounds.Y + 3, bounds.Width, bounds.Height);
                using var shadowPath = RoundedRect(shadowRect, CORNER_R);
                g.FillPath(shadowBrush, shadowPath);
            }
            using (var bgBrush = new SolidBrush(ColRow))
                g.FillPath(bgBrush, outlinePath);
            using (var pen = new Pen(ColOutline, 1.2f))
                g.DrawPath(pen, outlinePath);

            // ── 2. Header (red, rounded top) ───────────────────
            float headerH = HEADER_H;
            var headerRect = new RectangleF(x, y, w, headerH + CORNER_R);
            using var headerPath = RoundedTop(new RectangleF(x, y, w, headerH), CORNER_R);
            using (var hBrush = new SolidBrush(ColHeader))
                g.FillPath(hBrush, headerPath);

            // Script name in header
            string scriptName = string.IsNullOrEmpty(comp.ScriptPath)
                ? "ScriptNode"
                : Path.GetFileNameWithoutExtension(comp.ScriptPath);
            using var headerFont = new Font("Segoe UI", 9f, FontStyle.Bold);
            using var headerBrush = new SolidBrush(ColHeaderText);
            var headerTextRect = new RectangleF(x + PAD, y, w - PAD * 2, headerH);
            var headerFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(scriptName, headerFont, headerBrush, headerTextRect, headerFormat);

            // ── 3. MCP Status row ──────────────────────────────
            float mcpY = y + headerH;
            DrawStatusRow(g, x, mcpY, w, "MCP Status",
                McpServer.Instance.IsRunning ? ColDotGreen : ColDotRed);

            // ── 4. Load File row ───────────────────────────────
            float loadY = mcpY + ROW_H;
            Color loadDot;
            if (string.IsNullOrEmpty(comp.ScriptPath) || !File.Exists(comp.ScriptPath))
                loadDot = ColDotRed;
            else if (comp.IsReloading)
                loadDot = ColDotAmber;
            else
                loadDot = ColDotGreen;
            DrawStatusRow(g, x, loadY, w, "Load file", loadDot);

            // ── 5. IO panel ────────────────────────────────────
            int inputCount  = Math.Max(0, comp.Params.Input.Count - 1);
            int outputCount = comp.Params.Output.Count;
            int rowCount    = Math.Max(inputCount, outputCount);
            bool hasIO      = rowCount > 0;

            if (hasIO)
            {
                float ioTop  = loadY + ROW_H;
                float halfW  = w / 2f;
                float ioH    = rowCount * PARAM_H;

                // IO background (rounded bottom)
                var ioRect = new RectangleF(x, ioTop, w, ioH);
                using var ioPath = RoundedBottom(ioRect, CORNER_R);
                using (var ioBrush = new SolidBrush(ColIoPanel))
                    g.FillPath(ioBrush, ioPath);

                // Divider line between inputs and outputs
                using var divPen = new Pen(Color.FromArgb(50, 255, 255, 255), 0.75f);
                g.DrawLine(divPen, x + halfW, ioTop + 2, x + halfW, ioTop + ioH - 2);

                // Draw input and output labels + grips
                using var paramFont = new Font("Segoe UI", 7.5f);
                using var paramBrush = new SolidBrush(ColIoText);
                var leftFmt  = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
                var rightFmt = new StringFormat { Alignment = StringAlignment.Far,  LineAlignment = StringAlignment.Center };

                for (int i = 0; i < rowCount; i++)
                {
                    float rowY  = ioTop + i * PARAM_H;
                    float rowCY = rowY + PARAM_H / 2f;

                    // Subtle alternating row tint
                    if (i % 2 == 1)
                    {
                        using var tintBrush = new SolidBrush(Color.FromArgb(15, 255, 255, 255));
                        g.FillRectangle(tintBrush, x, rowY, w, PARAM_H);
                    }

                    // Input grip + label
                    if (i < inputCount)
                    {
                        var p = comp.Params.Input[i + 1];
                        // Wire grip dot (left edge)
                        DrawGrip(g, x, rowCY, isInput: true);
                        // Label
                        var labelRect = new RectangleF(x + 13f, rowY, halfW - 16f, PARAM_H);
                        g.DrawString(p.Name, paramFont, paramBrush, labelRect, leftFmt);
                    }

                    // Output grip + label
                    if (i < outputCount)
                    {
                        var p = comp.Params.Output[i];
                        // Wire grip dot (right edge)
                        DrawGrip(g, x + w, rowCY, isInput: false);
                        // Label
                        var labelRect = new RectangleF(x + halfW + 2f, rowY, halfW - 14f, PARAM_H);
                        g.DrawString(p.Name, paramFont, paramBrush, labelRect, rightFmt);
                    }
                }
            }

            // ── 6. Selection / error highlight ─────────────────
            if (comp.Attributes.Selected)
            {
                using var selPen = new Pen(Color.FromArgb(220, 180, 80, 255), 2f);
                using var selPath = RoundedRect(new RectangleF(x - 1, y - 1, w + 2, bounds.Height + 2), CORNER_R + 1);
                g.DrawPath(selPen, selPath);
            }

            if (comp.RuntimeMessageLevel == GH_RuntimeMessageLevel.Error)
            {
                using var errPen = new Pen(Color.FromArgb(200, 255, 60, 60), 2f);
                using var errPath = RoundedRect(new RectangleF(x - 1, y - 1, w + 2, bounds.Height + 2), CORNER_R + 1);
                g.DrawPath(errPen, errPath);
            }
            else if (comp.RuntimeMessageLevel == GH_RuntimeMessageLevel.Warning)
            {
                using var wrnPen = new Pen(Color.FromArgb(200, 255, 200, 0), 2f);
                using var wrnPath = RoundedRect(new RectangleF(x - 1, y - 1, w + 2, bounds.Height + 2), CORNER_R + 1);
                g.DrawPath(wrnPen, wrnPath);
            }

            g.SmoothingMode = SmoothingMode.Default;
        }

        // ── Hit-test: Load File button click ──────────────────
        public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left
                && _loadFileRect.Contains(e.CanvasLocation))
            {
                using var dlg = new OpenFileDialog
                {
                    Title  = "Select Python Script",
                    Filter = "Python files (*.py)|*.py|All files (*.*)|*.*",
                };
                if (!string.IsNullOrEmpty(Owner.ScriptPath))
                    dlg.InitialDirectory = Path.GetDirectoryName(Owner.ScriptPath);

                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    Owner.SetScriptPath(dlg.FileName);
                }
                return GH_ObjectResponse.Handled;
            }
            return base.RespondToMouseDown(sender, e);
        }

        // ── Helpers ───────────────────────────────────────────
        private static void DrawStatusRow(Graphics g, float x, float y, float w, string label, Color dotColor)
        {
            // Row bg (already painted by full outline background — just draw the label)
            // Subtle top border line
            using var sepPen = new Pen(Color.FromArgb(40, 255, 255, 255), 0.75f);
            g.DrawLine(sepPen, x + 4, y, x + w - 4, y);

            // Label
            using var font  = new Font("Segoe UI", 7.5f);
            using var brush = new SolidBrush(Color.FromArgb(200, 200, 200));
            var textRect = new RectangleF(x + PAD, y, w - PAD * 2 - DOT_R * 2 - 8, ROW_H);
            var fmt = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
            g.DrawString(label, font, brush, textRect, fmt);

            // Status dot
            float dotX = x + w - PAD - DOT_R * 2;
            float dotY = y + (ROW_H - DOT_R * 2) / 2f;
            using var dotBrush = new SolidBrush(dotColor);
            using var dotPen   = new Pen(Color.FromArgb(60, 0, 0, 0), 0.75f);
            g.FillEllipse(dotBrush, dotX, dotY, DOT_R * 2, DOT_R * 2);
            g.DrawEllipse(dotPen,   dotX, dotY, DOT_R * 2, DOT_R * 2);
            // Specular highlight on dot
            using var hiLight = new SolidBrush(Color.FromArgb(80, 255, 255, 255));
            g.FillEllipse(hiLight, dotX + DOT_R * 0.2f, dotY + DOT_R * 0.15f, DOT_R * 0.6f, DOT_R * 0.5f);
        }

        private static void DrawGrip(Graphics g, float edgeX, float cy, bool isInput)
        {
            const float gr = 4.5f; // grip radius
            float gx = isInput ? edgeX - gr : edgeX - gr;
            float gy = cy - gr;

            using var gripBrush = new SolidBrush(Color.FromArgb(200, 200, 200));
            using var gripPen   = new Pen(Color.FromArgb(80, 0, 0, 0), 0.75f);
            g.FillEllipse(gripBrush, gx, gy, gr * 2, gr * 2);
            g.DrawEllipse(gripPen,   gx, gy, gr * 2, gr * 2);
        }

        // ── Path builders ─────────────────────────────────────
        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X,              r.Y,               d, d, 180, 90);
            path.AddArc(r.Right - d,      r.Y,               d, d, 270, 90);
            path.AddArc(r.Right - d,      r.Bottom - d,      d, d,   0, 90);
            path.AddArc(r.X,              r.Bottom - d,      d, d,  90, 90);
            path.CloseFigure();
            return path;
        }

        private static GraphicsPath RoundedTop(RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X,         r.Y, d, d, 180, 90);       // top-left
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);       // top-right
            path.AddLine(r.Right, r.Bottom, r.X, r.Bottom);      // bottom straight
            path.CloseFigure();
            return path;
        }

        private static GraphicsPath RoundedBottom(RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddLine(r.X, r.Y, r.Right, r.Y);                // top straight
            path.AddArc(r.Right - d, r.Bottom - d, d, d,   0, 90); // bottom-right
            path.AddArc(r.X,         r.Bottom - d, d, d,  90, 90); // bottom-left
            path.CloseFigure();
            return path;
        }
    }
}
