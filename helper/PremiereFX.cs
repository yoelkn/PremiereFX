// PremiereFX helper for Premiere Pro.
// Catches Ctrl+Space while Premiere is the active window, shows a search box of
// effects, and sends the chosen effect to the PremiereFX UXP plugin over a local
// WebSocket (port 8791). Built with the .NET Framework csc (C# 5), see build.ps1.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PremiereFX
{
    class Effect
    {
        public string Name, Match, Kind, Lower, Hint;
        public string Key { get { return Kind + "|" + Match; } }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var m = new Mutex(true, "Local\\PremiereFXHelper", out created))
            {
                if (!created) return; // already running
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new App());
            }
        }
    }

    static class Native
    {
        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")] public static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr id, uint ms, IntPtr fn);
        [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [StructLayout(LayoutKind.Sequential)] public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

        public static string ProcessPath(uint pid)
        {
            IntPtr h = OpenProcess(0x1000 /*QUERY_LIMITED_INFORMATION*/, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(1024); int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
            }
            finally { CloseHandle(h); }
        }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

        public static void ForceForeground(IntPtr hwnd)
        {
            IntPtr fg = GetForegroundWindow();
            uint pid;
            uint fgThread = GetWindowThreadProcessId(fg, out pid);
            uint me = GetCurrentThreadId();
            bool attached = fgThread != me && AttachThreadInput(me, fgThread, true);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            if (attached) AttachThreadInput(me, fgThread, false);
        }

        public static void RoundCorners(IntPtr hwnd)
        {
            try { int pref = 2; DwmSetWindowAttribute(hwnd, 33, ref pref, 4); } catch { }
        }
    }

    // ---------------------------------------------------------------- app
    class App : ApplicationContext
    {
        const int PORT = 8791;
        static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PremiereFX");
        static readonly string EffectsFile = Path.Combine(DataDir, "effects.json");
        static readonly string RecentFile = Path.Combine(DataDir, "recent.txt");

        readonly Popup popup;
        readonly Toast toast;
        readonly NotifyIcon tray;
        readonly ToolStripMenuItem statusItem;
        readonly WsServer server = new WsServer();
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        readonly KeyHook keyHook = new KeyHook();
        IntPtr premiereHwnd;
        int nextId = 1;

        public List<Effect> Effects = new List<Effect>();
        public List<string> Recent = new List<string>();
        public bool Connected;

        public App()
        {
            Directory.CreateDirectory(DataDir);
            LoadCache();

            popup = new Popup(this);
            toast = new Toast();
            IntPtr force = popup.Handle; // create handle so BeginInvoke works

            statusItem = new ToolStripMenuItem("Waiting for Premiere plugin...") { Enabled = false };
            var menu = new ContextMenuStrip();
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit PremiereFX", null, delegate { ExitThread(); });
            tray = new NotifyIcon { Icon = MakeIcon(), Text = "PremiereFX (Premiere) - Ctrl+Space", ContextMenuStrip = menu, Visible = true };

            server.Message += delegate (string msg) { popup.BeginInvoke(new Action(delegate { OnMessage(msg); })); };
            server.ConnectionChanged += delegate (bool on) { popup.BeginInvoke(new Action(delegate { SetConnected(on); })); };
            server.Start(PORT);

            tray.MouseDoubleClick += delegate { premiereHwnd = IntPtr.Zero; ShowPopup(); };

            keyHook.Hotkey += delegate (IntPtr fg) { popup.BeginInvoke(new Action(delegate { premiereHwnd = fg; ShowPopup(); })); };
            keyHook.Start();
            Log("started");
        }

        public static void Log(string msg)
        {
            try
            {
                string f = Path.Combine(DataDir, "log.txt");
                if (File.Exists(f) && new FileInfo(f).Length > 200000) File.Delete(f);
                File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + "\r\n");
            }
            catch { }
        }

        protected override void ExitThreadCore()
        {
            keyHook.Stop();
            tray.Visible = false;
            server.Stop();
            base.ExitThreadCore();
        }

        void SetConnected(bool on)
        {
            Connected = on;
            statusItem.Text = on ? "Connected to Premiere (" + Effects.Count + " effects)" : "Waiting for Premiere plugin...";
        }

        void ShowPopup()
        {
            if (popup.Visible) { popup.Close2(); return; }
            Native.RECT r;
            Rectangle host = premiereHwnd != IntPtr.Zero && Native.GetWindowRect(premiereHwnd, out r)
                ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)
                : Screen.FromPoint(Cursor.Position).WorkingArea;
            popup.ShowAt(Cursor.Position, host);
        }

        // ---------- plugin messages
        void OnMessage(string text)
        {
            Dictionary<string, object> msg;
            try { msg = json.Deserialize<Dictionary<string, object>>(text); } catch { return; }
            string type = Get(msg, "type");
            if (type == "effects")
            {
                var list = new List<Effect>();
                var items = msg["items"] as System.Collections.ArrayList;
                if (items == null) return;
                foreach (Dictionary<string, object> it in items)
                    list.Add(new Effect { Name = Get(it, "n"), Match = Get(it, "m"), Kind = Get(it, "k") });
                SetEffects(list);
                try { File.WriteAllText(EffectsFile, text, Encoding.UTF8); } catch { }
                SetConnected(true);
            }
            else if (type == "result")
            {
                bool ok = msg.ContainsKey("ok") && msg["ok"] is bool && (bool)msg["ok"];
                toast.ShowMessage(Get(msg, "msg"), ok, Cursor.Position);
            }
        }

        static string Get(Dictionary<string, object> d, string k)
        {
            object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : "";
        }

        void SetEffects(List<Effect> list)
        {
            // Mark duplicate display names so they can be told apart.
            var dupes = new HashSet<string>(list.GroupBy(e => e.Kind + "|" + e.Name.ToLowerInvariant()).Where(g => g.Count() > 1).Select(g => g.Key));
            foreach (var e in list)
            {
                e.Lower = e.Name.ToLowerInvariant();
                if (dupes.Contains(e.Kind + "|" + e.Lower)) e.Hint = e.Match;
            }
            Effects = list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            popup.RefreshResults();
        }

        void LoadCache()
        {
            try { if (File.Exists(EffectsFile)) OnMessageCacheOnly(File.ReadAllText(EffectsFile, Encoding.UTF8)); } catch { }
            try { if (File.Exists(RecentFile)) Recent = File.ReadAllLines(RecentFile).Where(l => l.Length > 0).ToList(); } catch { }
        }

        void OnMessageCacheOnly(string text)
        {
            var msg = json.Deserialize<Dictionary<string, object>>(text);
            var items = msg["items"] as System.Collections.ArrayList;
            var list = new List<Effect>();
            foreach (Dictionary<string, object> it in items)
                list.Add(new Effect { Name = Get(it, "n"), Match = Get(it, "m"), Kind = Get(it, "k") });
            var dupes = new HashSet<string>(list.GroupBy(e => e.Kind + "|" + e.Name.ToLowerInvariant()).Where(g => g.Count() > 1).Select(g => g.Key));
            foreach (var e in list) { e.Lower = e.Name.ToLowerInvariant(); if (dupes.Contains(e.Kind + "|" + e.Lower)) e.Hint = e.Match; }
            Effects = list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public void FocusPremiere()
        {
            if (premiereHwnd != IntPtr.Zero) Native.ForceForeground(premiereHwnd);
        }

        public void Apply(Effect e)
        {
            FocusPremiere();
            popup.Close2();
            Recent.Remove(e.Key);
            Recent.Insert(0, e.Key);
            if (Recent.Count > 40) Recent.RemoveRange(40, Recent.Count - 40);
            try { File.WriteAllLines(RecentFile, Recent); } catch { }

            if (!Connected) { toast.ShowMessage("Premiere plugin not connected", false, Cursor.Position); return; }
            var payload = new Dictionary<string, object> { { "type", "apply" }, { "id", nextId++ }, { "k", e.Kind }, { "m", e.Match }, { "n", e.Name } };
            if (!server.Send(json.Serialize(payload)))
                toast.ShowMessage("Premiere plugin not connected", false, Cursor.Position);
        }

        static Icon MakeIcon()
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using (var b = new SolidBrush(Color.FromArgb(0x9A, 0x6B, 0xFF))) g.FillEllipse(b, 1, 1, 30, 30);
                using (var f = new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("fx", f, Brushes.White, new RectangleF(0, 0, 32, 31), sf);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    // ---------------------------------------------------------------- Ctrl+Space hook
    // Runs on its own thread with its own message loop, so a busy UI thread can never
    // make Windows drop the hook. It is also re-installed every 30 s as a safety net.
    class KeyHook
    {
        public event Action<IntPtr> Hotkey;
        Native.HookProc proc;
        IntPtr hook;
        uint threadId;
        bool swallowSpaceUp;
        uint cachedPid; bool cachedIsPremiere;

        public void Start()
        {
            var t = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "KeyHook" };
            t.Start();
        }

        public void Stop() { if (threadId != 0) Native.PostThreadMessage(threadId, 0x12 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero); }

        void Run()
        {
            threadId = Native.GetCurrentThreadId();
            proc = Callback;
            Install();
            Native.SetTimer(IntPtr.Zero, UIntPtr.Zero, 30000, IntPtr.Zero);
            Native.MSG msg;
            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == 0x113 /*WM_TIMER*/) Install();
            }
            if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
        }

        void Install()
        {
            if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
            hook = Native.SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, proc, Native.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) App.Log("hook install FAILED " + Marshal.GetLastWin32Error());
        }

        IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                uint vk = (uint)Marshal.ReadInt32(lParam);
                if (vk == 0x20)
                {
                    int m = wParam.ToInt32();
                    bool down = m == 0x100 || m == 0x104;
                    bool up = m == 0x101 || m == 0x105;
                    if (up && swallowSpaceUp) { swallowSpaceUp = false; return (IntPtr)1; }
                    if (down && IsDown(0x11) && !IsDown(0x12) && !IsDown(0x10) && !IsDown(0x5B) && !IsDown(0x5C))
                    {
                        IntPtr fg = Native.GetForegroundWindow();
                        bool premiere = IsPremiere(fg);
                        App.Log("Ctrl+Space, premiere in front: " + premiere);
                        if (premiere)
                        {
                            swallowSpaceUp = true;
                            var h = Hotkey;
                            if (h != null) h(Native.GetAncestor(fg, 3 /*GA_ROOTOWNER*/));
                            return (IntPtr)1;
                        }
                    }
                }
            }
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
        }

        static bool IsDown(int vk) { return (Native.GetAsyncKeyState(vk) & 0x8000) != 0; }

        bool IsPremiere(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            if (pid == cachedPid) return cachedIsPremiere;
            string exe = Path.GetFileName(Native.ProcessPath(pid));
            cachedPid = pid;
            cachedIsPremiere = exe.StartsWith("Adobe Premiere Pro", StringComparison.OrdinalIgnoreCase);
            return cachedIsPremiere;
        }
    }

    // ---------------------------------------------------------------- search popup
    class Popup : Form
    {
        static readonly Color Bg = Color.FromArgb(30, 30, 32);
        static readonly Color Field = Color.FromArgb(44, 44, 48);
        static readonly Color Sel = Color.FromArgb(0x5B, 0x4B, 0xD6);
        static readonly Color Dim = Color.FromArgb(140, 140, 150);

        const string Hebrew = "/'קראטוןםפשדגכעיחלךףזסבהנמצתץ";
        const string Latin  = "qwertyuiopasdfghjkl;zxcvbnm,.";

        readonly App app;
        readonly TextBox box = new TextBox();
        readonly ListBox list = new ListBox();
        readonly Panel spacer = new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Bg };
        List<Effect> shown = new List<Effect>();
        const int MaxRows = 8;

        public Popup(App app)
        {
            this.app = app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Bg;
            Size = new Size(420, 58);
            KeyPreview = true;
            Padding = new Padding(10);

            var fieldPanel = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = Field, Padding = new Padding(10, 8, 10, 0) };
            box.BorderStyle = BorderStyle.None;
            box.BackColor = Field;
            box.ForeColor = Color.White;
            box.Font = new Font("Segoe UI", 12.5f);
            box.Dock = DockStyle.Fill;
            fieldPanel.Controls.Add(box);

            list.Dock = DockStyle.Fill;
            list.BorderStyle = BorderStyle.None;
            list.BackColor = Bg;
            list.ForeColor = Color.White;
            list.DrawMode = DrawMode.OwnerDrawFixed;
            list.ItemHeight = 28;
            list.IntegralHeight = false;
            list.Font = new Font("Segoe UI", 10f);
            list.DrawItem += DrawItem;
            list.MouseClick += delegate { if (list.SelectedIndex >= 0) app.Apply(shown[list.SelectedIndex]); };

            Controls.Add(list);
            Controls.Add(spacer);
            Controls.Add(fieldPanel);

            box.TextChanged += delegate { RefreshResults(); };
            box.KeyDown += OnKey;
            box.KeyPress += delegate (object s, KeyPressEventArgs e)
            {
                // Hebrew keyboard layout: turn the letter into the English letter on the same key.
                int i = Hebrew.IndexOf(e.KeyChar);
                if (i >= 0) e.KeyChar = Latin[i];
            };
            Deactivate += delegate { Close2(); };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;  // CS_DROPSHADOW
                cp.ExStyle |= 0x80;        // WS_EX_TOOLWINDOW (hide from Alt+Tab)
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle);
        }

        public void ShowAt(Point cursor, Rectangle host)
        {
            box.Text = "";
            RefreshResults();
            var area = Screen.FromPoint(cursor).WorkingArea;
            int x = cursor.X - Width / 2, y = cursor.Y - 24;
            x = Math.Max(area.Left + 8, Math.Min(x, area.Right - Width - 8));
            y = Math.Max(area.Top + 8, Math.Min(y, area.Bottom - Height - 8));
            Location = new Point(x, y);
            Show();
            Native.ForceForeground(Handle);
            Activate();
            box.Focus();
        }

        public void Close2() { if (Visible) Hide(); }

        void OnKey(object sender, KeyEventArgs e)
        {
            int n = shown.Count;
            switch (e.KeyCode)
            {
                case Keys.Escape: app.FocusPremiere(); Close2(); break;
                case Keys.Enter: if (list.SelectedIndex >= 0) app.Apply(shown[list.SelectedIndex]); break;
                case Keys.Down: if (n > 0) list.SelectedIndex = (list.SelectedIndex + 1) % n; break;
                case Keys.Up: if (n > 0) list.SelectedIndex = (list.SelectedIndex - 1 + n) % n; break;
                case Keys.PageDown: if (n > 0) list.SelectedIndex = Math.Min(n - 1, list.SelectedIndex + 10); break;
                case Keys.PageUp: if (n > 0) list.SelectedIndex = Math.Max(0, list.SelectedIndex - 10); break;
                case Keys.Space: if (e.Control) { app.FocusPremiere(); Close2(); } else return; break;
                default: return;
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        public void RefreshResults()
        {
            string q = box.Text.Trim().ToLowerInvariant();
            var recentRank = new Dictionary<string, int>();
            for (int i = 0; i < app.Recent.Count; i++) if (!recentRank.ContainsKey(app.Recent[i])) recentRank[app.Recent[i]] = i;

            if (q.Length == 0) shown = new List<Effect>();
            else
            {
                string[] tokens = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                shown = app.Effects
                    .Select(e => new { e, s = Score(e, q, tokens, recentRank) })
                    .Where(x => x.s > int.MinValue)
                    .OrderByDescending(x => x.s).ThenBy(x => x.e.Name.Length)
                    .Take(200).Select(x => x.e).ToList();
            }
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var e in shown) list.Items.Add(e.Name);
            if (shown.Count > 0) list.SelectedIndex = 0;
            list.EndUpdate();
            // Just the search line until there are results, then grow down to fit them.
            int rows = Math.Min(shown.Count, MaxRows);
            spacer.Visible = rows > 0;
            Height = 58 + (rows > 0 ? spacer.Height + rows * list.ItemHeight : 0);
        }

        static int Score(Effect e, string q, string[] tokens, Dictionary<string, int> recent)
        {
            string n = e.Lower;
            int s;
            int idx = n.IndexOf(q, StringComparison.Ordinal);
            if (idx >= 0)
            {
                s = 1000 - Math.Min(idx, 50) * 4;
                if (idx == 0) s += 400;
                else if (!char.IsLetterOrDigit(n[idx - 1])) s += 200;
                if (n == q) s += 1000;
            }
            else
            {
                s = 600;
                foreach (var t in tokens)
                {
                    int i = n.IndexOf(t, StringComparison.Ordinal);
                    if (i < 0) { s = int.MinValue; break; }
                    if (i == 0 || !char.IsLetterOrDigit(n[i - 1])) s += 60;
                }
                if (s == int.MinValue)
                {
                    string initials = new string(n.Split(new[] { ' ', '-', '_', '(', ')' }, StringSplitOptions.RemoveEmptyEntries).Select(w => w[0]).ToArray());
                    string compact = q.Replace(" ", "");
                    if (initials.StartsWith(compact)) s = 500;
                    else if (IsSubsequence(compact, n)) s = 100;
                    else return int.MinValue;
                }
            }
            int r;
            if (recent.TryGetValue(e.Key, out r)) s += Math.Max(0, 300 - r * 10);
            return s - n.Length;
        }

        static bool IsSubsequence(string q, string n)
        {
            int j = 0;
            for (int i = 0; i < n.Length && j < q.Length; i++) if (n[i] == q[j]) j++;
            return j == q.Length;
        }

        void DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= shown.Count) return;
            var fx = shown[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Bg)) g.FillRectangle(bg, e.Bounds);
            if (sel)
            {
                var r = new Rectangle(e.Bounds.X, e.Bounds.Y + 1, e.Bounds.Width - 1, e.Bounds.Height - 2);
                using (var path = Rounded(r, 6)) using (var b = new SolidBrush(Sel)) g.FillPath(b, path);
            }
            string tag = fx.Kind == "a" ? "AUDIO" : "";
            if (fx.Hint != null) tag = (tag.Length > 0 ? tag + "  " : "") + Short(fx.Hint);
            var tagFont = new Font("Segoe UI", 7.5f);
            int tagW = tag.Length > 0 ? TextRenderer.MeasureText(tag, tagFont).Width + 8 : 0;
            var nameRect = new Rectangle(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 20 - tagW, e.Bounds.Height);
            TextRenderer.DrawText(g, fx.Name, list.Font, nameRect, Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (tagW > 0)
            {
                var tagRect = new Rectangle(e.Bounds.Right - tagW - 8, e.Bounds.Y, tagW, e.Bounds.Height);
                TextRenderer.DrawText(g, tag, tagFont, tagRect, sel ? Color.FromArgb(220, 220, 255) : Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
            }
            tagFont.Dispose();
        }

        static string Short(string match)
        {
            string s = match.StartsWith("AE.") ? match.Substring(3) : match;
            return s.Length > 22 ? s.Substring(0, 21) + "…" : s;
        }

        static GraphicsPath Rounded(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // ---------------------------------------------------------------- toast
    class Toast : Form
    {
        readonly Label label = new Label();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1800 };

        public Toast()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(30, 30, 32);
            label.Dock = DockStyle.Fill;
            label.ForeColor = Color.White;
            label.Font = new Font("Segoe UI", 10f);
            label.TextAlign = ContentAlignment.MiddleCenter;
            Controls.Add(label);
            timer.Tick += delegate { timer.Stop(); Hide(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;
                cp.ExStyle |= 0x80 | 0x08000000 | 0x8; // TOOLWINDOW | NOACTIVATE | TOPMOST
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.RoundCorners(Handle); }

        public void ShowMessage(string text, bool ok, Point near)
        {
            if (string.IsNullOrEmpty(text)) return;
            label.Text = (ok ? "✓  " : "⚠  ") + text;
            label.ForeColor = ok ? Color.White : Color.FromArgb(255, 170, 110);
            int w = Math.Min(520, TextRenderer.MeasureText(label.Text, label.Font).Width + 36);
            Size = new Size(w, 38);
            var area = Screen.FromPoint(near).WorkingArea;
            int x = Math.Max(area.Left + 8, Math.Min(near.X - w / 2, area.Right - w - 8));
            int y = Math.Max(area.Top + 8, Math.Min(near.Y + 20, area.Bottom - Height - 8));
            Location = new Point(x, y);
            if (!Visible) Show();
            timer.Stop(); timer.Start();
        }
    }

    // ---------------------------------------------------------------- minimal WebSocket server (one client)
    class WsServer
    {
        public event Action<string> Message;
        public event Action<bool> ConnectionChanged;
        readonly List<TcpListener> listeners = new List<TcpListener>();
        readonly object sendLock = new object();
        NetworkStream current;

        public void Start(int port)
        {
            foreach (var addr in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
            {
                try
                {
                    var l = new TcpListener(addr, port);
                    l.Start();
                    listeners.Add(l);
                    var t = new Thread(() => AcceptLoop(l)) { IsBackground = true };
                    t.Start();
                }
                catch { }
            }
        }

        public void Stop() { foreach (var l in listeners) try { l.Stop(); } catch { } }

        void AcceptLoop(TcpListener l)
        {
            while (true)
            {
                TcpClient c;
                try { c = l.AcceptTcpClient(); } catch { return; }
                var t = new Thread(() => Handle(c)) { IsBackground = true };
                t.Start();
            }
        }

        void Handle(TcpClient c)
        {
            NetworkStream s = null;
            try
            {
                c.NoDelay = true;
                s = c.GetStream();
                string req = ReadHeaders(s);
                var m = System.Text.RegularExpressions.Regex.Match(req, @"Sec-WebSocket-Key:\s*(\S+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!m.Success) { c.Close(); return; }
                string accept;
                using (var sha = SHA1.Create())
                    accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(m.Groups[1].Value + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                byte[] resp = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
                s.Write(resp, 0, resp.Length);

                lock (sendLock)
                {
                    if (current != null) try { current.Close(); } catch { }
                    current = s;
                }
                if (ConnectionChanged != null) ConnectionChanged(true);

                var buffer = new MemoryStream();
                while (true)
                {
                    int b0 = s.ReadByte(), b1 = s.ReadByte();
                    if (b0 < 0 || b1 < 0) break;
                    bool fin = (b0 & 0x80) != 0;
                    int op = b0 & 0x0F;
                    long len = b1 & 0x7F;
                    if (len == 126) { var x = ReadExact(s, 2); len = (x[0] << 8) | x[1]; }
                    else if (len == 127) { var x = ReadExact(s, 8); len = 0; for (int i = 0; i < 8; i++) len = (len << 8) | x[i]; }
                    byte[] mask = (b1 & 0x80) != 0 ? ReadExact(s, 4) : null;
                    byte[] data = ReadExact(s, (int)len);
                    if (mask != null) for (int i = 0; i < data.Length; i++) data[i] ^= mask[i % 4];

                    if (op == 8) break;
                    if (op == 9) { SendFrame(s, 0x8A, data); continue; }
                    if (op == 10) continue;
                    if (op == 1 || op == 0) buffer.Write(data, 0, data.Length);
                    if (fin && buffer.Length > 0)
                    {
                        string text = Encoding.UTF8.GetString(buffer.ToArray());
                        buffer.SetLength(0);
                        if (Message != null) Message(text);
                    }
                }
            }
            catch { }
            finally
            {
                bool wasCurrent;
                lock (sendLock) { wasCurrent = current == s; if (wasCurrent) current = null; }
                try { c.Close(); } catch { }
                if (wasCurrent && ConnectionChanged != null) ConnectionChanged(false);
            }
        }

        public bool Send(string text)
        {
            lock (sendLock)
            {
                if (current == null) return false;
                try { SendFrame(current, 0x81, Encoding.UTF8.GetBytes(text)); return true; }
                catch { return false; }
            }
        }

        static void SendFrame(Stream s, int head, byte[] data)
        {
            var f = new MemoryStream();
            f.WriteByte((byte)head);
            if (data.Length < 126) f.WriteByte((byte)data.Length);
            else if (data.Length < 65536) { f.WriteByte(126); f.WriteByte((byte)(data.Length >> 8)); f.WriteByte((byte)data.Length); }
            else { f.WriteByte(127); for (int i = 7; i >= 0; i--) f.WriteByte((byte)((long)data.Length >> (8 * i))); }
            f.Write(data, 0, data.Length);
            var bytes = f.ToArray();
            s.Write(bytes, 0, bytes.Length);
        }

        static string ReadHeaders(Stream s)
        {
            var sb = new StringBuilder();
            while (!sb.ToString().EndsWith("\r\n\r\n"))
            {
                int b = s.ReadByte();
                if (b < 0) throw new IOException("closed");
                sb.Append((char)b);
                if (sb.Length > 16384) throw new IOException("too long");
            }
            return sb.ToString();
        }

        static byte[] ReadExact(Stream s, int n)
        {
            var buf = new byte[n];
            int off = 0;
            while (off < n)
            {
                int r = s.Read(buf, off, n - off);
                if (r <= 0) throw new IOException("closed");
                off += r;
            }
            return buf;
        }
    }
}
