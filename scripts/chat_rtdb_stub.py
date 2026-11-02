#!/usr/bin/env python3
"""Experimental logging Firebase RTDB websocket stub (chat experiment, see docs/re-notes/chat.md).

Usage: chat_rtdb_stub.py [port] [logfile]
Logs every HTTP request and websocket frame verbatim. Answers the RTDB handshake and any
'q' (listen) request with ok + a few fake chat messages at the listened path.
"""
import os, asyncio, base64, hashlib, json, struct, sys, time

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 5099
LOG = open(sys.argv[2], "a", buffering=1) if len(sys.argv) > 2 else sys.stdout
GUID = b"258EAFA5-E914-47DA-95CA-C5AB0DC85B11"


def log(*a):
    print(time.strftime("%H:%M:%S"), *a, file=LOG, flush=True)


def frame(payload: bytes, opcode=1) -> bytes:
    n = len(payload)
    h = bytes([0x80 | opcode])
    if n < 126:
        h += bytes([n])
    elif n < 65536:
        h += bytes([126]) + struct.pack(">H", n)
    else:
        h += bytes([127]) + struct.pack(">Q", n)
    return h + payload


async def read_frame(r):
    b = await r.readexactly(2)
    op = b[0] & 0x0F
    masked = b[1] & 0x80
    n = b[1] & 0x7F
    if n == 126:
        n = struct.unpack(">H", await r.readexactly(2))[0]
    elif n == 127:
        n = struct.unpack(">Q", await r.readexactly(8))[0]
    mask = await r.readexactly(4) if masked else b""
    data = await r.readexactly(n)
    if masked:
        data = bytes(c ^ mask[i % 4] for i, c in enumerate(data))
    return op, data


def fake_messages(path):
    now = time.strftime(os.environ.get("CHAT_STUB_DT_FMT", "%Y-%m-%dT%H:%M:%S-06:00"), time.localtime(time.time() - int(os.environ.get("CHAT_STUB_AGE", "3600"))))
    return {
        "1001": {"userId": "1", "name": "StubUser", "comment": "hello from stub", "stampId": 0, "honorId": 1, "insertDatetime": now},
        "1002": {"userId": "2", "name": "StubUser2", "comment": "second message", "stampId": 0, "honorId": 1, "insertDatetime": now},
    }


async def live_push(w, b):
    """Simulate a write: a NEW child pushed 6 s after the listen (live append test)."""
    await asyncio.sleep(6)
    p = b.get("p", "").strip("/")
    key = str(2000 + int(time.time()) % 1000)
    msg = {"userId": "3", "name": "LiveUser", "comment": "live message " + key, "stampId": 0, "honorId": 1,
           "insertDatetime": time.strftime("%Y-%m-%dT%H:%M:%S-06:00")}
    push = {"p": p + "/" + key, "d": msg}
    if "t" in b:
        push["t"] = b["t"]
    o = {"t": "d", "d": {"a": "d", "b": push}}
    log("WS-SEND(live)", json.dumps(o))
    try:
        w.write(frame(json.dumps(o).encode())); await w.drain()
    except Exception as e:
        log("live push failed", e)


async def handle(r, w):
    peer = w.get_extra_info("peername")
    try:
        head = await r.readuntil(b"\r\n\r\n")
    except Exception as e:
        log("conn", peer, "no head", e)
        return
    text = head.decode("latin1")
    log("HTTP-REQ", peer, repr(text))
    lines = text.split("\r\n")
    hdrs = {l.split(":", 1)[0].lower(): l.split(":", 1)[1].strip() for l in lines[1:] if ":" in l}
    if hdrs.get("upgrade", "").lower() != "websocket":
        body = b"{}"
        w.write(b"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n" + body)
        await w.drain(); w.close(); return
    key = hdrs["sec-websocket-key"].encode()
    acc = base64.b64encode(hashlib.sha1(key + GUID).digest()).decode()
    w.write(("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: %s\r\n\r\n" % acc).encode())
    await w.drain()
    host = hdrs.get("host", "stub")
    hs = {"t": "c", "d": {"t": "h", "d": {"ts": int(time.time() * 1000), "v": "5", "h": host, "s": "stubsession"}}}
    log("WS-SEND", json.dumps(hs))
    w.write(frame(json.dumps(hs).encode()))
    await w.drain()
    try:
        while True:
            op, data = await read_frame(r)
            if op == 8:
                log("WS-CLOSE", data); break
            if op == 9:
                w.write(frame(data, 10)); continue
            s = data.decode("utf8", "replace")
            log("WS-RECV", op, s)
            try:
                m = json.loads(s)
            except Exception:
                continue
            d = m.get("d") if isinstance(m, dict) else None
            if m.get("t") == "d" and isinstance(d, dict) and "r" in d:
                a, b = d.get("a"), d.get("b", {})
                out = [{"t": "d", "d": {"r": d["r"], "b": {"s": "ok", "d": {}}}}]
                if a == "q":
                    p = b.get("p", "")
                    push = {"p": p.strip("/"), "d": fake_messages(p)}
                    if "t" in b:
                        push["t"] = b["t"]  # tagged (query) listen: data must carry the tag
                    out.append({"t": "d", "d": {"a": "d", "b": push}})
                for o in out:
                    log("WS-SEND", json.dumps(o))
                    w.write(frame(json.dumps(o).encode()))
                await w.drain()
                if a == "q":
                    asyncio.get_event_loop().create_task(live_push(w, b))
    except asyncio.IncompleteReadError:
        log("WS-EOF", peer)
    finally:
        w.close()


async def main():
    srv = await asyncio.start_server(handle, "0.0.0.0", PORT)
    log("listening", PORT)
    async with srv:
        await srv.serve_forever()

asyncio.run(main())
