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
    // 新版授权字段：UTC ISO 8601（"O" 格式）。旧版授权缺少这两个字段时按旧签名规则验证。
    public string IssuedAtUtc { get; set; } = "";
    public string? ExpiresAtUtc { get; set; }
    public string Signature { get; set; } = "";
}

internal static class LicenseManager
{
    // Ed25519 SubjectPublicKeyInfo（SPKI），由软件发布者提供。
    private const string PublicKeyBase64 = "MCowBQYDK2VwAyEABLHkBjACjcxTjOw2P6yQUuTOau4hTVzp6/1NgtH9he4=";
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
            var license = JsonSerializer.Deserialize<LicenseDocument>(File.ReadAllText(LicensePath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return license is not null && Verify(license);
        }
        catch { return false; }
    }

    public static bool TryImport(string path, out string error)
    {
        try
        {
            var license = JsonSerializer.Deserialize<LicenseDocument>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (license is null || !Verify(license))
            {
                error = "授权文件无效，或授权并非签发给这台电脑。";
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(LicensePath)!);
            File.Copy(path, LicensePath, true);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = "无法读取授权文件：" + ex.Message;
            return false;
        }
    }

    private static bool Verify(LicenseDocument license)
    {
        if (license.Product != "ezScreenShot" || license.Version != 1 ||
            !string.Equals(license.MachineCode, GetMachineCode(), StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(license.Licensee) || string.IsNullOrWhiteSpace(license.Signature))
            return false;

        byte[] signature;
        try { signature = Convert.FromBase64String(license.Signature); }
        catch { return false; }

        // 配置中的公钥是标准 DER SubjectPublicKeyInfo（SPKI）Base64。
        // NSec 的 Ed25519 导入接口使用 32 字节 RawPublicKey，因此先校验并提取原始公钥。
        byte[] spki = Convert.FromBase64String(PublicKeyBase64);
        byte[] spkiPrefix = Convert.FromHexString("302A300506032B6570032100");
        if (spki.Length != spkiPrefix.Length + 32 ||
            !spki.AsSpan(0, spkiPrefix.Length).SequenceEqual(spkiPrefix))
            return false;

        byte[] rawPublicKey = spki.AsSpan(spkiPrefix.Length, 32).ToArray();
        var publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, rawPublicKey, KeyBlobFormat.RawPublicKey);
        string payloadText;

        // 兼容升级前已经签发的旧版永久授权。
        bool legacyLicense = string.IsNullOrWhiteSpace(license.IssuedAtUtc) &&
                             license.ExpiresAtUtc is null;
        if (legacyLicense)
        {
            payloadText = $"ezScreenShot\n1\n{license.MachineCode}\n{license.Licensee.Trim()}";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(license.IssuedAtUtc))
                return false;

            if (!DateTimeOffset.TryParseExact(
                    license.IssuedAtUtc, "O",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out DateTimeOffset issuedAt) || issuedAt.Offset != TimeSpan.Zero)
                return false;

            string expiryText;
            if (license.ExpiresAtUtc is null)
            {
                expiryText = "PERPETUAL";
            }
            else
            {
                if (!DateTimeOffset.TryParseExact(
                        license.ExpiresAtUtc, "O",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind,
                        out DateTimeOffset expiresAt) || expiresAt.Offset != TimeSpan.Zero)
                    return false;

                if (expiresAt <= issuedAt || DateTimeOffset.UtcNow >= expiresAt)
                    return false;

                expiryText = license.ExpiresAtUtc;
            }

            if (issuedAt > DateTimeOffset.UtcNow.AddMinutes(5))
                return false;

            payloadText = $"ezScreenShot\n1\n{license.MachineCode}\n{license.Licensee.Trim()}\n{license.IssuedAtUtc}\n{expiryText}";
        }

        byte[] payload = Encoding.UTF8.GetBytes(payloadText);
        return SignatureAlgorithm.Ed25519.Verify(publicKey, payload, signature);
    }
}

internal sealed class LicenseDialog : Form
{
    public LicenseDialog()
    {
        Text = "ezScreenShot 授权激活";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(540, 220);
        Font = new Font("Microsoft YaHei UI", 9F);

        Controls.Add(new Label
        {
            Text = "本软件采用电脑绑定授权。请复制下方机器码发给授权签发方，取得授权文件后导入。",
            Location = new Point(18, 18), Size = new Size(505, 42)
        });

        var machineCode = new TextBox
        {
            Text = LicenseManager.GetMachineCode(), ReadOnly = true,
            Location = new Point(18, 70), Size = new Size(385, 28), Font = new Font("Consolas", 11F)
        };
        Controls.Add(machineCode);

        var copy = new Button { Text = "复制机器码", Location = new Point(412, 68), Size = new Size(105, 30) };
        copy.Click += (_, _) =>
        {
            Clipboard.SetText(machineCode.Text);
            MessageBox.Show("机器码已复制。", "ezScreenShot", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        Controls.Add(copy);

        Controls.Add(new Label
        {
            Text = "授权文件由授权签发方提供，导入成功后即可启动软件。",
            Location = new Point(18, 112), Size = new Size(500, 24)
        });

        var import = new Button { Text = "导入授权文件…", Location = new Point(294, 165), Size = new Size(112, 32) };
        import.Click += (_, _) => ImportLicense();
        Controls.Add(import);

        var exit = new Button { Text = "退出", DialogResult = DialogResult.Cancel, Location = new Point(420, 165), Size = new Size(96, 32) };
        Controls.Add(exit);
        CancelButton = exit;
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
