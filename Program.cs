// TaskBoard: visual grid for Windows Task Scheduler, plus "Add server" to install new background apps.
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TaskBoard;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest") return SelfTest.Run();
        if (args.Length > 0 && args[0] == "--update") return Updater.RunHeadless();
        Updater.Cleanup();
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}

record TaskInfo(string Name, string Path, string Folder, int State, DateTime? LastRun, uint Result,
    DateTime? NextRun, string Exe, string Args, string Description)
{
    public string Command => $"{Exe} {Args}".Trim();
}

record NewTask(string Name, string File, string Args, string Trigger, DateTime Time, int Minutes, bool Hidden, bool KeepAlive);

// Task Scheduler via its COM API (Schedule.Service). State: 1 disabled, 2 queued, 3 ready, 4 running.
static class Sched
{
    // Tasks whose full path matches are hidden from the board. Edit to taste.
    static readonly Regex Hide = new(@"\\OneDrive |\\User_Feed_Synchronization|\\NVIDIA App|^\\GoogleUserPEH\\|^\\SoftLanding\\",
        RegexOptions.IgnoreCase);

    static dynamic svc;
    static dynamic Svc
    {
        get
        {
            if (svc == null) { svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")); svc.Connect(); }
            return svc;
        }
    }

    public static List<TaskInfo> List()
    {
        var list = new List<TaskInfo>();
        Walk(Svc.GetFolder("\\"), list);
        return list.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void Walk(dynamic folder, List<TaskInfo> list)
    {
        string fp = folder.Path;
        if (fp.Equals(@"\Microsoft", StringComparison.OrdinalIgnoreCase) || fp.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) return;
        dynamic tasks = folder.GetTasks(1); // 1 = include hidden
        for (int i = 1; i <= tasks.Count; i++)
        {
            dynamic t = tasks[i];
            string path = t.Path;
            if (Hide.IsMatch(path)) continue;
            dynamic def = t.Definition;
            string exe = "", args = "";
            dynamic actions = def.Actions;
            for (int j = 1; j <= actions.Count; j++)
            {
                dynamic a = actions[j];
                if ((int)a.Type == 0) { exe = (string)a.Path ?? ""; args = (string)a.Arguments ?? ""; break; }
            }
            list.Add(new TaskInfo((string)t.Name, path, FolderOf(path), (int)t.State, Date((DateTime)t.LastRunTime),
                unchecked((uint)(int)t.LastTaskResult), Date((DateTime)t.NextRunTime), exe, args,
                (string)def.RegistrationInfo.Description ?? ""));
        }
        dynamic folders = folder.GetFolders(0);
        for (int i = 1; i <= folders.Count; i++) Walk(folders[i], list);
    }

    static DateTime? Date(DateTime d) => d.Year > 2000 ? d : null;
    public static string FolderOf(string path) { int i = path.LastIndexOf('\\'); return i <= 0 ? "\\" : path[..i]; }
    static string NameOf(string path) => path[(path.LastIndexOf('\\') + 1)..];
    static dynamic Get(string path) => Svc.GetFolder(FolderOf(path)).GetTask(NameOf(path));

    public static void Run(string path) => Get(path).Run(null);
    public static void Stop(string path) => Get(path).Stop(0);
    public static void SetEnabled(string path, bool on) => Get(path).Enabled = on;
    public static void Delete(string path) => Svc.GetFolder(FolderOf(path)).DeleteTask(NameOf(path), 0);
    public static bool Exists(string name)
    {
        try { Svc.GetFolder("\\").GetTask(name); return true; }
        catch (Exception e) when (e is FileNotFoundException or COMException) { return false; }
    }

    public static void Create(NewTask n)
    {
        var (exe, args) = Command(n.File, n.Args);
        if (n.Hidden) (exe, args) = Wrap(exe, args);

        dynamic def = Svc.NewTask(0);
        def.RegistrationInfo.Description = "Added by TaskBoard";
        dynamic s = def.Settings;
        s.ExecutionTimeLimit = "PT0S";       // default is 72h, which kills long-running servers
        s.DisallowStartIfOnBatteries = false;
        s.StopIfGoingOnBatteries = false;
        s.StartWhenAvailable = true;
        s.MultipleInstances = 2;             // IgnoreNew: never start a second copy
        def.Principal.LogonType = 3;         // run as the current user, only when logged on

        string now = DateTime.Now.ToString("s");
        switch (n.Trigger)
        {
            case "logon":
                dynamic logon = def.Triggers.Create(9);
                logon.UserId = WindowsIdentity.GetCurrent().Name;
                break;
            case "daily":
                dynamic daily = def.Triggers.Create(2);
                daily.StartBoundary = DateTime.Today.Add(n.Time.TimeOfDay).ToString("s");
                daily.DaysInterval = 1;
                break;
            case "repeat":
                dynamic every = def.Triggers.Create(1);
                every.StartBoundary = now;
                every.Repetition.Interval = $"PT{n.Minutes}M";
                break;
        }
        if (n.KeepAlive)
        {
            // Fires every minute; IgnoreNew makes it a no-op while running, a restart once it has died.
            dynamic watchdog = def.Triggers.Create(1);
            watchdog.StartBoundary = now;
            watchdog.Repetition.Interval = "PT1M";
        }

        dynamic a = def.Actions.Create(0);
        a.Path = exe;
        a.Arguments = args;
        a.WorkingDirectory = System.IO.Path.GetDirectoryName(n.File);
        Svc.GetFolder("\\").RegisterTaskDefinition(n.Name, def, 6, null, null, 3, null); // 6 = create or update
    }

    // What actually runs for a picked file. Scripts get their interpreter.
    public static (string exe, string args) Command(string file, string extra)
    {
        string tail = string.IsNullOrWhiteSpace(extra) ? "" : " " + extra.Trim();
        string q = $"\"{file}\"";
        return System.IO.Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".ps1" => (System.IO.Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                       $"-NoProfile -ExecutionPolicy Bypass -File {q}{tail}"),
            ".js" or ".mjs" or ".cjs" => (Which("node.exe"), q + tail),
            ".py" => (Which("python.exe"), q + tail),
            ".bat" or ".cmd" => (System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c \"{q}{tail}\""),
            _ => (file, tail.TrimStart()),
        };
    }

    // conhost --headless runs a console program with no window (same trick as JarvisServer).
    public static (string exe, string args) Wrap(string exe, string args) =>
        (System.IO.Path.Combine(Environment.SystemDirectory, "conhost.exe"), $"--headless \"{exe}\" {args}".TrimEnd());

    public static string Which(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (dir.Contains(@"\WindowsApps", StringComparison.OrdinalIgnoreCase)) continue; // Store stubs
            var p = System.IO.Path.Combine(dir.Trim(), name);
            if (File.Exists(p)) return p;
        }
        return name;
    }

