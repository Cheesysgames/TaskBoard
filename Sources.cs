// Everything the board shows besides Task Scheduler: Windows services and startup apps (Run keys, Startup folders).
// Plex, Sonarr, Radarr etc. never touch Task Scheduler; they live here.
using System.Diagnostics;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TaskBoard;

record Row(string Label, string Value, bool Alert = false);

// One card on the board, whatever it came from. Source: task | service | startup.
// Noise = updaters/helpers/Windows built-ins, hidden by default (Settings can change that).
record Item(string Id, string Source, string Name, string Group, string Kind, string Label, Row[] Rows,
    bool CanRun, bool CanStop, bool Enabled, bool CanDelete, string Tip, string IconExe)
{
    public bool Noise { get; init; }
    public Item[] Parts { get; init; } // set when one app's service/task/startup pieces are shown as one card
    public bool Has(string source) => Source == source || (Parts?.Any(p => p.Source == source) ?? false);
}

record Proc(int Pid, string Exe, string Cmd);

static class Board
{
    public static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    static readonly Regex NoiseName = new(@"updat|elevation|crash|telemetry|helper|\bcontainer\b|frameview|maintenance|autolaunch|securityhealth",
        RegexOptions.IgnoreCase);

    public static bool IsNoise(Item i) =>
        NoiseName.IsMatch(i.Name) // by name only: Discord etc. launch through an Update.exe
        || (i.Source == "startup" && Services.IsMicrosoft(i.IconExe ?? ""));

    // Runs on a background thread. A broken source shows an error but doesn't blank the others.
    public static (List<Item> Items, List<string> Errors) Gather()
    {
        var items = new List<Item>();
        var errors = new List<string>();
        List<Proc> procs = new();
        try { procs = Procs.Snapshot(); } catch (Exception e) { errors.Add("processes: " + e.Message); }
        try { items.AddRange(Sched.List().Select(FromTask)); } catch (Exception e) { errors.Add("Task Scheduler: " + e.Message); }
        try { items.AddRange(Services.List()); } catch (Exception e) { errors.Add("services: " + e.Message); }
        try { items.AddRange(Startup.List(procs)); } catch (Exception e) { errors.Add("startup apps: " + e.Message); }
        var merged = Merge(items.Select(i => i with { Noise = IsNoise(i) }).ToList());
        return (merged.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList(), errors);
    }

