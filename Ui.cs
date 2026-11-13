// Board UI: logo-tinted tiles in your own drag-and-drop groups, header controls, Settings window.
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;

namespace TaskBoard;

class GroupDef
{
    public string Name { get; set; } = "";
    public List<string> Ids { get; set; } = new();
}

// Everything remembered between runs: %APPDATA%\TaskBoard\settings.json
class Settings
{
    public string View { get; set; } = "grid";
    public int Filter { get; set; }
    public bool HideNoise { get; set; } = true;
    public List<string> Hidden { get; set; } = new();  // always hidden
    public List<string> Shown { get; set; } = new();   // always shown, even if it looks like an updater
    public List<GroupDef> Groups { get; set; } = new(); // your own groups, in order; apps not in one go to a group named after their type
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
        s.Groups ??= new();
        foreach (var g in s.Groups) g.Ids ??= new();
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

        // Dim so text stays readable, fading out to the right.
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
    // Icon-only round button (Segoe MDL2 glyph) when set; plain text pill when null.
    public string Glyph { get; set; }
    static readonly Font GlyphFont = new("Segoe MDL2 Assets", 11f);

    public Pill(string text)
    {
        Text = text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI Semibold", 9f);
        Height = MainForm.S(34);
    }

    public void Fit() { Width = Glyph != null ? Height : Draw.Measure(Text, Font) + MainForm.S(26); Invalidate(); }
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
        if (Glyph != null) Draw.Text(g, Glyph, GlyphFont, ClientRectangle, fg, TextFormatFlags.HorizontalCenter);
        else Draw.Text(g, Text, Font, ClientRectangle, fg, TextFormatFlags.HorizontalCenter);
    }
}

// Your own groups. Apps you haven't placed sit in a group named after their type ("Services", ...).
record Group(string Name, List<Item> Items);

static class Grouping
{
    static readonly string[] TypeOrder = { "Services", "Scheduled tasks", "Startup apps" };
    public static string TypeOf(Item i) => i.Source switch { "service" => "Services", "task" => "Scheduled tasks", _ => "Startup apps" };

    public static List<Group> Build(List<Item> items, List<GroupDef> defs)
    {
        var map = items.ToDictionary(i => i.Id);
        var placed = new HashSet<string>();
        var groups = defs.Select(d => new Group(d.Name, d.Ids.Where(id => map.ContainsKey(id) && placed.Add(id)).Select(id => map[id]).ToList())).ToList();
        foreach (var it in items.Where(i => !placed.Contains(i.Id)).OrderBy(i => Array.IndexOf(TypeOrder, TypeOf(i))))
        {
            var g = groups.FirstOrDefault(x => x.Name.Equals(TypeOf(it), StringComparison.OrdinalIgnoreCase));
            if (g == null) groups.Add(g = new Group(TypeOf(it), new()));
            g.Items.Add(it);
        }
        return groups;
    }

    // Write what's on screen into the saved groups, keeping ids that aren't showing (hidden apps) where they were.
    static void Save(Settings s, List<Group> shown)
    {
        var onScreen = shown.SelectMany(g => g.Items).Select(i => i.Id).ToHashSet();
        var old = s.Groups.ToDictionary(g => g.Name, g => g.Ids.Where(id => !onScreen.Contains(id)).ToList(), StringComparer.OrdinalIgnoreCase);
        s.Groups = shown.Select(g => new GroupDef
        {
            Name = g.Name,
            Ids = g.Items.Select(i => i.Id).Concat(old.TryGetValue(g.Name, out var rest) ? rest : new List<string>()).ToList(),
        })
        // An emptied default group ("Services" etc.) goes away; your own empty groups stay to drop into.
        .Where(d => d.Ids.Count > 0 || !TypeOrder.Contains(d.Name, StringComparer.OrdinalIgnoreCase))
        .ToList();
    }