    // Wrappers (conhost/cmd) have a dull icon; use the real program from the arguments.
    public static string IconExe(TaskInfo t)
    {
        var exe = Environment.ExpandEnvironmentVariables(t.Exe.Trim('"'));
        var file = System.IO.Path.GetFileName(exe).ToLowerInvariant();
        if (file is "conhost.exe" or "cmd.exe")
        {
            var m = Regex.Match(t.Args, "\"([^\"]+\\.exe)\"|(\\S+\\.exe)", RegexOptions.IgnoreCase);
            if (m.Success) exe = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        }
        return System.IO.Path.IsPathRooted(exe) ? exe : Which(exe);
    }

    public static (string Kind, string Label) Status(TaskInfo t) =>
        t.State == 1 ? ("off", "Disabled")
        : t.State == 4 ? ("running", "Running")
        : t.LastRun == null || t.Result == 0x41303 ? ("off", "Never run")
        : t.Result == 0 ? ("ok", "OK")
        : t.Result == 0x41306 ? ("warn", "Terminated")
        : ("fail", "Failed");

    public static string Describe(uint r) => r switch
    {
        0 => "OK",
        0x41300 => "Ready",
        0x41301 => "Running",
        0x41303 => "Never run",
        0x41306 => "Terminated",
        0x8004131F => "Already running",
        0x800710E0 => "Refused (conditions)",
        0x80070002 => "File not found",
        0x8007010B => "Bad working folder",
        < 0x10000 => $"Exit code {r}",
        _ => $"Error 0x{r:X}",
    };
}

// Self-update from GitHub Releases (public repo; a token is optional, see Token()).
// release.ps1 publishes a release tagged vX.Y.Z with TaskBoard.exe attached.
static class Updater
{
    const string Repo = "Cheesysgames/TaskBoard";
    public record Release(Version Version, string AssetUrl);