    // One app often installs twice: a service (the engine) plus a startup app (its tray icon), e.g. Tailscale.
    // Pieces from different sources that share a name or install folder become one card.
    static readonly Regex Filler = new(@"\b(service|server|tray|app|daemon|agent|gui|ipn)\b|[^a-z0-9]", RegexOptions.IgnoreCase);
    static readonly string[] Interpreters = { "node.exe", "python.exe", "pythonw.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "conhost.exe", "wscript.exe" };

    public static string Norm(string name) => Filler.Replace(Regex.Replace(name ?? "", @"\(.*?\)", ""), "").ToLowerInvariant();

    static string Folder(Item i)
    {
        var exe = i.IconExe ?? "";
        if (exe == "" || Interpreters.Contains(Path.GetFileName(exe).ToLowerInvariant())) return null; // node.exe's folder says nothing
        return Path.GetDirectoryName(exe)?.TrimEnd('\\').ToLowerInvariant();
    }

    public static bool SameApp(Item a, Item b)
    {
        if (a.Source == b.Source || a.Noise != b.Noise) return false; // an updater never folds into the real app
        string na = Norm(a.Name), nb = Norm(b.Name);
        if (na.Length >= 3 && na == nb) return true; // exact: "Claude" is not "Claude Code Usage Monitor"
        var fa = Folder(a);
        return fa != null && fa == Folder(b);
    }

    public static List<Item> Merge(List<Item> items)
    {
        int Rank(Item i) => i.Source switch { "service" => 0, "task" => 1, _ => 2 }; // the engine leads
        var used = new HashSet<Item>();
        var result = new List<Item>();
        foreach (var lead in items.OrderBy(Rank))
        {
            if (used.Contains(lead)) continue;
            used.Add(lead);
            var extras = items.Where(o => !used.Contains(o) && Rank(o) > Rank(lead) && SameApp(lead, o))
                .GroupBy(o => o.Source).Select(g => g.First()).ToList();
            foreach (var e in extras) used.Add(e);
            result.Add(extras.Count == 0 ? lead : Combine(lead, extras));
        }
        return result;
    }

    static string Word(string source) => source switch { "service" => "Service", "task" => "Task", _ => "Startup app" };

    static Item Combine(Item lead, List<Item> extras)
    {
        var parts = new[] { lead }.Concat(extras).ToArray();
        var rows = lead.Rows.Take(2).ToList();
        foreach (var e in extras.Take(3 - rows.Count))
            rows.Add(new Row(e.Source == "startup" ? "Tray app" : Word(e.Source), e.Label, e.Kind is "fail" or "warn"));
        return lead with
        {
            Name = extras.FirstOrDefault(e => e.Source == "startup")?.Name ?? lead.Name, // the tray app has the name people know ("GlobalProtect", not "PanGPS")
            Group = string.Join(" + ", parts.Select(p => Word(p.Source))),
            Rows = rows.ToArray(),
            CanRun = parts.Any(p => p.CanRun),
            CanStop = parts.Any(p => p.CanStop),
            Enabled = parts.Any(p => p.Enabled),
            CanDelete = false,
            Tip = string.Join("\n\n", parts.Select(p => $"{Word(p.Source)}: {p.Tip}")),
            IconExe = extras.FirstOrDefault(e => e.Source == "startup")?.IconExe ?? lead.IconExe, // the tray app has the nicer icon
            Noise = parts.All(p => p.Noise),
            Parts = parts,
        };
    }

    public static Item FromTask(TaskInfo t)
    {
        var (kind, label) = Sched.Status(t);
        return new Item("task:" + t.Path, "task", t.Name, "Task · " + t.Folder, kind, label,
            new[] { new Row("Last run", Fmt.Ago(t.LastRun)), new Row("Result", Sched.Describe(t.Result), t.Result is not (0 or 0x41301)),
                    new Row("Next run", Fmt.Ago(t.NextRun)) },
            t.State == 3, t.State == 4, t.State != 1, true,
            string.IsNullOrEmpty(t.Description) ? t.Command : $"{t.Description}\n\n{t.Command}", Sched.IconExe(t));
    }

    public static string Key(Item i) => i.Id[(i.Id.IndexOf(':') + 1)..];
}

static class Fmt
{
    public static string Ago(DateTime? d)
    {
        if (d == null) return "—";
        var s = (d.Value - DateTime.Now).TotalSeconds;
        var a = Math.Abs(s);
        var txt = a < 60 ? $"{a:0}s" : a < 3600 ? $"{a / 60:0}m" : a < 86400 ? $"{a / 3600:0}h" : $"{a / 86400:0}d";
        return s < 0 ? txt + " ago" : "in " + txt;
    }
}

// Splitting command lines like "C:\a b\x.exe" --flag  /  C:\x.exe --flag  /  C:\Program Files\x\y.exe (unquoted).
static class Cmd
{
    static readonly string[] Scripts = { ".js", ".mjs", ".cjs", ".py", ".pyw", ".ps1", ".cmd", ".bat" };

    public static (string Exe, string Args) Split(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables((cmd ?? "").Trim());
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            return end < 0 ? (cmd.Trim('"'), "") : (cmd[1..end], cmd[(end + 1)..].Trim());
        }
        var m = Regex.Match(cmd, @"^(.+?\.(exe|com|cmd|bat))(\s|$)", RegexOptions.IgnoreCase);
        if (m.Success) return (m.Groups[1].Value, cmd[m.Groups[1].Length..].Trim());
        int sp = cmd.IndexOf(' ');
        return sp < 0 ? (cmd, "") : (cmd[..sp], cmd[(sp + 1)..].Trim());
    }

    // The script a wrapper/interpreter runs (server.mjs, go.cmd ...), if any.
    public static string Script(string args)
    {
        foreach (Match m in Regex.Matches(args ?? "", "\"([^\"]+)\"|(\\S+)"))
        {
            var tok = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (Scripts.Contains(Path.GetExtension(tok).ToLowerInvariant())) return tok;
        }
        return null;
    }
}

