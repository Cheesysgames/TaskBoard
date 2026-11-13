// Board UI: owner-drawn cards (grid or list) tinted by each app's own logo, header controls, Settings window.
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;

namespace TaskBoard;

// Everything remembered between runs: %APPDATA%\TaskBoard\settings.json
class Settings
{
    public string View { get; set; } = "grid";
    public int Filter { get; set; }
    public bool HideNoise { get; set; } = true;
    public List<string> Hidden { get; set; } = new();  // always hidden
    public List<string> Shown { get; set; } = new();   // always shown, even if it looks like an updater
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskBoard");
    public static string FilePath => Path.Combine(Dir, "settings.json");

    public bool IsHidden(Item i) => !Shown.Contains(i.Id) && (Hidden.Contains(i.Id) || (HideNoise && i.Noise));

    public void SetHidden(Item i, bool hide)
    {
        Hidden.Remove(i.Id);
        Shown.Remove(i.Id);
        bool byDefault = HideNoise && i.Noise;
        if (hide != byDefault) (hide ? Hidden : Shown).Add(i.Id);
    }

    public static Settings Load()
    {
        Settings s = null;
        try { if (File.Exists(FilePath)) s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)); } catch { }
        s ??= new Settings();
        s.Hidden ??= new();
        s.Shown ??= new();
        // v1.2 kept hidden cards in hidden.txt
        var old = Path.Combine(Dir, "hidden.txt");
        try
        {
            if (File.Exists(old))
            {
                foreach (var id in File.ReadAllLines(old).Where(l => l.Trim() != "" && !s.Hidden.Contains(l))) s.Hidden.Add(id);
                File.Delete(old);
                s.Save();
            }
        }
        catch { }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

static class Draw
{
    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Fill(Graphics g, RectangleF r, float rad, Color c)
    {
        using var p = Round(r, rad);
        using var b = new SolidBrush(c);
        g.FillPath(b, p);
    }