    public static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
    public static Version Current => Norm(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0));
    public static Version ParseTag(string tag) =>
        Version.TryParse((tag ?? "").Trim().TrimStart('v', 'V'), out var v) ? Norm(v) : null;

    static string TokenFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskBoard", "github_token.txt");

    // Env var, then %APPDATA%\TaskBoard\github_token.txt, then a logged-in GitHub CLI.
    static string Token()
    {
        var t = Environment.GetEnvironmentVariable("TASKBOARD_GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(t)) return t.Trim();
        try { if (File.Exists(TokenFile)) { t = File.ReadAllText(TokenFile).Trim(); if (t != "") return t; } } catch { }
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Sched.Which("gh.exe"), "auth token")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi);
            var output = p.StandardOutput.ReadToEnd().Trim();
            if (p.WaitForExit(5000) && p.ExitCode == 0 && output != "") return output;
        }
        catch { }
        return null;
    }

    static HttpClient Client()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("TaskBoard-Updater");
        // Repo is public, so no token is needed; one is still used if present (higher rate limit).
        var token = Token();
        if (token != null) c.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return c;
    }

    // Returns the newer release, or null when up to date.
    public static async Task<Release> Check()
    {
        using var c = Client();
        using var res = await c.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet (or no access)
        res.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var v = ParseTag(doc.RootElement.GetProperty("tag_name").GetString());
        if (v == null || v <= Current) return null;
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
            if (a.GetProperty("name").GetString().Equals("TaskBoard.exe", StringComparison.OrdinalIgnoreCase))
                return new Release(v, a.GetProperty("url").GetString());
        return null;
    }

    // Windows lets a running exe be renamed, so: download → rename self to .old → move new in → restart.
    public static async Task Apply(Release r)
    {
        var exe = Environment.ProcessPath;
        var fresh = exe + ".new";
        using (var c = Client())
        {
            var req = new HttpRequestMessage(HttpMethod.Get, r.AssetUrl);
            req.Headers.Accept.ParseAdd("application/octet-stream");
            using var res = await c.SendAsync(req); // redirect to the CDN drops the auth header, as it should
            res.EnsureSuccessStatusCode();
            var bytes = await res.Content.ReadAsByteArrayAsync();
            if (bytes.Length < 10_000 || bytes[0] != 'M' || bytes[1] != 'Z') throw new Exception("download isn't a valid exe");
            await File.WriteAllBytesAsync(fresh, bytes);
        }
        var old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try { File.Move(fresh, exe); }
        catch { File.Move(old, exe); throw; }
    }

    public static void Restart() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = true });

    // TaskBoard.exe --update → update.log next to the exe; exit 0 = updated or already current.
    public static int RunHeadless()
    {
        var log = Path.Combine(AppContext.BaseDirectory, "update.log");
        try
        {
            var r = Check().GetAwaiter().GetResult();
            if (r == null) { File.WriteAllText(log, $"up to date (v{Current.ToString(3)})"); return 0; }
            Apply(r).GetAwaiter().GetResult();
            File.WriteAllText(log, $"updated v{Current.ToString(3)} -> v{r.Version.ToString(3)}");
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(log, "FAIL: " + ex.Message); return 1; }
    }

    // The previous exe may still be exiting for a moment after a restart.
    public static void Cleanup()
    {
        var old = Environment.ProcessPath + ".old";
        if (!File.Exists(old)) return;
        Task.Run(async () =>
        {
            for (int i = 0; i < 20; i++)
            {
                try { File.Delete(old); return; } catch { await Task.Delay(500); }
            }
        });
    }
}

static class Theme
{
    public static readonly bool Dark =
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int v && v == 0;
    static Color H(int rgb) => Color.FromArgb(255, Color.FromArgb(rgb));
    public static readonly Color
        Bg = Dark ? H(0x111318) : H(0xF4F5F7),
        Card = Dark ? H(0x1B1E25) : H(0xFFFFFF),
        Text = Dark ? H(0xE7E9EE) : H(0x1D2129),
        Muted = Dark ? H(0x9097A3) : H(0x6B7280),
        Line = Dark ? H(0x2A2E37) : H(0xE3E5E9),
        Ok = Dark ? H(0x22C55E) : H(0x16A34A),
        Run = Dark ? H(0x60A5FA) : H(0x2563EB),
        Fail = Dark ? H(0xF87171) : H(0xDC2626),
        Warn = Dark ? H(0xFBBF24) : H(0xD97706),
        Off = Dark ? H(0x6B7280) : H(0x9CA3AF);

