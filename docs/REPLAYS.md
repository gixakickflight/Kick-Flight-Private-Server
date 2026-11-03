# Battle replays (Kicker + Featured tabs)

Server-side implementation of the client's replay feature. The design and the disassembly evidence behind it
are in `handoff/dsh-replay-1/report.md`; this file records what was actually built, the config surface, and
what is still unverified on a device.

Verified vs. not: everything below marked **verified** was checked statically against the pristine
`libil2cpp.so` (RVA == file offset) and against the server tests in
`tests/KickFlight.BootstrapApi.Tests/BattleReplayTests.cs`. Nothing here has been run on the emulator yet
(no APK was built for Part A).

## Flow

```
client (recording on by default)
   │  match ends
   ├─ POST /battle/end            -> server records rule/field/roster + human count, answers replayUploadFlag=true
   │  (custom: POST /customBattle/end -> same recording, response stays field-less; the cave sets the upload flag)
   ├─ POST /battle/upload         -> base64(gzip(archive)) sent to otomo patch as one recording, response {}
   │
   ├─ POST /battleReplay/index    -> Featured (channel 1) + 14 kicker channels (1000+kickerId)
   └─ POST /battleReplay/play     -> { battleReplayUrl, encryptionKey } (32-char key, 10 min)
          GET  battleReplayUrl     -> IV || AES-256-CBC/PKCS7(gzip archive)
```

* Tab 1 ("Replay") is device-local and untouched by the server (owner decision 1).
* Tab 2 (Kicker) is data-driven from `config/masters_battle_replay_channel.json` (type 2, one row per
  kicker) and the index's per-channel replay list: patch's `?tag=kicker:<id>` listing, newest
  `Otomo:Recordings:KickerChannelSize` first.
* Tab 3 (Rank in the stock prefab) is repurposed as **Featured**: one type-3 row (id 1) whose list is the
  curated list `Otomo:Recordings:FeaturedList` (`featured`) of the published `patch.curated` document, read
  through patch (`?list=featured`) in the published order. Nothing is ranked or rotated here; an unpublished
  or empty list is an empty row. The tab label is not patched (owner decision).
* `appMovieList` stays `[]` (the Pickup tab is out of scope).
* Custom-battle uploads require the Part B client cave (`scripts/re/replay_upload_cave.py`): the stock
  `GameManager.CallbackCustomBattleEndSuccess` never sets `ArchiveData.IsUploadReplayData`.

## Ownership: patch holds the replays

This server stores no replay. Recorded content is otomo's patch service (`services/patch`, "Recorded
content"); `Otomo/OtomoRecordings.cs` is the client and `BattleReplayService` the translation.

| Client need | Patch contract |
| --- | --- |
| `/battle/upload` | `PUT /internal/patch/recording/{battleId}` with the game-server key; multipart `kind`, `sha256`, `size`, `players` (otomo account ids: the uploader and every linked human), `tags`, `meta` (the index row as JSON), then the bytes in the final `file` part. An identical retry is 200, a different upload under that id is 409 `recording_conflict` (first upload wins). |
| Tags | `kicker:<id>` per kicker that played, then `humans:<n>`, `rule:<id>`, `ver:<appVersion>` and `custom`. Patch allows 16 tags of `[A-Za-z0-9_.:-]`, 1-64 chars; kickers are written first. |
| Kicker channel | `GET /patch/v1/recordings?tag=kicker:<id>&limit=20` with the player's token. |
| Featured | `GET /patch/v1/recordings?list=featured`. A 404 (never published) is an empty row. |
| `/battleReplay/play`, blob | the sealed URL id below; bytes from `GET /patch/v1/recordings/{id}/blob` with the player's token. |

Retention is patch's: it has no scheduled sweep (a recording is deleted by its producer or an operator). The old
30 day / 5 GiB policy no longer exists here.

