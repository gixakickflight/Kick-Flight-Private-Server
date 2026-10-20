#!/usr/bin/env python3
"""D2C codec and a minimal Kick-Flight API client.

The client protects every POST body with the same scheme the C# side implements in
`src/KickFlight.BootstrapApi/D2CCodec.cs`: AES-256-CBC with PKCS#7, where the 16-byte IV is prepended to the
ciphertext and the key is the 32-byte ASCII "hash" the client sent in its `/auth/index` request.

    body = IV (16 bytes) || AES-CBC(plaintext, key = hash, IV)

Reimplemented here rather than reached through the .NET code because the checks that need it are the ones
that must run against a *deployed* server - `verify-cdn.sh`, the Stage 2 forced-username flow - and spinning
up dotnet to make one HTTP call would tie a deployment check to a local build.

AES comes from whichever backend is installed, since Windows Python and WSL Python disagree:

    pip install cryptography     (WSL: present)
    pip install pycryptodome     (Windows Python: present as `Crypto`)

Usage as a library:

    from d2c import Client
    client = Client("http://10.10.20.148:18080")
    client.auth("some-device-uuid")            # populates client.token / client.key / client.user_id
    status, headers, body = client.post("/startup/index", {})

Usage from the shell, mostly to prove the server is speaking the protocol:

    python scripts/d2c.py http://10.10.20.148:18080 --uuid probe-0001

Careful with --post from Git Bash on Windows: MSYS rewrites an argument that looks like a POSIX path into a
Windows one, so `/startup/index` arrives as `C:/Program Files/Git/startup/index` and urllib rejects it. Prefix
the command with MSYS_NO_PATHCONV=1, or double the leading slash (`//startup/index`).
"""

from __future__ import annotations

import argparse
import json
import os
import secrets
import string
import sys
import urllib.error
import urllib.request

# The shared "parallel code" the server uses to encrypt a response when the caller has no session yet, and
# the key a client that never authenticated would be expected to use.
COMMON_CODE = b"1a837b9ee2ae11a07a0f529a4cd4b61c"
KEY_SIZE = 32
IV_SIZE = 16


def _backend():
    """Return (encrypt, decrypt) callables, or raise if no AES backend is installed."""
    try:
        from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
        from cryptography.hazmat.primitives import padding

        def encrypt(plaintext: bytes, key: bytes, iv: bytes) -> bytes:
            padder = padding.PKCS7(128).padder()
            padded = padder.update(plaintext) + padder.finalize()
            enc = Cipher(algorithms.AES(key), modes.CBC(iv)).encryptor()
            return enc.update(padded) + enc.finalize()

        def decrypt(body: bytes, key: bytes, iv: bytes) -> bytes:
            dec = Cipher(algorithms.AES(key), modes.CBC(iv)).decryptor()
            padded = dec.update(body) + dec.finalize()
            unpadder = padding.PKCS7(128).unpadder()
            return unpadder.update(padded) + unpadder.finalize()

        return encrypt, decrypt
    except ImportError:
        pass

    try:
        from Crypto.Cipher import AES  # pycryptodome

        def encrypt(plaintext: bytes, key: bytes, iv: bytes) -> bytes:
            return AES.new(key, AES.MODE_CBC, iv).encrypt(
                plaintext + bytes([16 - len(plaintext) % 16]) * (16 - len(plaintext) % 16)
            )

        def decrypt(body: bytes, key: bytes, iv: bytes) -> bytes:
            plain = AES.new(key, AES.MODE_CBC, iv).decrypt(body)
            return plain[: -plain[-1]]

        return encrypt, decrypt
    except ImportError:
        raise SystemExit(
            "No AES backend found. Install one:  pip install cryptography   (or pycryptodome)"
        )


_ENCRYPT, _DECRYPT = _backend()


def _check_key(key: bytes) -> None:
    if len(key) != KEY_SIZE:
        raise ValueError(f"key must be exactly {KEY_SIZE} bytes, got {len(key)}")


def encode(plaintext: bytes, key: bytes, iv: bytes | None = None) -> bytes:
    """IV || AES-256-CBC(plaintext). A fresh random IV is used unless one is given."""
    _check_key(key)
    iv = secrets.token_bytes(IV_SIZE) if iv is None else iv
    if len(iv) != IV_SIZE:
        raise ValueError(f"iv must be exactly {IV_SIZE} bytes, got {len(iv)}")
    return iv + _ENCRYPT(plaintext, key, iv)