    public static Color Of(string kind) => kind switch { "ok" => Ok, "running" => Run, "fail" => Fail, "warn" => Warn, _ => Off };

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    public static void TitleBar(Form f) { if (!Dark) return; int on = 1; DwmSetWindowAttribute(f.Handle, 20, ref on, 4); }

    public static Button Btn(string text)
    {
        var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = Card, ForeColor = Text, Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
        b.FlatAppearance.BorderColor = Line;
        return b;
    }
}

static class Icons
{
    static readonly Dictionary<string, Image> cache = new();
    public static string LogoDir => Path.Combine(AppContext.BaseDirectory, "logos");
    public static readonly string[] Exts = { ".png", ".jpg", ".jpeg", ".ico" };

    public static void Forget(string path) => cache.Remove(path);

    public static Image Get(TaskInfo t)
    {
        if (cache.TryGetValue(t.Path, out var img)) return img;
        return cache[t.Path] = Custom(t.Name) ?? FromExe(Sched.IconExe(t)) ?? Letter(t.Name);
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
        try { return (Icon.ExtractIcon(exe, 0, 64) ?? Icon.ExtractAssociatedIcon(exe))?.ToBitmap(); } catch { return null; }
    }

    static Image Letter(string name)
    {
        var b = new Bitmap(64, 64);
        using var g = Graphics.FromImage(b);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var bg = new SolidBrush(Theme.Line);
        g.FillEllipse(bg, 0, 0, 63, 63);
        using var font = new Font("Segoe UI Semibold", 26f, GraphicsUnit.Pixel);
        using var fg = new SolidBrush(Theme.Muted);
        g.DrawString(name[..1].ToUpperInvariant(), font, fg, new RectangleF(0, 0, 64, 64),
            new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        return b;
    }

    public static Icon AppIcon()
    {
        var b = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(b))
        using (var br = new SolidBrush(Theme.Run))
        {
            foreach (var (x, y) in new[] { (2, 2), (17, 2), (2, 17), (17, 17) }) g.FillRectangle(br, x, y, 13, 13);
        }
        return Icon.FromHandle(b.GetHicon());
    }
}

class Card : Panel
{
    public readonly string Id;
    public event Action<string, string> Act; // (task path, action)

    static readonly Font FName = new("Segoe UI Semibold", 10.5f), FSmall = new("Segoe UI", 8.5f),
        FBody = new("Segoe UI", 9f), FStatus = new("Segoe UI Semibold", 9f);
    readonly PictureBox logo = new() { SizeMode = PictureBoxSizeMode.Zoom };
    readonly Label name, folder, status, last, result, next;
    readonly Button run, stop, toggle;
    readonly ToolTip tip = new();
    Color accent = Theme.Off;
    int state;

    static int S(int px) => (int)Math.Round(px * MainForm.DpiScale);
    static Rectangle R(int x, int y, int w, int h) => new(S(x), S(y), S(w), S(h));

    public Card(string id)
    {
        Id = id;
        Size = new Size(S(300), S(184));
        Margin = new Padding(S(8));
        BackColor = Theme.Card;
        DoubleBuffered = true;

        logo.Bounds = R(16, 22, 40, 40);
        Controls.Add(logo);
        name = L(66, 20, 132, 22, FName, Theme.Text);
        folder = L(66, 42, 132, 18, FSmall, Theme.Muted);
        status = L(196, 22, 90, 20, FStatus, Theme.Text, ContentAlignment.MiddleRight);
        L(16, 74, 90, 20, FBody, Theme.Muted).Text = "Last run";
        L(16, 95, 90, 20, FBody, Theme.Muted).Text = "Result";
        L(16, 116, 90, 20, FBody, Theme.Muted).Text = "Next run";
        last = L(100, 74, 186, 20, FBody, Theme.Text, ContentAlignment.MiddleRight);
        result = L(100, 95, 186, 20, FBody, Theme.Text, ContentAlignment.MiddleRight);
        next = L(100, 116, 186, 20, FBody, Theme.Text, ContentAlignment.MiddleRight);

        run = B("Run", 16, 62, "run");
        stop = B("Stop", 82, 62, "stop");
        toggle = B("Disable", 148, 74, "toggle");
        B("Delete", 226, 60, "delete");
    }