The play URL id (`/battleReplay/blob/{keyId}`) is the replay id, the player's otomo device id and an expiry,
sealed with AES-GCM under `Otomo:Recordings:PlaySecret` (default `Otomo:TokenSecret`); the 32-char encryption key
is an HMAC of that id. No key table exists, so a restart or a second replica serves an id the first minted. As
before, an id is valid until it expires (`PlayLifetime`, 10 minutes), not for one use. Without any secret the key
is generated at startup and ids stop working at a restart.

The only state left in the process is the battle-end snapshot `/battle/end` leaves for the master client's upload
(kicker costumes are not in the archive); losing it costs a default costume on a row.

## Config keys

Section `Otomo:Recordings` (`Otomo__Recordings__X` environment). Defaults are in
`src/KickFlight.BootstrapApi/appsettings.json`. The old `Replays:*` section and its volume mounts are gone.

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Off: upload, index and play answer 503 (`replay-unavailable`, reason `Disabled`). |
| `BaseUrl` | (Otomo:Social:BaseUrl) | Patch's player listener through the gateway (list, blob). |
| `InternalBaseUrl` | (BaseUrl) | Patch's internal listener, `PATCH_INTERNAL_ADDR` (upload). |
| `ServiceKeyFile` / `ServiceKey` | empty | `patch_gameserver.key`. Without it uploads are `Disabled`. |
| `FeaturedList` | `featured` | The `patch.curated` list the Featured row reads. |
| `KickerChannelSize` | 20 | Replays per kicker channel (patch allows up to 200). |
| `MaxUploadBytes` | 67108864 (64 MiB) | Decoded gzip archive cap; base64 is rejected above 4/3 of it. Patch caps at `PATCH_RECORDING_MAX_BYTES` (64 MiB default). |
| `MaxDecompressedBytes` | 268435456 (256 MiB) | Gunzip cap (zip-bomb guard). |
| `PlayLifetime` | 10 min | Lifetime of the `/battleReplay/play` URL and key. |
| `PlaySecret` | empty | Seals the play URL; empty uses `Otomo:TokenSecret`. |
| `Timeout`, `TransferTimeout`, `RetryDelay` | 5 s, 60 s, 100 ms | Bounds on a list call, an upload or blob transfer, and the one retry. |

## Masters

Two new tables, registered in `DemoSessionApi.InitializeMasters`:

* `config/masters_battle_replay_channel.json` → `_encryptedMasters["BattleReplayChannel"]`:
  one `{id:1, battleReplayChannelType:3, name:"Featured", ...}` plus one
  `{id:1000+kickerId, battleReplayChannelType:2, name:<kicker>, kickerId:<n>, ...}` per kicker 1..14.
  Field names are `BattleReplayChannelMasterData` (`dump.cs:639875`).
* `config/masters_movie_category.json` → `_encryptedMasters["MovieCategory"]` (empty `[]`).

**Why those key names (verified):** the download name is the client's master class name without the
`Master` suffix. `MasterBase.get_ClassName` (RVA `0x1798DF4`) is a tail call to `Object.GetType()` followed
by `Type.get_Name` through the vtable (`ldp x2, x1, [x8, #0x198]`), i.e. it returns the concrete class name
(`BattleReplayChannelMaster`). Every master the server already serves follows the same convention
(`KickerParameterMaster` ↔ `KickerParameter`, `BattleRuleScaffoldMaster` ↔ …); the two properties are
`MasterManager.get_BattleReplayChannelMaster` (`dump.cs:526484`) and `get_MovieCategoryMaster`
(`dump.cs:526492`), so the keys are `BattleReplayChannel` and `MovieCategory`. **Not verified on device**:
the boot log is the final confirmation.

The `BattleReplayChannelInfo` ctor (`0x188D81C`, report §3.2) iterates the master rows and matches them to
`battleReplayChannelList` entries by `battleReplayChannelId`; that is why the index always serves all 15
channels, ids `1` and `1001..1014`.

## Uploaded archive format

The client's `replay.zip` is **not** a ZIP. Verified from disassembly:

