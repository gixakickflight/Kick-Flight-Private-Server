#!/usr/bin/env python3
"""Reproduce the synthetic AES vector independently with the OpenSSL CLI."""

from __future__ import annotations

import subprocess
import sys


key = bytes(range(32))
iv = bytes(range(16, 32))
plaintext = b'{"assetVersion":12345,"smartBeatAvailableFlag":false,"rebateUrl":""}'
expected = bytes.fromhex(
    "101112131415161718191a1b1c1d1e1f"
    "96ba20b0347601822f0fcc77f426fa06e7185e9c82ef1db8da040c1eb03b41b"
    "024fe43e243f5f80706a6c1dddbf166066015ed4fdaaaca6e56e14535a61c708"
    "25cf9f71ca0a49b94e4a10ec01e2c7e4d"
)

result = subprocess.run(
    [
        "openssl", "enc", "-aes-256-cbc", "-K", key.hex(), "-iv", iv.hex(),
    ],
    input=plaintext,
    stdout=subprocess.PIPE,
    stderr=subprocess.PIPE,
    check=False,
)
if result.returncode != 0:
    sys.stderr.write(result.stderr.decode("utf-8", errors="replace"))
    raise SystemExit(result.returncode)
actual = iv + result.stdout
if actual != expected:
    raise SystemExit("OpenSSL vector mismatch; expected bytes changed or padding behavior differs.")
print("PASS independent OpenSSL AES-256-CBC/PKCS7 vector reproduction")