    Label L(int x, int y, int w, int h, Font f, Color c, ContentAlignment a = ContentAlignment.MiddleLeft)
    {
        var l = new Label { Bounds = R(x, y, w, h), Font = f, ForeColor = c, BackColor = Theme.Card, TextAlign = a, AutoEllipsis = true };
        Controls.Add(l);
        return l;
    }

    Button B(string text, int x, int w, string act)
    {
        var b = Theme.Btn(text);
        b.Bounds = R(x, 146, w, 28);
        b.Font = FBody;
        b.Click += (_, _) => Act?.Invoke(Id, act == "toggle" ? (state == 1 ? "enable" : "disable") : act);
        Controls.Add(b);
        return b;
    }

    public void Bind(TaskInfo t, Image img)
    {
        state = t.State;
        var (kind, label) = Sched.Status(t);
        accent = Theme.Of(kind);
        if (logo.Image != img) logo.Image = img;
        name.Text = t.Name;
        folder.Text = t.Folder;
        status.Text = "● " + label;
        status.ForeColor = accent;
        last.Text = Ago(t.LastRun);
        result.Text = Sched.Describe(t.Result);
        result.ForeColor = t.Result is 0 or 0x41301 ? Theme.Text : accent;
        next.Text = Ago(t.NextRun);
        run.Enabled = t.State == 3;
        stop.Enabled = t.State == 4;
        toggle.Text = t.State == 1 ? "Enable" : "Disable";
        var info = string.IsNullOrEmpty(t.Description) ? t.Command : $"{t.Description}\n\n{t.Command}";
        tip.SetToolTip(logo, info);
        tip.SetToolTip(name, info);
        Invalidate();
    }

    static string Ago(DateTime? d)
    {
        if (d == null) return "—";
        var s = (d.Value - DateTime.Now).TotalSeconds;
        var a = Math.Abs(s);
        var txt = a < 60 ? $"{a:0}s" : a < 3600 ? $"{a / 60:0}m" : a < 86400 ? $"{a / 3600:0}h" : $"{a / 86400:0}d";
        return s < 0 ? txt + " ago" : "in " + txt;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(Theme.Line);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        using var bar = new SolidBrush(accent);
        e.Graphics.FillRectangle(bar, 0, 0, Width, S(4));
    }
}

