using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Windows.Forms;

namespace DeviceCheckScreenshot;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        if (!LicenseManager.IsLicensed())
        {
            using var activation = new LicenseDialog();
            if (activation.ShowDialog() != DialogResult.OK) return;
        }
        Application.Run(new App());
    }
}

internal sealed class Settings
{
    public string OutputFolder { get; set; } = "";
    public uint HotkeyModifiers { get; set; } = 6;
    public uint HotkeyVirtualKey { get; set; } = 70;
    public string HotkeyDisplay { get; set; } = "Ctrl + Shift + F";
    public int RegionX { get; set; }
    public int RegionY { get; set; }
    public int RegionWidth { get; set; }
    public int RegionHeight { get; set; }

    public Rectangle? CaptureRegion =>
        RegionWidth > 0 && RegionHeight > 0
            ? new Rectangle(RegionX, RegionY, RegionWidth, RegionHeight)
            : null;

    static string P => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(P))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(P)) ?? new();
        }
        catch { }
        return new();
    }

    public void SetRegion(Rectangle r)
    {
        RegionX = r.X;
        RegionY = r.Y;
        RegionWidth = r.Width;
        RegionHeight = r.Height;
        Save();
    }

    public void Save() =>
        File.WriteAllText(P, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

internal sealed class App : ApplicationContext
{
    readonly Settings s = Settings.Load();
    readonly HotkeyWindow hw;
    readonly NotifyIcon tray;
    bool busy;

    public App()
    {
        hw = new HotkeyWindow(Capture);

        var m = new ContextMenuStrip();
        m.Items.Add("选择/更改截图区域…", null, (_, _) => ChooseRegion());
        m.Items.Add("选择/更改保存文件夹…", null, (_, _) => ChooseFolder());
        m.Items.Add("打开保存文件夹", null, (_, _) => OpenFolder());
        m.Items.Add("复制本机授权机器码", null, (_, _) =>
        {
            Clipboard.SetText(LicenseManager.GetMachineCode());
            MessageBox.Show("机器码已复制。", "ezScreenShot", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("退出", null, (_, _) => ExitThread());

        tray = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Text = "ezScreenShot",
            ContextMenuStrip = m,
            Visible = true
        };
        tray.DoubleClick += (_, _) => OpenFolder();

        bool folder = EnsureFolder();
        bool region = EnsureRegion();
        bool key = Native.RegisterHotKey(hw.Handle, 0xD001, s.HotkeyModifiers, s.HotkeyVirtualKey);
        string msg = key ? $"快捷键：{s.HotkeyDisplay}" : "快捷键注册失败，可能已被占用。";
        if (!folder) msg += "\n请右键托盘图标选择保存目录。";
        if (!region) msg += "\n请右键托盘图标选择截图区域。";
        tray.ShowBalloonTip(3500, "ezScreenShot 已启动", msg, ToolTipIcon.Info);
    }

    bool EnsureFolder()
    {
        if (!string.IsNullOrWhiteSpace(s.OutputFolder))
        {
            try { Directory.CreateDirectory(s.OutputFolder); return true; }
            catch { }
        }
        using var d = new FolderBrowserDialog
        {
            Description = "请选择固定截图保存文件夹（只需设置一次）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (d.ShowDialog() != DialogResult.OK) return false;
        s.OutputFolder = d.SelectedPath;
        s.Save();
        return true;
    }

    void ChooseFolder()
    {
        using var d = new FolderBrowserDialog
        {
            Description = "选择新的截图保存目录；程序会记住此位置",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(s.OutputFolder)
                ? s.OutputFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (d.ShowDialog() != DialogResult.OK) return;
        s.OutputFolder = d.SelectedPath;
        s.Save();
        tray.ShowBalloonTip(2000, "保存目录已更新", s.OutputFolder, ToolTipIcon.Info);
    }

    bool EnsureRegion()
    {
        if (s.CaptureRegion is not null) return true;
        return ChooseRegion();
    }

    bool ChooseRegion()
    {
        using var selector = new RegionSelector();
        if (selector.ShowDialog() != DialogResult.OK || selector.SelectedRegion is null)
            return false;

        Rectangle r = selector.SelectedRegion.Value;
        s.SetRegion(r);
        tray.ShowBalloonTip(2500, "截图区域已保存",
            $"区域：X={r.X}, Y={r.Y}, {r.Width} × {r.Height}", ToolTipIcon.Info);
        return true;
    }

    void Capture()
    {
        if (busy) return;
        busy = true;
        try
        {
            if (string.IsNullOrWhiteSpace(s.OutputFolder) && !EnsureFolder()) return;
            if (!EnsureRegion()) return;

            Rectangle r = s.CaptureRegion!.Value;
            using var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size, CopyPixelOperation.SourceCopy);

            using var d = new NameDialog(s.OutputFolder);
            if (d.ShowDialog() != DialogResult.OK) return;

            string name = d.FileName.Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = Path.GetFileName(name).Trim().TrimEnd('.', ' ');
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("文件名不能为空。");
                return;
            }

            Directory.CreateDirectory(s.OutputFolder);
            string dest = Path.Combine(s.OutputFolder, name + ".png");
            using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
            bmp.Save(fs, ImageFormat.Png);
            // 按要求：截图保存成功后不显示右下角通知。
        }
        catch (Exception e)
        {
            MessageBox.Show("截图或保存失败：\n" + e.Message, "ezScreenShot",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { busy = false; }
    }

    void OpenFolder()
    {
        if (string.IsNullOrWhiteSpace(s.OutputFolder) && !EnsureFolder()) return;
        Directory.CreateDirectory(s.OutputFolder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = s.OutputFolder,
            UseShellExecute = true
        });
    }

    protected override void ExitThreadCore()
    {
        Native.UnregisterHotKey(hw.Handle, 0xD001);
        hw.Dispose();
        tray.Visible = false;
        tray.Dispose();
        base.ExitThreadCore();
    }
}

internal sealed class RegionSelector : Form
{
    Point start;
    Point current;
    bool dragging;

    public Rectangle? SelectedRegion { get; private set; }

    public RegionSelector()
    {
        Rectangle virtualScreen = SystemInformation.VirtualScreen;
        Bounds = virtualScreen;
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Color.Black;
        Opacity = 0.28;
        Cursor = Cursors.Cross;
        KeyPreview = true;

        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
            }
        };
        Shown += (_, _) => Activate();
    }

    void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        start = PointToScreen(e.Location);
        current = start;
        dragging = true;
        Invalidate();
    }

    void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (!dragging) return;
        current = PointToScreen(e.Location);
        Invalidate();
    }

    void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (!dragging || e.Button != MouseButtons.Left) return;
        current = PointToScreen(e.Location);
        dragging = false;

        int left = Math.Min(start.X, current.X);
        int top = Math.Min(start.Y, current.Y);
        int width = Math.Abs(current.X - start.X);
        int height = Math.Abs(current.Y - start.Y);

        if (width < 5 || height < 5)
        {
            MessageBox.Show("截取区域太小，请重新选择。", "ezScreenShot",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            Invalidate();
            return;
        }

        SelectedRegion = new Rectangle(left, top, width, height);
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var titleFont = new Font("Microsoft YaHei UI", 16, FontStyle.Bold);
        using var hintFont = new Font("Microsoft YaHei UI", 10);
        using var white = new SolidBrush(Color.White);
        e.Graphics.DrawString("拖动鼠标框选需要截取的区域", titleFont, white, 24, 22);
        e.Graphics.DrawString("松开鼠标确认；按 Esc 取消。此区域会被记住，以后无需重复框选。", hintFont, white, 26, 58);

        if (dragging)
        {
            Point a = PointToClient(start);
            Point b = PointToClient(current);
            Rectangle rect = Rectangle.FromLTRB(
                Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
            using var pen = new Pen(Color.Lime, 3);
            e.Graphics.DrawRectangle(pen, rect);
        }
    }
}

internal sealed class NameDialog : Form
{
    readonly TextBox box = new();
    public string FileName => box.Text;

    public NameDialog(string folder)
    {
        Text = "保存截图 - ezScreenShot";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        ShowInTaskbar = true;
        ClientSize = new Size(500, 160);
        Font = new Font("Microsoft YaHei UI", 9F);

        Controls.Add(new Label { Text = "请输入自定义文件名（无需输入 .png）：", AutoSize = true, Location = new Point(16, 16) });
        box.Location = new Point(18, 44);
        box.Size = new Size(460, 26);
        Controls.Add(box);
        Controls.Add(new Label { Text = "保存位置：" + folder, AutoEllipsis = true, Location = new Point(18, 78), Size = new Size(460, 20) });

        var ok = new Button { Text = "保存", DialogResult = DialogResult.OK, Location = new Point(315, 116), Size = new Size(75, 28) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(400, 116), Size = new Size(75, 28) };
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
        Shown += (_, _) => box.Focus();
    }
}

internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    readonly Action cb;
    public HotkeyWindow(Action a) { cb = a; CreateHandle(new CreateParams()); }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && m.WParam == (IntPtr)0xD001) cb();
        base.WndProc(ref m);
    }

    public void Dispose() => DestroyHandle();
}

internal static class Native
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
