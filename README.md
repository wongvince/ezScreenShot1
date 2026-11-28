# ezScreenShot（Windows）

Windows 托盘截图工具，.NET 8 WinForms，支持自定义截图区域、快捷键、保存目录记忆和截图文件命名。

## 功能

- 默认快捷键：`Ctrl + Shift + F`
- 首次选择矩形截图区域，之后记住该区域；可从托盘菜单重新选择
- 保存目录首次选择后记住；可从托盘菜单更改
- 截图后输入文件名，回车保存为 PNG；同名文件直接覆盖
- 截图保存成功后不显示右下角通知
- 不联网、不上传截图
- 使用 Ed25519 验证授权，授权绑定到单台 Windows 电脑

## GitHub Actions 构建

将本项目所有文件直接放在 GitHub 仓库根目录（不要再套一层项目文件夹）。推送到 `main`/`master`，或在 Actions 中手动运行，即可构建 Windows x64 单文件程序。

构建产物名称：`ezScreenShot-Windows-x64.zip`。

## 电脑绑定授权流程

1. 启动程序，在激活窗口复制机器码。
2. 将机器码和授权对象名称交给授权签发方。
3. 签发方使用与程序内置公钥匹配的 Ed25519 私钥，在离线环境生成授权 JSON。
4. 将授权 JSON 发回用户，在激活窗口点击“导入授权文件”。

授权签名与本地授权管理器统一为以下六行 UTF-8 内容（行间使用 LF 换行，末尾不额外添加换行）：

```text
ezScreenShot
1
机器码
授权对象名称
UTC签发时间
UTC到期时间或 PERPETUAL
```

授权 JSON 字段：`product`、`version`、`machineCode`、`licensee`、`issuedAtUtc`、`expiresAtUtc`、`signature`。`expiresAtUtc` 为 `null` 表示永久授权；签名覆盖的最后一行此时为 `PERPETUAL`。客户端验证签名、机器码、签发时间和到期时间，并允许最多 5 分钟的系统时钟偏差。

### 离线签发示例

签发电脑安装 Python 和依赖：

```bash
python -m pip install cryptography
```

私钥必须是与程序内置公钥匹配的 Ed25519 PEM 私钥。执行：

```bash
python tools/sign_license.py --private-key ed25519-private.pem --machine-code EZ-机器码 --licensee "客户或设备名称" --output license.json
```

**重要：** 请妥善保管 Ed25519 私钥，不要放进 GitHub 仓库、程序目录或发给最终用户。程序只内置公钥。当前公钥为：

```text
MCowBQYDK2VwAyEABLHkBjACjcxTjOw2P6yQUuTOau4hTVzp6/1NgtH9he4=
```

如果这把公钥没有对应的私钥，或签发私钥与它不匹配，就无法生成可通过验证的授权文件。机器码基于 Windows MachineGuid 计算；重装系统或更换系统身份信息后，机器码可能变化，需要重新签发。
