using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

internal static class Program
{
    const string StateKey = @"SOFTWARE\SteamStudyLock";
    const string ImageKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\steam.exe";
    const string TaskName = "SteamStudyLock_Expiry";
    const string PlayTask = "SteamStudyLock_PlayTimer";
    static bool preview;
    static string Installed { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SteamStudyLock", "SteamStudyLock.exe"); } }
    static string DebuggerCommand { get { return "\"" + Installed + "\" --blocked"; } }
    static RegistryKey Machine { get { return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64); } }

    sealed class Record
    {
        public DateTime Start;
        public int Minutes;
        public bool Active { get { return IsActive(Start, Minutes, DateTime.UtcNow); } }
    }
    static bool IsActive(DateTime start, int minutes, DateTime now) { return now < start.AddMinutes(minutes); }
    sealed class PlayRecord
    {
        public DateTime Start;
        public int Minutes;
        public DateTime? Warned;
    }
    // The grace period starts when the reminder appears, including after sleep.
    static int PlayStage(PlayRecord r, DateTime now)
    {
        if (r.Warned.HasValue) return now >= r.Warned.Value.AddMinutes(10) ? 2 : 1;
        return now >= r.Start.AddMinutes(r.Minutes) ? 1 : 0;
    }
    static PlayRecord ReadPlay()
    {
        if (preview) return null;
        using (var root = Machine)
        using (var key = root.OpenSubKey(StateKey))
        {
            string value = key == null ? null : key.GetValue("PlaySession") as string;
            if (value == null) return null;
            var p = value.Split('|');
            long warning = long.Parse(p[2], CultureInfo.InvariantCulture);
            return new PlayRecord { Start = new DateTime(long.Parse(p[0], CultureInfo.InvariantCulture), DateTimeKind.Utc), Minutes = int.Parse(p[1], CultureInfo.InvariantCulture), Warned = warning == 0 ? (DateTime?)null : new DateTime(warning, DateTimeKind.Utc) };
        }
    }
    static void SavePlay(PlayRecord r)
    {
        using (var root = Machine)
        using (var key = root.CreateSubKey(StateKey))
            key.SetValue("PlaySession", r.Start.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + r.Minutes.ToString(CultureInfo.InvariantCulture) + "|" + (r.Warned.HasValue ? r.Warned.Value.Ticks : 0).ToString(CultureInfo.InvariantCulture));
    }
    static void FinishPlay()
    {
        using (var root = Machine)
        using (var key = root.CreateSubKey(StateKey)) key.DeleteValue("PlaySession", false);
        TaskCommand("/Delete /TN " + PlayTask + " /F", false);
    }
    static void EnsureInstalled()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Installed));
        if (!string.Equals(Application.ExecutablePath, Installed, StringComparison.OrdinalIgnoreCase))
        {
            byte[] source = File.ReadAllBytes(Application.ExecutablePath);
            byte[] target = File.Exists(Installed) ? File.ReadAllBytes(Installed) : new byte[0];
            bool equal = source.Length == target.Length;
            for (int i = 0; equal && i < source.Length; i++) equal = source[i] == target[i];
            if (!equal) File.Copy(Application.ExecutablePath, Installed, true);
        }
    }
    static void BeginPlay(int minutes)
    {
        using (var mutex = new System.Threading.Mutex(false, @"Global\SteamStudyLock_State"))
        {
            try { mutex.WaitOne(); } catch (System.Threading.AbandonedMutexException) { }
            try
            {
                var locked = Read();
                if (locked != null && locked.Active) throw new InvalidOperationException("封禁期间不能开始游玩计时。");
                if (ReadPlay() != null) throw new InvalidOperationException("游玩计时已开始，不能重新设置。");
                EnsureInstalled();
                CreateTask(PlayTask, "--play-monitor", true);
                try
                {
                    SavePlay(new PlayRecord { Start = DateTime.UtcNow, Minutes = minutes });
                    Process.Start(new ProcessStartInfo(Installed, "--play-monitor") { UseShellExecute = true });
                }
                catch { FinishPlay(); throw; }
            }
            finally { mutex.ReleaseMutex(); }
        }
    }
    static Record Read()
    {
        using (var root = Machine)
        using (var key = root.OpenSubKey(StateKey))
        {
            string value = key == null ? null : key.GetValue("Session") as string;
            if (value == null) return null;
            string[] parts = value.Split('|');
            return new Record { Start = new DateTime(long.Parse(parts[0], CultureInfo.InvariantCulture), DateTimeKind.Utc), Minutes = int.Parse(parts[1], CultureInfo.InvariantCulture) };
        }
    }
    static string CurrentDebugger()
    {
        using (var root = Machine)
        using (var key = root.OpenSubKey(ImageKey))
            return key == null ? null : key.GetValue("Debugger") as string;
    }
    static void TaskCommand(string args, bool required)
    {
        using (var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
        {
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (required && p.ExitCode != 0) throw new InvalidOperationException("无法建立自动恢复任务：" + output);
        }
    }
    static string TaskXml(string executable, string argument, bool interactive)
    {
        string sid = interactive ? WindowsIdentity.GetCurrent().User.Value : "S-1-5-18";
        string esc = System.Security.SecurityElement.Escape(executable);
        return "<?xml version=\"1.0\" encoding=\"utf-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
            "<Triggers><TimeTrigger><Repetition><Interval>PT1M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition><StartBoundary>" + DateTime.Now.AddMinutes(1).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>" +
            "<Principals><Principal id=\"Author\"><UserId>" + sid + "</UserId><LogonType>" + (interactive ? "InteractiveToken" : "ServiceAccount") + "</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
            "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable><ExecutionTimeLimit>PT0S</ExecutionTimeLimit></Settings>" +
            "<Actions Context=\"Author\"><Exec><Command>" + esc + "</Command><Arguments>" + argument + "</Arguments></Exec></Actions></Task>";
    }
    static void CreateTask(string name, string argument, bool interactive)
    {
        string temp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(temp, TaskXml(Installed, argument, interactive), System.Text.Encoding.Unicode);
            TaskCommand("/Create /TN " + name + " /XML \"" + temp + "\" /F", true);
        }
        finally { File.Delete(temp); }
    }
    static void Restore()
    {
        if (string.Equals(CurrentDebugger(), DebuggerCommand, StringComparison.OrdinalIgnoreCase))
        {
            using (var root = Machine)
            using (var key = root.OpenSubKey(ImageKey, true)) key.DeleteValue("Debugger", false);
        }
        TaskCommand("/Delete /TN " + TaskName + " /F", false);
    }
    static void Begin(int minutes)
    {
        using (var mutex = new System.Threading.Mutex(false, @"Global\SteamStudyLock_State"))
        {
            try { mutex.WaitOne(); } catch (System.Threading.AbandonedMutexException) { }
            try { BeginCore(minutes); } finally { mutex.ReleaseMutex(); }
        }
    }
    static Record ReadAndRestore()
    {
        if (preview) return null;
        using (var mutex = new System.Threading.Mutex(false, @"Global\SteamStudyLock_State"))
        {
            try { mutex.WaitOne(); } catch (System.Threading.AbandonedMutexException) { }
            try
            {
                Record r = Read();
                if (r != null && !r.Active && string.Equals(CurrentDebugger(), DebuggerCommand, StringComparison.OrdinalIgnoreCase)) Restore();
                return r;
            }
            finally { mutex.ReleaseMutex(); }
        }
    }
    static void BeginCore(int minutes)
    {
        if (ReadPlay() != null) throw new InvalidOperationException("游玩计时正在进行，请等待本次计时结束后再封禁。");
        var existing = Read();
        if (existing != null && existing.Active) throw new InvalidOperationException("封禁已经开始，不能修改时长。");
        string debugger = CurrentDebugger();
        if (!string.IsNullOrEmpty(debugger) && !string.Equals(debugger, DebuggerCommand, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Steam 已有其他启动拦截设置，请先处理该设置。");
        EnsureInstalled();
        // Install recovery first. No Steam interception is changed if this fails.
        CreateTask(TaskName, "--maintenance", false);
        try
        {
            using (var root = Machine)
            {
                using (var key = root.CreateSubKey(StateKey))
                    key.SetValue("Session", DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + minutes.ToString(CultureInfo.InvariantCulture), RegistryValueKind.String);
                using (var key = root.CreateSubKey(ImageKey)) key.SetValue("Debugger", DebuggerCommand, RegistryValueKind.String);
            }
        }
        catch { Restore(); throw; }
        foreach (var p in Process.GetProcessesByName("steam"))
        {
            try { p.Kill(); } catch { } finally { p.Dispose(); }
        }
    }

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--self-test")
        {
            DateTime start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            if (!IsActive(start, 60, start) || !IsActive(start, 60, start.AddMinutes(59)) || IsActive(start, 60, start.AddMinutes(60)) || IsActive(start, 60, start.AddDays(1))) Environment.Exit(1);
            var play = new PlayRecord { Start = start, Minutes = 60 };
            if (PlayStage(play, start.AddMinutes(59)) != 0 || PlayStage(play, start.AddMinutes(60)) != 1) Environment.Exit(2);
            play.Warned = start.AddMinutes(75);
            if (PlayStage(play, start.AddMinutes(75)) != 1 || PlayStage(play, start.AddMinutes(84).AddSeconds(59)) != 1 || PlayStage(play, start.AddMinutes(85)) != 2 || PlayStage(play, start.AddDays(1)) != 2) Environment.Exit(3);
            var xml = new System.Xml.XmlDocument();
            xml.LoadXml(TaskXml(@"C:\Program Files\SteamStudyLock\SteamStudyLock.exe", "--play-monitor", true));
            var ns = new System.Xml.XmlNamespaceManager(xml.NameTable);
            ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
            if (xml.SelectSingleNode("//t:Command", ns).InnerText != Installed || xml.SelectSingleNode("//t:LogonType", ns).InnerText != "InteractiveToken" || xml.SelectSingleNode("//t:DisallowStartIfOnBatteries", ns).InnerText != "false") Environment.Exit(4);
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "self-test.txt"), "PASS: lock expiry; play limit; full ten-minute grace after delayed reminder; exact forced-close boundary. No system settings changed.");
            return;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length > 1 && args[0] == "--render-preview")
        {
            preview = true;
            using (var form = new LockWindow())
            {
                form.Show(); form.Refresh();
                using (var bitmap = new Bitmap(form.Width, form.Height))
                { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png); }
                form.Close();
            }
            return;
        }
        try
        {
            if (args.Length > 0 && args[0] == "--play-monitor")
            {
                using (var singleton = new System.Threading.Mutex(false, @"Global\SteamStudyLock_PlayMonitor"))
                {
                    bool acquired;
                    try { acquired = singleton.WaitOne(0); } catch (System.Threading.AbandonedMutexException) { acquired = true; }
                    if (!acquired) return;
                    try { Application.Run(new PlayMonitor()); } finally { singleton.ReleaseMutex(); }
                }
                return;
            }
            bool background = args.Length > 0 && args[0] == "--maintenance";
            bool blocked = args.Length > 0 && args[0] == "--blocked";
            Record record = ReadAndRestore();
            if (background) return;
            if (blocked)
            {
                if (record != null && record.Active) return;
                // IFEO supplies the original executable as the next argument.
                if (args.Length > 1 && string.Equals(Path.GetFileName(args[1]), "steam.exe", StringComparison.OrdinalIgnoreCase))
                {
                    // Steam can be opened normally again after restoration.
                    Process.Start(new ProcessStartInfo(args[1]) { UseShellExecute = true });
                }
                return;
            }
            Application.Run(new LockWindow());
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Steam 小黑屋", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    sealed class PlayMonitor : ApplicationContext
    {
        SteamGames games = new SteamGames();
        Timer pulse = new Timer();
        Form reminder;
        bool initialized;
        bool saveReminderShown;
        public PlayMonitor()
        {
            pulse.Interval = 1000;
            pulse.Tick += delegate { Check(); };
            pulse.Start();
        }
        void ShowReminder()
        {
            if (reminder != null && !reminder.IsDisposed) return;
            reminder = new Form { Text = "游玩时间到了", ClientSize = new Size(420, 190), StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, TopMost = true, Font = new Font("SimSun", 11) };
            var title = new Label { Text = "该收尾了。", Font = new Font("Microsoft YaHei", 19, FontStyle.Bold), AutoSize = true, Location = new Point(25, 20) };
            var message = new Label { Text = "游玩时间已到，请保存进度并退出游戏。\n10 分钟后将请求游戏正常退出，\n确认游戏结束后，再退出 Steam。", Location = new Point(27, 70), Size = new Size(370, 65) };
            var ok = new Button { Text = "知道了", Location = new Point(290, 145), Size = new Size(100, 30), Font = new Font("Microsoft YaHei", 10) };
            ok.Click += delegate { reminder.Close(); };
            reminder.Controls.AddRange(new Control[] { title, message, ok });
            reminder.Show();
        }
        void Check()
        {
            using (var mutex = new System.Threading.Mutex(false, @"Global\SteamStudyLock_State"))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(0); } catch (System.Threading.AbandonedMutexException) { acquired = true; }
                if (!acquired) return;
                try
                {
                    var r = ReadPlay();
                    if (r == null) { Stop(); return; }
                    // Track throughout the session, before launchers or Steam exit.
                    games.Observe();
                    int stage = PlayStage(r, DateTime.UtcNow);
                    if (stage == 0) { initialized = true; return; }
                    if (!r.Warned.HasValue)
                    {
                        // A nonmodal reminder cannot pause enforcement when ignored.
                        ShowReminder();
                        r.Warned = DateTime.UtcNow;
                        SavePlay(r);
                    }
                    else if (stage == 1 && !initialized) ShowReminder();
                    initialized = true;
                    if (stage == 2)
                    {
                        bool allClosed = games.CloseGamesAndSteam();
                        if (!allClosed && games.WaitingForGames && !saveReminderShown)
                        {
                            saveReminderShown = true;
                            if (reminder != null) reminder.Close();
                            ShowReminder();
                            reminder.Text = "请先保存并退出游戏";
                            foreach (Control c in reminder.Controls)
                                if (c is Label && c.Height > 50) c.Text = "已请求游戏正常退出，请在游戏中确认保存。\n游戏仍运行时会保留 Steam，不会强杀游戏。\n请完成保存并退出游戏。";
                        }
                        if (allClosed) { FinishPlay(); Stop(); }
                    }
                }
                catch (Exception ex)
                {
                    pulse.Stop();
                    if (reminder != null) reminder.Close();
                    MessageBox.Show("游玩计时遇到错误，系统将于下次检查重试。\n" + ex.Message, "Steam 小黑屋", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    ExitThread();
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
        void Stop()
        {
            pulse.Stop();
            if (reminder != null) reminder.Close();
            ExitThread();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { pulse.Dispose(); if (reminder != null) reminder.Dispose(); }
            base.Dispose(disposing);
        }
    }

    sealed class LockWindow : Form
    {
        Label info = new Label();
        NumericUpDown duration = new NumericUpDown();
        Button begin = new Button();
        Label unit = new Label();
        Label playInfo = new Label();
        NumericUpDown playDuration = new NumericUpDown();
        Button playBegin = new Button();
        Label playUnit = new Label();
        Timer timer = new Timer();
        public LockWindow()
        {
            Text = "Steam 小黑屋";
            ClientSize = new Size(410, 510);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(250, 250, 248);
            Font = new Font("SimSun", 11);
            var title = new Label { Text = "先写论文。", Font = new Font("Microsoft YaHei", 22, FontStyle.Bold), Location = new Point(28, 24), AutoSize = true };
            info.SetBounds(30, 82, 350, 65);
            duration.SetBounds(30, 156, 160, 30);
            duration.Minimum = 1; duration.Maximum = 43200; duration.Value = 60;
            unit.Text = "分钟"; unit.SetBounds(202, 160, 60, 26);
            begin.Text = "开始封禁"; begin.SetBounds(30, 207, 350, 40);
            begin.Font = new Font("Microsoft YaHei", 11);
            begin.Click += delegate {
                begin.Enabled = false;
                try { Begin((int)duration.Value); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
                RefreshState();
            };
            var divider = new Label { BorderStyle = BorderStyle.Fixed3D, Location = new Point(30, 268), Size = new Size(350, 2) };
            var playTitle = new Label { Text = "也给游戏一个期限。", Font = new Font("Microsoft YaHei", 15, FontStyle.Bold), Location = new Point(28, 285), AutoSize = true };
            playInfo.SetBounds(30, 328, 350, 65);
            playDuration.SetBounds(30, 403, 160, 30); playDuration.Minimum = 1; playDuration.Maximum = 43200; playDuration.Value = 60;
            playUnit.Text = "分钟"; playUnit.SetBounds(202, 407, 60, 26);
            playBegin.Text = "开始游玩计时"; playBegin.SetBounds(30, 451, 350, 40); playBegin.Font = new Font("Microsoft YaHei", 11);
            playBegin.Click += delegate {
                playBegin.Enabled = false;
                try { BeginPlay((int)playDuration.Value); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
                RefreshState();
            };
            Controls.AddRange(new Control[] { title, info, duration, unit, begin, divider, playTitle, playInfo, playDuration, playUnit, playBegin });
            timer.Interval = 2000;
            timer.Tick += delegate { RefreshState(); };
            RefreshState(); timer.Start();
            FormClosed += delegate { timer.Dispose(); };
        }
        void RefreshState()
        {
            try
            {
                var r = ReadAndRestore(); bool active = r != null && r.Active;
                var play = ReadPlay();
                duration.Visible = unit.Visible = !active;
                begin.Enabled = !active && play == null;
                begin.Text = active ? "封禁中 · 到期自动恢复" : "开始封禁";
                info.Text = r == null ? "设置时长后立即生效，无法提前取消。\n将关闭 Steam；关闭本窗口仍然有效。" :
                    (active ? "已封禁 Steam" : "上次封禁已结束") + "\n开始时间：" + r.Start.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + "\n封禁时长：" + r.Minutes + " 分钟";
                playDuration.Visible = playUnit.Visible = play == null;
                playBegin.Enabled = !active && play == null;
                playBegin.Text = play == null ? "开始游玩计时" : "计时已开始 · 无法修改";
                playInfo.Text = play == null ? "点击后开始计时，关闭本窗口仍然有效。\n到时提醒，10 分钟后先退出游戏再退出 Steam。" :
                    (play.Warned.HasValue ? "已提醒 · 请保存进度并关闭 Steam" : "游玩计时中") + "\n开始时间：" + play.Start.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + "\n游玩时长：" + play.Minutes + " 分钟";
            }
            catch (Exception ex) { timer.Stop(); info.Text = "读取状态失败：" + ex.Message; begin.Enabled = playBegin.Enabled = false; }
        }
    }
}

// Game ownership is determined by installed library directories, not by the
// current Steam parent PID alone. A game may outlive its launcher or Steam.
internal sealed class SteamGames
{
    static readonly uint CurrentSession = GetCurrentSession();
    static uint GetCurrentSession() { using (var p = Process.GetCurrentProcess()) return (uint)p.SessionId; }
    internal sealed class Item
    {
        internal int Id, Parent;
        internal long Started;
        internal string Path;
    }
    readonly Dictionary<int, Item> tracked = new Dictionary<int, Item>();
    readonly HashSet<string> closeRequests = new HashSet<string>();
    readonly HashSet<string> steamRequests = new HashSet<string>();
    internal bool WaitingForGames { get; private set; }
    List<string> roots = new List<string>();
    DateTime nextRoots;
    static string Normalize(string path) { return System.IO.Path.GetFullPath(path).TrimEnd('\\', '/') + "\\"; }
    internal static bool Under(string path, string folder)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder)) return false;
        try { return System.IO.Path.GetFullPath(path).StartsWith(Normalize(folder), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }
    static string Field(string text, string name)
    {
        var m = Regex.Match(text, "\"" + Regex.Escape(name) + "\"\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Replace("\\\\", "\\") : null;
    }
    internal static List<string> RootsFromLibraries(IEnumerable<string> libraries)
    {
        var result = new List<string>();
        foreach (string library in libraries)
        {
            string apps = System.IO.Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (string manifest in Directory.GetFiles(apps, "appmanifest_*.acf"))
            {
                string dir;
                try { dir = Field(File.ReadAllText(manifest), "installdir"); }
                catch (FileNotFoundException) { continue; }
                if (string.IsNullOrEmpty(dir)) continue;
                string common = System.IO.Path.Combine(apps, "common");
                string game = System.IO.Path.Combine(common, dir);
                // Reject rooted paths and traversal from a malformed manifest.
                if (Under(game, common) && Directory.Exists(game)) result.Add(Normalize(game));
            }
        }
        return result;
    }
    static List<string> GameRoots()
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using (var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
            using (var steam = root.OpenSubKey(@"Software\Valve\Steam"))
            {
                string path = steam == null ? null : steam.GetValue("SteamPath") as string;
                if (!string.IsNullOrEmpty(path)) libraries.Add(path);
            }
            using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
            using (var steam = root.OpenSubKey(@"SOFTWARE\Valve\Steam"))
            {
                string path = steam == null ? null : steam.GetValue("InstallPath") as string;
                if (!string.IsNullOrEmpty(path)) libraries.Add(path);
            }
        }
        var initial = new List<string>(libraries);
        foreach (string library in initial)
        {
            string vdf = System.IO.Path.Combine(library, @"steamapps\libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            string text = File.ReadAllText(vdf);
            foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase))
                libraries.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            // Older Steam clients used numeric keys with path values.
            foreach (Match m in Regex.Matches(text, "\"[0-9]+\"\\s*\"([^\"]+)\""))
            {
                string path = m.Groups[1].Value.Replace("\\\\", "\\");
                if (System.IO.Path.IsPathRooted(path)) libraries.Add(path);
            }
        }
        return RootsFromLibraries(libraries);
    }
    internal static bool Same(Item a, Item b)
    {
        return a.Id == b.Id && a.Started == b.Started && string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
    }
    internal static List<Item> Select(List<Item> snapshot, List<string> gameRoots, Dictionary<int, Item> known)
    {
        var current = new Dictionary<int, Item>();
        foreach (var p in snapshot) current[p.Id] = p;
        foreach (int id in new List<int>(known.Keys))
            if (!current.ContainsKey(id) || !Same(known[id], current[id])) known.Remove(id);
        foreach (var p in snapshot)
            foreach (string root in gameRoots)
                if (Under(p.Path, root)) { known[p.Id] = p; break; }
        bool added;
        do
        {
            added = false;
            foreach (var p in snapshot)
            {
                Item parent;
                if (!known.ContainsKey(p.Id) && !Under(p.Path, Environment.GetFolderPath(Environment.SpecialFolder.Windows)) && known.TryGetValue(p.Parent, out parent) && p.Started >= parent.Started)
                { known[p.Id] = p; added = true; }
            }
        } while (added);
        return new List<Item>(known.Values);
    }
    internal void Observe()
    {
        if (DateTime.UtcNow >= nextRoots) { roots = GameRoots(); nextRoots = DateTime.UtcNow.AddSeconds(15); }
        Select(Snapshot(), roots, tracked);
    }
    internal bool CloseGamesAndSteam()
    {
        // Refresh libraries at enforcement, even if a game was installed recently.
        roots = GameRoots();
        var snapshot = Snapshot();
        var targets = Select(snapshot, roots, tracked);
        // Capture game targets before terminating Steam; parent relationships can disappear.
        RequestCloseSelected(targets, closeRequests);
        var remaining = Select(Snapshot(), roots, tracked);
        WaitingForGames = remaining.Count != 0;
        if (WaitingForGames) return false;
        var steam = snapshot.FindAll(delegate(Item p) { return string.Equals(System.IO.Path.GetFileName(p.Path), "steam.exe", StringComparison.OrdinalIgnoreCase); });
        foreach (var item in steam)
        {
            Item live = Inspect(item.Id, item.Parent);
            if (live == null || !Same(item, live)) continue;
            string key = item.Id + ":" + item.Started;
            if (!steamRequests.Contains(key))
            {
                using (var request = Process.Start(new ProcessStartInfo(item.Path, "-shutdown") { UseShellExecute = false, CreateNoWindow = true })) { }
                steamRequests.Add(key);
            }
        }
        // Keep monitoring while Steam handles its own shutdown and cloud sync.
        return Snapshot().FindAll(delegate(Item p) { return string.Equals(System.IO.Path.GetFileName(p.Path), "steam.exe", StringComparison.OrdinalIgnoreCase); }).Count == 0;
    }
    internal static void RequestCloseSelected(List<Item> targets, HashSet<string> sent)
    {
        // Newest children normally precede older launcher parents.
        targets.Sort(delegate(Item a, Item b) { return b.Started.CompareTo(a.Started); });
        foreach (var item in targets)
        {
            Item live = Inspect(item.Id, item.Parent);
            if (live == null || !Same(item, live)) continue;
            string key = item.Id + ":" + item.Started;
            if (sent.Contains(key)) continue;
            try
            {
                using (var p = Process.GetProcessById(item.Id))
                {
                    // Request normal window closure, allowing the game to prompt
                    // for saving or refuse. Never force-terminate a game.
                    if (p.StartTime.ToUniversalTime().Ticks != item.Started) continue;
                    if (p.CloseMainWindow()) sent.Add(key);
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Entry
    {
        public uint Size, Usage, Id;
        public UIntPtr Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool Process32FirstW(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool Process32NextW(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr handle, uint flags, StringBuilder text, ref uint length);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern bool ProcessIdToSessionId(uint id, out uint session);
    static Item Inspect(int id, int parent)
    {
        uint session;
        if (!ProcessIdToSessionId((uint)id, out session) || session != CurrentSession) return null;
        IntPtr handle = OpenProcess(0x1000, false, id);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(32768); uint length = (uint)path.Capacity;
            long creation, exit, kernel, user;
            if (!QueryFullProcessImageNameW(handle, 0, path, ref length) || !GetProcessTimes(handle, out creation, out exit, out kernel, out user)) return null;
            return new Item { Id = id, Parent = parent, Started = DateTime.FromFileTimeUtc(creation).Ticks, Path = path.ToString() };
        }
        finally { CloseHandle(handle); }
    }
    internal static List<Item> Snapshot()
    {
        var result = new List<Item>();
        IntPtr handle = CreateToolhelp32Snapshot(2, 0);
        if (handle == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entry = new Entry { Size = (uint)Marshal.SizeOf(typeof(Entry)) };
            if (!Process32FirstW(handle, ref entry)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            do
            {
                var item = Inspect((int)entry.Id, (int)entry.Parent);
                if (item != null) result.Add(item);
            } while (Process32NextW(handle, ref entry));
        }
        finally { CloseHandle(handle); }
        return result;
    }
}

