using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NSec.Cryptography;

namespace DeviceCheckScreenshot;

internal sealed class LicenseDocument
{
    public string Product { get; set; } = "";
    public int Version { get; set; }
    public string MachineCode { get; set; } = "";
    public string Licensee { get; set; } = "";
    public string IssuedAtUtc { get; set; } = "";
    public string? ExpiresAtUtc { get; set; }
    public string Signature { get; set; } = "";
}

internal static class LicenseManager
{
    // Ed25519 SPKI 公钥；与本地授权管理器使用同一把公钥。
    private const string PublicKeyBase64 = "MCowBQYDK2VwAyEABLHkBjACjcxTjOw2P6yQUuTOau4hTVzp6/1NgtH9he4=";
    private static readonly byte[] SpkiPrefix = Convert.FromHexString("302A300506032B6570032100");
    private static readonly string LicensePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ezScreenShot", "license.json");

    public static string GetMachineCode()
    {
        string machineGuid = "";
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            machineGuid = key?.GetValue("MachineGuid")?.ToString() ?? "";
        }
        catch { }

        if (string.IsNullOrWhiteSpace(machineGuid))
            machineGuid = Environment.MachineName + "|" + Environment.OSVersion.VersionString;

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("ezScreenShot|1|" + machineGuid));
        return "EZ-" + Convert.ToHexString(hash)[..24];
    }

    public static bool IsLicensed()
    {
        try
        {
            if (!File.Exists(LicensePath)) return false;
            var license = JsonSerializer.Deserialize<LicenseDocument>(File.ReadAllText(LicensePath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return license is not null && Verify(license);
        }
        catch { return false; }
    }

    public static string GetStatusText()
    {
        try
        {
            if (!File.Exists(LicensePath)) return "授权状态：尚未激活";
            var license = JsonSerializer.Deserialize<LicenseDocument>(File.ReadAllText(LicensePath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (license is null || !Verify(license))
                return "授权状态：未激活（本机授权文件无效或已过期）";

            string expiry = license.ExpiresAtUtc is null
                ? "永久有效"
                : DateTimeOffset.Parse(license.ExpiresAtUtc, System.Globalization.CultureInfo.InvariantCulture)
                    .ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            return $"授权状态：已激活\r\n授权对象：{license.Licensee}\r\n有效期：{expiry}";
        }
        catch
        {
            return "授权状态：未激活（无法读取授权文件）";
        }
    }

    public static bool TryImport(string path, out string error)
    {
        try
        {
            var license = JsonSerializer.Deserialize<LicenseDocument>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (license is null || !Verify(license))
            {
                error = "授权验证失败：签名、公钥、机器码或授权期限不匹配。请确认使用配套授权管理器生成的 JSON 文件，并检查电脑系统时间。";
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(LicensePath)!);
            File.Copy(path, LicensePath, true);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = "无法读取或验证授权文件：" + ex.Message;
            return false;
        }
    }

    private static bool Verify(LicenseDocument license)
    {
        if (license.Product != "ezScreenShot" || license.Version != 1 ||
            !string.Equals(license.MachineCode, GetMachineCode(), StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(license.Licensee) ||
            string.IsNullOrWhiteSpace(license.IssuedAtUtc) ||
            string.IsNullOrWhiteSpace(license.Signature))
            return false;

        // 时间字段必须是有效的 ISO 8601 UTC 时间；允许最多 5 分钟系统时钟偏差。
        if (!DateTimeOffset.TryParse(license.IssuedAtUtc,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var issuedAt))
            return false;

        var now = DateTimeOffset.UtcNow;
        if (issuedAt > now.AddMinutes(5)) return false;

        string expiryPayload;
        if (license.ExpiresAtUtc is null)
        {
            expiryPayload = "PERPETUAL";
        }
        else
        {
            if (!DateTimeOffset.TryParse(license.ExpiresAtUtc,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var expiresAt))
                return false;
            if (expiresAt <= issuedAt || now >= expiresAt) return false;
            expiryPayload = license.ExpiresAtUtc;
        }

        byte[] signature;
        try { signature = Convert.FromBase64String(license.Signature); }
        catch { return false; }
        if (signature.Length != 64) return false;

        // 将 SPKI DER 公钥提取为 NSec 所需的 32 字节 RawPublicKey。
        byte[] spki;
        try { spki = Convert.FromBase64String(PublicKeyBase64); }
        catch { return false; }
        if (spki.Length != SpkiPrefix.Length + 32 ||
            !spki.AsSpan(0, SpkiPrefix.Length).SequenceEqual(SpkiPrefix))
            return false;

        byte[] rawPublicKey = spki.AsSpan(SpkiPrefix.Length, 32).ToArray();
        var publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, rawPublicKey, KeyBlobFormat.RawPublicKey);

        // 必须与授权管理器 MainForm.GenerateLicense() 的六行 payload 完全一致。
        byte[] payload = Encoding.UTF8.GetBytes(
            $"ezScreenShot\n1\n{license.MachineCode}\n{license.Licensee.Trim()}\n{license.IssuedAtUtc}\n{expiryPayload}");
        return SignatureAlgorithm.Ed25519.Verify(publicKey, payload, signature);
    }
}

internal sealed class LicenseDialog : Form
{
    public LicenseDialog()
    {
        Text = "ezScreenShot 软件授权";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(560, 300);
        Font = new Font("Microsoft YaHei UI", 9F);

        Controls.Add(new Label
        {
            Text = LicenseManager.GetStatusText(),
            Location = new Point(18, 16), Size = new Size(520, 66),
            ForeColor = LicenseManager.IsLicensed() ? Color.DarkGreen : Color.DarkRed
        });

        Controls.Add(new Label
        {
            Text = "本软件采用电脑绑定授权。请复制下方机器码发给授权签发方，取得授权文件后导入。",
            Location = new Point(18, 88), Size = new Size(520, 42)
        });

        var machineCode = new TextBox
        {
            Text = LicenseManager.GetMachineCode(), ReadOnly = true,
            Location = new Point(18, 138), Size = new Size(400, 28), Font = new Font("Consolas", 11F)
        };
        Controls.Add(machineCode);

        var copy = new Button { Text = "复制机器码", Location = new Point(428, 136), Size = new Size(105, 30) };
        copy.Click += (_, _) =>
        {
            Clipboard.SetText(machineCode.Text);
            MessageBox.Show("机器码已复制。", "ezScreenShot", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        Controls.Add(copy);

        Controls.Add(new Label
        {
            Text = "如需续期或更换授权，请导入授权签发方提供的 JSON 文件。",
            Location = new Point(18, 180), Size = new Size(520, 24)
        });

        var import = new Button { Text = "导入/更新授权…", Location = new Point(300, 238), Size = new Size(125, 34) };
        import.Click += (_, _) => ImportLicense();
        Controls.Add(import);

        var close = new Button { Text = "关闭", DialogResult = DialogResult.Cancel, Location = new Point(438, 238), Size = new Size(95, 34) };
        Controls.Add(close);
        CancelButton = close;
    }

    private void ImportLicense()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 ezScreenShot 授权文件",
            Filter = "授权文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        if (!LicenseManager.TryImport(dialog.FileName, out string error))
        {
            MessageBox.Show(error, "授权验证失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        MessageBox.Show("授权验证成功。", "ezScreenShot", MessageBoxButtons.OK, MessageBoxIcon.Information);
        DialogResult = DialogResult.OK;
        Close();
    }
}
