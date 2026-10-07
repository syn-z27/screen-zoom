// ScreenZoom: macOS のアクセシビリティズームを Windows で再現する常駐ツール。
// Ctrl+Shift を押しながらホイールを回すと画面全体を拡大/縮小し、拡大中はカーソルに追従する。
// 管理者権限のウィンドウ (タスクマネージャー等) 上でも入力を捕捉できるよう、管理者権限で動かす (ScreenZoom.manifest)。
// .NET Framework 4.x 付属の csc.exe (C# 5) でビルドできるよう、新しい言語機能は使わない。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    [STAThread]
    static void Main()
    {
        bool createdNew;
        using (var mutex = new Mutex(true, "ScreenZoom_SingleInstance", out createdNew))
        {
            if (!createdNew) return;

            // カーソル座標と画面サイズを物理ピクセルで扱うため Per-Monitor V2 を宣言
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { }

            if (!Native.MagInitialize())
            {
                MessageBox.Show("Magnification API の初期化に失敗しました。", "ScreenZoom",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Application.EnableVisualStyles();
            Application.Run(new ZoomContext());
        }
    }
}

sealed class ZoomContext : ApplicationContext
{
    const float MaxLevel = 20f;
    const double StepPerNotch = 1.12; // ホイール 1 ノッチ (delta=120) あたりの倍率
    // 管理者権限の exe は Run キーからは起動されないため、自動起動はタスクスケジューラに登録する。
    // Run キーは以前の版の登録を移行するためだけに参照する。
    const string TaskName = "ScreenZoom";
    const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string LegacyRunValue = "ScreenZoom";

    // GC 回収防止のためフックのデリゲートはフィールドで保持
    readonly Native.LowLevelHookProc hookProc;
    readonly IntPtr hookHandle;
    readonly Native.LowLevelHookProc keyHookProc;
    readonly IntPtr keyHookHandle;
    readonly OverlayForm overlay;
    readonly System.Windows.Forms.Timer overlayWatch;
    readonly System.Windows.Forms.Timer sourceTimer;
    // 透明ウィンドウに届いたが、入力元 (マウスかタッチパッドか) が未確定のスクロール
    readonly List<WheelEvent> pendingWheels = new List<WheelEvent>();
    readonly Stopwatch clock = Stopwatch.StartNew();
    long lastRawWheelMs = long.MinValue / 2; // Raw Input でマウス本体のホイール操作が届いた時刻
    int lastRawWheelSign;
    readonly NotifyIcon tray;
    readonly ToolStripMenuItem enabledItem;
    readonly ToolStripMenuItem startupItem;

    float level = 1f;
    bool enabled = true;
    // 拡大表示している領域の左上 (等倍時の画面座標)。カーソルが領域の端を越えたときだけ動かす
    double viewX;
    double viewY;

    public ZoomContext()
    {
        hookProc = HookCallback;
        hookHandle = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, hookProc,
            Native.GetModuleHandle(null), 0);
        keyHookProc = KeyHookCallback;
        keyHookHandle = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyHookProc,
            Native.GetModuleHandle(null), 0);
        if (hookHandle == IntPtr.Zero || keyHookHandle == IntPtr.Zero)
        {
            MessageBox.Show("マウス/キーボードフックの登録に失敗しました。", "ScreenZoom",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        overlay = new OverlayForm(OnOverlayWheel, OnRawInput);
        RegisterRawMouseInput();
        sourceTimer = new System.Windows.Forms.Timer();
        sourceTimer.Interval = 10;
        sourceTimer.Tick += delegate { ResolveWheelSources(); };
        // Win+L 等でキーを離したことを取りこぼしても透明ウィンドウが残り続けないよう定期確認する
        overlayWatch = new System.Windows.Forms.Timer();
        overlayWatch.Interval = 200;
        overlayWatch.Tick += delegate { if (!IsModifierDown()) HideOverlay(); };

        MigrateLegacyStartup();
        enabledItem = new ToolStripMenuItem("有効", null, delegate { ToggleEnabled(); });
        enabledItem.Checked = true;
        startupItem = new ToolStripMenuItem("Windows 起動時に実行", null, delegate { ToggleStartup(); });
        startupItem.Checked = IsStartupRegistered();

        var menu = new ContextMenuStrip();
        menu.Items.Add(enabledItem);
        menu.Items.Add(new ToolStripMenuItem("ズームをリセット", null, delegate { SetLevel(1f, Cursor.Position); }));
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("終了", null, delegate { ExitThread(); }));

        tray = new NotifyIcon();
        tray.Icon = CreateIcon();
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.DoubleClick += delegate { ToggleEnabled(); };
        UpdateTooltip();

        // ログオフ・シャットダウン時にもズームを戻す
        SystemEvents.SessionEnding += delegate { Cleanup(); };
    }

    IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == Native.WM_MOUSEWHEEL || (msg == Native.WM_MOUSEMOVE && level > 1f))
            {
                var info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                var pt = new Point(info.pt.x, info.pt.y);

                if (msg == Native.WM_MOUSEWHEEL)
                {
                    if (enabled && IsModifierDown())
                    {
                        // 透明ウィンドウが出ていれば、そちらで入力元を判定してズームする。
                        // ここで止めると Raw Input も届かなくなるため、通過させる。
                        if (overlay.IsShown) return Native.CallNextHookEx(hookHandle, nCode, wParam, lParam);

                        OnZoomWheel((short)((info.mouseData >> 16) & 0xFFFF), pt);
                        return new IntPtr(1); // ホイール入力自体はアプリに渡さない
                    }
                }
                else
                {
                    FollowCursor(pt);
                }
            }
        }
        return Native.CallNextHookEx(hookHandle, nCode, wParam, lParam);
    }

    // 高精度タッチパッドの二本指スクロールは Chrome やエクスプローラー等へ直接届き、
    // マウスフックでは捕捉できない。Ctrl+Shift を押している間だけ透明ウィンドウを最前面に置き、
    // スクロールをそのウィンドウで受け取ることでアプリへ届かないようにする。
    IntPtr KeyHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
            bool isCtrl = info.vkCode == Native.VK_LCONTROL || info.vkCode == Native.VK_RCONTROL;
            bool isShift = info.vkCode == Native.VK_LSHIFT || info.vkCode == Native.VK_RSHIFT;
            if (isCtrl || isShift)
            {
                int msg = wParam.ToInt32();
                bool pressed = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                // フック内ではこのキーの状態がまだ反映されていないため、このキーだけはイベントから判断する
                bool ctrl = isCtrl ? pressed : IsKeyDown(Native.VK_CONTROL);
                bool shift = isShift ? pressed : IsKeyDown(Native.VK_SHIFT);
                if (ctrl && shift)
                {
                    if (enabled) ShowOverlay();
                }
                else
                {
                    HideOverlay();
                }
            }
        }
        return Native.CallNextHookEx(keyHookHandle, nCode, wParam, lParam);
    }

    void ShowOverlay()
    {
        if (overlay.IsShown) return;
        overlay.ShowOverlay();
        overlayWatch.Start();
    }

    void HideOverlay()
    {
        overlayWatch.Stop();
        overlay.HideOverlay();
    }

    // マウスとタッチパッドのスクロールは、マウスフックでも透明ウィンドウでも同じ形で届き区別できない。
    // マウス本体のホイール操作だけが Raw Input に入力元の機器付きで届くため、
    // 同じ向きの Raw Input が近い時刻にあればマウス、なければタッチパッドとみなす。
    // ホイールを速く回すと透明ウィンドウ側では複数ノッチがまとめて届くため、スクロール量は突き合わせない。
    const int SourceWaitMs = 40;  // Raw Input を待つ時間。過ぎたらタッチパッドとみなす
    const int RawRecentMs = 150;  // Raw Input が先に届いていた場合に、マウスとみなす時間

    sealed class WheelEvent
    {
        public int Delta;
        public Point Cursor;
        public long ReceivedMs;
    }

    void RegisterRawMouseInput()
    {
        var device = new Native.RAWINPUTDEVICE();
        device.usUsagePage = 0x01; // Generic Desktop
        device.usUsage = 0x02;     // Mouse
        device.dwFlags = Native.RIDEV_INPUTSINK; // 非アクティブでも受け取る
        device.hwndTarget = overlay.Handle;
        if (!Native.RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICE))))
        {
            MessageBox.Show("Raw Input の登録に失敗しました。マウスホイールのズーム方向が逆になる場合があります。",
                "ScreenZoom", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void OnOverlayWheel(int delta, Point cursor)
    {
        long now = clock.ElapsedMilliseconds;
        bool mouse = now - lastRawWheelMs <= RawRecentMs && Math.Sign(delta) == lastRawWheelSign;
        if (mouse)
        {
            OnZoomWheel(delta, cursor);
            return;
        }
        pendingWheels.Add(new WheelEvent { Delta = delta, Cursor = cursor, ReceivedMs = now });
        sourceTimer.Start();
    }

    void OnRawInput(IntPtr hRawInput)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf(typeof(Native.RAWINPUTHEADER));
        Native.GetRawInputData(hRawInput, Native.RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.GetRawInputData(hRawInput, Native.RID_INPUT, buffer, ref size, headerSize) != size) return;
            var header = (Native.RAWINPUTHEADER)Marshal.PtrToStructure(buffer, typeof(Native.RAWINPUTHEADER));
            if (header.dwType != Native.RIM_TYPEMOUSE) return;

            // RAWMOUSE: usFlags(2) + padding(2) + usButtonFlags(2) + usButtonData(2)
            IntPtr mouse = IntPtr.Add(buffer, (int)headerSize);
            ushort buttonFlags = (ushort)Marshal.ReadInt16(mouse, 4);
            if ((buttonFlags & Native.RI_MOUSE_WHEEL) == 0) return;
            int delta = Marshal.ReadInt16(mouse, 6);

            if (header.hDevice == IntPtr.Zero) return; // 機器を特定できない入力 (タッチパッド由来の可能性) は扱わない

            lastRawWheelMs = clock.ElapsedMilliseconds;
            lastRawWheelSign = Math.Sign(delta);

            // 先に届いて判定待ちになっている同じ向きのスクロールは、マウスのものとして処理する
            foreach (WheelEvent pending in pendingWheels.FindAll(e => Math.Sign(e.Delta) == lastRawWheelSign))
            {
                OnZoomWheel(pending.Delta, pending.Cursor);
            }
            pendingWheels.RemoveAll(e => Math.Sign(e.Delta) == lastRawWheelSign);
            if (pendingWheels.Count == 0) sourceTimer.Stop();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // 判定待ちのまま SourceWaitMs 経ったスクロールはタッチパッドとして扱う
    void ResolveWheelSources()
    {
        long now = clock.ElapsedMilliseconds;
        while (pendingWheels.Count > 0 && now - pendingWheels[0].ReceivedMs >= SourceWaitMs)
        {
            WheelEvent e = pendingWheels[0];
            pendingWheels.RemoveAt(0);
            // タッチパッドは指の動きとズーム方向を合わせるため、向きを逆にする
            OnZoomWheel(-e.Delta, e.Cursor);
        }
        if (pendingWheels.Count == 0) sourceTimer.Stop();
    }

    void OnZoomWheel(int delta, Point cursor)
    {
        SetLevel((float)(level * Math.Pow(StepPerNotch, delta / 120.0)), cursor);
    }

    static bool IsModifierDown()
    {
        return IsKeyDown(Native.VK_CONTROL) && IsKeyDown(Native.VK_SHIFT);
    }

    static bool IsKeyDown(int vk)
    {
        return (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    void SetLevel(float newLevel, Point cursor)
    {
        if (newLevel < 1.02f) newLevel = 1f; // 端数が残らないよう等倍にスナップ
        if (newLevel > MaxLevel) newLevel = MaxLevel;
        if (newLevel == level) return;

        Rectangle screen = VirtualScreen();
        if (level <= 1f)
        {
            viewX = screen.Left;
            viewY = screen.Top;
        }
        // カーソルが画面上の同じ位置に見えたままになるよう、カーソルを中心に拡大/縮小する
        viewX = cursor.X - (cursor.X - viewX) * level / newLevel;
        viewY = cursor.Y - (cursor.Y - viewY) * level / newLevel;
        level = newLevel;
        ApplyTransform();
        UpdateTooltip();
    }

    // カーソルが拡大表示している領域の端を越えたときだけ、越えた分だけ領域を動かす
    void FollowCursor(Point cursor)
    {
        Rectangle screen = VirtualScreen();
        double viewWidth = screen.Width / level;
        double viewHeight = screen.Height / level;
        double x = viewX;
        double y = viewY;

        if (cursor.X < x) x = cursor.X;
        else if (cursor.X > x + viewWidth - 1) x = cursor.X - viewWidth + 1;
        if (cursor.Y < y) y = cursor.Y;
        else if (cursor.Y > y + viewHeight - 1) y = cursor.Y - viewHeight + 1;

        if (x == viewX && y == viewY) return;
        viewX = x;
        viewY = y;
        ApplyTransform();
    }

    void ApplyTransform()
    {
        if (level <= 1f)
        {
            Native.MagSetFullscreenTransform(1f, 0, 0);
            return;
        }

        Rectangle screen = VirtualScreen();
        viewX = Clamp(viewX, screen.Left, screen.Right - screen.Width / level);
        viewY = Clamp(viewY, screen.Top, screen.Bottom - screen.Height / level);
        Native.MagSetFullscreenTransform(level, (int)Math.Round(viewX), (int)Math.Round(viewY));
    }

    static Rectangle VirtualScreen()
    {
        return new Rectangle(
            Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN),
            Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
            Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN),
            Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));
    }

    static double Clamp(double v, double min, double max)
    {
        return v < min ? min : (v > max ? max : v);
    }

    void ToggleEnabled()
    {
        enabled = !enabled;
        enabledItem.Checked = enabled;
        if (!enabled)
        {
            HideOverlay();
            SetLevel(1f, Cursor.Position);
        }
        UpdateTooltip();
    }

    void UpdateTooltip()
    {
        tray.Text = enabled
            ? string.Format("ScreenZoom - {0:0.0}x", level)
            : "ScreenZoom - 無効";
    }

    static bool IsStartupRegistered()
    {
        return RunSchtasks("/Query /TN \"" + TaskName + "\"") == 0;
    }

    void ToggleStartup()
    {
        if (IsStartupRegistered()) RunSchtasks("/Delete /TN \"" + TaskName + "\" /F");
        else RegisterStartupTask();
        startupItem.Checked = IsStartupRegistered();
    }

    // 以前の版が Run キーに登録した自動起動は管理者権限では機能しないため、タスクスケジューラへ移す
    static void MigrateLegacyStartup()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, true))
        {
            if (key == null || key.GetValue(LegacyRunValue) == null) return;
            key.DeleteValue(LegacyRunValue, false);
        }
        RegisterStartupTask();
    }

    // ログオン時に「最上位の特権」で起動するタスクを登録する (UAC の確認なしで管理者権限になる)。
    // ノートPCでバッテリー駆動中も起動・継続するよう、電源条件を外すため XML で定義する。
    static void RegisterStartupTask()
    {
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        string xml =
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
            "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId></LogonTrigger></Triggers>\n" +
            "  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId>" +
            "<LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\n" +
            "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
            "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
            "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
            "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>4</Priority></Settings>\n" +
            "  <Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(Application.ExecutablePath) +
            "</Command></Exec></Actions>\n" +
            "</Task>\n";

        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, xml, Encoding.Unicode);
            if (RunSchtasks("/Create /TN \"" + TaskName + "\" /XML \"" + path + "\" /F") != 0)
            {
                MessageBox.Show("タスクスケジューラへの登録に失敗しました。", "ScreenZoom",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    static int RunSchtasks(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using (var process = Process.Start(psi))
        {
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode;
        }
    }

    static Icon CreateIcon()
    {
        using (var bmp = new Bitmap(32, 32))
        {
            using (var g = Graphics.FromImage(bmp))
            using (var lens = new Pen(Color.White, 4f))
            using (var handle = new Pen(Color.White, 6f))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                g.FillEllipse(new SolidBrush(Color.FromArgb(0, 120, 212)), 3, 3, 20, 20);
                g.DrawEllipse(lens, 3, 3, 20, 20);
                handle.StartCap = LineCap.Round;
                handle.EndCap = LineCap.Round;
                g.DrawLine(handle, 21, 21, 28, 28);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    bool cleanedUp;

    void Cleanup()
    {
        if (cleanedUp) return;
        cleanedUp = true;
        if (hookHandle != IntPtr.Zero) Native.UnhookWindowsHookEx(hookHandle);
        if (keyHookHandle != IntPtr.Zero) Native.UnhookWindowsHookEx(keyHookHandle);
        overlayWatch.Stop();
        sourceTimer.Stop();
        overlay.Dispose();
        Native.MagSetFullscreenTransform(1f, 0, 0);
        Native.MagUninitialize();
        tray.Visible = false;
        tray.Dispose();
    }

    protected override void ExitThreadCore()
    {
        Cleanup();
        base.ExitThreadCore();
    }
}

// 仮想画面全体を覆うほぼ透明な最前面ウィンドウ。アクティブにならず、受け取ったホイールでズームする。
sealed class OverlayForm : Form
{
    const int WS_EX_TOPMOST = 0x00000008;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WM_MOUSEACTIVATE = 0x0021;
    const int MA_NOACTIVATE = 3;

    readonly Action<int, Point> onWheel;
    readonly Action<IntPtr> onRawInput;

    public bool IsShown { get; private set; }

    public OverlayForm(Action<int, Point> onWheel, Action<IntPtr> onRawInput)
    {
        this.onWheel = onWheel;
        this.onRawInput = onRawInput;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Opacity = 0.01; // 完全透明 (0) だとマウス入力が素通りするため、見えない程度の値にする
        CreateHandle();
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public void ShowOverlay()
    {
        IsShown = true;
        // WinForms の DPI 補正を避けるため、位置とサイズは物理ピクセルで直接指定する
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST,
            Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN),
            Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
            Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN),
            Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN),
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    public void HideOverlay()
    {
        if (!IsShown) return;
        IsShown = false;
        Native.ShowWindow(Handle, Native.SW_HIDE);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_INPUT)
        {
            onRawInput(m.LParam);
            base.WndProc(ref m); // 後始末のため DefWindowProc に渡す
            return;
        }
        if (m.Msg == Native.WM_MOUSEWHEEL)
        {
            onWheel((short)((m.WParam.ToInt64() >> 16) & 0xFFFF), Cursor.Position);
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = new IntPtr(MA_NOACTIVATE);
            return;
        }
        base.WndProc(ref m);
    }
}

static class Native
{
    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const int SW_HIDE = 0;
    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_MOUSEWHEEL = 0x020A;
    public const int WM_INPUT = 0x00FF;
    public const uint RIDEV_INPUTSINK = 0x00000100;
    public const uint RID_INPUT = 0x10000003;
    public const uint RIM_TYPEMOUSE = 0;
    public const ushort RI_MOUSE_WHEEL = 0x0400;
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_LSHIFT = 0xA0;
    public const int VK_RSHIFT = 0xA1;
    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    public delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll")]
    public static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("Magnification.dll")]
    public static extern bool MagInitialize();

    [DllImport("Magnification.dll")]
    public static extern bool MagUninitialize();

    [DllImport("Magnification.dll")]
    public static extern bool MagSetFullscreenTransform(float magLevel, int xOffset, int yOffset);
}
