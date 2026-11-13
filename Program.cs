// TaskBoard: visual grid of background apps (Task Scheduler, services, startup apps), plus "Add server".
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
        if (args.Length > 0 && args[0] == "--list") return ListToFile();
        // The window always runs as admin (sees every task, controls services). Command-line modes stay unelevated.
        if (!Board.IsAdmin && !args.Contains("--no-elevate"))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = true, Verb = "runas" });
                return 0;
            }
            catch (System.ComponentModel.Win32Exception) { } // said no to UAC: open normally, "Run as admin" button stays
        }
        Updater.Cleanup();
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    // TaskBoard.exe --list → board.txt next to the exe: everything the board would show (for troubleshooting).
    static int ListToFile()
    {
        var (items, errors) = Board.Gather();
        var settings = Settings.Load();
        var lines = errors.Select(e => "ERROR " + e)
            .Concat(items.Select(i => $"{(settings.IsHidden(i) ? "HIDDEN " : "       ")}{i.Source,-8} {i.Label,-12} {i.Name}  [{string.Join(" | ", i.Rows.Select(r => r.Label + ": " + r.Value))}]  {i.Tip.Replace("\n", " ")}"));
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "board.txt"), lines);
        return errors.Count == 0 ? 0 : 1;
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

    [ThreadStatic] static dynamic svc; // one per thread: the board refreshes in the background
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

    // Start a program as the signed-in user at normal (non-admin) level, even from an elevated TaskBoard:
    // a throwaway interactive task does the launch, then is removed (the program keeps running).
    public static void RunAsUser(string file, string extra)
    {
        var (exe, args) = Command(file, extra);
        dynamic def = Svc.NewTask(0);
        def.RegistrationInfo.Description = "TaskBoard one-off launch";
        def.Settings.ExecutionTimeLimit = "PT0S";
        def.Settings.DisallowStartIfOnBatteries = false;
        def.Settings.StopIfGoingOnBatteries = false;
        def.Principal.LogonType = 3;   // interactive, current user
        def.Principal.RunLevel = 0;    // least privilege
        dynamic a = def.Actions.Create(0);
        a.Path = exe;
        a.Arguments = args;
        a.WorkingDirectory = System.IO.Path.GetDirectoryName(file) ?? "";
        string name = "TaskBoard launch " + Guid.NewGuid().ToString("N")[..8];
        dynamic folder = Svc.GetFolder("\\");
        dynamic task = folder.RegisterTaskDefinition(name, def, 6, WindowsIdentity.GetCurrent().Name, null, 3, null);
        try
        {
            task.Run(null);
            for (int i = 0; i < 30 && (int)task.State != 4 && (DateTime)task.LastRunTime < DateTime.Now.AddMinutes(-1); i++) Thread.Sleep(100);
            Thread.Sleep(300);
        }
        finally { folder.DeleteTask(name, 0); }
    }
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
        Bg = Dark ? H(0x0D0F14) : H(0xF2F3F6),
        Card = Dark ? H(0x171A21) : H(0xFFFFFF),
        Raised = Dark ? H(0x20242D) : H(0xF7F8FA),
        Text = Dark ? H(0xECEEF2) : H(0x1A1D24),
        Muted = Dark ? H(0x8E95A2) : H(0x6B7280),
        Line = Dark ? H(0x2A2F3A) : H(0xE2E4E9),
        Accent = Dark ? H(0x7C9CFF) : H(0x3B5BDB),
        Ok = Dark ? H(0x7C9CFF) : H(0x3B5BDB),      // task finished fine
        Run = Dark ? H(0x34D399) : H(0x059669),     // up and running
        Fail = Dark ? H(0xF87171) : H(0xDC2626),
        Warn = Dark ? H(0xFBBF24) : H(0xD97706),
        Off = Dark ? H(0xA1A8B5) : H(0x6B7280);

    // Translucent white on dark, black on light: for pills/buttons drawn over the blurred logo backdrops.
    public static Color Wash(int alpha) => Dark ? Color.FromArgb(alpha, 255, 255, 255) : Color.FromArgb(alpha, 0, 0, 0);

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
            Icons.Forget("task:\\" + n);
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

            // One-off launch as the user: the script runs, and no task is left behind.
            var marker = Path.Combine(Path.GetTempPath(), "taskboard-runasuser.txt");
            File.Delete(marker);
            var launcher = Path.Combine(Path.GetTempPath(), "taskboard-runasuser.cmd");
            File.WriteAllText(launcher, $"@echo ran> \"{marker}\"");
            Sched.RunAsUser(launcher, "");
            for (int i = 0; i < 50 && !File.Exists(marker); i++) Thread.Sleep(100);
            Check(File.Exists(marker), "run as user launched");
            Check(!Sched.List().Any(t => t.Name.StartsWith("TaskBoard launch")), "run as user cleaned up");
            File.Delete(marker);
            File.Delete(launcher);

            // Command splitting for Run keys.
            Check(Cmd.Split("\"C:\\Program Files\\Plex\\Plex Media Server.exe\" --x") == (@"C:\Program Files\Plex\Plex Media Server.exe", "--x"), "split quoted");
            Check(Cmd.Split(@"C:\Program Files\Docker\Docker Desktop.exe") == (@"C:\Program Files\Docker\Docker Desktop.exe", ""), "split unquoted spaces");
            Check(Cmd.Split(@"C:\a\b.exe -dataPath=x") == (@"C:\a\b.exe", "-dataPath=x"), "split args");
            Check(Startup.IsOn(null) && Startup.IsOn(new byte[] { 2, 0 }) && Startup.IsOn(new byte[] { 6, 0 }) && !Startup.IsOn(new byte[] { 3, 0 }), "approved bytes");

            // Process matching: interpreters need their script on the command line; launchers find the app they start.
            var procs = new List<Proc>
            {
                new(1, @"C:\Program Files\nodejs\node.exe", "node \"C:\\pop\\api\\server.mjs\""),
                new(2, @"C:\Program Files\nodejs\node.exe", "node C:\\other\\thing.js"),
                new(3, @"C:\Users\x\Discord\app-1\Discord.exe", "Discord.exe"),
                new(4, @"C:\Program Files\Plex\Plex Media Server.exe", ""),
            };
            Check(Procs.Match(@"C:\Program Files\nodejs\node.exe", "\"C:\\pop\\api\\server.mjs\"", procs).Single().Pid == 1, "match node script");
            Check(Procs.Match(@"C:\Program Files\Plex\Plex Media Server.exe", "", procs).Single().Pid == 4, "match exe");
            Check(Procs.Match(@"C:\Users\x\Discord\Update.exe", "--processStart Discord.exe", procs).Single().Pid == 3, "match launcher");
            Check(Procs.Match(@"C:\pop\start.cmd", "", procs).Count == 0, "no false cmd match");

            // Hiding: updaters hidden by default; a hand-picked choice beats the default either way.
            Item I(string id, string name, bool noise = false) => new Item(id, "service", name, "", "off", "", Array.Empty<Row>(), false, false, true, false, "", "") with { Noise = noise };
            var upd = I("service:gupd", "Google Updater Service", true);
            var plex = I("startup:hkcu|Plex Media Server", "Plex Media Server");
            Check(Board.IsNoise(I("x", "Google Updater Service")) && Board.IsNoise(I("x", "Discord System Helper")) && !Board.IsNoise(plex), "noise names");
            var st = new Settings();
            Check(st.IsHidden(upd) && !st.IsHidden(plex), "default hiding");
            st.SetHidden(upd, false); st.SetHidden(plex, true);
            Check(!st.IsHidden(upd) && st.IsHidden(plex) && st.Shown.Contains(upd.Id) && st.Hidden.Contains(plex.Id), "manual hiding");
            st.SetHidden(upd, true);
            Check(st.IsHidden(upd) && !st.Shown.Contains(upd.Id) && !st.Hidden.Contains(upd.Id), "back to default");
            var round = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(st));
            Check(round.Hidden.SequenceEqual(st.Hidden) && round.View == st.View && round.HideNoise, "settings json");

            // Merging: Tailscale's service + tray app become one card; same-source neighbours and node scripts don't.
            Item P(string src, string id, string name, string exe, string kind = "running") =>
                new Item(id, src, name, "", kind, kind, new[] { new Row("a", "1"), new Row("b", "2"), new Row("c", "3") }, kind != "running", kind == "running", true, src != "service", "", exe);
            var merged = Board.Merge(new List<Item>
            {
                P("service", "service:Tailscale", "Tailscale", @"C:\Program Files\Tailscale\tailscaled.exe"),
                P("startup", "startup:folder|Tailscale.lnk", "Tailscale", @"C:\Program Files\Tailscale\tailscale-ipn.exe", "off"),
                P("service", "service:SplashRemote", "Splashtop Remote Service", @"C:\Splashtop\SRService.exe"),
                P("service", "service:SplashUpd", "Splashtop Update Service", @"C:\Splashtop\SRUpdate.exe"),
                P("task", "task:\\JarvisServer", "JarvisServer", @"C:\Program Files\nodejs\node.exe"),
                P("startup", "startup:folder|Gateway.cmd", "OpenClaw Gateway", @"C:\Program Files\nodejs\node.exe"),
                P("service", "service:Sonarr", "Sonarr", @"C:\ProgramData\Sonarr\bin\Sonarr.Console.exe"),
                P("startup", "startup:hkcu|Sonarr Tray", "Sonarr Tray", @"C:\Other\Sonarr.exe", "off"),
                P("service", "service:Claude", "Claude", @"C:\WindowsApps\Claude\claude.exe"),
                P("startup", "startup:folder|ccum.lnk", "Claude Code Usage Monitor", @"C:\Users\x\ccum.exe"),
            });
            var ts = merged.Single(m => m.Name == "Tailscale");
            Check(merged.Count == 8, "merge count " + merged.Count);
            Check(ts.Id == "service:Tailscale" && ts.Parts.Length == 2 && ts.Group == "Service + Startup app" && ts.CanRun && ts.CanStop && !ts.CanDelete
                && ts.Rows[2].Label == "Tray app" && ts.Has("startup") && ts.Has("service"), "merged tailscale");
            Check(merged.Single(m => m.Id == "service:Sonarr").Parts.Length == 2, "merged by name");
            Check(merged.Count(m => m.Name.StartsWith("Splashtop")) == 2 && merged.Single(m => m.Id == "task:\\JarvisServer").Parts == null, "no false merges");

            // Your own groups: unplaced apps fall into type groups; moves, new groups, renames and deletes stick (and keep hidden apps).
            var gs = new Settings();
            var svc = P("service", "service:Plex", "Plex", @"C:\p.exe");
            var son = P("service", "service:Sonarr", "Sonarr", @"C:\s.exe");
            var job = P("task", "task:\\Backup", "Backup", @"C:\b.exe");
            var tray = P("startup", "startup:hkcu|Discord", "Discord", @"C:\d.exe");
            var all = new List<Item> { svc, son, job, tray };
            var gl = Grouping.Build(all, gs.Groups);
            Check(string.Join(",", gl.Select(g => g.Name)) == "Services,Scheduled tasks,Startup apps", "type groups");
            Grouping.NewGroup(gs, gl, son.Id, "Media");
            Grouping.Move(gs, gl, svc.Id, gl.FindIndex(g => g.Name == "Media"), 0);
            gl = Grouping.Build(all, gs.Groups);
            var media = gl.Single(g => g.Name == "Media");
            Check(string.Join(",", media.Items.Select(i => i.Name)) == "Plex,Sonarr" && !gl.Any(g => g.Name == "Services"), "moved into new group: " + string.Join(",", gl.Select(g => g.Name)));
            Grouping.Move(gs, gl, svc.Id, gl.IndexOf(media), 5);
            gl = Grouping.Build(all, gs.Groups);
            Check(string.Join(",", gl.Single(g => g.Name == "Media").Items.Select(i => i.Name)) == "Sonarr,Plex", "reorder");
            // Plex gets hidden: it stays in Media's saved list while other moves happen
            var shownNow = all.Where(i => i.Id != svc.Id).ToList();
            gl = Grouping.Build(shownNow, gs.Groups);
            Grouping.Rename(gs, gl, gl.FindIndex(g => g.Name == "Media"), "Movies & TV");
            Grouping.Move(gs, gl, tray.Id, gl.FindIndex(g => g.Name == "Movies & TV"), 0);
            Check(gs.Groups.Single(g => g.Name == "Movies & TV").Ids.Contains(svc.Id), "hidden app kept through rename/move");
            gl = Grouping.Build(all, gs.Groups);
            Grouping.Shift(gs, gl, gl.FindIndex(g => g.Name == "Scheduled tasks"), -1);
            gl = Grouping.Build(all, gs.Groups);
            Check(gl[0].Name == "Scheduled tasks", "move group up");
            Grouping.Delete(gs, gl, gl.FindIndex(g => g.Name == "Movies & TV"));
            gl = Grouping.Build(all, gs.Groups);
            Check(!gl.Any(g => g.Name == "Movies & TV") && gl.Single(g => g.Name == "Services").Items.Count == 2 && gl.Any(g => g.Name == "Startup apps"), "delete group");
            var back = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(gs));
            Check(back.Groups.Select(g => g.Name).SequenceEqual(gs.Groups.Select(g => g.Name)), "groups saved");

            // Logo colour: a red logo tints red.
            using (var red = new Bitmap(32, 32)) { using (var rg = Graphics.FromImage(red)) rg.Clear(Color.FromArgb(220, 30, 40)); var c = Icons.Dominant(red, "x"); Check(c.R > 150 && c.G < 90 && c.B < 90, $"dominant {c}"); }

            // Live read of every source, then a startup entry round trip in HKCU\...\Run.
            var (items, errors) = Board.Gather();
            Check(errors.Count == 0, "gather: " + string.Join("; ", errors));
            Check(items.Any(i => i.Source == "task") && items.Any(i => i.Source == "startup"), "gather sources");
            const string entry = "TaskBoard SelfTest";
            using (var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                run.SetValue(entry, "\"C:\\Windows\\System32\\notepad.exe\" x");
            try
            {
                var key = "hkcu|" + entry;
                Check(Board.Gather().Items.Single(i => i.Id == "startup:" + key).Enabled, "startup listed");
                Startup.Do(key, "disable");
                Check(!Board.Gather().Items.Single(i => i.Id == "startup:" + key).Enabled, "startup disabled");
                Startup.Do(key, "enable");
                Check(Board.Gather().Items.Single(i => i.Id == "startup:" + key).Enabled, "startup enabled");
                Startup.Do(key, "delete");
                Check(!Board.Gather().Items.Any(i => i.Id == "startup:" + key), "startup deleted");
            }
            finally
            {
                using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                run?.DeleteValue(entry, false);
            }

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
