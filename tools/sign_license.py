#!/usr/bin/env python3
"""Offline ezScreenShot Ed25519 license signer. Keep the private key offline."""
import argparse
import base64
import json
from datetime import datetime, timedelta, timezone
from cryptography.hazmat.primitives import serialization

parser = argparse.ArgumentParser(description="为指定机器签发 ezScreenShot 授权文件")
parser.add_argument("--private-key", required=True, help="Ed25519 私钥 PEM 文件")
parser.add_argument("--machine-code", required=True, help="软件激活窗口显示的机器码")
parser.add_argument("--licensee", required=True, help="授权对象名称")
parser.add_argument("--days", required=True, type=int, choices=[0, 7, 30, 90, 365],
                    help="授权天数：0 表示永久；也可选 7、30、90、365")
parser.add_argument("--output", default="license.json", help="输出授权 JSON 文件")
args = parser.parse_args()

machine_code = args.machine_code.strip().upper()
if len(machine_code) != 27 or not machine_code.startswith("EZ-") or any(
        c not in "0123456789ABCDEF" for c in machine_code[3:]):
    parser.error("--machine-code 必须是 EZ- 加 24 位十六进制字符")

licensee = args.licensee.strip()
if not licensee:
    parser.error("--licensee 不能为空")

with open(args.private_key, "rb") as f:
    private_key = serialization.load_pem_private_key(f.read(), password=None)

def dotnet_utc_roundtrip(value):
    # .NET DateTimeOffset "O" 格式使用 7 位小数；Python datetime 提供 6 位。
    return value.isoformat(timespec="microseconds").replace("+00:00", "0+00:00")

issued = datetime.now(timezone.utc)
issued_text = dotnet_utc_roundtrip(issued)
if args.days == 0:
    expires_text = None
    expiry_payload = "PERPETUAL"
else:
    expires = issued + timedelta(days=args.days)
    expires_text = dotnet_utc_roundtrip(expires)
    expiry_payload = expires_text

payload = (
    f"ezScreenShot\n1\n{machine_code}\n{licensee}\n"
    f"{issued_text}\n{expiry_payload}"
).encode("utf-8")
signature = private_key.sign(payload)

license_doc = {
    "product": "ezScreenShot",
    "version": 1,
    "machineCode": machine_code,
    "licensee": licensee,
    "issuedAtUtc": issued_text,
    "expiresAtUtc": expires_text,
    "signature": base64.b64encode(signature).decode("ascii"),
}
with open(args.output, "w", encoding="utf-8") as f:
    json.dump(license_doc, f, ensure_ascii=False, indent=2)
print(f"授权文件已生成：{args.output}")