class MainForm : Form
{
    public static float DpiScale = 1;
    readonly FlowLayoutPanel grid = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
    readonly Label error = new() { Dock = DockStyle.Top, ForeColor = Theme.Fail, AutoEllipsis = true };
    readonly Label nRunning = Count(Theme.Run), nOk = Count(Theme.Ok), nFail = Count(Theme.Fail);
    readonly Dictionary<string, Card> cards = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 3000 };
    readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 60 * 60 * 1000 };
    readonly Button update = Theme.Btn("Update");
    Updater.Release pending;

    static int S(int px) => (int)Math.Round(px * DpiScale);
    static Label Count(Color c) => new() { AutoSize = true, ForeColor = c, Margin = new Padding(0, 0, 16, 0) };

    public MainForm()
    {
        DpiScale = DeviceDpi / 96f;
        Text = "TaskBoard";
        Icon = Icons.AppIcon();
        Font = new Font("Segoe UI", 9f);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        ClientSize = new Size(S(1000), S(640));
        StartPosition = FormStartPosition.CenterScreen;

        var header = new Panel { Dock = DockStyle.Top, Height = S(64), Padding = new Padding(S(24), S(14), S(24), S(14)) };
        var title = new Label { Text = "TaskBoard", Font = new Font("Segoe UI Semibold", 16f), AutoSize = true, Dock = DockStyle.Left };
        var counts = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, Padding = new Padding(S(20), S(10), 0, 0) };
        counts.Controls.AddRange(new Control[] { nRunning, nOk, nFail });
        var add = Theme.Btn("+  Add server");
        add.Dock = DockStyle.Right;
        add.Width = S(130);
        add.Font = new Font("Segoe UI Semibold", 9.5f);
        add.Click += (_, _) => { using var f = new AddForm(); if (f.ShowDialog(this) == DialogResult.OK) Reload(); };
        update.Dock = DockStyle.Right;
        update.Width = S(150);
        update.Font = new Font("Segoe UI Semibold", 9.5f);
        update.ForeColor = Theme.Run;
        update.Visible = false;
        update.Click += async (_, _) => await ApplyUpdate();
        var gap = new Panel { Dock = DockStyle.Right, Width = S(10) };
        var version = new Label { Text = "v" + Updater.Current.ToString(3), AutoSize = true, Dock = DockStyle.Right, ForeColor = Theme.Muted, Padding = new Padding(0, S(10), S(12), 0) };
        header.Controls.Add(version);
        header.Controls.Add(update);
        header.Controls.Add(gap);
        header.Controls.Add(add);
        header.Controls.Add(counts);
        header.Controls.Add(title);

        error.Height = S(22);
        error.Padding = new Padding(S(24), 0, S(24), 0);
        grid.Padding = new Padding(S(16), S(4), S(16), S(16));

        Controls.Add(grid);
        Controls.Add(error);
        Controls.Add(header);

        timer.Tick += (_, _) => Reload();
        updateTimer.Tick += async (_, _) => await CheckUpdate();
        Load += async (_, _) => { Theme.TitleBar(this); Reload(); timer.Start(); updateTimer.Start(); await CheckUpdate(); };
    }

    // Quiet: a failed check (offline, no token) just leaves the button hidden.
    async Task CheckUpdate()
    {
        try { pending = await Updater.Check(); } catch { return; }
        update.Visible = pending != null;
        if (pending != null) update.Text = $"Update to v{pending.Version.ToString(3)}";
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
            error.Text = "Update failed: " + ex.Message;
            update.Enabled = true;
            update.Text = $"Update to v{pending.Version.ToString(3)}";
        }
    }

    void Reload()
    {
        List<TaskInfo> list;
        try { list = Sched.List(); }
        catch (Exception ex) { error.Text = "Could not read Task Scheduler: " + ex.Message; return; }

        var ids = list.Select(t => t.Path).ToHashSet();
        foreach (var gone in cards.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            cards[gone].Dispose();
            cards.Remove(gone);
        }

        grid.SuspendLayout();
        for (int i = 0; i < list.Count; i++)
        {
            var t = list[i];
            if (!cards.TryGetValue(t.Path, out var card))
            {
                card = cards[t.Path] = new Card(t.Path);
                card.Act += OnAct;
                grid.Controls.Add(card);
            }
            card.Bind(t, Icons.Get(t));
            grid.Controls.SetChildIndex(card, i);
        }
        grid.ResumeLayout();

        var kinds = list.Select(t => Sched.Status(t).Kind).ToList();
        nRunning.Text = $"● {kinds.Count(k => k == "running")} running";
        nOk.Text = $"● {kinds.Count(k => k == "ok")} ok";
        nFail.Text = $"● {kinds.Count(k => k == "fail")} failed";
    }

    void OnAct(string path, string act)
    {
        try
        {
            switch (act)
            {
                case "run": Sched.Run(path); break;
                case "stop": Sched.Stop(path); break;
                case "enable": Sched.SetEnabled(path, true); break;
                case "disable": Sched.SetEnabled(path, false); break;
                case "delete":
                    if (MessageBox.Show(this, $"Remove \"{path.TrimStart('\\')}\" from Task Scheduler?\n\nThis doesn't stop it if it's running right now, and doesn't delete any files.",
                            "Delete task", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    Sched.Delete(path);
                    Icons.Forget(path);
                    break;
            }
            error.Text = "";
        }
        catch (Exception ex) { error.Text = $"{act} failed: {ex.Message}"; }
        Reload();
    }
}

class AddForm : Form
{
    readonly TextBox name = Box(), file = Box(), args = Box(), logo = Box();
    readonly ComboBox when = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    readonly DateTimePicker time = new() { Format = DateTimePickerFormat.Time, ShowUpDown = true, Width = 110, Value = DateTime.Today.AddHours(3), Visible = false };
    readonly NumericUpDown minutes = new() { Minimum = 1, Maximum = 1440, Value = 15, Width = 70, Visible = false };
    readonly CheckBox keep = Chk("Keep running (restart within a minute if it stops)"), hidden = Chk("Hide console window"), start = Chk("Start it now");
    readonly Label preview = new() { AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = Theme.Muted, Font = new Font("Consolas", 8.5f), Margin = new Padding(3, 8, 3, 8) };

    static TextBox Box() => new() { Width = 380, BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
    static CheckBox Chk(string text) => new() { Text = text, Checked = true, AutoSize = true, ForeColor = Theme.Text };

    public AddForm()
    {
        Text = "Add server";
        Icon = Icons.AppIcon();
        Font = new Font("Segoe UI", 9f);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        when.Items.AddRange(new object[] { "At logon (always on)", "Daily at", "Every N minutes" });
        when.SelectedIndex = 0;
        when.SelectedIndexChanged += (_, _) =>
        {
            time.Visible = when.SelectedIndex == 1;
            minutes.Visible = when.SelectedIndex == 2;
            keep.Enabled = when.SelectedIndex == 0;
        };

        var browseFile = Theme.Btn("Browse…");
        browseFile.Click += (_, _) => PickFile();
        var browseLogo = Theme.Btn("Browse…");
        browseLogo.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.ico" };
            if (d.ShowDialog(this) == DialogResult.OK) logo.Text = d.FileName;
        };
        file.TextChanged += (_, _) => UpdatePreview();
        args.TextChanged += (_, _) => UpdatePreview();
        hidden.CheckedChanged += (_, _) => UpdatePreview();

        var schedule = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        schedule.Controls.AddRange(new Control[] { when, time, minutes });

        var ok = Theme.Btn("Add");
        ok.Size = new Size(90, 30);
        ok.Click += (_, _) => Save();
        var cancel = Theme.Btn("Cancel");
        cancel.Size = new Size(90, 30);
        cancel.DialogResult = DialogResult.Cancel;
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        AcceptButton = ok;
        CancelButton = cancel;

        var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Fill };
        int r = 0;
        void Row(string label, Control input, Control extra = null)
        {
            if (label != "") t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Theme.Muted }, 0, r);
            t.Controls.Add(input, 1, r);
            if (extra != null) { extra.Size = new Size(80, 26); t.Controls.Add(extra, 2, r); }
            else t.SetColumnSpan(input, 2);
            r++;
        }
        Row("Name", name);
        Row("Program", file, browseFile);
        Row("Arguments", args);
        Row("Logo (optional)", logo, browseLogo);
        Row("Run", schedule);
        Row("", keep);
        Row("", hidden);
        Row("", start);
        Row("Command", preview);
        Row("", buttons);
        Controls.Add(t);

        Load += (_, _) => Theme.TitleBar(this);
    }

    void PickFile()
    {
        using var d = new OpenFileDialog { Filter = "Programs and scripts|*.exe;*.ps1;*.js;*.mjs;*.cjs;*.py;*.bat;*.cmd|All files|*.*" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        file.Text = d.FileName;
        if (name.Text.Trim() != "") return;
        // "server.js" says nothing; the folder it lives in usually names the app.
        var n = Path.GetFileNameWithoutExtension(d.FileName);
        if (n.ToLowerInvariant() is "index" or "server" or "main" or "app" or "start" or "run" or "supervisor")
            n = new DirectoryInfo(Path.GetDirectoryName(d.FileName)).Name;
        name.Text = n;
    }

    void UpdatePreview()
    {
        if (file.Text.Trim() == "") { preview.Text = ""; return; }
        var (exe, a) = Sched.Command(file.Text.Trim(), args.Text);
        if (hidden.Checked) (exe, a) = Sched.Wrap(exe, a);
        preview.Text = $"{exe} {a}";
    }

    void Save()
    {
        var n = name.Text.Trim();
        var f = file.Text.Trim();
        if (n == "" || n.IndexOfAny(@"\/:*?""<>|".ToCharArray()) >= 0) { Warn("Give it a name without \\ / : * ? \" < > |"); return; }
        if (!File.Exists(f)) { Warn("Pick the program or script to run."); return; }
        if (logo.Text.Trim() != "" && !File.Exists(logo.Text.Trim())) { Warn("The logo file doesn't exist."); return; }
        if (Sched.Exists(n) && MessageBox.Show(this, $"A task named \"{n}\" already exists. Replace it?", "Add server",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        try
        {
            var trigger = new[] { "logon", "daily", "repeat" }[when.SelectedIndex];
            Sched.Create(new NewTask(n, f, args.Text, trigger, time.Value, (int)minutes.Value, hidden.Checked,
                keep.Checked && trigger == "logon"));

            if (logo.Text.Trim() != "")
            {
                Directory.CreateDirectory(Icons.LogoDir);
                foreach (var ext in Icons.Exts) File.Delete(Path.Combine(Icons.LogoDir, n + ext));
                var src = logo.Text.Trim();
                File.Copy(src, Path.Combine(Icons.LogoDir, n + Path.GetExtension(src).ToLowerInvariant()), true);
            }
            Icons.Forget("\\" + n);
            if (start.Checked) Sched.Run("\\" + n);
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex) { Warn("Couldn't add it: " + ex.Message); }
    }

    void Warn(string msg) => MessageBox.Show(this, msg, "Add server", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}

// TaskBoard.exe --selftest → writes PASS/FAIL to selftest.log next to the exe, exit code 0/1.
static class SelfTest
{
    public static int Run()
    {
        var log = Path.Combine(AppContext.BaseDirectory, "selftest.log");
        try
        {
            Check(Updater.ParseTag("v1.2.0") == new Version(1, 2, 0) && Updater.ParseTag("1.2") == new Version(1, 2, 0)
                && Updater.ParseTag("junk") == null, "parse tag");
            Check(Updater.ParseTag("v1.10.0") > Updater.ParseTag("v1.9.3") && Updater.ParseTag("v" + Updater.Current) == Updater.Current, "version compare");
            Check(Sched.Describe(0) == "OK" && Sched.Describe(1) == "Exit code 1" && Sched.Describe(0x80070002) == "File not found", "describe");
            Check(Sched.Status(T(1, null, 0)).Kind == "off", "disabled");
            Check(Sched.Status(T(4, DateTime.Now, 0x41301)).Kind == "running", "running");
            Check(Sched.Status(T(3, DateTime.Now, 0)).Kind == "ok", "ok");
            Check(Sched.Status(T(3, DateTime.Now, 1)).Kind == "fail", "fail");
            Check(Sched.Status(T(3, null, 0x41303)).Kind == "off", "never run");

            var (e1, a1) = Sched.Command(@"C:\a b\s.ps1", "-X 1");
            Check(e1.EndsWith("powershell.exe") && a1 == "-NoProfile -ExecutionPolicy Bypass -File \"C:\\a b\\s.ps1\" -X 1", "ps1: " + a1);
            var (e2, a2) = Sched.Command(@"C:\a\app.mjs", "");
            Check(e2.EndsWith("node.exe") && a2 == "\"C:\\a\\app.mjs\"", "node: " + a2);
            var (e3, a3) = Sched.Command(@"C:\a\go.bat", "x");
            Check(e3.EndsWith("cmd.exe") && a3 == "/c \"\"C:\\a\\go.bat\" x\"", "bat: " + a3);
            var (e4, a4) = Sched.Command(@"C:\a\srv.exe", " --port 80 ");
            Check(e4 == @"C:\a\srv.exe" && a4 == "--port 80", "exe: " + a4);

            var jarvis = new TaskInfo("J", "\\J", "\\", 4, null, 0, null, @"C:\WINDOWS\System32\conhost.exe",
                "--headless \"C:\\Program Files\\nodejs\\node.exe\" \"C:\\x\\supervisor.mjs\"", "");
            Check(Sched.IconExe(jarvis) == @"C:\Program Files\nodejs\node.exe", "icon exe");

            // Real round trip through Task Scheduler, using the hardest path: logon + keep-alive + hidden.
            const string name = "TaskBoard SelfTest";
            var script = Path.Combine(Path.GetTempPath(), "taskboard-selftest.cmd");
            File.WriteAllText(script, "@exit /b 0");
            Sched.Create(new NewTask(name, script, "", "logon", DateTime.Now, 0, true, true));
            try
            {
                var t = Sched.List().Single(x => x.Path == "\\" + name);
                Check(t.Exe.EndsWith("conhost.exe") && t.Args.Contains("--headless") && t.Args.Contains("taskboard-selftest.cmd"), "created: " + t.Command);
                Sched.SetEnabled(t.Path, false);
                Check(Sched.List().Single(x => x.Path == "\\" + name).State == 1, "disable");
            }
            finally { Sched.Delete("\\" + name); }
            Check(!Sched.Exists(name), "deleted");

            File.WriteAllText(log, "PASS");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(log, "FAIL: " + ex);
            return 1;
        }
    }

    static TaskInfo T(int state, DateTime? last, uint result) => new("x", "\\x", "\\", state, last, result, null, "", "", "");
    static void Check(bool ok, string what) { if (!ok) throw new Exception("check failed: " + what); }
}