* `ReplayUtility.ZipFiles` (RVA `0x1868578`) builds a `object[]{ length, name }` pair per file, calls
  `ReplayValue.SerializeObjects` (RVA `0x1868B84` → core `0x186AE3C` → `SerializeObject` `0x186AF18`), then
  `Stream.CopyTo`s the raw file bytes after it.
* `ReplayUtility.WriteDecompressedReplayData` (RVA `0x18698C4`) is the reader: for every member it calls
  `ReplayValue.DeserializeUInt` (RVA `0x18696FC`) then `DeserializeString` (RVA `0x18697E0`) then reads
  exactly `length` raw bytes.
* `DeserializeUInt` first calls `ReplayValue.ReadValueType` (RVA `0x186D50C`) and requires `TYPE_UINT = 2`;
  `ReadUInt` (RVA `0x186D6BC`) reads 4 bytes and `CustomBitConverter.ToUInt32` (RVA `0x17D4614`) copies them
  through `PutBytes` (RVA `0x17D40E8`, a forward byte copy) into a native `uint`, i.e. **little-endian**.
* `DeserializeString` requires `TYPE_STRING = 6`; `ReadString` (RVA `0x186DA6C`) reads an `int32` length with
  `ReadInt` (RVA `0x186D5D0`, same little-endian 4-byte read) and decodes `Encoding.UTF8.GetString(bytes, 0,
  length)`.
* `ReplayUtility.GetReplayWorkZipData` (`0x1868C04`) gzips the result with `D2CManager.GZipCompressFromBytes`
  (`0x1A6F9FC`), and `UploadReplayDataAsync` (`0x18A8AA0`) base64s it into `BattleUploadRequestData.uploadFile`.

So each member is:

```
[02] uint32-le length   [06] int32-le nameLen   name(UTF8)   raw bytes[length]
```

and the upload body is `base64( gzip( member... ) )` inside the D2C-encrypted JSON. `BattleReplayService.ParseArchive`
implements exactly this (unknown members such as `frames` are skipped but still validated).

The stored blob is the uploaded gzip bytes unchanged; playback plaintext is that same gzip.

## Playback contract (from the client)

`ReplaySelectCellView.<DownloadAndPlayReplayData>d__43.MoveNext` (`0x1789D04`) does
`NetworkManager.GetFromDataBinary(body, encryptionKey)` where `key = UTF8(encryptionKey)` and
`body[0..16)` is the IV. `D2CCodec.Encode` is the same AES-256-CBC/PKCS7 `IV || ciphertext`, so
`/battleReplay/blob/{keyId}` returns `D2CCodec.Encode(blob, UTF8(key), randomIV)`. The key must be exactly
32 bytes; `CreatePlayKeyAsync` emits 32 ASCII hex chars.

## What is verified / unverified

Verified by tests (`BattleReplayTests`, against `FakeOtomoRecordings`, which follows patch's own contract):

* archive parser round-trip and rejection of a wrong type tag;
* the upload's tags, players, kind and meta, the battle-end fallback snapshot, and first-upload-wins;
* kicker channel contents and newest-first order from the by-tag listing, and the channel size cap;
* Featured is the published curated list in its order, drops deleted recordings, and is empty (not invented)
  when nothing is published;
* the sealed play URL: round trip to the uploaded bytes, refusal of unknown, foreign, forged and expired ids,
  and service-restart safety;
* `/battle/end`, `/battle/upload`, `/battleReplay/index` and `/battleReplay/play` end to end, and the 503
  contract when patch is down or switched off.

Unverified / to check on a device:

* the master key names (`BattleReplayChannel`, `MovieCategory`) actually bind to `MasterManager`;
* the Kicker/Featured tabs render and playback enters the replay scene (report §7 list);
* whether `ReplayPlayFile.IsValid` accepts replays from another build (the index serves
  `applicationVersion` from the upload header verbatim);
* who publishes `patch.curated` (an owner decision; until then Featured is empty);
* large-match upload timing / gzip ratio against the 64 MiB cap.
