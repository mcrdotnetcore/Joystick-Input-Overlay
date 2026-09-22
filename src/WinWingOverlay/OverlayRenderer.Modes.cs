using System.Drawing.Drawing2D;

namespace WinWingOverlay;

/// <summary>
/// Collective mode, the radial mode dials, and the in-overlay settings page.
///
/// Every interactive thing drawn here also records a <see cref="HitRegion"/>, so the form
/// never has to duplicate layout maths to know what was clicked.
/// </summary>
internal sealed partial class OverlayRenderer
{
    private enum MenuRowKind { Slider, Toggle, Button, Colour, Band }

    private readonly record struct MenuRow(string Id, string Label, MenuRowKind Kind);

    /// <summary>
    /// The settings page, built fresh each time because the colour bands only appear when
    /// banding is switched on, and there is one row per band.
    /// </summary>
    private static List<MenuRow> BuildRows(OverlayConfig config)
    {
        var rows = new List<MenuRow>
        {
            new("opacity", "Everything", MenuRowKind.Slider),
            new("bgopacity", "Background", MenuRowKind.Slider),
            new("obs", "Show in capture list (OBS)", MenuRowKind.Toggle),
            new("buttons", "Button grid", MenuRowKind.Toggle),
            new("readouts", "Axis readouts", MenuRowKind.Toggle),
            new("bands", "Collective colour bands", MenuRowKind.Toggle),
            new("colour", "Default colour", MenuRowKind.Colour)
        };

        if (config.CollectiveBandsEnabled)
        {
            int count = config.CollectiveBands?.Count ?? 0;
            for (int i = 0; i < count; i++)
                rows.Add(new MenuRow($"band:{i}", "", MenuRowKind.Band));

            if (count < BandColours.Max)
                rows.Add(new MenuRow("band:add", "Add colour band", MenuRowKind.Button));
        }

        rows.Add(new MenuRow("lock", "Lock overlay", MenuRowKind.Button));
        rows.Add(new MenuRow("reset", "Reset position", MenuRowKind.Button));
        rows.Add(new MenuRow("rescan", "Rescan devices", MenuRowKind.Button));
        rows.Add(new MenuRow("config", "Open config folder", MenuRowKind.Button));
        rows.Add(new MenuRow("exit", "Exit overlay", MenuRowKind.Button));

        return rows;
    }

    /// <summary>Clickable regions from the most recent render. Empty while locked.</summary>
    public List<HitRegion> Hits { get; } = new();

    private Font? _bigFont;
    private float _bigFontPx = -1f;
    private Font? _dialFont;
    private float _dialFontPx = -1f;

    private Bitmap? _scratch;
    private Graphics? _measure;

    private Color _tintColour = Color.Empty;
    private SolidBrush? _tintSoft;
    private Pen? _tintPen;

