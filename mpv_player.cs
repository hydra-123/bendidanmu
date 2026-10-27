using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal sealed class MpvPlayerEngine : IDisposable
{
    private const int FormatFlag = 3;
    private const int FormatDouble = 5;
    private static readonly IntPtr Zero = IntPtr.Zero;
    private readonly Panel surface;
    private readonly Timer pollTimer;
    private readonly Action<double, double, bool, bool, double> stateChanged;
    private IntPtr context;
    private bool disposed;
    private bool lastPaused = true;
    private bool lastEnded;
    private bool mediaLoaded;
    private string pendingSubtitle;
    private double pendingResume;
    private double currentVolume = 100;

    public MpvPlayerEngine(Panel renderSurface, Action<double, double, bool, bool, double> onStateChanged)
    {
        surface = renderSurface;
        stateChanged = onStateChanged;
        pollTimer = new Timer { Interval = 250 };
        pollTimer.Tick += delegate { PollState(); };
    }

    public string Initialize()
    {
        try
        {
            context = mpv_create();
            if (context == Zero) return "mpv 无法创建播放器实例";
            SetOption("wid", surface.Handle.ToInt64().ToString());
            SetOption("vo", "gpu");
            SetOption("hwdec", "auto-safe");
            SetOption("panscan", "1");
            SetOption("idle", "yes");
            SetOption("force-window", "yes");
            SetOption("keep-open", "yes");
            SetOption("terminal", "no");
            SetOption("input-default-bindings", "no");
            SetOption("sub-auto", "all");
            SetOption("sid", "auto");
            int result = mpv_initialize(context);
            if (result < 0) return ErrorText(result);
            pollTimer.Start();
            return null;
        }
        catch (DllNotFoundException) { return "找不到 libmpv-2.dll 或它的运行依赖"; }
        catch (EntryPointNotFoundException ex) { return "libmpv API 不兼容：" + ex.Message; }
        catch (Exception ex) { return ex.Message; }
    }

    private void SetOption(string name, string value)
    {
        int result = mpv_set_option_string(context, name, value);
        if (result < 0) throw new InvalidOperationException("mpv 选项 " + name + "：" + ErrorText(result));
    }

    public void Load(string path, double resumeAt)
    {
        if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("视频文件不存在", path);
        lastEnded = false;
        lastPaused = false;
        mediaLoaded = false;
        pendingSubtitle = null;
        pendingResume = Math.Max(0, resumeAt);
        Execute("loadfile", path, "replace");
        SetString("pause", "no");
    }

    public void Stop() { Execute("stop"); lastPaused = true; lastEnded = false; }
    public double Volume { get { return currentVolume; } }
    public void Play() { SetString("pause", "no"); lastPaused = false; }
    public void Pause() { SetString("pause", "yes"); lastPaused = true; }
    public void TogglePause() { if (lastPaused) Play(); else Pause(); }
    public void Seek(double seconds) { Execute("seek", Math.Max(0, seconds).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "absolute", "exact"); }
    public void SetSubtitleVisible(bool visible) { SetString("sub-visibility", visible ? "yes" : "no"); }
    public void SetSubtitleDelay(double seconds) { SetString("sub-delay", seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)); }
    public void SetVolume(double value)
    {
        value = Math.Max(0, Math.Min(100, value));
        currentVolume = value;
        SetString("volume", value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
        stateChanged(ReadDouble("time-pos"), ReadDouble("duration"), lastPaused, lastEnded, value);
    }
    public void AddSubtitle(string path)
    {
        if (!File.Exists(path)) return;
        if (mediaLoaded) Execute("sub-add", path, "select");
        else pendingSubtitle = path;
    }
    public void ClearSubtitle() { pendingSubtitle = null; SetSubtitleVisible(false); }

    private void PollState()
    {
        if (context == Zero || disposed) return;
        double time = ReadDouble("time-pos");
        double duration = ReadDouble("duration");
        int pause = 1, ended = 0;
        mpv_get_property(context, "pause", FormatFlag, ref pause);
        mpv_get_property(context, "eof-reached", FormatFlag, ref ended);
        bool paused = pause != 0;
        bool isEnded = ended != 0;
        if (!mediaLoaded && duration > 0)
        {
            mediaLoaded = true;
            if (pendingResume > 0)
            {
                try { Execute("seek", pendingResume.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "absolute", "exact"); } catch { }
                pendingResume = 0;
            }
            if (!String.IsNullOrEmpty(pendingSubtitle))
            {
                try { Execute("sub-add", pendingSubtitle, "select"); } catch { }
                pendingSubtitle = null;
            }
        }
        if (paused != lastPaused || isEnded != lastEnded || !paused)
        {
            lastPaused = paused;
            lastEnded = isEnded;
            currentVolume = ReadDouble("volume");
            stateChanged(time, duration, paused, isEnded, currentVolume);
        }
    }

    private double ReadDouble(string name)
    {
        double value = 0;
        return mpv_get_property(context, name, FormatDouble, ref value) < 0 ? 0 : value;
    }

    private void SetString(string name, string value)
    {
        if (context != Zero) mpv_set_property_string(context, name, value);
    }

    private void Execute(params string[] args)
    {
        if (context == Zero) return;
        IntPtr vector = Marshal.AllocHGlobal((args.Length + 1) * IntPtr.Size);
        IntPtr[] strings = new IntPtr[args.Length];
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(args[i] + "\0");
                strings[i] = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, strings[i], bytes.Length);
                Marshal.WriteIntPtr(vector, i * IntPtr.Size, strings[i]);
            }
            Marshal.WriteIntPtr(vector, args.Length * IntPtr.Size, Zero);
            int result = mpv_command(context, vector);
            if (result < 0) throw new InvalidOperationException("mpv 命令 " + args[0] + "：" + ErrorText(result));
        }
        finally
        {
            foreach (IntPtr item in strings) if (item != Zero) Marshal.FreeHGlobal(item);
            Marshal.FreeHGlobal(vector);
        }
    }

    private static string ErrorText(int code)
    {
        IntPtr value = mpv_error_string(code);
        return value == Zero ? "libmpv 错误 " + code : Marshal.PtrToStringAnsi(value);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        pollTimer.Stop();
        if (context != Zero) { mpv_terminate_destroy(context); context = Zero; }
        pollTimer.Dispose();
    }

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_create();
    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_set_option_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.LPStr)] string value);
    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_initialize(IntPtr ctx);
    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_command(IntPtr ctx, IntPtr args);
    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_set_property_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.LPStr)] string value);
    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_get_property(IntPtr ctx, [MarshalAs(UnmanagedType.LPStr)] string name, int format, ref double value);
    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property")] private static extern int mpv_get_property(IntPtr ctx, [MarshalAs(UnmanagedType.LPStr)] string name, int format, ref int value);
    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_error_string(int error);
    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_terminate_destroy(IntPtr ctx);
}