    public static void Text(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags align = TextFormatFlags.Left) =>
        TextRenderer.DrawText(g, s ?? "", f, r, c, align | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

    public static int Measure(string s, Font f) => TextRenderer.MeasureText(s ?? "", f, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;

    public static Color Mix(Color a, Color b, double t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    public static void Smooth(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }
}

static class Icons
{
    static readonly Dictionary<string, Image> cache = new();
    static readonly Dictionary<string, Color> palette = new();
    static readonly Dictionary<string, Bitmap> backdrops = new();
    public static string LogoDir => Path.Combine(AppContext.BaseDirectory, "logos");
    public static readonly string[] Exts = { ".png", ".jpg", ".jpeg", ".ico" };

    public static void Forget(string id)
    {
        cache.Remove(id);
        palette.Remove(id);
        foreach (var k in backdrops.Keys.Where(k => k.StartsWith(id + "@")).ToList()) { backdrops[k].Dispose(); backdrops.Remove(k); }
    }

    public static Image Get(Item i)
    {
        if (cache.TryGetValue(i.Id, out var img)) return img;
        return cache[i.Id] = Custom(i.Name) ?? FromExe(i.IconExe) ?? Letter(i.Name);
    }

    // The logo's own colour: average of its colourful pixels, nudged to be vivid enough to tint with.
    public static Color Palette(Item i)
    {
        if (palette.TryGetValue(i.Id, out var c)) return c;
        return palette[i.Id] = Dominant(Get(i), i.Name);
    }

    public static Color Dominant(Image img, string name)
    {
        double r = 0, g = 0, b = 0, w = 0;
        using (var bmp = new Bitmap(24, 24))
        {
            using (var gr = Graphics.FromImage(bmp)) { gr.InterpolationMode = InterpolationMode.HighQualityBilinear; gr.DrawImage(img, 0, 0, 24, 24); }
            for (int y = 0; y < 24; y++)
                for (int x = 0; x < 24; x++)
                {
                    var p = bmp.GetPixel(x, y);
                    if (p.A < 100) continue;
                    float s = p.GetSaturation(), l = p.GetBrightness();
                    double wt = (l < 0.1 || l > 0.93) ? 0.03 : 0.1 + s * s * 4;
                    r += p.R * wt; g += p.G * wt; b += p.B * wt; w += wt;
                }
        }
        if (w < 0.5) return Hue(name);
        var avg = Color.FromArgb((int)(r / w), (int)(g / w), (int)(b / w));
        if (avg.GetSaturation() < 0.12) return Hue(name); // grey logo: use a stable colour from the name instead
        return FromHsl(avg.GetHue(), Math.Max(avg.GetSaturation(), 0.55f), Math.Clamp(avg.GetBrightness(), 0.45f, 0.6f));
    }

    static Color Hue(string name)
    {
        int h = 0;
        foreach (var ch in name ?? "") h = h * 31 + char.ToLowerInvariant(ch);
        return FromHsl(Math.Abs(h % 360), 0.55f, 0.55f);
    }

    static Color FromHsl(float h, float s, float l)
    {
        float c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = h < 60 ? (c, x, 0f) : h < 120 ? (x, c, 0f) : h < 180 ? (0f, c, x) : h < 240 ? (0f, x, c) : h < 300 ? (x, 0f, c) : (c, 0f, x);
        return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }

    // Card background: the logo blown up and blurred (tiny → huge with bicubic = soft blobs of its colours), then dimmed for text.
    public static Bitmap Backdrop(Item i, Size size, bool list)
    {
        var key = $"{i.Id}@{size.Width}x{size.Height}{(list ? "L" : "G")}";
        if (backdrops.TryGetValue(key, out var bd)) return bd;
        int w = Math.Max(size.Width, 1), h = Math.Max(size.Height, 1);
        var pal = Palette(i);
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        Draw.Smooth(g);
        g.Clear(Theme.Card);

        using (var tiny = new Bitmap(8, 8))
        {
            using (var tg = Graphics.FromImage(tiny))
            {
                tg.Clear(Draw.Mix(pal, Theme.Card, 0.35));
                tg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                tg.DrawImage(Get(i), 1, 1, 6, 6);
            }
            using var ia = new ImageAttributes();
            ia.SetWrapMode(WrapMode.TileFlipXY);
            var dest = list ? new RectangleF(-w * 0.05f, -h * 1.5f, w * 0.5f, h * 4f) : new RectangleF(-w * 0.375f, -w * 0.75f, w * 1.5f, w * 1.5f);
            g.DrawImage(tiny, Rectangle.Round(dest), 0, 0, 8, 8, GraphicsUnit.Pixel, ia);
        }

        // Dim so text stays readable, fading out towards the buttons (bottom, or right in list view).
        using (var veil = new SolidBrush(Color.FromArgb(Theme.Dark ? 150 : 175, Theme.Card))) g.FillRectangle(veil, 0, 0, w, h);
        if (list)
        {
            int end = (int)(w * 0.42f);
            using (var fade = new LinearGradientBrush(new Rectangle(0, 0, end + 1, h), Color.FromArgb(0, Theme.Card), Theme.Card, LinearGradientMode.Horizontal))
                g.FillRectangle(fade, 0, 0, end, h);
            using var rest = new SolidBrush(Theme.Card);
            g.FillRectangle(rest, end, 0, w - end, h);
        }
        else
        {
            using var fade = new LinearGradientBrush(new Rectangle(0, 0, w, h + 1), Color.FromArgb(0, Theme.Card), Color.FromArgb(200, Theme.Card), LinearGradientMode.Vertical);
            g.FillRectangle(fade, 0, 0, w, h);
        }

        if (backdrops.Count > 400) { foreach (var b in backdrops.Values) b.Dispose(); backdrops.Clear(); }
        return backdrops[key] = bmp;
    }

    static Image Custom(string name)
    {
        foreach (var ext in Exts)
        {
            var f = Path.Combine(LogoDir, name + ext);
            if (!File.Exists(f)) continue;
            try
            {
                if (ext == ".ico") return new Icon(f, 64, 64).ToBitmap();
                using var tmp = new Bitmap(new MemoryStream(File.ReadAllBytes(f)));
                return new Bitmap(tmp);
            }
            catch { }
        }
        return null;
    }

    static Image FromExe(string exe)
    {
        // Only the exe's own icon: Windows' generic "blank window" fallback looks worse than a coloured letter.
        try
        {
            if (string.IsNullOrEmpty(exe)) return null;
            if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return Icon.ExtractIcon(exe, 0, 64)?.ToBitmap();
            return File.Exists(exe) ? Icon.ExtractAssociatedIcon(exe)?.ToBitmap() : null;
        }
        catch { return null; }
    }

    static Image Letter(string name)
    {
        var b = new Bitmap(64, 64);
        using var g = Graphics.FromImage(b);
        Draw.Smooth(g);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var bg = new SolidBrush(Hue(name));
        g.FillEllipse(bg, 0, 0, 63, 63);
        using var font = new Font("Segoe UI Semibold", 28f, GraphicsUnit.Pixel);
        g.DrawString(string.IsNullOrEmpty(name) ? "?" : name[..1].ToUpperInvariant(), font, Brushes.White, new RectangleF(0, 0, 64, 64),
            new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        return b;
    }

    public static Icon AppIcon()
    {
        var b = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(b))
        {
            Draw.Smooth(g);
            foreach (var (x, y, c) in new[] { (2, 2, Theme.Accent), (17, 2, Theme.Run), (2, 17, Theme.Run), (17, 17, Theme.Accent) })
                Draw.Fill(g, new RectangleF(x, y, 13, 13), 3, c);
        }
        return Icon.FromHandle(b.GetHicon());
    }
}

// Flat rounded button, painted to match the cards.
class Pill : Control
{
    bool hover;
    public bool Primary { get; set; }
    public Color? TextColor { get; set; }

    public Pill(string text)
    {
        Text = text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI Semibold", 9f);
        Height = MainForm.S(34);
    }

    public void Fit() => Width = Draw.Measure(Text, Font) + MainForm.S(30);
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Draw.Smooth(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        var fill = Primary ? (hover ? Draw.Mix(Theme.Accent, Color.White, 0.12) : Theme.Accent) : (hover ? Theme.Wash(28) : Theme.Wash(14));
        Draw.Fill(g, r, Height / 2f, fill);
        var fg = !Enabled ? Theme.Muted : Primary ? Color.White : TextColor ?? Theme.Text;
        Draw.Text(g, Text, Font, ClientRectangle, fg, TextFormatFlags.HorizontalCenter);
    }
}

// "All | Tasks | Services | Startup" style switch.
class Segmented : Control
{
    readonly string[] options;
    int selected, hover = -1;
    public event Action Changed;

    public Segmented(params string[] options)
    {
        this.options = options;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Font = new Font("Segoe UI Semibold", 9f);
        Height = MainForm.S(34);
        Cursor = Cursors.Hand;
        Width = options.Sum(o => Draw.Measure(o, Font) + MainForm.S(26)) + MainForm.S(8);
    }

    public int Selected { get => selected; set { selected = Math.Clamp(value, 0, options.Length - 1); Invalidate(); } }

    IEnumerable<(int i, RectangleF r)> Segs()
    {
        float x = MainForm.S(4);
        for (int i = 0; i < options.Length; i++)
        {
            float w = Draw.Measure(options[i], Font) + MainForm.S(26);
            yield return (i, new RectangleF(x, MainForm.S(4), w, Height - MainForm.S(8)));
            x += w;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var h = Segs().FirstOrDefault(s => s.r.Contains(e.Location), (-1, default)).i;
        if (h != hover) { hover = h; Invalidate(); }
    }
    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        var h = Segs().FirstOrDefault(s => s.r.Contains(e.Location), (-1, default)).i;
        if (h >= 0 && h != selected) { Selected = h; Changed?.Invoke(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Draw.Smooth(g);
        Draw.Fill(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Height / 2f, Theme.Wash(12));
        foreach (var (i, r) in Segs())
        {
            if (i == selected) Draw.Fill(g, r, r.Height / 2, Theme.Dark ? Theme.Raised : Color.White);
            else if (i == hover) Draw.Fill(g, r, r.Height / 2, Theme.Wash(10));
            Draw.Text(g, options[i], Font, Rectangle.Round(r), i == selected ? Theme.Text : Theme.Muted, TextFormatFlags.HorizontalCenter);
        }
    }
}

// Rounded search field.
class SearchBox : Panel
{
    public readonly TextBox Box = new() { BorderStyle = BorderStyle.None, PlaceholderText = "Search", Font = new Font("Segoe UI", 10f) };

    public SearchBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = MainForm.S(34);
        Box.BackColor = Draw.Mix(Theme.Bg, Theme.Dark ? Color.White : Color.Black, 0.055);
        Box.ForeColor = Theme.Text;
        Controls.Add(Box);
        Resize += (_, _) =>
        {
            Box.Width = Width - MainForm.S(46);
            Box.Location = new Point(MainForm.S(32), (Height - Box.Height) / 2);
        };
        Click += (_, _) => Box.Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        Draw.Smooth(g);
        Draw.Fill(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Height / 2f, Box.BackColor);
        using var pen = new Pen(Theme.Muted, MainForm.S(2) * 0.75f);
        float cx = MainForm.S(16), cy = Height / 2f - MainForm.S(1), rad = MainForm.S(5);
        g.DrawEllipse(pen, cx - rad, cy - rad, rad * 2, rad * 2);
        g.DrawLine(pen, cx + rad * 0.7f, cy + rad * 0.7f, cx + rad * 1.6f, cy + rad * 1.6f);
    }
}

// One app, drawn by hand: blurred-logo backdrop, logo, name, status pill, three facts, action buttons.
class Card : Control
{
    public readonly string Id;
    public event Action<string, string> Act; // (item id, action)
    public bool List { get; }

    static readonly Font FName = new("Segoe UI Semibold", 11f), FSmall = new("Segoe UI", 8.5f), FBody = new("Segoe UI", 9f),
        FStatus = new("Segoe UI Semibold", 8.5f), FBtn = new("Segoe UI Semibold", 8.75f), FKey = new("Segoe UI", 7.75f);
    record Hit(Rectangle R, string Act, string Text, bool Enabled);

    Item it;
    Image logo;
    readonly List<Hit> hits = new();
    int hover = -1;
    readonly ToolTip tip = new() { InitialDelay = 600 };
    readonly ContextMenuStrip menu = new();

    static int S(int px) => MainForm.S(px);

    public Card(string id, bool list)
    {
        Id = id;
        List = list;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        Size = list ? new Size(S(900), S(68)) : new Size(S(310), S(200));
        Margin = list ? new Padding(S(8), S(4), S(8), S(4)) : new Padding(S(8));
        menu.Items.Add("Hide from board", null, (_, _) => Act?.Invoke(Id, "hide"));
        ContextMenuStrip = menu;
    }

    public void Bind(Item item, Image img)
    {
        bool tipChanged = it?.Tip != item.Tip;
        it = item;
        logo = img;
        if (tipChanged) tip.SetToolTip(this, item.Tip);
        Invalidate();
    }

    string[] Actions() => new[]
    {
        "run", "stop", it.Enabled ? "disable" : "enable", it.CanDelete ? "delete" : "hide",
    };

    static string Label(string act) => act switch
    {
        "run" => "Run", "stop" => "Stop", "disable" => "Disable", "enable" => "Enable", "delete" => "Delete", _ => "Hide",
    };

    bool Allowed(string act) => act switch { "run" => it.CanRun, "stop" => it.CanStop, _ => true };

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        if (it == null) return;
        Draw.Smooth(g);
        hits.Clear();

        var bounds = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        float radius = S(List ? 12 : 16);
        using (var path = Draw.Round(bounds, radius))
        {
            var bd = Icons.Backdrop(it, Size, List);
            using var tex = new TextureBrush(bd, WrapMode.Clamp);
            g.FillPath(tex, path);
            using var border = new Pen(Color.FromArgb(Theme.Dark ? 60 : 90, Icons.Palette(it)));
            g.DrawPath(border, path);
        }
        if (List) PaintRow(g); else PaintCard(g);
    }

    void PaintLogo(Graphics g, Rectangle r)
    {
        Draw.Fill(g, r, S(10), Theme.Wash(16));
        var inner = Rectangle.Inflate(r, -S(6), -S(6));
        if (logo != null) g.DrawImage(logo, inner);
    }

    int StatusPill(Graphics g, int right, int cy)
    {
        var c = Theme.Of(it.Kind);
        int w = Draw.Measure(it.Label, FStatus) + S(30), h = S(24);
        var r = new Rectangle(right - w, cy - h / 2, w, h);
        Draw.Fill(g, r, h / 2f, Color.FromArgb(Theme.Dark ? 38 : 30, c));
        using (var dot = new SolidBrush(c)) g.FillEllipse(dot, r.X + S(10), cy - S(3), S(7), S(7));
        Draw.Text(g, it.Label, FStatus, new Rectangle(r.X + S(21), r.Y, w - S(24), h), c);
        return w;
    }

    void Buttons(Graphics g, Rectangle area, int gap)
    {
        var acts = Actions();
        int bw = (area.Width - gap * (acts.Length - 1)) / acts.Length;
        for (int i = 0; i < acts.Length; i++)
        {
            var r = new Rectangle(area.X + i * (bw + gap), area.Y, bw, area.Height);
            bool on = Allowed(acts[i]);
            int idx = hits.Count;
            hits.Add(new Hit(r, acts[i], Label(acts[i]), on));
            bool hov = on && idx == hover;
            bool danger = acts[i] == "delete";
            Draw.Fill(g, r, r.Height / 2f, hov ? (danger ? Color.FromArgb(70, Theme.Fail) : Theme.Wash(34)) : Theme.Wash(on ? 16 : 7));
            var fg = !on ? Color.FromArgb(Theme.Dark ? 80 : 110, Theme.Muted) : danger && hov ? Theme.Text : Theme.Text;
            Draw.Text(g, Label(acts[i]), FBtn, r, fg, TextFormatFlags.HorizontalCenter);
        }
    }

    void PaintCard(Graphics g)
    {
        int pad = S(16);
        PaintLogo(g, new Rectangle(pad, pad, S(48), S(48)));
        int pillW = StatusPill(g, Width - pad, pad + S(14));
        int tx = pad + S(60), tw = Width - tx - pillW - S(22);
        Draw.Text(g, it.Name, FName, new Rectangle(tx, pad, tw, S(26)), Theme.Text);
        Draw.Text(g, it.Group, FSmall, new Rectangle(tx, pad + S(26), Width - tx - pad, S(20)), Theme.Muted);

        int y = S(80);
        foreach (var row in it.Rows.Take(3))
        {
            Draw.Text(g, row.Label, FBody, new Rectangle(pad, y, S(110), S(22)), Theme.Muted);
            Draw.Text(g, row.Value, FBody, new Rectangle(pad + S(100), y, Width - pad * 2 - S(100), S(22)),
                row.Alert ? Theme.Of(it.Kind) : Theme.Text, TextFormatFlags.Right);
            y += S(22);
        }
        Buttons(g, new Rectangle(pad, Height - pad - S(30), Width - pad * 2, S(30)), S(6));
    }

    void PaintRow(Graphics g)
    {
        int pad = S(14), cy = Height / 2;
        PaintLogo(g, new Rectangle(pad, cy - S(22), S(44), S(44)));
        int x = pad + S(58);

        int btnW = S(300), btnX = Width - pad - btnW;
        Buttons(g, new Rectangle(btnX, cy - S(15), btnW, S(30)), S(6));

        int nameW = Math.Clamp((Width - x - btnW) / 4, S(160), S(280));
        Draw.Text(g, it.Name, FName, new Rectangle(x, cy - S(20), nameW, S(22)), Theme.Text);
        Draw.Text(g, it.Group, FSmall, new Rectangle(x, cy + S(1), nameW, S(18)), Theme.Muted);
        x += nameW + S(12);

        int pillW = Draw.Measure(it.Label, FStatus) + S(30);
        StatusPill(g, x + pillW, cy);
        x += Math.Max(pillW, S(110)) + S(16);

        int avail = btnX - S(16) - x;
        int cols = Math.Min(3, Math.Max(0, avail / S(110)));
        if (cols == 0) return;
        int colW = Math.Min(S(170), avail / cols);
        foreach (var row in it.Rows.Take(cols))
        {
            Draw.Text(g, row.Label.ToUpperInvariant(), FKey, new Rectangle(x, cy - S(19), colW - S(8), S(16)), Theme.Muted);
            Draw.Text(g, row.Value, FBody, new Rectangle(x, cy - S(2), colW - S(8), S(20)), row.Alert ? Theme.Of(it.Kind) : Theme.Text);
            x += colW;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = hits.FindIndex(x => x.R.Contains(e.Location));
        Cursor = h >= 0 && hits[h].Enabled ? Cursors.Hand : Cursors.Default;
        if (h != hover) { hover = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        var h = hits.FirstOrDefault(x => x.R.Contains(e.Location));
        if (h != null && h.Enabled) Act?.Invoke(Id, h.Act);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { tip.Dispose(); menu.Dispose(); }
        base.Dispose(disposing);
    }
}

class Board2 : FlowLayoutPanel
{
    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

    public Board2() { DoubleBuffered = true; SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (Theme.Dark) SetWindowTheme(Handle, "DarkMode_Explorer", null); // dark scrollbar
    }
}

class MainForm : Form
{
    public static float DpiScale = 1;
    public static int S(int px) => (int)Math.Round(px * DpiScale);

    readonly Settings settings = Settings.Load();
    readonly Board2 grid = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
    readonly Label error = new() { Dock = DockStyle.Top, ForeColor = Theme.Fail, AutoEllipsis = true, Visible = false };
    readonly Panel header = new() { Dock = DockStyle.Top, BackColor = Theme.Bg };
    readonly Dictionary<string, Card> cards = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 3000 };
    readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 60 * 60 * 1000 };
    readonly Pill add = new("+  Add server") { Primary = true }, admin = new("Run as admin"), gear = new("Settings"), update = new("Update");
    readonly Segmented filter = new("All", "Tasks", "Services", "Startup apps"), view = new("Grid", "List");
    readonly SearchBox search = new();
    static readonly string[] Sources = { null, "task", "service", "startup" };
    Updater.Release pending;
    List<Item> last = new();
    string actionError = "";
    bool loading;
    (int running, int ok, int fail, int hidden) counts;

    public MainForm()
    {
        DpiScale = DeviceDpi / 96f;
        Text = Board.IsAdmin ? "TaskBoard (admin)" : "TaskBoard";
        Icon = Icons.AppIcon();
        Font = new Font("Segoe UI", 9f);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(S(760), S(480));
        ClientSize = settings.Width > 0 ? new Size(Math.Max(settings.Width, S(760)), Math.Max(settings.Height, S(480))) : new Size(S(1180), S(760));
        if (settings.Maximized) WindowState = FormWindowState.Maximized;

        header.Height = S(124);
        header.Paint += PaintHeader;
        header.Resize += (_, _) => LayoutHeader();

        add.Click += (_, _) => { using var f = new AddForm(); if (f.ShowDialog(this) == DialogResult.OK) Reload(); };
        admin.Visible = !Board.IsAdmin;
        new ToolTip().SetToolTip(admin, "Shows tasks set up by an administrator and lets you start/stop services.");
        admin.Click += (_, _) => RestartAsAdmin();
        gear.Click += (_, _) => OpenSettings();
        update.TextColor = Theme.Run;
        update.Visible = false;
        update.Click += async (_, _) => await ApplyUpdate();
        filter.Selected = settings.Filter;
        filter.Changed += () => { settings.Filter = filter.Selected; settings.Save(); Render(); };
        view.Selected = settings.View == "list" ? 1 : 0;
        view.Changed += () => { settings.View = view.Selected == 1 ? "list" : "grid"; settings.Save(); Rebuild(); };
        search.Box.TextChanged += (_, _) => Render();
        foreach (var p in new[] { add, admin, gear, update }) p.Fit();
        header.Controls.AddRange(new Control[] { add, admin, gear, update, filter, view, search });

        error.Height = S(26);
        error.Padding = new Padding(S(28), 0, S(28), S(4));
        grid.Padding = new Padding(S(16), S(4), S(16), S(16));
        grid.Resize += (_, _) => SizeRows();

        Controls.Add(grid);
        Controls.Add(error);
        Controls.Add(header);

        timer.Tick += (_, _) => Reload();
        updateTimer.Tick += async (_, _) => await CheckUpdate();
        Load += async (_, _) => { Theme.TitleBar(this); LayoutHeader(); Reload(); timer.Start(); updateTimer.Start(); await CheckUpdate(); };
        FormClosing += (_, _) =>
        {
            settings.Maximized = WindowState == FormWindowState.Maximized;
            if (WindowState == FormWindowState.Normal) { settings.Width = ClientSize.Width; settings.Height = ClientSize.Height; }
            settings.Save();
        };
    }

    bool ListView => settings.View == "list";

    void LayoutHeader()
    {
        int pad = S(28), top = S(20), gap = S(8);
        int x = header.Width - pad;
        foreach (var p in new Control[] { add, gear, admin, update })
        {
            if (!p.Visible) continue;
            x -= p.Width;
            p.Location = new Point(x, top);
            x -= gap;
        }
        int row2 = S(72);
        filter.Location = new Point(pad, row2);
        view.Location = new Point(header.Width - pad - view.Width, row2);
        int sx = filter.Right + S(12);
        search.Location = new Point(sx, row2);
        search.Width = Math.Max(S(160), Math.Min(S(320), view.Left - S(12) - sx));
        header.Invalidate();
    }

    void PaintHeader(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        Draw.Smooth(g);
        int pad = S(28);
        using var title = new Font("Segoe UI Semibold", 18f);
        int tw = Draw.Measure("TaskBoard", title);
        Draw.Text(g, "TaskBoard", title, new Rectangle(pad - S(4), S(16), tw + S(4), S(40)), Theme.Text);
        using var vf = new Font("Segoe UI", 8.5f);
        var ver = "v" + Updater.Current.ToString(3);
        Draw.Text(g, ver, vf, new Rectangle(pad + tw + S(2), S(28), S(60), S(20)), Theme.Muted);

        int x = pad + tw + S(14) + Draw.Measure(ver, vf);
        using var cf = new Font("Segoe UI Semibold", 9f);
        foreach (var (n, label, c) in new[] { (counts.running, "running", Theme.Run), (counts.ok, "ok", Theme.Ok), (counts.fail, "failed", Theme.Fail) })
        {
            if (n == 0 && label != "running") continue;
            var text = $"{n} {label}";
            int w = Draw.Measure(text, cf) + S(30);
            var r = new Rectangle(x, S(24), w, S(26));
            Draw.Fill(g, r, r.Height / 2f, Color.FromArgb(Theme.Dark ? 34 : 26, c));
            using (var dot = new SolidBrush(c)) g.FillEllipse(dot, r.X + S(10), r.Y + r.Height / 2 - S(3), S(7), S(7));
            Draw.Text(g, text, cf, new Rectangle(r.X + S(21), r.Y, w - S(24), r.Height), c);
            x += w + S(8);
        }
        if (counts.hidden > 0)
            Draw.Text(g, $"{counts.hidden} hidden", cf, new Rectangle(x + S(4), S(24), S(100), S(26)), Theme.Muted);
    }

    void RestartAsAdmin()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = true, Verb = "runas" });
            Close();
        }
        catch (System.ComponentModel.Win32Exception) { } // said no to the UAC prompt
    }

    void OpenSettings()
    {
        using var f = new SettingsForm(last, settings);
        if (f.ShowDialog(this) == DialogResult.OK) { settings.Save(); Render(); }
    }

    // Quiet: a failed check (offline, rate limited) just leaves the button hidden.
    async Task CheckUpdate()
    {
        try { pending = await Updater.Check(); } catch { return; }
        update.Visible = pending != null;
        if (pending != null) { update.Text = $"Update to v{pending.Version.ToString(3)}"; update.Fit(); }
        LayoutHeader();
    }

    async Task ApplyUpdate()
    {
        if (pending == null) return;
        update.Enabled = false;
        update.Text = "Updating…";
        try
        {
            await Updater.Apply(pending);
            Updater.Restart();
            Close();
        }
        catch (Exception ex)
        {
            ShowError("Update failed: " + ex.Message, true);
            update.Enabled = true;
            update.Text = $"Update to v{pending.Version.ToString(3)}";
        }
    }

    void ShowError(string msg, bool sticky = false)
    {
        if (sticky) actionError = msg;
        error.Text = msg;
        error.Visible = msg != "";
    }

    // Reading services/processes takes a moment, so it happens off the UI thread.
    async void Reload()
    {
        if (loading) return;
        loading = true;
        try
        {
            var (items, errors) = await Task.Run(Board.Gather);
            last = items;
            ShowError(actionError != "" ? actionError : errors.Count > 0 ? "Couldn't read " + string.Join("; ", errors) : "");
            Render();
        }
        catch (Exception ex) { ShowError("Refresh failed: " + ex.Message); }
        finally { loading = false; }
    }

    void Rebuild()
    {
        grid.SuspendLayout();
        foreach (var c in cards.Values) c.Dispose();
        cards.Clear();
        grid.ResumeLayout();
        Render();
    }

    // List rows span the window; grid cards stretch so the columns fill the width.
    void SizeRows()
    {
        int avail = grid.ClientSize.Width - grid.Padding.Horizontal - (grid.VerticalScroll.Visible ? 0 : SystemInformation.VerticalScrollBarWidth);
        int w;
        if (ListView) w = Math.Max(avail - S(16), S(600));
        else
        {
            int slot = S(300) + S(16);
            int cols = Math.Max(1, avail / slot);
            w = Math.Min(avail / cols - S(16) - 1, S(420));
        }
        foreach (var c in cards.Values) if (c.Width != w) c.Width = w;
    }

    void Render()
    {
        var source = Sources[Math.Clamp(filter.Selected, 0, Sources.Length - 1)];
        var q = search.Box.Text.Trim();
        var visible = last.Where(i => !settings.IsHidden(i)).ToList();
        counts = (visible.Count(i => i.Kind == "running"), visible.Count(i => i.Kind == "ok"), visible.Count(i => i.Kind == "fail"), last.Count - visible.Count);
        header.Invalidate();

        var list = visible.Where(i => (source == null || i.Has(source))
            && (q == "" || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Tip.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();

        var ids = list.Select(i => i.Id).ToHashSet();
        grid.SuspendLayout();
        foreach (var gone in cards.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            cards[gone].Dispose();
            cards.Remove(gone);
        }
        for (int n = 0; n < list.Count; n++)
        {
            var it = list[n];
            if (!cards.TryGetValue(it.Id, out var card))
            {
                card = cards[it.Id] = new Card(it.Id, ListView);
                card.Act += OnAct;
                grid.Controls.Add(card);
            }
            card.Bind(it, Icons.Get(it));
            if (grid.Controls.GetChildIndex(card) != n) grid.Controls.SetChildIndex(card, n);
        }
        SizeRows();
        grid.ResumeLayout();
    }

    void OnAct(string id, string act)
    {
        var it = last.FirstOrDefault(i => i.Id == id);
        if (it == null) return;
        var key = Board.Key(it);
        try
        {
            switch (act)
            {
                case "hide":
                    settings.SetHidden(it, true);
                    settings.Save();
                    break;
                case "delete":
                    var what = it.Source == "task"
                        ? $"Remove \"{key.TrimStart('\\')}\" from Task Scheduler?\n\nThis doesn't stop it if it's running right now, and doesn't delete any files."
                        : $"Stop \"{it.Name}\" from starting at sign-in by removing its startup entry?\n\nThis doesn't stop it if it's running right now, and doesn't uninstall anything.";
                    if (MessageBox.Show(this, what, "Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    if (it.Source == "task") Sched.Delete(key); else Startup.Do(key, "delete");
                    Icons.Forget(id);
                    break;
                default:
                    if (it.Has("service") && act == "disable" &&
                        MessageBox.Show(this, $"Disable \"{it.Name}\"? It won't start until you enable it again.", "Disable",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    // A merged card acts on each piece that the action makes sense for (start what's stopped, stop what's running).
                    foreach (var part in it.Parts ?? new[] { it })
                    {
                        if ((act == "run" && !part.CanRun) || (act == "stop" && !part.CanStop)) continue;
                        if ((act == "enable" && part.Enabled) || (act == "disable" && !part.Enabled)) continue;
                        var pk = Board.Key(part);
                        switch (part.Source)
                        {
                            case "task":
                                if (act == "run") Sched.Run(pk);
                                else if (act == "stop") Sched.Stop(pk);
                                else Sched.SetEnabled(pk, act == "enable");
                                break;
                            case "service": Services.Do(pk, act); break;
                            case "startup": Startup.Do(pk, act); break;
                        }
                    }
                    break;
            }
            actionError = "";
            ShowError("");
        }
        catch (Exception ex) { ShowError($"{act} failed: {ex.Message}", true); }
        Render();
        Reload();
    }
}

// Pick what never shows on the board. Saved to settings.json, so it sticks across restarts.
class SettingsForm : Form
{
    readonly List<Item> items;
    readonly Settings settings;
    readonly Dictionary<string, bool> hide = new();
    readonly CheckBox noise = new() { Text = "Hide updaters, helpers and Windows built-ins", AutoSize = true, ForeColor = Theme.Text };
    readonly TextBox filter = new() { PlaceholderText = "Search", BorderStyle = BorderStyle.FixedSingle };
    readonly CheckedListBox list = new() { CheckOnClick = true, BorderStyle = BorderStyle.None, IntegralHeight = false };
    List<Item> shown = new();
    bool filling;

    static int S(int px) => MainForm.S(px);

    public SettingsForm(List<Item> items, Settings settings)
    {
        this.items = items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        this.settings = settings;
        foreach (var i in items) hide[i.Id] = settings.IsHidden(i);

        Text = "Settings";
        Icon = Icons.AppIcon();
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(S(520), S(600));
        MinimumSize = new Size(S(420), S(400));
        Padding = new Padding(S(20));

        var heading = new Label { Text = "Hidden apps", Font = new Font("Segoe UI Semibold", 13f), AutoSize = true, Dock = DockStyle.Top };
        var hint = new Label
        {
            Text = "Ticked apps never show on the board. This is remembered after you close TaskBoard.",
            AutoSize = false, Height = S(52), Dock = DockStyle.Top, ForeColor = Theme.Muted, Padding = new Padding(0, S(4), 0, S(8)),
        };
        noise.Checked = settings.HideNoise;
        noise.Dock = DockStyle.Top;
        noise.Padding = new Padding(0, 0, 0, S(10));
        noise.CheckedChanged += (_, _) =>
        {
            // Only flip apps you haven't picked by hand.
            foreach (var i in items.Where(i => i.Noise && !settings.Hidden.Contains(i.Id) && !settings.Shown.Contains(i.Id)))
                hide[i.Id] = noise.Checked;
            Fill();
        };

        filter.Dock = DockStyle.Top;
        filter.BackColor = Theme.Card;
        filter.ForeColor = Theme.Text;
        filter.TextChanged += (_, _) => Fill();
        var spacer = new Panel { Dock = DockStyle.Top, Height = S(8) };

        list.Dock = DockStyle.Fill;
        list.BackColor = Theme.Card;
        list.ForeColor = Theme.Text;
        list.ItemCheck += (_, e) => { if (!filling) hide[shown[e.Index].Id] = e.NewValue == CheckState.Checked; };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = S(52), Padding = new Padding(0, S(14), 0, 0) };
        var save = new Pill("Save") { Primary = true };
        var cancel = new Pill("Cancel");
        save.Fit(); cancel.Fit();
        save.Width = cancel.Width = Math.Max(save.Width, S(90));
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; };
        buttons.Controls.AddRange(new Control[] { save, cancel });

        Controls.Add(list);
        Controls.Add(spacer);
        Controls.Add(filter);
        Controls.Add(noise);
        Controls.Add(hint);
        Controls.Add(heading);
        Controls.Add(buttons);
        CancelButton = new Button { DialogResult = DialogResult.Cancel }; // Esc closes

        Load += (_, _) => { Theme.TitleBar(this); Fill(); };
    }

    static string Kind(Item i) => i.Parts != null ? i.Group : i.Source switch { "task" => "Task", "service" => "Service", _ => "Startup app" };

    void Fill()
    {
        filling = true;
        var q = filter.Text.Trim();
        shown = items.Where(i => q == "" || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var i in shown)
            list.Items.Add($"{i.Name}    ·  {Kind(i)}{(i.Noise ? "  ·  auto-hidden type" : "")}", hide[i.Id]);
        list.EndUpdate();
        filling = false;
    }

    void Save()
    {
        settings.HideNoise = noise.Checked;
        foreach (var i in items) settings.SetHidden(i, hide[i.Id]);
        DialogResult = DialogResult.OK;
    }
}
