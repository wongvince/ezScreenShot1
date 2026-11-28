#!/usr/bin/env python3
"""Offline ezScreenShot Ed25519 signer. Keep the private key offline."""
import argparse
import base64
import json
from datetime import datetime, timedelta, timezone
from cryptography.hazmat.primitives import serialization

parser = argparse.ArgumentParser(description="为指定机器签发 ezScreenShot Ed25519 授权")
parser.add_argument("--private-key", required=True, help="Ed25519 私钥 PEM 文件")
parser.add_argument("--machine-code", required=True, help="激活窗口显示的机器码")
parser.add_argument("--licensee", required=True, help="授权对象名称")
parser.add_argument("--days", type=int, default=0, choices=[0, 7, 30, 90, 365], help="授权天数；0 表示永久")
parser.add_argument("--output", default="license.json", help="输出授权 JSON 文件")
args = parser.parse_args()

machine_code = args.machine_code.strip().upper()
licensee = args.licensee.strip()
if not machine_code.startswith("EZ-") or len(machine_code) != 27:
    parser.error("机器码格式应为 EZ- 加 24 位十六进制字符")
if not licensee or "\n" in licensee or "\r" in licensee:
    parser.error("授权对象名称不能为空或包含换行符")

with open(args.private_key, "rb") as f:
    private_key = serialization.load_pem_private_key(f.read(), password=None)

issued_at = datetime.now(timezone.utc)
issued_at_text = issued_at.isoformat(timespec="microseconds")
expires_at = issued_at + timedelta(days=args.days) if args.days else None
expires_at_text = expires_at.isoformat(timespec="microseconds") if expires_at else None
expiry_payload = expires_at_text or "PERPETUAL"
payload = f"ezScreenShot\n1\n{machine_code}\n{licensee}\n{issued_at_text}\n{expiry_payload}".encode("utf-8")
signature = private_key.sign(payload)
license_doc = {
    "product": "ezScreenShot",
    "version": 1,
    "machineCode": machine_code,
    "licensee": licensee,
    "issuedAtUtc": issued_at_text,
    "expiresAtUtc": expires_at_text,
    "signature": base64.b64encode(signature).decode("ascii"),
}
with open(args.output, "w", encoding="utf-8") as f:
    json.dump(license_doc, f, ensure_ascii=False, indent=2)
print(f"授权文件已生成：{args.output}")