    // The settings page keeps a fixed size regardless of how large the overlay is, so it stays
    // a sane shape on screen once the colour bands are listed on it.
    private readonly Font _menuFont = new("Segoe UI", 14f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _menuTitleFont = new("Segoe UI Semibold", 17f, FontStyle.Regular, GraphicsUnit.Pixel);

    /// <summary>A throwaway surface purely for text measurement outside a paint pass.</summary>
    private Graphics Measure()
    {
        if (_measure is null)
        {
            _scratch = new Bitmap(1, 1);
            _measure = Graphics.FromImage(_scratch);
        }
        return _measure;
    }

    private static Color ParseColour(string? text, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;

        var named = BandColours.Value(text);
        if (named != Color.Empty) return named;

        try { return ColorTranslator.FromHtml(text.Trim()); }
        catch { return fallback; }
    }

    /// <summary>Throttle-like axes, in the order Collective view falls back through.</summary>
    private static readonly ushort[] CollectiveFallbacks =
    {
        Native.USAGE_SLIDER, Native.USAGE_Z, Native.USAGE_RZ, Native.USAGE_DIAL, Native.USAGE_WHEEL
    };

    /// <summary>
    /// The axis Collective view reads: the configured one when the device has it, otherwise the
    /// first throttle-like axis it does report. That way the view works on whatever is plugged
    /// in rather than showing "--" because the config names an axis this device lacks.
    /// </summary>
    private static ushort CollectiveUsage(OverlayConfig config, JoystickDevice? device)
    {
        ushort configured = BarUsageFor(
            string.IsNullOrWhiteSpace(config.CollectiveAxis) ? "Slider" : config.CollectiveAxis.Trim());

        if (device is null) return configured;

        bool Has(ushort usage) => usage != 0 &&
            device.Axes.Any(a => a.Usage == usage && a.UsagePage == Native.USAGE_PAGE_GENERIC);

        if (Has(configured)) return configured;

        foreach (ushort usage in CollectiveFallbacks)
            if (Has(usage)) return usage;

        return configured;
    }

    /// <summary>
    /// Colour for a collective reading, or null when banding is off so the caller keeps its own
    /// default. This is what makes the same colour appear on the number in Collective view and
    /// on the collective bar in the other views.
    /// </summary>
    public Color? BandTint(OverlayConfig config, double percent)
    {
        if (!config.CollectiveBandsEnabled) return null;

        if (config.CollectiveBands is { Count: > 0 })
        {
            foreach (var band in config.CollectiveBands)
            {
                int low = Math.Min(band.Min, band.Max);
                int high = Math.Max(band.Min, band.Max);
                if (percent >= low && percent <= high) return ParseColour(band.Colour, Color.White);
            }
        }

        return ParseColour(config.CollectiveText, Color.White);
    }

    /// <summary>Cached fill and line for a banded gauge. Only one bar is ever tinted at a time.</summary>
    private (SolidBrush Soft, Pen Line) Tint(Color colour)
    {
        if (_tintSoft is null || _tintPen is null || _tintColour != colour)
        {
            _tintSoft?.Dispose();
            _tintPen?.Dispose();
            _tintColour = colour;
            _tintSoft = new SolidBrush(Color.FromArgb(52, colour));
            _tintPen = new Pen(colour, 1.6f);
        }
        return (_tintSoft, _tintPen);
    }

    // ---- Collective ------------------------------------------------------

    private Font CollectiveFontPx(float px)
    {
        px = Math.Clamp(px, 10f, 400f);
        if (_bigFont is null || Math.Abs(_bigFontPx - px) > 0.5f)
        {
            _bigFont?.Dispose();
            _bigFont = new Font("Segoe UI Semibold", px, FontStyle.Regular, GraphicsUnit.Pixel);
            _bigFontPx = px;
        }
        return _bigFont;
    }

    /// <summary>Number size for a Collective view that has never been resized by hand.</summary>
    private static float DefaultCollectivePx(Size basis) =>
        Math.Clamp(Math.Min(basis.Height * 0.30f, basis.Width * 0.20f), 22f, 190f);

    /// <summary>
    /// The widest string Collective can produce. Sizing against it, rather than the current
    /// value, keeps the number stable as the axis moves and when the % symbol is toggled off.
    /// </summary>
    private const string WidestCollective = "-100%";

    /// <summary>Collective mode is sized to the number, but never narrower than the dial row.</summary>
    public Size MeasureCollective(Size basis, OverlayConfig config)
    {
        EnsureFonts(basis.Height);

        float pad = Math.Max(6f, basis.Height * 0.025f);
        var font = CollectiveFontPx(DefaultCollectivePx(basis));

        float textW = MeasureText(Measure(), WidestCollective, font);
        float minW = DialsWidth(basis) + pad * 2f;

        return new Size(
            (int)Math.Ceiling(Math.Max(textW + pad * 2.5f, minW)),
            (int)Math.Ceiling(font.Height + pad * 1.6f));
    }

    private void DrawCollective(Graphics g, Rectangle client, JoystickDevice? device,
        OverlayConfig config, Size basis, bool locked, double backgroundAlpha, float bottomInset)
    {
        var navy = ParseColour(config.CollectiveBackground, Color.FromArgb(16, 30, 58));
        int alpha = (int)Math.Round(Math.Clamp(backgroundAlpha, 0.0, 1.0) * 255);

        using (var back = new SolidBrush(Color.FromArgb(alpha, navy)))
            g.FillRectangle(back, client);

        using (var border = new Pen(locked ? Color.FromArgb(46, 66, 104) : Accent, locked ? 1f : 2f))
            g.DrawRectangle(border, 0, 0, client.Width - 1, client.Height - 1);

        ushort usage = CollectiveUsage(config, device);

        string text = "--";
        Color colour = ParseColour(config.CollectiveText, Color.White);

        if (device is not null && usage != 0 && device.Axes.Any(a => a.Usage == usage))
        {
            bool centred = CentreOriginSet(config).Contains(AxisInfo.NameFor(usage));
            double value = Value(device.State, usage, centred ? 0.5 : 0.0, config);

            text = Readout(value, centred);

            // The corner button only drops the symbol; the value stays a percentage.
            if (!config.CollectiveShowPercent) text = text.TrimEnd('%');

            colour = BandTint(config, value * 100.0) ?? colour;
        }

        var area = new RectangleF(client.X, client.Y, client.Width,
            Math.Max(10f, client.Height - bottomInset));

        // Fit the number to the box on both axes, against the text actually being shown, so a
        // small window still gets a large number. Short readings therefore draw bigger than
        // long ones.
        float marginX = Math.Max(2f, area.Width * 0.05f);
        float marginY = Math.Max(2f, area.Height * 0.07f);

        var reference = g.MeasureString(text, _fontSmall, PointF.Empty, StringFormat.GenericTypographic);
        float px = area.Height * 0.9f;
        if (reference.Width > 0.1f && reference.Height > 0.1f)
        {
            px = _fontSmall.Size * Math.Min(
                (area.Width - marginX) / reference.Width,
                (area.Height - marginY) / reference.Height);
        }

        var font = CollectiveFontPx(MathF.Round(px));

        // Outline first, fill second: a thin dark stroke keeps the number legible against a
        // bright cockpit or a translucent background.
        using var path = new GraphicsPath();
        path.AddString(text, font.FontFamily, (int)font.Style, font.Size, area, _centreTight);

        using (var outline = new Pen(Color.FromArgb(215, 0, 0, 0), Math.Max(1.2f, font.Size * 0.05f))
               { LineJoin = LineJoin.Round })
            g.DrawPath(outline, path);

        using (var fill = new SolidBrush(colour))
            g.FillPath(fill, path);

        if (!locked) DrawPercentToggle(g, client, config, basis);
    }

    /// <summary>
    /// The tiny corner button that hides or shows the F / M / C bar. It stays visible whenever
    /// the overlay is unlocked, otherwise there would be no way to bring the bar back.
    /// </summary>
    private void DrawChromeToggle(Graphics g, Rectangle client, OverlayConfig config, Size basis)
    {
        float d = DialDiameter(basis) * 0.6f;
        float inset = Math.Max(3f, basis.Height * 0.012f);
        var rect = new RectangleF(client.X + inset, client.Y + inset, d, d);

        g.FillEllipse(_panel, rect);
        g.DrawEllipse(config.ShowDials ? _accentPen : _gridPen, rect);

        if (config.ShowDials)
        {
            float r = d * 0.24f;
            g.FillEllipse(_accent, rect.X + d / 2f - r, rect.Y + d / 2f - r, r * 2, r * 2);
        }

        Hits.Add(new HitRegion("dials", rect, HitKind.Button));
    }

    /// <summary>The small corner button that shows or hides the % symbol.</summary>
    private void DrawPercentToggle(Graphics g, Rectangle client, OverlayConfig config, Size basis)
    {
        float d = DialDiameter(basis) * 0.72f;
        float inset = Math.Max(4f, basis.Height * 0.014f);
        var rect = new RectangleF(client.Right - inset - d, client.Y + inset, d, d);

        bool on = config.CollectiveShowPercent;
        g.FillEllipse(on ? _accent : _panel, rect);
        g.DrawEllipse(on ? _accentPen : _gridPen, rect);

        using (var glyph = new SolidBrush(on ? Background : Text))
            g.DrawString("%", DialFont(basis), glyph, rect, _centreTight);

        Hits.Add(new HitRegion("collective:percent", rect, HitKind.Button));
    }

    // ---- Radial mode dials ----------------------------------------------

    private static float DialDiameter(Size basis) => Math.Clamp(basis.Height * 0.055f, 18f, 30f);

    private static float DialGap(Size basis) => DialDiameter(basis) * 0.4f;

    private static float DialsWidth(Size basis) => 4f * DialDiameter(basis) + 3f * DialGap(basis);

    /// <summary>
    /// Height the window gains while unlocked so the dials get their own strip along the
    /// bottom instead of covering a gauge. Zero while locked, when no dials are drawn.
    /// </summary>
    public float DialStripHeight(Size basis) =>
        DialDiameter(basis) + Math.Max(6f, basis.Height * 0.025f) * 1.6f;

    private Font DialFont(Size basis)
    {
        float px = Math.Max(MinFontPx, DialDiameter(basis) * 0.46f);
        if (_dialFont is null || Math.Abs(_dialFontPx - px) > 0.3f)
        {
            _dialFont?.Dispose();
            _dialFont = new Font("Segoe UI Semibold", px, FontStyle.Regular, GraphicsUnit.Pixel);
            _dialFontPx = px;
        }
        return _dialFont;
    }

    /// <summary>
    /// The mode selector: one round dial per mode plus a menu dial, drawn bottom-right over
    /// whatever is behind them. Only ever shown while the overlay is unlocked.
    /// </summary>
    private void DrawDials(Graphics g, Rectangle client, ViewMode mode, bool menuOpen, Size basis)
    {
        float d = DialDiameter(basis);
        float gap = DialGap(basis);
        float pad = Math.Max(6f, basis.Height * 0.025f);
        float total = DialsWidth(basis);

        float x = client.Right - pad - total;
        float y = client.Bottom - pad - d;
        if (x < pad) x = pad;
        if (y < pad) y = pad;

        // A backdrop keeps the dials readable when they sit over a gauge.
        var tray = new RectangleF(x - gap * 0.7f, y - gap * 0.7f,
            total + gap * 1.4f, d + gap * 1.4f);
        using (var backdrop = new SolidBrush(Color.FromArgb(190, 10, 13, 18)))
        using (var path = RoundedRect(tray, d * 0.55f))
            g.FillPath(backdrop, path);

        (string Id, string Glyph, bool Active)[] dials =
        {
            ("mode:full", "F", !menuOpen && mode == ViewMode.Full),
            ("mode:minimal", "M", !menuOpen && mode == ViewMode.Minimal),
            ("mode:collective", "C", !menuOpen && mode == ViewMode.Collective),
            ("menu", "=", menuOpen)
        };

        var font = DialFont(basis);

        foreach (var (id, glyph, active) in dials)
        {
            var rect = new RectangleF(x, y, d, d);

            g.FillEllipse(active ? _accent : _panel, rect);
            g.DrawEllipse(active ? _accentPen : _gridPen, rect);

            using var text = new SolidBrush(active ? Background : Text);
            g.DrawString(glyph, font, text, rect, _centreTight);

            Hits.Add(new HitRegion(id, rect, HitKind.Button));
            x += d + gap;
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ---- Settings page ---------------------------------------------------

    private (float Pad, float RowH, float Gap, float TitleH, float LabelW, List<MenuRow> Rows, Size Size)
        MenuLayout(Size basis, OverlayConfig config)
    {
        var rows = BuildRows(config);

        float pad = 12f;
        float rowH = _menuFont.Height + 12f;
        float gap = 6f;
        float titleH = _menuTitleFont.Height + 6f;

        float labelW = 0f;
        var g = Measure();
        foreach (var row in rows)
            if (row.Kind is MenuRowKind.Slider or MenuRowKind.Toggle or MenuRowKind.Colour)
                labelW = Math.Max(labelW, MeasureText(g, row.Label, _menuFont));
        labelW += pad;

        float width = Math.Clamp(labelW + 170f + pad * 2f, 330f, 480f);
        float height = pad + titleH + rows.Count * (rowH + gap)
                       + DialDiameter(basis) + pad * 2f;

        return (pad, rowH, gap, titleH, labelW, rows,
            new Size((int)Math.Ceiling(width), (int)Math.Ceiling(height)));
    }

    public Size MeasureMenu(Size basis, OverlayConfig config) => MenuLayout(basis, config).Size;

    private void DrawMenuPage(Graphics g, Rectangle client, OverlayConfig config, Size basis, bool locked)
    {
        var (pad, rowH, gap, titleH, labelW, rows, _) = MenuLayout(basis, config);

        g.FillRectangle(_bg, client);
        using (var border = new Pen(locked ? Edge : Accent, locked ? 1f : 2f))
            g.DrawRectangle(border, 0, 0, client.Width - 1, client.Height - 1);

        g.DrawString("Overlay settings", _menuTitleFont, _text, pad, pad * 0.5f);

        float y = pad * 0.5f + titleH;
        float right = client.Width - pad;

        foreach (var row in rows)
        {
            var area = new RectangleF(pad, y, right - pad, rowH);

            switch (row.Kind)
            {
                case MenuRowKind.Slider:
                {
                    double value = row.Id == "opacity" ? config.Opacity : config.BackgroundOpacity;
                    DrawMenuSlider(g, area, row, labelW, value);
                    break;
                }

                case MenuRowKind.Toggle:
                {
                    bool on = row.Id switch
                    {
                        "obs" => config.ShowInWindowList,
                        "buttons" => config.ShowButtons,
                        "bands" => config.CollectiveBandsEnabled,
                        _ => config.ShowAxisReadouts
                    };
                    DrawMenuToggle(g, area, row, labelW, on);
                    break;
                }

                case MenuRowKind.Colour:
                    DrawMenuColour(g, area, labelW, config);
                    break;

                case MenuRowKind.Band:
                    DrawMenuBand(g, area, int.Parse(row.Id.AsSpan(5)), config);
                    break;

                default:
                    DrawMenuButton(g, area, row);
                    break;
            }

            y += rowH + gap;
        }
    }

    /// <summary>The four colour choices for readings that fall outside every band.</summary>
    private void DrawMenuColour(Graphics g, RectangleF area, float labelW, OverlayConfig config)
    {
        g.DrawString("Default colour", _menuFont, _textDim,
            new RectangleF(area.X, area.Y, labelW, area.Height), _leftTight);

        float d = area.Height * 0.56f;
        float gap = d * 0.5f;
        float x = area.Right - (BandColours.Names.Length * d + (BandColours.Names.Length - 1) * gap);

        foreach (string name in BandColours.Names)
        {
            var rect = new RectangleF(x, area.Y + (area.Height - d) / 2f, d, d);
            bool selected = BandColours.IsName(config.CollectiveText, name);

            using (var swatch = new SolidBrush(BandColours.Value(name)))
                g.FillEllipse(swatch, rect);

            g.DrawEllipse(selected ? _accentPen : _gridPen, rect);
            if (selected) g.DrawEllipse(_accentPen, RectangleF.Inflate(rect, 3f, 3f));

            Hits.Add(new HitRegion("colour:" + name, RectangleF.Inflate(rect, 3f, 3f), HitKind.Button));
            x += d + gap;
        }
    }

    /// <summary>One band: colour swatch, a stepper for each end of the range, and a remove button.</summary>
    private void DrawMenuBand(Graphics g, RectangleF area, int index, OverlayConfig config)
    {
        var bands = config.CollectiveBands;
        if (bands is null || index < 0 || index >= bands.Count) return;

        var band = bands[index];
        float h = area.Height;
        float swatch = h * 0.6f;
        float btn = h * 0.74f;
        float valueW = MeasureText(g, "100", _menuFont) + 10f;
        float gap = 4f;

        var swatchRect = new RectangleF(area.X, area.Y + (h - swatch) / 2f, swatch, swatch);
        using (var fill = new SolidBrush(ParseColour(band.Colour, Color.White)))
            g.FillEllipse(fill, swatchRect);
        g.DrawEllipse(_gridPen, swatchRect);
        Hits.Add(new HitRegion($"band:{index}:colour", swatchRect, HitKind.Button));

        float x = swatchRect.Right + gap * 2f;
        x = DrawStepper(g, x, area, btn, valueW, band.Min, $"band:{index}:min");

        g.DrawString("-", _menuFont, _textDim, new RectangleF(x, area.Y, gap * 3f, h), _centreTight);
        x += gap * 3f;

        x = DrawStepper(g, x, area, btn, valueW, band.Max, $"band:{index}:max");

        var remove = new RectangleF(area.Right - btn, area.Y + (h - btn) / 2f, btn, btn);
        SmallButton(g, remove, "x", $"band:{index}:del");
    }

    private float DrawStepper(Graphics g, float x, RectangleF area, float btn, float valueW,
        int value, string id)
    {
        float y = area.Y + (area.Height - btn) / 2f;

        SmallButton(g, new RectangleF(x, y, btn, btn), "-", id + ":-");
        x += btn;

        g.DrawString(value.ToString(), _menuFont, _text,
            new RectangleF(x, area.Y, valueW, area.Height), _centreTight);
        x += valueW;

        SmallButton(g, new RectangleF(x, y, btn, btn), "+", id + ":+");
        return x + btn;
    }

    private void SmallButton(Graphics g, RectangleF rect, string glyph, string id)
    {
        using (var path = RoundedRect(rect, rect.Height * 0.3f))
        {
            g.FillPath(_panel, path);
            g.DrawPath(_gridPen, path);
        }

        g.DrawString(glyph, _menuFont, _text, rect, _centreTight);
        Hits.Add(new HitRegion(id, rect, HitKind.Button));
    }

    private void DrawMenuSlider(Graphics g, RectangleF area, MenuRow row, float labelW, double value)
    {
        g.DrawString(row.Label, _menuFont, _textDim,
            new RectangleF(area.X, area.Y, labelW, area.Height), _leftTight);

        float knob = area.Height * 0.28f;
        float readoutW = MeasureText(g, "100%", _menuFont) + 10f;

        // Inset by the knob radius at both ends so a knob at 0 % or 100 % stays inside the row.
        var track = new RectangleF(area.X + labelW + knob, area.Y + area.Height * 0.35f,
            Math.Max(20f, area.Width - labelW - readoutW - knob * 2f), area.Height * 0.30f);

        g.FillRectangle(_panel, track);
        g.DrawRectangle(_edge, track.X, track.Y, track.Width, track.Height);

        float x = track.X + (float)(Math.Clamp(value, 0.0, 1.0) * track.Width);
        if (x > track.X + 1f)
            g.FillRectangle(_accentSoft, new RectangleF(track.X + 1, track.Y + 1, x - track.X - 1, track.Height - 1));

        g.FillEllipse(_accent, x - knob, track.Y + track.Height / 2f - knob, knob * 2, knob * 2);

        g.DrawString($"{value * 100:0}%", _menuFont, _text,
            new RectangleF(area.Right - readoutW, area.Y, readoutW, area.Height), _rightTight);

        // Grab anywhere on the row height, not just the thin track.
        Hits.Add(new HitRegion(row.Id, new RectangleF(track.X, area.Y, track.Width, area.Height),
            HitKind.Slider));
    }

    private void DrawMenuToggle(Graphics g, RectangleF area, MenuRow row, float labelW, bool on)
    {
        g.DrawString(row.Label, _menuFont, _textDim,
            new RectangleF(area.X, area.Y, labelW, area.Height), _leftTight);

        float h = area.Height * 0.52f;
        float w = h * 1.9f;
        var pill = new RectangleF(area.Right - w, area.Y + (area.Height - h) / 2f, w, h);

        using (var path = RoundedRect(pill, h / 2f))
        {
            g.FillPath(on ? _accent : _panel, path);
            g.DrawPath(on ? _accentPen : _gridPen, path);
        }

        float knob = h * 0.36f;
        float cx = on ? pill.Right - h / 2f : pill.X + h / 2f;
        using (var dot = new SolidBrush(on ? Background : TextDim))
            g.FillEllipse(dot, cx - knob, pill.Y + h / 2f - knob, knob * 2, knob * 2);

        Hits.Add(new HitRegion(row.Id, area, HitKind.Button));
    }

    private void DrawMenuButton(Graphics g, RectangleF area, MenuRow row)
    {
        using (var path = RoundedRect(area, area.Height * 0.25f))
        {
            g.FillPath(_panel, path);
            g.DrawPath(_gridPen, path);
        }

        g.DrawString(row.Label, _menuFont, _text, area, _centreTight);
        Hits.Add(new HitRegion(row.Id, area, HitKind.Button));
    }

    private void DisposeModeResources()
    {
        _bigFont?.Dispose();
        _dialFont?.Dispose();
        _menuFont.Dispose();
        _menuTitleFont.Dispose();
        _tintSoft?.Dispose();
        _tintPen?.Dispose();
        _measure?.Dispose();
        _scratch?.Dispose();
    }
}