    public static void Move(Settings s, List<Group> shown, string id, int group, int index)
    {
        var it = shown.SelectMany(g => g.Items).First(i => i.Id == id);
        foreach (var g in shown) g.Items.RemoveAll(i => i.Id == id);
        var target = shown[group].Items;
        target.Insert(Math.Clamp(index, 0, target.Count), it);
        Save(s, shown);
    }

    public static string NewName(List<Group> shown)
    {
        for (int n = 1; ; n++) if (!shown.Any(g => g.Name == $"Group {n}")) return $"Group {n}";
    }

    public static void NewGroup(Settings s, List<Group> shown, string id, string name)
    {
        shown.Add(new Group(name, new()));
        Move(s, shown, id, shown.Count - 1, 0);
    }

    public static void Rename(Settings s, List<Group> shown, int group, string name)
    {
        var saved = s.Groups.FirstOrDefault(g => g.Name.Equals(shown[group].Name, StringComparison.OrdinalIgnoreCase));
        if (saved != null) saved.Name = name; // so hidden apps in it move along
        shown[group] = shown[group] with { Name = name };
        Save(s, shown);
    }

    public static void Shift(Settings s, List<Group> shown, int group, int by)
    {
        int to = Math.Clamp(group + by, 0, shown.Count - 1);
        var g = shown[group];
        shown.RemoveAt(group);
        shown.Insert(to, g);
        Save(s, shown);
    }

    // Its apps go back to their type groups.
    public static void Delete(Settings s, List<Group> shown, int group)
    {
        var name = shown[group].Name;
        shown.RemoveAt(group);
        Save(s, shown);
        s.Groups.RemoveAll(g => g.Name == name);
    }
}

// Simple one-line text prompt.
class Prompt : Form
{
    readonly TextBox box = new() { BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10f) };

    Prompt(string title, string value)
    {
        Text = title;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(MainForm.S(320), MainForm.S(110));
        box.Text = value;
        box.BackColor = Theme.Card;
        box.ForeColor = Theme.Text;
        box.SetBounds(MainForm.S(16), MainForm.S(16), MainForm.S(288), MainForm.S(28));
        var ok = new Pill("Save") { Primary = true };
        var cancel = new Pill("Cancel");
        ok.Fit(); cancel.Fit();
        ok.Width = cancel.Width = MainForm.S(86);
        ok.Location = new Point(MainForm.S(304) - ok.Width, MainForm.S(64));
        cancel.Location = new Point(ok.Left - MainForm.S(8) - cancel.Width, MainForm.S(64));
        ok.Click += (_, _) => DialogResult = DialogResult.OK;
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        Controls.AddRange(new Control[] { box, ok, cancel });
        AcceptButton = new Button { DialogResult = DialogResult.OK };
        CancelButton = new Button { DialogResult = DialogResult.Cancel };
        Load += (_, _) => { Theme.TitleBar(this); box.SelectAll(); box.Focus(); };
    }

    public static string Ask(IWin32Window owner, string title, string value)
    {
        using var p = new Prompt(title, value);
        if (p.ShowDialog(owner) != DialogResult.OK) return null;
        var v = p.box.Text.Trim();
        return v == "" ? null : v;
    }
}

class MenuColors : ProfessionalColorTable
{
    static Color Bg => Theme.Raised;
    static Color Sel => Draw.Mix(Theme.Raised, Theme.Text, 0.12);
    public override Color ToolStripDropDownBackground => Bg;
    public override Color ImageMarginGradientBegin => Bg;
    public override Color ImageMarginGradientMiddle => Bg;
    public override Color ImageMarginGradientEnd => Bg;
    public override Color MenuBorder => Theme.Line;
    public override Color MenuItemBorder => Sel;
    public override Color MenuItemSelected => Sel;
    public override Color MenuItemSelectedGradientBegin => Sel;
    public override Color MenuItemSelectedGradientEnd => Sel;
    public override Color SeparatorDark => Theme.Line;
    public override Color SeparatorLight => Theme.Line;
}