internal sealed class PlayerControlsOverlayForm : Form
{
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;
    private readonly Button playButton;
    private readonly Button fullscreenButton;
    private readonly TrackBar seekBar;
    private readonly TrackBar volumeBar;
    private readonly Label timeLabel;
    private readonly Timer hideTimer;
    private readonly Action togglePlayback;
    private readonly Action<double> seek;
    private readonly Action<double> setVolume;
    private readonly Action toggleFullscreen;
    private bool changingSeek;
    private double duration;

    public PlayerControlsOverlayForm(Form owner, Action playPause, Action<double> onSeek, Action<double> onVolume, Action fullscreen)
    {
        Owner = owner;
        togglePlayback = playPause;
        seek = onSeek;
        setVolume = onVolume;
        toggleFullscreen = fullscreen;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(32, 34, 38);
        Opacity = 0.94;
        Height = 66;

        playButton = MakeButton("Ⅱ", "播放或暂停");
        fullscreenButton = MakeButton("⛶", "切换全屏");
        timeLabel = new Label { ForeColor = Color.White, Text = "0:00 / 0:00", TextAlign = ContentAlignment.MiddleCenter, Width = 100, Dock = DockStyle.Left };
        seekBar = new TrackBar { Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, Dock = DockStyle.Fill, Height = 42, BackColor = BackColor };
        volumeBar = new TrackBar { Minimum = 0, Maximum = 100, Value = 100, TickStyle = TickStyle.None, Width = 120, Dock = DockStyle.Right, BackColor = BackColor };
        Panel row = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 5, 12, 2), BackColor = BackColor };
        row.Controls.Add(seekBar);
        row.Controls.Add(fullscreenButton);
        row.Controls.Add(volumeBar);
        row.Controls.Add(timeLabel);
        row.Controls.Add(playButton);
        Controls.Add(row);

        playButton.Click += delegate { togglePlayback(); };
        fullscreenButton.Click += delegate { toggleFullscreen(); };
        seekBar.MouseDown += delegate { changingSeek = true; };
        seekBar.MouseUp += delegate { changingSeek = false; if (duration > 0) seek(duration * seekBar.Value / seekBar.Maximum); };
        seekBar.ValueChanged += delegate { if (changingSeek && duration > 0) timeLabel.Text = FormatTime(duration * seekBar.Value / seekBar.Maximum) + " / " + FormatTime(duration); };
        volumeBar.ValueChanged += delegate { if (setVolume != null) setVolume(volumeBar.Value); };
        hideTimer = new Timer { Interval = 2500 };
        hideTimer.Tick += delegate
        {
            hideTimer.Stop();
            if (!Bounds.Contains(Cursor.Position)) Hide();
        };
    }

    protected override CreateParams CreateParams
    {
        get { CreateParams cp = base.CreateParams; cp.ExStyle |= WsExToolWindow | WsExNoActivate; return cp; }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    private static Button MakeButton(string text, string tooltip)
    {
        Button button = new Button { Text = text, Width = 42, Dock = DockStyle.Left, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(48, 51, 56), TabStop = false };
        button.FlatAppearance.BorderSize = 0;
        ToolTip tip = new ToolTip();
        tip.SetToolTip(button, tooltip);
        return button;
    }

    public void Place(Rectangle videoBounds)
    {
        if (videoBounds.Width <= 0 || videoBounds.Height <= 0) { Hide(); return; }
        Bounds = new Rectangle(videoBounds.Left, videoBounds.Bottom - Height, videoBounds.Width, Height);
    }

    public void Reveal()
    {
        if (Owner == null || Owner.IsDisposed || !Owner.Visible) return;
        if (!Visible) Show(Owner);
        BringToFront();
        hideTimer.Stop();
        hideTimer.Start();
    }

    public void SetState(double current, double total, bool paused, double volume)
    {
        duration = Math.Max(0, total);
        playButton.Text = paused ? "▶" : "Ⅱ";
        timeLabel.Text = FormatTime(current) + " / " + FormatTime(duration);
        if (!changingSeek)
        {
            int value = duration <= 0 ? 0 : (int)Math.Round(Math.Max(0, Math.Min(duration, current)) * seekBar.Maximum / duration);
            seekBar.Value = Math.Max(seekBar.Minimum, Math.Min(seekBar.Maximum, value));
        }
        int volumeValue = Math.Max(volumeBar.Minimum, Math.Min(volumeBar.Maximum, (int)Math.Round(volume)));
        if (volumeBar.Value != volumeValue) volumeBar.Value = volumeValue;
    }

    private static string FormatTime(double seconds)
    {
        if (Double.IsNaN(seconds) || Double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        TimeSpan value = TimeSpan.FromSeconds(seconds);
        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
    }
}

internal sealed class DanmakuItem
{
    public double time { get; set; }
    public string text { get; set; }
    public string mode { get; set; }
    public string color { get; set; }
}

internal sealed class DanmakuSettings
{
    public int size { get; set; }
    public double speed { get; set; }
    public double range { get; set; }
    public double opacity { get; set; }
    public bool top { get; set; }
    public bool blockBottom { get; set; }
    public bool enabled { get; set; }
}

internal sealed class DanmakuOverlayForm : Form
{
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExLayered = 0x80000;
    private const int WsExNoActivate = 0x08000000;
    private const int UlwAlpha = 2;
    private const byte AcSrcOver = 0;
    private const byte AcSrcAlpha = 1;
    private readonly List<DanmakuItem> items = new List<DanmakuItem>();
    private double time;
    private bool playing;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private long clockAnchor;
    private readonly Timer redrawTimer;
    private DanmakuSettings settings = new DanmakuSettings { size = 25, speed = 9, range = 80, opacity = 1, top = true, enabled = true };

    public DanmakuOverlayForm(Form owner)
    {
        Owner = owner;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = false;
        redrawTimer = new Timer { Interval = 33 };
        redrawTimer.Tick += delegate { if (Visible && items.Count > 0) RefreshLayer(); };
        redrawTimer.Start();
    }

    protected override CreateParams CreateParams
    {
        get { CreateParams cp = base.CreateParams; cp.ExStyle |= WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate; return cp; }
    }

    public void SetItems(DanmakuItem[] values) { items.Clear(); if (values != null) items.AddRange(values); RefreshLayer(); }
    public void SetSettings(DanmakuSettings value) { if (value != null) settings = value; RefreshLayer(); }
    public void SetPlaybackState(double value, bool paused)
    {
        time = Math.Max(0, value);
        playing = !paused;
        clockAnchor = clock.ElapsedMilliseconds;
        RefreshLayer();
    }
    public void SetViewport(Rectangle bounds, bool visible)
    {
        Bounds = bounds;
        if (visible && !Visible) Show(Owner);
        else if (!visible && Visible) Hide();
        if (visible) RefreshLayer();
    }

    private void RefreshLayer()
    {
        if (!Visible || Width <= 0 || Height <= 0) return;
        using (Bitmap bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            if (settings.enabled)
            {
                int index = 0;
                double now = time + (playing ? (clock.ElapsedMilliseconds - clockAnchor) / 1000.0 : 0);
                foreach (DanmakuItem item in items)
                {
                    if (item == null || String.IsNullOrWhiteSpace(item.text) || item.time > now + 0.15) { index++; continue; }
                    bool top = IsMode(item.mode, "top", "5");
                    bool bottom = IsMode(item.mode, "bottom", "4");
                    if (top && !settings.top || bottom && settings.blockBottom) { index++; continue; }
                    double duration = top || bottom ? 4.2 : Math.Max(1, settings.speed);
                    double elapsed = now - item.time;
                    if (elapsed < 0 || elapsed > duration) { index++; continue; }
                    float fontSize = Math.Max(12, settings.size);
                    using (Font font = new Font("Microsoft YaHei UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (GraphicsPath path = new GraphicsPath())
                    {
                        path.AddString(item.text, font.FontFamily, (int)font.Style, font.Size, Point.Empty, StringFormat.GenericDefault);
                        RectangleF textBounds = path.GetBounds();
                        int laneCount = Math.Max(1, (int)(Height * Math.Max(30, Math.Min(100, settings.range)) / 100 / (fontSize + 10)));
                        int lane = index % laneCount;
                        float y = 6 + lane * (fontSize + 8);
                        if (bottom) y = Height * (float)Math.Max(30, Math.Min(100, settings.range)) / 100 - fontSize - lane * (fontSize + 8);
                        else if (top) y = 6 + lane * (fontSize + 8);
                        float x = top || bottom ? (Width - textBounds.Width) / 2 : Width - (float)((elapsed / duration) * (Width + textBounds.Width));
                        using (Matrix matrix = new Matrix())
                        {
                            matrix.Translate(x, y);
                            path.Transform(matrix);
                        }
                        Color fill = ParseColor(item.color, settings.opacity);
                        using (Pen outline = new Pen(Color.FromArgb((int)(230 * settings.opacity), 0, 0, 0), Math.Max(2, fontSize / 10)))
                        using (SolidBrush brush = new SolidBrush(fill))
                        {
                            outline.LineJoin = LineJoin.Round;
                            graphics.DrawPath(outline, path);
                            graphics.FillPath(brush, path);
                        }
                    }
                    index++;
                }
            }
            Present(bitmap);
        }
    }

    private static bool IsMode(string value, string word, string number)
    {
        value = (value ?? "").ToLowerInvariant();
        return value.Contains(word) || value == number;
    }

    private static Color ParseColor(string value, double opacity)
    {
        try
        {
            Color parsed = ColorTranslator.FromHtml(value ?? "#ffffff");
            return Color.FromArgb((int)(Math.Max(0, Math.Min(1, opacity)) * 255), parsed.R, parsed.G, parsed.B);
        }
        catch { return Color.FromArgb((int)(Math.Max(0, Math.Min(1, opacity)) * 255), Color.White); }
    }

    private void Present(Bitmap bitmap)
    {
        IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen), hBitmap = bitmap.GetHbitmap(Color.FromArgb(0)), old = SelectObject(memory, hBitmap);
        try
        {
            Point destination = Location, source = Point.Empty;
            Size size = bitmap.Size;
            Blend blend = new Blend { Operation = AcSrcOver, Flags = 0, SourceConstantAlpha = 255, AlphaFormat = AcSrcAlpha };
            UpdateLayeredWindow(Handle, screen, ref destination, ref size, memory, ref source, 0, ref blend, UlwAlpha);
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(hBitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    protected override void WndProc(ref Message message)
    {
        const int WmNchittest = 0x84, HtTransparent = -1;
        base.WndProc(ref message);
        if (message.Msg == WmNchittest) message.Result = new IntPtr(HtTransparent);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) redrawTimer.Dispose();
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Blend { public byte Operation, Flags, SourceConstantAlpha, AlphaFormat; }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref Point dst, ref Size size, IntPtr srcDc, ref Point src, int key, ref Blend blend, int flags);
}
