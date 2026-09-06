//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RemoteGameHub.App;

// Colours of the tray menu, matched to the Windows theme: WinForms draws a context menu the way
// Office 2007 did whatever the desktop looks like, and the stock check mark is a black bitmap.
internal sealed class MenuColors : ProfessionalColorTable
{
    private readonly bool _dark;

    internal MenuColors(bool dark)
    {
        _dark = dark;

        // A flat menu, no gradients: that is how Windows 11 draws its own, and an Office 2007
        // gradient gives away a foreign program at a glance.
        UseSystemColors = false;
    }

    private Color Surface => _dark ? Color.FromArgb(0x2B, 0x2B, 0x2B) : Color.FromArgb(0xF9, 0xF9, 0xF9);
    private Color Hover => _dark ? Color.FromArgb(0x3D, 0x3D, 0x3D) : Color.FromArgb(0xE9, 0xE9, 0xE9);
    private Color Edge => _dark ? Color.FromArgb(0x45, 0x45, 0x45) : Color.FromArgb(0xE0, 0xE0, 0xE0);

    public override Color ToolStripDropDownBackground => Surface;

    // The strip on the left where the check marks sit.
    public override Color ImageMarginGradientBegin => Surface;
    public override Color ImageMarginGradientMiddle => Surface;
    public override Color ImageMarginGradientEnd => Surface;

    public override Color MenuItemSelected => Hover;
    public override Color MenuItemSelectedGradientBegin => Hover;
    public override Color MenuItemSelectedGradientEnd => Hover;
    public override Color MenuItemPressedGradientBegin => Surface;
    public override Color MenuItemPressedGradientMiddle => Surface;
    public override Color MenuItemPressedGradientEnd => Surface;

    public override Color MenuItemBorder => Hover;
    public override Color MenuBorder => Edge;

    public override Color SeparatorDark => Edge;
    public override Color SeparatorLight => Edge;

    // Backdrop of a check mark. No reason to differ from hover: the mark is visible anyway.
    public override Color CheckBackground => Hover;
    public override Color CheckSelectedBackground => Hover;
    public override Color CheckPressedBackground => Hover;
}

// The menu renderer, differing from the stock one in the text colour and the check mark: the
// colour table sets neither, and WinForms puts the system black there whatever the theme.
internal sealed class MenuRenderer : ToolStripProfessionalRenderer
{
    private readonly bool _dark;

    internal MenuRenderer(bool dark) : base(new MenuColors(dark))
    {
        _dark = dark;
        RoundedEdges = false;
    }

    private Color Foreground => _dark ? Color.FromArgb(0xF0, 0xF0, 0xF0) : Color.FromArgb(0x1A, 0x1A, 0x1A);

    // A disabled item: the same colour at half strength.
    private Color Disabled => _dark ? Color.FromArgb(0x88, 0x88, 0x88) : Color.FromArgb(0x8A, 0x8A, 0x8A);

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Foreground : Disabled;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // Two lines at an angle, as Windows draws it, in the text colour — on both themes, so the
        // mark looks the same rather than a bitmap on one and a drawing on the other.
        var box = e.ImageRectangle;
        using var pen = new Pen(Foreground, 1.6f);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawLines(pen, new[]
        {
            new PointF(box.Left + box.Width * 0.28f, box.Top + box.Height * 0.52f),
            new PointF(box.Left + box.Width * 0.44f, box.Top + box.Height * 0.70f),
            new PointF(box.Left + box.Width * 0.74f, box.Top + box.Height * 0.32f),
        });
    }
}