def decode(body: bytes, key: bytes) -> bytes:
    """Inverse of encode, reading the IV out of the body's first 16 bytes."""
    _check_key(key)
    if len(body) < IV_SIZE * 2 or len(body) % IV_SIZE != 0:
        raise ValueError("D2C body must be a 16-byte vector followed by whole AES blocks")
    return _DECRYPT(body[IV_SIZE:], key, body[:IV_SIZE])


def random_hash() -> str:
    """A stand-in for the client's 32-character auth hash. The server only checks the length."""
    alphabet = string.ascii_letters + string.digits
    return "".join(secrets.choice(alphabet) for _ in range(KEY_SIZE))


class Client:
    """Just enough of a Kick-Flight client to exercise the API from a script."""

    def __init__(self, base_url: str, timeout: float = 30.0):
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout
        self.token: str | None = None
        self.key: bytes = COMMON_CODE
        self.user_id: str | None = None

    def _request(self, path: str, data: bytes | None, headers: dict[str, str]):
        req = urllib.request.Request(self.base_url + path, data=data, headers=headers, method="POST")
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as response:
                return response.status, dict(response.headers), response.read()
        except urllib.error.HTTPError as exc:
            return exc.code, dict(exc.headers), exc.read()

    def auth(self, uuid: str, hash_value: str | None = None) -> tuple[int, dict, dict]:
        """Perform /auth/index. On success the returned session key becomes the key for later calls.

        `uuid` is the device identity: the server is expected to hand the same player back for the same
        uuid, which is exactly what the persistence check asserts.
        """
        auth_hash = hash_value or random_hash()
        payload = json.dumps({"hash": auth_hash, "uuid": uuid}).encode()
        status, headers, body = self._request(
            "/auth/index", encode(payload, COMMON_CODE), {"content-type": "application/octet-stream"}
        )
        if status == 200 and body:
            # The session key for everything that follows is the hash we just sent, so the key has to be
            # set before decoding the response.
            self.key = auth_hash.encode()
            self.token = headers.get("x-app-access-token")
            self.user_id = headers.get("x-app-user-id")
        return status, headers, self._maybe_json(body)

    def post(self, path: str, payload: dict) -> tuple[int, dict, dict]:
        """POST a JSON payload, D2C-encoded, and decode the response with the session key."""
        if self.token is None:
            raise RuntimeError("call auth() first")
        body = encode(json.dumps(payload).encode(), self.key)
        status, headers, raw = self._request(
            path,
            body,
            {
                "content-type": "application/octet-stream",
                "x-app-access-token": self.token,
            },
        )
        return status, headers, self._maybe_json(raw)

    def _maybe_json(self, raw: bytes) -> dict:
        if not raw:
            return {}
        try:
            return json.loads(decode(raw, self.key).decode())
        except Exception:
            # A body that is not D2C (an error page, say) is worth seeing verbatim rather than crashing on.
            return {"_undecoded": raw[:400].decode("utf-8", "replace")}


def main() -> int:
    parser = argparse.ArgumentParser(description="Probe a Kick-Flight server with the client's own protocol")
    parser.add_argument("base_url", help="e.g. http://10.10.20.148:18080")
    parser.add_argument("--uuid", default="d2c-probe-device", help="device uuid to authenticate as")
    parser.add_argument("--post", action="append", default=[], metavar="PATH",
                        help="path to POST after auth (repeatable); body is {} unless --body is given")
    parser.add_argument("--body", default="{}", help="JSON body for every --post")
    args = parser.parse_args()

    client = Client(args.base_url)
    status, headers, body = client.auth(args.uuid)
    print(f"POST /auth/index -> {status}")
    print(f"  x-app-user-id     {headers.get('x-app-user-id')}")
    print(f"  x-app-access-token {headers.get('x-app-access-token')}")
    print(f"  x-app-status-code {headers.get('x-app-status-code')}")
    print(f"  body              {json.dumps(body)[:200]}")

    for path in args.post:
        status, headers, body = client.post(path, json.loads(args.body))
        print(f"POST {path} -> {status}  x-app-status-code={headers.get('x-app-status-code')}")
        print(f"  {json.dumps(body)[:400]}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