static class Wmi
{
    [ThreadStatic] static dynamic cimv2;
    static dynamic Root => cimv2 ??= ((dynamic)Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator"))).ConnectServer(".", "root\\cimv2");

    public static List<object> Query(string wql)
    {
        var list = new List<object>();
        dynamic set = Root.ExecQuery(wql, "WQL", 0); // synchronous
        foreach (dynamic o in set) list.Add(o);
        return list;
    }

    public static dynamic Get(string path) => Root.Get(path);
    public static object P(object o, string name) => ((dynamic)o).Properties_.Item(name).Value;
    public static string S(object o, string name) => P(o, name) as string ?? "";
}

static class Procs
{
    public static List<Proc> Snapshot() =>
        Wmi.Query("SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process")
            .Select(o => new Proc(Convert.ToInt32(Wmi.P(o, "ProcessId")), Wmi.S(o, "ExecutablePath"), Wmi.S(o, "CommandLine")))
            .ToList();

    // Processes that are this command running. Interpreters (node, python, cmd...) must also have the script on their command line.
    public static List<Proc> Match(string exe, string args, List<Proc> procs)
    {
        var real = Sched.IconExe(new TaskInfo("", "", "", 0, null, 0, null, exe, args, ""));
        var script = Cmd.Script(args);
        if (script == null && Path.GetExtension(exe).ToLowerInvariant() is ".cmd" or ".bat") { script = exe; real = "cmd.exe"; }
        var scriptName = script == null ? null : Path.GetFileName(script);
        bool interp = Path.GetFileName(real).ToLowerInvariant() is "node.exe" or "python.exe" or "pythonw.exe" or "cmd.exe" or "powershell.exe" or "pwsh.exe";

        var hits = procs.Where(p =>
        {
            bool exeOk = Path.IsPathRooted(real) && p.Exe != ""
                ? p.Exe.Equals(real, StringComparison.OrdinalIgnoreCase)
                : Path.GetFileName(p.Exe).Equals(Path.GetFileName(real), StringComparison.OrdinalIgnoreCase);
            if (!exeOk) return false;
            return !interp || (scriptName != null && p.Cmd.Contains(scriptName, StringComparison.OrdinalIgnoreCase));
        }).ToList();
        if (hits.Count > 0 || interp) return hits;

        // Launchers like Discord's "Update.exe --processStart Discord.exe": look for the app it starts.
        var inner = Regex.Match(args ?? "", @"(?:^|[\s""])([^\s""]+\.exe)\b", RegexOptions.IgnoreCase);
        if (!inner.Success) return hits;
        var name = Path.GetFileName(inner.Groups[1].Value.Trim());
        return procs.Where(p => Path.GetFileName(p.Exe).Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static void Kill(IEnumerable<int> pids)
    {
        foreach (var pid in pids)
        {
            try { using var p = Process.GetProcessById(pid); p.Kill(true); }
            catch (ArgumentException) { } // already gone
        }
    }
}

// Third-party Windows services (Sonarr/Radarr/Prowlarr, Plex updater, ...). Microsoft's own are skipped.
static class Services
{
    static readonly Dictionary<string, bool> isMicrosoft = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsMicrosoft(string exe)
    {
        lock (isMicrosoft)
        {
            if (isMicrosoft.TryGetValue(exe, out var ms)) return ms;
            var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            try
            {
                ms = exe.StartsWith(windir, StringComparison.OrdinalIgnoreCase)
                     || (File.Exists(exe) && (FileVersionInfo.GetVersionInfo(exe).CompanyName ?? "").Contains("Microsoft", StringComparison.OrdinalIgnoreCase));
            }
            catch { ms = false; }
            return isMicrosoft[exe] = ms;
        }
    }

    public static List<Item> List()
    {
        var list = new List<Item>();
        foreach (var o in Wmi.Query("SELECT Name, DisplayName, State, StartMode, PathName, ProcessId, StartName, Description FROM Win32_Service"))
        {
            var path = Wmi.S(o, "PathName");
            var (exe, _) = Cmd.Split(path);
            if (exe == "" || IsMicrosoft(exe)) continue;
            string name = Wmi.S(o, "Name"), state = Wmi.S(o, "State"), mode = Wmi.S(o, "StartMode");
            int pid = Convert.ToInt32(Wmi.P(o, "ProcessId") ?? 0);
            bool running = state == "Running", disabled = mode == "Disabled";
            var (kind, label) = disabled ? ("off", "Disabled")
                : running ? ("running", "Running")
                : state is "Start Pending" or "Stop Pending" ? ("warn", state.Replace(" Pending", "ing…"))
                : mode == "Auto" ? ("warn", "Stopped") : ("off", "Stopped");
            var desc = Wmi.S(o, "Description");
            list.Add(new Item("service:" + name, "service", Wmi.S(o, "DisplayName") is { Length: > 0 } dn ? dn : name, "Service", kind, label,
                new[] { new Row("Starts", mode == "Auto" ? "Automatically" : mode), new Row("Process", running && pid > 0 ? $"PID {pid}" : "—"),
                        new Row("Runs as", Wmi.S(o, "StartName").Replace("NT AUTHORITY\\", "")) },
                !running && !disabled && state == "Stopped", running, !disabled, false,
                (desc == "" ? "" : desc + "\n\n") + path, exe));
        }
        return list;
    }

    // Start/stop/start-mode need admin for most services; WMI says so with code 2.
    public static void Do(string name, string act)
    {
        dynamic s = Wmi.Get($"Win32_Service.Name='{name.Replace("'", "\\'")}'");
        int code = act switch
        {
            "run" => (int)s.StartService(),
            "stop" => (int)s.StopService(),
            "enable" => (int)s.ChangeStartMode("Automatic"),
            "disable" => (int)s.ChangeStartMode("Disabled"),
            _ => 0,
        };
        if (code is 0 or 10) return; // 10 = already running
        throw new Exception(code == 2 ? "Windows needs admin for that. Click \"Run as admin\" at the top." : $"Windows service error {code}");
    }
}

// Apps that start at sign-in: the Run registry keys and the Startup folders, with Task Manager's on/off switch.
static class Startup
{
    const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Run32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    record Spot(string Code, RegistryKey Hive, string Key, string Folder, string ApprovedKey, string From);

    static Spot[] Spots => new[]
    {
        new Spot("hkcu", Registry.CurrentUser, Run, null, "Run", "Registry (you)"),
        new Spot("hklm", Registry.LocalMachine, Run, null, "Run", "Registry (all users)"),
        new Spot("hklm32", Registry.LocalMachine, Run32, null, "Run32", "Registry (all users)"),
        new Spot("folder", Registry.CurrentUser, null, Environment.GetFolderPath(Environment.SpecialFolder.Startup), "StartupFolder", "Startup folder (you)"),
        new Spot("commonfolder", Registry.LocalMachine, null, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "StartupFolder", "Startup folder (all users)"),
    };

    // Ids look like startup:hkcu|Plex Media Server  or  startup:folder|OpenClaw Gateway.cmd
    static (Spot spot, string entry) Parse(string key)
    {
        int bar = key.IndexOf('|');
        return (Spots.Single(s => s.Code == key[..bar]), key[(bar + 1)..]);
    }

    public static bool IsOn(byte[] approved) => approved == null || approved.Length == 0 || (approved[0] & 1) == 0; // 02/06 on, 03/07 off

    static bool On(Spot s, string entry)
    {
        using var k = s.Hive.OpenSubKey(Approved + s.ApprovedKey);
        return IsOn(k?.GetValue(entry) as byte[]);
    }

    static (string Exe, string Args) CommandOf(Spot s, string entry)
    {
        if (s.Folder == null)
        {
            using var k = s.Hive.OpenSubKey(s.Key);
            return Cmd.Split(k?.GetValue(entry) as string);
        }
        var file = Path.Combine(s.Folder, entry);
        if (!entry.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return (file, "");
        try
        {
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic lnk = sh.CreateShortcut(file);
            return ((string)lnk.TargetPath, (string)lnk.Arguments);
        }
        catch { return (file, ""); }
    }

    static IEnumerable<string> Entries(Spot s)
    {
        if (s.Folder != null)
            return Directory.Exists(s.Folder) ? Directory.GetFiles(s.Folder).Select(Path.GetFileName).Where(f => !f.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) : Array.Empty<string>();
        using var k = s.Hive.OpenSubKey(s.Key);
        return k?.GetValueNames().Where(n => n != "").ToArray() ?? Array.Empty<string>();
    }

    public static List<Item> List(List<Proc> procs)
    {
        var list = new List<Item>();
        foreach (var s in Spots)
            foreach (var entry in Entries(s))
            {
                var (exe, args) = CommandOf(s, entry);
                bool on = On(s, entry);
                var hits = Procs.Match(exe, args, procs);
                // Running wins: a disabled entry can still be running from this session or started by hand.
                var (kind, label) = hits.Count > 0 ? ("running", "Running") : !on ? ("off", "Disabled") : ("off", "Not running");
                var name = s.Folder == null ? entry : Path.GetFileNameWithoutExtension(entry);
                list.Add(new Item($"startup:{s.Code}|{entry}", "startup", name, "Startup app", kind, label,
                    new[] { new Row("Starts", on ? "At sign-in" : "Not at sign-in"),
                            new Row("Process", hits.Count == 0 ? "—" : hits.Count == 1 ? $"PID {hits[0].Pid}" : $"{hits.Count} running"),
                            new Row("From", s.From) },
                    hits.Count == 0, hits.Count > 0, on, true, $"{exe} {args}".Trim(), Launched(exe, args) ?? exe));
            }
        return list;
    }

    // "Update.exe --processStart Discord.exe": the real app sits next to the launcher (or in an app-x.y folder), use its icon.
    public static string Launched(string exe, string args)
    {
        try
        {
            var m = Regex.Match(args ?? "", @"(?:^|[\s""])([^\s""\\/]+\.exe)\b", RegexOptions.IgnoreCase);
            var dir = Path.GetDirectoryName(exe);
            if (!m.Success || dir == null || !Directory.Exists(dir)) return null;
            var name = m.Groups[1].Value;
            var direct = Path.Combine(dir, name);
            if (File.Exists(direct)) return direct;
            return Directory.GetDirectories(dir).OrderByDescending(d => d).Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
        }
        catch { return null; }
    }

    public static void Do(string key, string act)
    {
        var (s, entry) = Parse(key);
        switch (act)
        {
            case "run" when Board.IsAdmin:
                // Don't hand admin rights to the app (Plex as admin would make files your account can't touch).
                var (re, ra) = CommandOf(s, entry);
                Sched.RunAsUser(re, ra);
                break;
            case "run":
                if (s.Folder != null) Process.Start(new ProcessStartInfo(Path.Combine(s.Folder, entry)) { UseShellExecute = true });
                else
                {
                    var (exe, args) = CommandOf(s, entry);
                    Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" });
                }
                break;
            case "stop":
                var (e, a) = CommandOf(s, entry);
                Procs.Kill(Procs.Match(e, a, Procs.Snapshot()).Select(p => p.Pid));
                break;
            case "enable":
            case "disable":
                Admin(() =>
                {
                    using var k = s.Hive.CreateSubKey(Approved + s.ApprovedKey);
                    var v = new byte[12];
                    v[0] = (byte)(act == "enable" ? 2 : 3);
                    if (act == "disable") BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(v, 4);
                    k.SetValue(entry, v, RegistryValueKind.Binary);
                });
                break;
            case "delete":
                Admin(() =>
                {
                    if (s.Folder != null) File.Delete(Path.Combine(s.Folder, entry));
                    else { using var k = s.Hive.OpenSubKey(s.Key, true); k?.DeleteValue(entry, false); }
                    using var ak = s.Hive.OpenSubKey(Approved + s.ApprovedKey, true);
                    ak?.DeleteValue(entry, false);
                });
                break;
        }
    }

    static void Admin(Action a)
    {
        try { a(); }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        { throw new Exception("Windows needs admin for that one (it's set for all users). Click \"Run as admin\" at the top."); }
    }
}