// The board: tiles in your groups, sized to fit the window so nothing scrolls (down to a minimum size).
// Drag a tile to reorder or regroup it (drop on "+ New group" to make one). Click a tile for actions, a group name to rename/move/delete.
class TileBoard : ScrollableControl
{
    public event Action<string, string> Act;
    public Settings Settings;
    static readonly Font FName = new("Segoe UI Semibold", 9.5f), FLine = new("Segoe UI", 8.25f), FHead = new("Segoe UI Semibold", 8f);

    record Slot(Group Group, Rectangle Head, Rectangle Area, List<(Rectangle R, Item It)> Tiles);

    List<Group> groups = new();
    List<Item> pendingItems;
    readonly List<Slot> slots = new();
    Rectangle newZone;
    Size tileSize;
    Item hover;
    readonly ToolTip tip = new() { InitialDelay = 500, AutoPopDelay = 15000 };

    // Dragging
    Item pressed;
    Point pressAt, dragAt, grabOffset;
    bool dragging;
    int dropGroup = -1, dropIndex = -1;
    bool dropNew;

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

    static int S(int px) => MainForm.S(px);

    public TileBoard()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        AutoScroll = true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (Theme.Dark) SetWindowTheme(Handle, "DarkMode_Explorer", null);
    }

    public void SetItems(List<Item> items)
    {
        if (dragging) { pendingItems = items; return; } // don't reshuffle under the cursor
        groups = Grouping.Build(items, Settings.Groups);
        if (hover != null) hover = items.FirstOrDefault(i => i.Id == hover.Id);
        DoLayout();
        Invalidate();
    }

    void Rebuild(List<Item> items) => SetItems(items ?? groups.SelectMany(g => g.Items).ToList());

    protected override void OnResize(EventArgs e) { base.OnResize(e); DoLayout(); Invalidate(); }

    // As many columns as the width allows, then tiles as tall as the height allows; shrink tile width before scrolling.
    void DoLayout()
    {
        slots.Clear();
        int pad = S(12), gap = S(8), headH = S(26), groupGap = S(6);
        int W = ClientSize.Width - pad * 2, H = ClientSize.Height - pad * 2;
        // "+ New group" floats over the bottom of the window while dragging (window coords), so nothing reflows.
        newZone = new Rectangle(pad, ClientSize.Height - pad - S(38), W, S(38));
        int G = groups.Count;
        int RowsOf(Group g, int cols) => Math.Max(1, (g.Items.Count + cols - 1) / cols); // an empty group keeps one row to drop into

        int cols = 1, tileH = S(44);
        foreach (var minW in new[] { 190, 170, 150, 135 })
        {
            cols = Math.Max(1, (W + gap) / (S(minW) + gap));
            int rows = groups.Sum(g => RowsOf(g, cols));
            tileH = (H - G * (headH + groupGap) - rows * gap) / Math.Max(rows, 1);
            if (tileH >= S(46)) break;
        }
        tileH = Math.Clamp(tileH, S(44), S(70));
        int tileW = (W - (cols - 1) * gap) / cols;
        tileSize = new Size(tileW, tileH);

        int y = pad;
        foreach (var g in groups)
        {
            var head = new Rectangle(pad, y, W, headH);
            y += headH;
            var list = new List<(Rectangle, Item)>();
            for (int i = 0; i < g.Items.Count; i++)
                list.Add((new Rectangle(pad + i % cols * (tileW + gap), y + i / cols * (tileH + gap), tileW, tileH), g.Items[i]));
            int h = RowsOf(g, cols) * (tileH + gap) - gap;
            slots.Add(new Slot(g, head, new Rectangle(pad, y, W, h), list));
            y += h + gap + groupGap;
        }
        int total = y + pad - gap;
        AutoScrollMinSize = total > ClientSize.Height ? new Size(0, total) : Size.Empty;
    }

    static string Detail(Item it)
    {
        var alert = it.Rows.FirstOrDefault(r => r.Alert);
        if (alert != null) return alert.Value;
        var row = it.Source == "task" ? it.Rows.FirstOrDefault(r => r.Label == "Next run") : it.Rows.FirstOrDefault(r => r.Label == "Process");
        if (row == null || row.Value == "—") return null;
        return it.Source == "task" ? "next " + row.Value : row.Value;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        Draw.Smooth(g);
        g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);

        for (int s = 0; s < slots.Count; s++)
        {
            var slot = slots[s];
            var text = $"{slot.Group.Name.ToUpperInvariant()}   {slot.Group.Items.Count}";
            bool hot = dragging && dropGroup == s && !dropNew;
            Draw.Text(g, text, FHead, new Rectangle(slot.Head.X + S(2), slot.Head.Y, slot.Head.Width, slot.Head.Height - S(4)), hot ? Theme.Accent : Theme.Muted);
            int tw = Draw.Measure(text, FHead) + S(12);
            using (var line = new Pen(hot ? Theme.Accent : Theme.Line))
                g.DrawLine(line, slot.Head.X + tw, slot.Head.Y + (slot.Head.Height - S(4)) / 2, slot.Head.Right, slot.Head.Y + (slot.Head.Height - S(4)) / 2);

            if (slot.Group.Items.Count == 0) Dashed(g, new Rectangle(slot.Area.X, slot.Area.Y, slot.Area.Width, tileSize.Height), "Drag apps here", hot);
            foreach (var (r, it) in slot.Tiles)
            {
                if (dragging && it.Id == pressed.Id) { Dashed(g, r, null, false); continue; } // its old spot
                PaintTile(g, r, it, 255);
            }
        }

        if (dragging)
        {
            var keep = g.Transform;
            g.ResetTransform();
            Draw.Fill(g, Rectangle.Inflate(newZone, S(4), S(4)), S(12), Color.FromArgb(235, Theme.Bg));
            Dashed(g, newZone, "+  Drop here to start a new group", dropNew);
            g.Transform = keep;
            if (!dropNew && dropGroup >= 0) DrawInsertMark(g);
            var at = new Point(dragAt.X - AutoScrollPosition.X - grabOffset.X, dragAt.Y - AutoScrollPosition.Y - grabOffset.Y);
            var r = new Rectangle(at, tileSize);
            using (var shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
            using (var sp = Draw.Round(new RectangleF(r.X + S(3), r.Y + S(5), r.Width, r.Height), S(10)))
                g.FillPath(shadow, sp);
            PaintTile(g, r, pressed, 235);
        }
    }

    void Dashed(Graphics g, Rectangle r, string text, bool hot)
    {
        var rf = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1);
        if (hot) Draw.Fill(g, rf, S(10), Color.FromArgb(40, Theme.Accent));
        using var path = Draw.Round(rf, S(10));
        using var pen = new Pen(hot ? Theme.Accent : Theme.Line, hot ? 1.5f : 1f) { DashStyle = DashStyle.Dash };
        g.DrawPath(pen, path);
        if (text != null) Draw.Text(g, text, FLine, r, hot ? Theme.Accent : Theme.Muted, TextFormatFlags.HorizontalCenter);
    }

    void DrawInsertMark(Graphics g)
    {
        var slot = slots[dropGroup];
        var tiles = slot.Tiles.Where(t => t.It.Id != pressed.Id).ToList();
        if (tiles.Count == 0) return; // empty group: its dashed box lights up instead
        Rectangle mark;
        if (dropIndex < tiles.Count) { var t = tiles[dropIndex].R; mark = new Rectangle(t.X - S(5), t.Y + S(4), S(3), t.Height - S(8)); }
        else { var t = tiles[^1].R; mark = new Rectangle(t.Right + S(2), t.Y + S(4), S(3), t.Height - S(8)); }
        Draw.Fill(g, mark, S(2), Theme.Accent);
    }

    void PaintTile(Graphics g, Rectangle r, Item it, int alpha)
    {
        var rf = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1);
        using (var path = Draw.Round(rf, S(10)))
        {
            using var tex = new TextureBrush(Icons.Backdrop(it, r.Size, true), WrapMode.Clamp);
            tex.TranslateTransform(r.X, r.Y);
            g.FillPath(tex, path);
            bool hov = !dragging && hover?.Id == it.Id;
            var edge = it.Kind == "fail" ? Color.FromArgb(170, Theme.Fail)
                : hov || alpha < 255 ? Theme.Wash(80) : Color.FromArgb(Theme.Dark ? 55 : 80, Icons.Palette(it));
            using var pen = new Pen(edge, hov || it.Kind == "fail" || alpha < 255 ? 1.5f : 1f);
            g.DrawPath(pen, path);
        }

        int logo = Math.Min(r.Height - S(18), S(34));
        var lr = new Rectangle(r.X + S(10), r.Y + (r.Height - logo) / 2, logo, logo);
        Draw.Fill(g, lr, S(8), Theme.Wash(16));
        var img = Icons.Get(it);
        if (img != null) g.DrawImage(img, Rectangle.Inflate(lr, -S(4), -S(4)));

        int tx = lr.Right + S(10), tw = r.Right - tx - S(8);
        int mid = r.Y + r.Height / 2;
        Draw.Text(g, it.Name, FName, new Rectangle(tx, mid - S(19), tw, S(19)), Theme.Text);

        var c = Theme.Of(it.Kind);
        using (var dot = new SolidBrush(c)) g.FillEllipse(dot, tx, mid + S(5), S(6), S(6));
        int lx = tx + S(10);
        int lw = Math.Min(Draw.Measure(it.Label, FLine), tw - S(10));
        Draw.Text(g, it.Label, FLine, new Rectangle(lx - S(3), mid, lw + S(2), S(16)), c);
        var detail = Detail(it);
        if (detail != null)
            Draw.Text(g, "· " + detail, FLine, new Rectangle(lx + lw - S(2), mid, tw - S(10) - lw + S(2), S(16)), it.Rows.Any(x => x.Alert) ? c : Theme.Muted);
    }

    Point Content(Point p) => new(p.X - AutoScrollPosition.X, p.Y - AutoScrollPosition.Y);
    (Rectangle R, Item It) TileAt(Point content) => slots.SelectMany(s => s.Tiles).FirstOrDefault(t => t.R.Contains(content));
    int HeadAt(Point content) => slots.FindIndex(s => s.Head.Contains(content));

    // Where a drop would land: the group under the cursor (header, tiles or the gap after them) and the slot in reading order.
    void FindDrop(Point content)
    {
        var client = new Point(content.X + AutoScrollPosition.X, content.Y + AutoScrollPosition.Y);
        dropNew = newZone.Contains(client);
        dropGroup = -1;
        dropIndex = -1;
        if (dropNew) return;
        for (int s = 0; s < slots.Count; s++)
        {
            var zone = Rectangle.FromLTRB(slots[s].Head.Left, slots[s].Head.Top, slots[s].Area.Right, slots[s].Area.Bottom + S(10));
            if (!zone.Contains(content)) continue;
            dropGroup = s;
            var tiles = slots[s].Tiles.Where(t => t.It.Id != pressed.Id).ToList();
            dropIndex = tiles.Count;
            for (int i = 0; i < tiles.Count; i++)
            {
                var r = tiles[i].R;
                if (content.Y < r.Top || (content.Y < r.Bottom + S(8) && content.X < r.X + r.Width / 2)) { dropIndex = i; break; }
            }
            return;
        }
        // Below everything / beside it: nearest group vertically
        if (slots.Count > 0)
        {
            dropGroup = content.Y < slots[0].Head.Top ? 0 : slots.FindLastIndex(s => s.Head.Top <= content.Y);
            if (dropGroup >= 0) dropIndex = slots[dropGroup].Tiles.Count(t => t.It.Id != pressed.Id);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var (r, it) = TileAt(Content(e.Location));
        pressed = it;
        pressAt = e.Location;
        if (it != null) { var c = Content(e.Location); grabOffset = new Point(c.X - r.X, c.Y - r.Y); }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (pressed != null) // set on left-button down, cleared on up
        {
            if (!dragging && (Math.Abs(e.X - pressAt.X) > SystemInformation.DragSize.Width || Math.Abs(e.Y - pressAt.Y) > SystemInformation.DragSize.Height))
            {
                dragging = true;
                tip.Hide(this);
                tip.Active = false;
                Capture = true;
                Invalidate();
            }
            if (dragging)
            {
                dragAt = e.Location;
                FindDrop(Content(e.Location));
                Cursor = Cursors.SizeAll;
                Invalidate();
                return;
            }
        }

        var c = Content(e.Location);
        var h = TileAt(c).It;
        Cursor = h != null || HeadAt(c) >= 0 ? Cursors.Hand : Cursors.Default;
        if (h?.Id == hover?.Id) return;
        hover = h;
        tip.SetToolTip(this, h == null ? "" : $"{h.Name}\n{h.Group}\n\n{string.Join("\n", h.Rows.Select(x => $"{x.Label}: {x.Value}"))}\n\nClick for actions · drag to move");
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) { if (!dragging) { hover = null; Invalidate(); } base.OnMouseLeave(e); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (dragging)
        {
            FindDrop(Content(e.Location)); // where the button came up, not the last move
            var it = pressed;
            dragging = false;
            pressed = null;
            Capture = false;
            tip.Active = true;
            Cursor = Cursors.Default;
            Drop(it);
            var items = pendingItems;
            pendingItems = null;
            Rebuild(items);
            return;
        }
        var c = Content(e.Location);
        var tile = TileAt(c).It;
        pressed = null;
        if (tile != null && (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)) { TileMenu(tile, e.Location); return; }
        int head = HeadAt(c);
        if (head >= 0) GroupMenu(head, e.Location);
    }

    void Drop(Item it)
    {
        if (dropNew)
        {
            var name = Prompt.Ask(FindForm(), "New group", Grouping.NewName(groups));
            if (name == null) return;
            Grouping.NewGroup(Settings, groups, it.Id, name);
        }
        else if (dropGroup >= 0) Grouping.Move(Settings, groups, it.Id, dropGroup, dropIndex);
        else return;
        Settings.Save();
    }

    ContextMenuStrip Menu() => new() { Renderer = new ToolStripProfessionalRenderer(new MenuColors()), ShowImageMargin = false, Font = new Font("Segoe UI", 9.5f) };

    void Item(ContextMenuStrip m, string text, Action a, bool on = true)
    {
        var mi = new ToolStripMenuItem(text) { Enabled = on, ForeColor = Theme.Text };
        mi.Click += (_, _) => a();
        m.Items.Add(mi);
    }

    void ShowMenu(ContextMenuStrip m, Point at)
    {
        tip.Hide(this);
        m.Closed += (_, _) => BeginInvoke(m.Dispose);
        m.Show(this, at);
    }

    void TileMenu(Item it, Point at)
    {
        var m = Menu();
        m.Items.Add(new ToolStripLabel(it.Name) { Font = new Font("Segoe UI Semibold", 9.5f), ForeColor = Theme.Text });
        m.Items.Add(new ToolStripLabel($"{it.Group} · {it.Label}") { ForeColor = Theme.Muted });
        m.Items.Add(new ToolStripSeparator());
        Item(m, "Run", () => Act?.Invoke(it.Id, "run"), it.CanRun);
        Item(m, "Stop", () => Act?.Invoke(it.Id, "stop"), it.CanStop);
        Item(m, it.Enabled ? "Disable" : "Enable", () => Act?.Invoke(it.Id, it.Enabled ? "disable" : "enable"));
        m.Items.Add(new ToolStripSeparator());
        if (it.CanDelete) Item(m, "Delete…", () => Act?.Invoke(it.Id, "delete"));
        Item(m, "Hide from board", () => Act?.Invoke(it.Id, "hide"));
        ShowMenu(m, at);
    }

    void GroupMenu(int index, Point at)
    {
        var m = Menu();
        m.Items.Add(new ToolStripLabel(groups[index].Name) { Font = new Font("Segoe UI Semibold", 9.5f), ForeColor = Theme.Text });
        m.Items.Add(new ToolStripSeparator());
        void Done() { Settings.Save(); Rebuild(null); }
        Item(m, "Rename…", () =>
        {
            var name = Prompt.Ask(FindForm(), "Rename group", groups[index].Name);
            if (name == null || name == groups[index].Name) return;
            if (groups.Any(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            { MessageBox.Show(FindForm(), $"There's already a group called \"{name}\".", "Rename group"); return; }
            Grouping.Rename(Settings, groups, index, name);
            Done();
        });
        Item(m, "Move up", () => { Grouping.Shift(Settings, groups, index, -1); Done(); }, index > 0);
        Item(m, "Move down", () => { Grouping.Shift(Settings, groups, index, 1); Done(); }, index < groups.Count - 1);
        m.Items.Add(new ToolStripSeparator());
        Item(m, "Delete group", () => { Grouping.Delete(Settings, groups, index); Done(); });
        ShowMenu(m, at);
    }
}

class MainForm : Form
{
    public static float DpiScale = 1;
    public static int S(int px) => (int)Math.Round(px * DpiScale);

    const string GUpdate = "", GSettings = "", GAdd = "", GAdmin = "";
    readonly Settings settings = Settings.Load();
    readonly TileBoard tiles = new() { Dock = DockStyle.Fill };
    readonly Label error = new() { Dock = DockStyle.Top, ForeColor = Theme.Fail, AutoEllipsis = true, Visible = false };
    readonly Panel header = new() { Dock = DockStyle.Top, BackColor = Theme.Bg };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 3000 };
    readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 60 * 60 * 1000 };
    readonly Pill add = new("") { Primary = true, Glyph = GAdd }, admin = new("") { Glyph = GAdmin }, gear = new("") { Glyph = GSettings },
        update = new("") { Glyph = GUpdate };
    readonly ToolTip tips = new();
    Updater.Release pending;
    List<Item> last = new();
    string actionError = "";
    bool loading, checking;
    (int running, int ok, int fail, int hidden) counts;

    public MainForm()
    {
        DpiScale = DeviceDpi / 96f;
        Text = Board.IsAdmin ? "TaskBoard (admin)" : "TaskBoard";
        Icon = Icons.AppIcon();
        Font = new Font("Segoe UI", 9f);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        MinimumSize = new Size(S(420), S(300));
        if (settings.Width > 0)
        {
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Math.Max(settings.Width, S(420)), Math.Max(settings.Height, S(300)));
        }
        else
        {
            // First run: a quarter of the screen, top-right corner.
            var wa = Screen.PrimaryScreen.WorkingArea;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(wa.Left + wa.Width / 2, wa.Top, wa.Width / 2, wa.Height / 2);
        }
        if (settings.Maximized) WindowState = FormWindowState.Maximized;

        header.Height = S(54);
        header.Paint += PaintHeader;
        header.Resize += (_, _) => LayoutHeader();

        foreach (var p in new[] { add, admin, gear, update }) p.Height = S(32);
        add.Click += (_, _) => { using var f = new AddForm(); if (f.ShowDialog(this) == DialogResult.OK) Reload(); };
        admin.Visible = !Board.IsAdmin;
        admin.Click += (_, _) => RestartAsAdmin();
        gear.Click += (_, _) => OpenSettings();
        update.Click += async (_, _) => { if (pending != null) await ApplyUpdate(); else await CheckUpdate(manual: true); };
        tips.SetToolTip(add, "Add server");
        tips.SetToolTip(admin, "Run as admin: see every task and control services");
        tips.SetToolTip(gear, "Settings: hidden apps");
        tips.SetToolTip(update, "Check for updates");
        foreach (var p in new[] { add, admin, gear, update }) p.Fit();
        header.Controls.AddRange(new Control[] { add, admin, gear, update });

        error.Height = S(22);
        error.Padding = new Padding(S(14), 0, S(14), S(2));
        tiles.Act += OnAct;

        Controls.Add(tiles);
        Controls.Add(error);
        Controls.Add(header);
        tiles.Settings = settings;

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

    void LayoutHeader()
    {
        int pad = S(14), gap = S(6), top = (header.Height - S(32)) / 2;
        int x = header.Width - pad;
        foreach (var p in new Control[] { add, gear, update, admin })
        {
            if (!p.Visible) continue;
            x -= p.Width;
            p.Location = new Point(x, top);
            x -= gap;
        }
        header.Invalidate();
    }

    void PaintHeader(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        Draw.Smooth(g);
        int pad = S(14), cy = header.Height / 2;
        int limit = new[] { add, gear, update, admin }.Where(p => p.Visible).Min(p => p.Left) - S(8);
        using var title = new Font("Segoe UI Semibold", 13f);
        int tw = Draw.Measure("TaskBoard", title);
        Draw.Text(g, "TaskBoard", title, new Rectangle(pad - S(3), cy - S(16), tw + S(4), S(32)), Theme.Text);
        int x = pad + tw + S(8);

        using var cf = new Font("Segoe UI Semibold", 8.5f);
        foreach (var (n, label, c) in new[] { (counts.running, "running", Theme.Run), (counts.ok, "ok", Theme.Ok), (counts.fail, "failed", Theme.Fail) })
        {
            if (n == 0 && label != "running") continue;
            var text = $"{n} {label}";
            int w = Draw.Measure(text, cf) + S(24);
            if (x + w > limit) return;
            var r = new Rectangle(x, cy - S(11), w, S(22));
            Draw.Fill(g, r, r.Height / 2f, Color.FromArgb(Theme.Dark ? 34 : 26, c));
            using (var dot = new SolidBrush(c)) g.FillEllipse(dot, r.X + S(8), cy - S(3), S(6), S(6));
            Draw.Text(g, text, cf, new Rectangle(r.X + S(17), r.Y, w - S(19), r.Height), c);
            x += w + S(6);
        }
        var hid = $"{counts.hidden} hidden";
        if (counts.hidden > 0 && x + Draw.Measure(hid, cf) < limit)
            Draw.Text(g, hid, cf, new Rectangle(x + S(2), cy - S(11), Draw.Measure(hid, cf) + S(4), S(22)), Theme.Muted);
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

    // The update button is a ⟳ icon; it turns into words only while there's something to say.
    void UpdateButton(string text, Color? color = null)
    {
        update.Glyph = text == null ? GUpdate : null;
        update.Text = text ?? "";
        update.TextColor = color;
        update.Fit();
        LayoutHeader();
    }

    // Hourly/at launch: quiet. From the button: says "Checking…", then "Up to date" or the error.
    async Task CheckUpdate(bool manual = false)
    {
        if (checking) return;
        checking = true;
        try
        {
            if (manual) { update.Enabled = false; UpdateButton("Checking…"); }
            pending = await Updater.Check();
            if (pending != null) UpdateButton($"Update to v{pending.Version.ToString(3)}", Theme.Run);
            else if (manual)
            {
                UpdateButton("Up to date", Theme.Run);
                await Task.Delay(2500);
                if (pending == null) UpdateButton(null);
            }
        }
        catch (Exception ex)
        {
            if (manual) { UpdateButton(null); ShowError("Couldn't check for updates: " + ex.Message); }
        }
        finally { update.Enabled = true; checking = false; }
    }

    async Task ApplyUpdate()
    {
        if (pending == null) return;
        update.Enabled = false;
        UpdateButton("Updating…");
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
            UpdateButton($"Update to v{pending.Version.ToString(3)}", Theme.Run);
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

    void Render()
    {
        var visible = last.Where(i => !settings.IsHidden(i)).ToList();
        counts = (visible.Count(i => i.Kind == "running"), visible.Count(i => i.Kind == "ok"), visible.Count(i => i.Kind == "fail"), last.Count - visible.Count);
        header.Invalidate();

        tiles.SetItems(visible);
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
