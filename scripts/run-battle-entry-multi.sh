#!/usr/bin/env bash
set -Eeuo pipefail

# macOS multi-AVD battle entry for already named profiles. Preserves app data.
# Stage 1–3 are driven by structured API logs. A roster screenshot is captured
# once after Stage 3; the experimental prime tap requires an explicit flag.

repo="$(cd "$(dirname "$0")/.." && pwd -P)"
serial_csv=""; player_csv=""; run_id="$(date -u +%Y%m%dT%H%M%SZ)"
port="${HTTP_PORT:-18080}"; api_log="${API_LOG:-}"
timeout=240; settle=60; launch_stagger=0; package=world.global.fenix
activity=world.global.fenix/com.google.firebase.MessagingUnityPlayerActivity
base="$repo/.local/battle-test"; adb="${ADB:-}"
phase=preflight; result=failed; failure=""; boot_status=not-run
identity_check=unavailable; profile_identity=not-verified; run_dir=""; listener_start=""; api_offset=-1
battle_id="not-verified"; human_count=0; bot_count=0; entry_span="not-verified"
offline_api_log=""; requested_battle_id=""; prime_control=0
offline_profile_log=""; expected_player_id=""
monitors=(); monitor_count=0; launched=0

usage() {
  echo "Usage: $0 --serials S1,S2,S3 --player-ids ID1,ID2,ID3 --api-log FILE [--run-id NAME] [--launch-stagger SECONDS] [--prime-control]"
  echo "Accepts 3-5 already booted devices; IDs pair with serials by list order."
  echo "--launch-stagger delays each app launch by N seconds (0-60); gameplay taps remain grouped."
  echo "--prime-control sends one experimental (540,350) tap after the Stage 3 screenshot checkpoint."
  echo "Offline API audit: $0 --offline-api-log FILE --player-ids ID1,ID2,ID3 [--battle-id ID]"
  echo "Offline profile check: $0 --offline-profile-log FILE --expected-player-id ID"
  echo "Profiles must already have unique names. No app data, caches, AVDs, or APKs are cleared/changed."
}
fail() { failure="$*"; echo "ERROR [$phase] $failure" >&2; exit 1; }

write_api_probe() {
  cat > "$1" <<'PY'
#!/usr/bin/env python3
"""Correlate one multi-client match from the API's JSON-line stdout log."""
import collections, datetime, json, re, sys

path, csv_ids = sys.argv[1:3]
requested = sys.argv[3] if len(sys.argv) > 3 and sys.argv[3] else None
players = csv_ids.split(',')
if len(players) not in (3, 4, 5) or len(set(players)) != len(players):
    sys.exit('expected 3–5 unique player IDs')

def stamp(value):
    if not value: return None
    try: return datetime.datetime.fromisoformat(value.replace('Z', '+00:00'))
    except ValueError: return None

entries = collections.defaultdict(list)
creates = {}
members = collections.defaultdict(set)
joins = collections.defaultdict(list)
closes = {}
stage1 = collections.defaultdict(list)
stage2 = collections.defaultdict(list)
stage3 = collections.defaultdict(list)
relevant = re.compile(r'battle/entry|battle room|joined room|Matchmaking window|Streamed Stage [123]', re.I)
try:
    stream = open(path, encoding='utf-8')
except OSError as exc:
    sys.exit(str(exc))
with stream:
    for number, raw in enumerate(stream, 1):
        try: row = json.loads(raw)
        except json.JSONDecodeError:
            if relevant.search(raw): sys.exit(f'line {number}: malformed JSON in matchmaking log')
            continue
        state = row.get('State') or {}
        message = row.get('Message') or state.get('Message') or ''
        ts = stamp(row.get('Timestamp'))
        if 'Handled /battle/entry for ' in message:
            match = re.search(r'Handled /battle/entry for (\d+):', message)
            if match and ts: entries[match.group(1)].append(ts)
        match = re.search(r'Created battle room (\S+) for user (\d+); window open for ([0-9.]+)s', message)
        if match and ts:
            bid, owner, seconds = match.groups()
            creates[bid] = (owner, ts, float(seconds))
            members[bid].add(owner)
        match = re.search(r'Human (\d+) joined room (\S+) \((\d+) human\(s\) waiting\)', message)
        if match and ts:
            user, bid, count = match.groups()
            members[bid].add(user); joins[bid].append((user, ts, int(count)))
        match = re.search(r'Matchmaking window of room (\S+) closed with (\d+) human\(s\); filling empty slots with bots', message)
        if match and ts:
            bid, humans = match.groups()
            closes[bid] = (ts, int(humans), True)
        match = re.search(r'Streamed Stage 1 \(room preparation / searching; (\d+) human\(s\)\) for user (\d+)', message)
        if match and ts: stage1[match.group(2)].append((ts, int(match.group(1))))
        match = re.search(r'Streamed Stage 2 \(full roster / Iniciar combate\) for user (\d+)', message)
        if match and ts: stage2[match.group(1)].append(ts)
        match = re.search(r'Streamed Stage 3 \(battle start assignment: (\S+)\) for user (\d+)', message)
        if match and ts: stage3[match.group(2)].append((match.group(1), ts))

candidates = [bid for bid, (owner, _, _) in creates.items() if owner in players]
if requested:
    candidates = [bid for bid in candidates if bid == requested]
if not candidates:
    if requested: sys.exit(f'BattleId {requested} was not created by any requested player')
    print('waiting-room\t-\t0\t0\t-\troom-not-created')
    sys.exit(2)

# The offline audit may contain many historic runs. Select the newest candidate;
# a live run has only the current room in its byte-offset delta.
bid = max(candidates, key=lambda key: creates[key][1])
owner, created, window_seconds = creates[bid]
closed = closes.get(bid)
deadline = closed[0] if closed else created + datetime.timedelta(seconds=window_seconds + 5)
run_entries = {}
for user in players:
    current = [t for t in entries.get(user, []) if created - datetime.timedelta(seconds=10) <= t <= deadline]
    if len(current) > 1: sys.exit(f'{user} sent multiple /battle/entry requests in room window {bid}')
    if current: run_entries[user] = current[0]
entry_span = None
if len(run_entries) == len(players):
    times = list(run_entries.values())
    entry_span = (max(times) - min(times)).total_seconds()
    if entry_span >= 60: sys.exit(f'entry spread {entry_span:.3f}s is not below 60s')
    if max(times) - created >= datetime.timedelta(seconds=window_seconds):
        sys.exit(f'last /battle/entry was outside the {window_seconds:g}s room window')

missing_entries = [u for u in players if u not in run_entries]
missing_members = [u for u in players if u not in members[bid]]
if missing_entries:
    print(f'waiting-entries\t{bid}\t{len(members[bid])}\t0\t-\t{",".join(missing_entries)}')
    sys.exit(2)
if missing_members:
    print(f'waiting-room-members\t{bid}\t{len(members[bid])}\t0\t{entry_span:.3f}\t{",".join(missing_members)}')
    sys.exit(2)

span = f'{entry_span:.3f}'
if not closed:
    print(f'waiting-room-close\t{bid}\t{len(members[bid])}\t0\t{span}\troom-open')
    sys.exit(2)
close_time, human_count, bots_filled = closed
if human_count != len(players):
    sys.exit(f'{bid} closed with {human_count} humans, expected {len(players)}')
if human_count > 8: sys.exit(f'{bid} exceeds the 8-slot 4v4 room capacity')
window_elapsed = (close_time - created).total_seconds()
if window_elapsed < max(0, window_seconds - 1) or window_elapsed > window_seconds + 5:
    sys.exit(f'{bid} closed after {window_elapsed:.3f}s, outside the configured window tolerance')
if members[bid] != set(players):
    sys.exit(f'{bid} human membership mismatch: {sorted(members[bid])}')
missing_stage1 = [u for u in players if not any(created <= t <= close_time for t, _ in stage1.get(u, []))]
if missing_stage1:
    print(f'waiting-stage1\t{bid}\t{human_count}\t{8-human_count}\t{span}\t{",".join(missing_stage1)}')
    sys.exit(2)
missing_stage2 = [u for u in players if not any(close_time <= t <= close_time + datetime.timedelta(seconds=10) for t in stage2.get(u, []))]
if missing_stage2:
    print(f'waiting-stage2\t{bid}\t{human_count}\t{8-human_count}\t{span}\t{",".join(missing_stage2)}')
    sys.exit(2)
missing_stage3 = [u for u in players if not any(room == bid and close_time <= t <= close_time + datetime.timedelta(seconds=15) for room, t in stage3.get(u, []))]
if missing_stage3:
    print(f'stage2-waiting-stage3\t{bid}\t{human_count}\t{8-human_count}\t{span}\t{",".join(missing_stage3)}')
    sys.exit(2)
print(f'stage3-ready\t{bid}\t{human_count}\t{8-human_count}\t{span}\t-')
PY
}

write_profile_probe() {
  cat > "$1" <<'PY'
#!/usr/bin/env python3
"""Check the numeric user IDs loaded from one fresh client logcat."""
import re, sys

path, expected = sys.argv[1:3]
pattern = re.compile(r'Load user profile\(id=([0-9]+)\)')
try:
    with open(path, encoding='utf-8', errors='replace') as stream:
        ids = [match.group(1) for line in stream for match in [pattern.search(line)] if match]
except OSError as exc:
    sys.exit(str(exc))
if not ids:
    print('profile-missing\t-')
    sys.exit(2)
unique = sorted(set(ids))
if unique != [expected]:
    print(f'profile-mismatch\t{",".join(unique)}')
    sys.exit(1)
print(f'profile-verified\t{expected}')
PY
}

verify_player_profile() {
  local profile_serial expected_profile_id profile_deadline profile_rc profile_output
  profile_serial="$1"; expected_profile_id="$2"; profile_deadline=$((SECONDS + 120))
  profile_identity="waiting-$profile_serial-$expected_profile_id"
  while ((SECONDS < profile_deadline)); do
    live_check
    if profile_output="$(python3 "$run_dir/profile-gate.py" "$run_dir/logcat-$profile_serial.txt" "$expected_profile_id" 2>> "$run_dir/profile-gate-errors.log")"; then
      profile_identity="verified-$profile_serial-$expected_profile_id"
      echo "$(date -u +%FT%TZ) $profile_identity" >> "$run_dir/actions.log"
      return 0
    else
      profile_rc=$?
    fi
    if ((profile_rc == 1)); then
      profile_identity="mismatch-$profile_serial-expected-$expected_profile_id"
      fail "$profile_serial loaded a different or multiple numeric profile IDs ($profile_output); expected $expected_profile_id"
    elif ((profile_rc != 2)); then
      [[ ! -s "$run_dir/profile-gate-errors.log" ]] || tail -n 12 "$run_dir/profile-gate-errors.log" >&2
      fail "profile identity probe failed for $profile_serial (exit $profile_rc)"
    fi
    sleep 2
  done
  profile_identity="missing-$profile_serial-expected-$expected_profile_id"
  fail "no numeric Load user profile(id=...) appeared for $profile_serial within 120 seconds; expected $expected_profile_id"
}

while (($#)); do
  case "$1" in
    --serials) serial_csv="${2:?}"; shift 2 ;;
    --player-ids) player_csv="${2:?}"; shift 2 ;;
    --run-id) run_id="${2:?}"; shift 2 ;;
    --api-log) api_log="${2:?}"; shift 2 ;;
    --offline-api-log) offline_api_log="${2:?}"; shift 2 ;;
    --offline-profile-log) offline_profile_log="${2:?}"; shift 2 ;;
    --expected-player-id) expected_player_id="${2:?}"; shift 2 ;;
    --battle-id) requested_battle_id="${2:?}"; shift 2 ;;
    --http-port) port="${2:?}"; shift 2 ;;
    --timeout) timeout="${2:?}"; shift 2 ;;
    --game-settle) settle="${2:?}"; shift 2 ;;
    --launch-stagger) launch_stagger="${2:?}"; shift 2 ;;
    --prime-control) prime_control=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; fail "unknown argument $1" ;;
  esac
done
if [[ -n "$offline_profile_log" ]]; then
  [[ -z "$offline_api_log" && -z "$serial_csv" && -z "$player_csv" ]] || fail "offline profile mode accepts only the profile log and expected player ID"
  [[ "$expected_player_id" =~ ^[0-9]{7,}$ ]] || fail "offline profile mode requires a numeric --expected-player-id"
  tmp_profile_probe="$(mktemp "${TMPDIR:-/tmp}/kf-profile-gate.XXXXXX")" || fail "cannot create temporary profile gate helper"
  write_profile_probe "$tmp_profile_probe"
  set +e
  python3 "$tmp_profile_probe" "$offline_profile_log" "$expected_player_id"
  offline_profile_status=$?
  set -e
  rm -f "$tmp_profile_probe"
  exit "$offline_profile_status"
fi
[[ -n "$player_csv" ]] || { usage >&2; fail "player IDs are required"; }
if [[ -z "$offline_api_log" ]]; then
  [[ -n "$serial_csv" ]] || { usage >&2; fail "serials are required outside offline API audit mode"; }
  [[ -n "$api_log" ]] || fail "--api-log or API_LOG is required to verify human battle entries"
else
  [[ -z "$serial_csv" ]] || fail "--serials cannot be used with --offline-api-log"
fi
[[ "$run_id" =~ ^[A-Za-z0-9._-]+$ ]] || fail "invalid run ID"
[[ "$launch_stagger" =~ ^[0-9]+$ ]] || fail "launch stagger must be an integer"
((launch_stagger <= 60)) || fail "launch stagger must be between 0 and 60 seconds"
IFS=',' read -r -a serials <<< "$serial_csv"
IFS=',' read -r -a players <<< "$player_csv"
names=()
(( ${#players[@]} >= 3 && ${#players[@]} <= 5 )) || fail "requires 3, 4, or 5 player IDs"
if [[ -z "$offline_api_log" ]]; then
  (( ${#serials[@]} >= 3 && ${#serials[@]} <= 5 )) || fail "requires 3, 4, or 5 serials"
  (( ${#serials[@]} == ${#players[@]} )) || fail "player ID count must match serial count"
fi
if [[ -z "$offline_api_log" ]]; then
for i in "${!serials[@]}"; do
  s="${serials[$i]}"; p="${players[$i]}"
  [[ -n "$s" ]] || fail "empty serial at index $i"
  [[ "$p" =~ ^[0-9]{7,}$ ]] || fail "invalid player ID $p"
  # Bash 3.2 ships with macOS and has no associative arrays. Lists are capped
  # at five devices, so a small pairwise check keeps this runner portable.
  for j in "${!serials[@]}"; do
    if (( j < i )); then
      [[ "${serials[$j]}" != "$s" ]] || fail "repeated serial $s"
      [[ "${players[$j]}" != "$p" ]] || fail "repeated player ID $p"
    fi
  done
done
fi
if [[ -n "$offline_api_log" ]]; then
  tmp_api_probe="$(mktemp "${TMPDIR:-/tmp}/kf-api-gate.XXXXXX")" || fail "cannot create temporary API gate helper"
  write_api_probe "$tmp_api_probe"
  set +e
  python3 "$tmp_api_probe" "$offline_api_log" "$player_csv" "$requested_battle_id"
  offline_status=$?
  set -e
  rm -f "$tmp_api_probe"
  exit "$offline_status"
fi
if [[ -z "$adb" ]]; then
  [[ ! -x "$repo/.local/android-sdk/platform-tools/adb" ]] || adb="$repo/.local/android-sdk/platform-tools/adb"
fi
[[ -n "$adb" ]] || adb="$(command -v adb || true)"
[[ -x "$adb" ]] || fail "adb not found; set ADB=/path/to/adb"
for tool in curl lsof sips; do command -v "$tool" >/dev/null || fail "$tool is required"; done

mkdir -p "$base"
run_dir="$base/multi-$run_id"
[[ ! -e "$run_dir" ]] || fail "run directory exists: $run_dir"
mkdir "$run_dir"
write_api_probe "$run_dir/api-gate.py"
mkdir "$base/.multi-run-lock" 2>/dev/null || fail "another multi-device run is active"
lock="$base/.multi-run-lock"; echo "$$" > "$lock/pid"

listener_pids() { lsof -nP -t -iTCP:"$port" -sTCP:LISTEN 2>/dev/null | sort -u; }
check_listener() {
  local now
  now="$(listener_pids || true)"
  [[ -n "$now" ]] || fail "API listener missing on port $port"
  [[ "$now" == "$listener_start" ]] || fail "API listener PID changed"
}
get_size() {
  local line size
  line="$("$adb" -s "$1" shell wm size 2>/dev/null | tail -n1 | tr -d '\r')"
  size="${line##* }"
  [[ "$size" =~ ^([0-9]+)x([0-9]+)$ ]] || fail "cannot read screen size for $1"
  echo "${BASH_REMATCH[1]} ${BASH_REMATCH[2]}"
}
point() {
  local dims w h
  dims="$(get_size "$1")"; read -r w h <<< "$dims"
  echo "$((($2 * w + 540) / 1080)) $((($3 * h + 960) / 1920))"
}
tap_all() {
  local x y s xy sx sy
  x="$1"; y="$2"; phase="tap-$3"
  for s in "${serials[@]}"; do
    xy="$(point "$s" "$x" "$y")"; read -r sx sy <<< "$xy"
    "$adb" -s "$s" shell input tap "$sx" "$sy" >/dev/null || fail "tap failed on $s"
  done
  echo "$(date -u +%FT%TZ) $phase" >> "$run_dir/actions.log"
}
tap_native_all() {
  local x y s dims w h sx sy
  x="$1"; y="$2"; phase="tap-native-$3"
  for s in "${serials[@]}"; do
    dims="$(get_size "$s")"; read -r w h <<< "$dims"
    sx="$(( (x * w + 540) / 1080 ))"; sy="$(( (y * h + 1170) / 2340 ))"
    "$adb" -s "$s" shell input tap "$sx" "$sy" >/dev/null || fail "native tap failed on $s"
  done
  echo "$(date -u +%FT%TZ) $phase" >> "$run_dir/actions.log"
}
capture_all() {
  local name s raw out
  name="$1"
  for s in "${serials[@]}"; do
    raw="$run_dir/.$name-$s-raw.png"; out="$run_dir/$name-$s-720.png"
    "$adb" -s "$s" exec-out screencap -p > "$raw" || fail "screencap failed for $s"
    sips --resampleWidth 720 "$raw" --out "$out" >/dev/null 2>&1 || fail "sips failed for $s"
    rm -f "$raw"
  done
}
capture_one() {
  local s name raw out
  s="$1"; name="$2"
  raw="$run_dir/.$name-$s-raw.png"; out="$run_dir/$name-$s-720.png"
  "$adb" -s "$s" exec-out screencap -p > "$raw" || fail "screencap failed for $s"
  sips --resampleWidth 720 "$raw" --out "$out" >/dev/null 2>&1 || fail "sips failed for $s"
  rm -f "$raw"
}
write_screen_probe() {
  cat > "$run_dir/screen-probe.py" <<'PY'
#!/usr/bin/env python3
"""Tiny stdlib-only PNG sampler for the existing runner's visual checkpoints."""
import struct, sys, zlib

mode, path = sys.argv[1:3]
blob = open(path, 'rb').read()
if blob[:8] != b'\x89PNG\r\n\x1a\n': sys.exit('not a PNG')
pos = 8; width = height = depth = color = None; compressed = bytearray()
while pos < len(blob):
    size = struct.unpack('>I', blob[pos:pos+4])[0]; kind = blob[pos+4:pos+8]
    data = blob[pos+8:pos+8+size]; pos += size + 12
    if kind == b'IHDR': width, height, depth, color, _, _, interlace = struct.unpack('>IIBBBBB', data)
    elif kind == b'IDAT': compressed.extend(data)
    elif kind == b'IEND': break
if depth != 8 or color not in (2, 6) or interlace != 0:
    sys.exit('unsupported PNG encoding (expected non-interlaced RGB/RGBA)')
bpp = 3 if color == 2 else 4; stride = width * bpp
raw = zlib.decompress(compressed); rows = []; prior = bytearray(stride); offset = 0
for _ in range(height):
    filt = raw[offset]; offset += 1; row = bytearray(raw[offset:offset+stride]); offset += stride
    for i in range(stride):
        a = row[i-bpp] if i >= bpp else 0; b = prior[i]; c = prior[i-bpp] if i >= bpp else 0
        if filt == 1: row[i] = (row[i] + a) & 255
        elif filt == 2: row[i] = (row[i] + b) & 255
        elif filt == 3: row[i] = (row[i] + ((a+b)//2)) & 255
        elif filt == 4:
            p = a+b-c; pa = abs(p-a); pb = abs(p-b); pc = abs(p-c)
            row[i] = (row[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        elif filt != 0: sys.exit('unsupported PNG filter')
    rows.append(row); prior = row
def pixel(x, y):
    x = min(width-1, max(0, int(x*width))); y = min(height-1, max(0, int(y*height)))
    i = x*bpp; r,g,b = rows[y][i:i+3]; return r,g,b
def scan(x0, x1, y0, y1, dx, dy, predicate):
    count = 0; y = y0
    while y <= y1:
        x = x0
        while x <= x1:
            if predicate(*pixel(x,y)): count += 1
            x += dx
        y += dy
    return count
if mode == 'title':
    n = scan(.04,.96,.03,.97,.042,.016,lambda r,g,b:r+g+b >= 180)
    overlay = scan(.02,.98,0,.32,.03,.02,lambda r,g,b:r>=225 and g>=225 and b>=225 and max(r,g,b)-min(r,g,b)<=18)
    if overlay >= 150: print('got-it-overlay')
    else: print('ready' if n >= 250 else 'loading')
elif mode == 'home':
    notices = scan(.20,.80,.06,.13,.037,.009,lambda r,g,b:r>=220 and g>=160 and b<=80)
    white = scan(.14,.86,.19,.62,.056,.026,lambda r,g,b:r>=220 and g>=220 and b>=220)
    rule_frame = scan(.06,.94,.22,.31,.035,.008,lambda r,g,b:r>=225 and g>=175 and b<=65)
    combat = scan(.52,.97,.24,.52,.022,.012,lambda r,g,b:r>=220 and g>=160 and b<=80)
    keyboard = scan(.02,.98,.70,.99,.035,.014,lambda r,g,b:r>=225 and g>=225 and b>=225)
    if notices >= 12: print('notices')
    elif rule_frame >= 45: print('rule-modal')
    elif white >= 120: print('unexpected-modal')
    elif keyboard >= 100: print('unexpected-modal')
    elif combat >= 40: print('combat')
    else: print('loading')
elif mode == 'download':
    yellow = scan(.02,.98,.05,.95,.025,.01,lambda r,g,b:r>=220 and g>=160 and b<=80)
    ink = scan(.10,.90,.43,.60,.025,.01,lambda r,g,b:r+g+b<=100)
    print('download-ready' if yellow >= 2500 and ink >= 70 else 'other-screen')
else: sys.exit('unknown screen mode')
PY
}
visual_state() {
  local s mode raw state
  s="$1"; mode="$2"; raw="$run_dir/.probe-$s.png"
  "$adb" -s "$s" exec-out screencap -p > "$raw" || fail "visual probe failed for $s"
  state="$(python3 "$run_dir/screen-probe.py" "$mode" "$raw")" || fail "cannot inspect $mode screen on $s"
  rm -f "$raw"
  echo "$state"
}
wait_visual() {
  local s mode wanted limit save end probes state
  s="$1"; mode="$2"; wanted="$3"; limit="$4"; save="${5:-save}"
  phase="wait-${mode}-${s}"; end=$((SECONDS + limit)); probes=0
  while ((SECONDS < end)); do
    live_check
    state="$(visual_state "$s" "$mode")"; probes=$((probes + 1))
    [[ "$state" != got-it-overlay ]] || fail "$s shows the system Got it overlay; dismiss it through onboarding before battle entry"
    if [[ "$state" == "$wanted" ]]; then
      [[ "$save" != save ]] || capture_one "$s" "${mode}-${wanted}"
      echo "$(date -u +%FT%TZ) visual-ready $s $mode $wanted probes=$probes" >> "$run_dir/actions.log"
      return 0
    fi
    sleep 5
  done
  fail "timeout waiting for $wanted visual state on $s"
}
tap_one_native() {
  local s x y action dims w h sx sy
  s="$1"; x="$2"; y="$3"; action="$4"
  dims="$(get_size "$s")"; read -r w h <<< "$dims"
  sx="$(((x * w + 540) / 1080))"; sy="$(((y * h + 1170) / 2340))"
  "$adb" -s "$s" shell input tap "$sx" "$sy" >/dev/null || fail "tap $action failed on $s"
  echo "$(date -u +%FT%TZ) $s tap-$action native=$x,$y actual=$sx,$sy" >> "$run_dir/actions.log"
}
wait_any_scene() {
  local first second limit end pending s
  first="$1"; second="$2"; limit="$3"; phase="wait-$first-or-$second"; end=$((SECONDS + limit))
  while ((SECONDS < end)); do
    live_check
    pending=0
    for s in "${serials[@]}"; do
      if ! grep -Fq "$first" "$run_dir/logcat-$s.txt" && ! grep -Fq "$second" "$run_dir/logcat-$s.txt"; then pending=$((pending + 1)); fi
    done
    ((pending == 0)) && return 0
    sleep 2
  done
  fail "timeout waiting for $first or $second"
}
swipe_game_scaled() {
  local s x1 y1 x2 y2 duration action dims w h sx1 sy1 sx2 sy2
  s="$1"; x1="$2"; y1="$3"; x2="$4"; y2="$5"; duration="$6"; action="$7"
  dims="$(get_size "$s")"; read -r w h <<< "$dims"
  sx1="$(((x1 * w + 540) / 1080))"; sy1="$(((y1 * h + 960) / 1920))"
  sx2="$(((x2 * w + 540) / 1080))"; sy2="$(((y2 * h + 960) / 1920))"
  "$adb" -s "$s" shell input swipe "$sx1" "$sy1" "$sx2" "$sy2" "$duration" >/dev/null || fail "swipe $action failed on $s"
  echo "$(date -u +%FT%TZ) $s swipe-$action $sx1,$sy1-$sx2,$sy2 ${duration}ms" >> "$run_dir/actions.log"
}
live_check() {
  local allow_missing_process s
  allow_missing_process="${1:-false}"
  for s in "${serials[@]}"; do
    grep -Eiq 'SIGSEGV|signal 11|SIGABRT|signal 6|Scudo|FATAL EXCEPTION|IndexOutOfRangeException' "$run_dir/logcat-$s.txt" && fail "fatal log marker on $s"
    if [[ "$allow_missing_process" != true ]]; then
      "$adb" -s "$s" shell pidof "$package" >/dev/null 2>&1 || fail "$s app process exited"
    fi
  done
  check_listener
}
refresh_api_delta() {
  local bytes
  bytes="$(wc -c < "$api_log" | tr -d ' ')"
  ((bytes >= api_offset)) || fail "API log was truncated or rotated during the run"
  tail -c +$((api_offset + 1)) "$api_log" > "$run_dir/api-log-delta.txt"
}
api_gate_snapshot() {
  local gate_rc gate_output
  gate_rc=0
  gate_output="$(python3 "$run_dir/api-gate.py" "$run_dir/api-log-delta.txt" "$player_csv" "$requested_battle_id" 2>> "$run_dir/api-gate-errors.log")" || gate_rc=$?
  ((gate_rc == 0 || gate_rc == 2)) || {
    [[ ! -s "$run_dir/api-gate-errors.log" ]] || tail -n 12 "$run_dir/api-gate-errors.log" >&2
    fail "API matchmaking correlation failed (parser exit $gate_rc)"
  }
  printf '%s\n' "$gate_output" | tail -n1
}
wait_api_gate() {
  local target limit end previous gate_line gate_phase gate_missing
  target="$1"; limit="$2"; phase="wait-api-$target"; end=$((SECONDS + limit)); previous=""
  while ((SECONDS < end)); do
    live_check
    refresh_api_delta
    gate_line="$(api_gate_snapshot)"
    IFS=$'\t' read -r gate_phase battle_id human_count bot_count entry_span gate_missing <<< "$gate_line"
    [[ -n "$gate_phase" ]] || fail "API gate returned no status"
    if [[ "$gate_line" != "$previous" ]]; then
      echo "$(date -u +%FT%TZ) api-gate $gate_line" >> "$run_dir/actions.log"
      previous="$gate_line"
    fi
    if [[ "$target" == stage3 && "$gate_phase" == stage3-ready ]]; then return 0; fi
    sleep 1
  done
  fail "timed out waiting for API-correlated $target ($gate_line)"
}
wait_scene() {
  local marker limit started end count s
  marker="$1"; limit="$2"; phase="wait-$marker"; started="$SECONDS"; end=$((SECONDS + limit))
  while ((SECONDS < end)); do
    # Android returns from am start before the app process exists on cold start.
    # Allow startup time only while waiting for the first Unity scene.
    if [[ "$marker" == TitleScene ]] && ((SECONDS - started < 15)); then
      live_check true
    else
      live_check
    fi
    count=0
    for s in "${serials[@]}"; do grep -Fq "$marker" "$run_dir/logcat-$s.txt" || count=$((count + 1)); done
    ((count == 0)) && return 0
    sleep 3
  done
  fail "timeout waiting for $marker on $count client(s)"
}
wait_home_combat() {
  local s="$1" probes=0 home_deadline=$((SECONDS + 480)) rule_taps=0 notice_taps=0 state
  while :; do
    ((SECONDS < home_deadline)) || fail "timeout waiting for rendered Combat/modal UI on $s"
    live_check
    state="$(visual_state "$s" home)"; probes=$((probes + 1))
    case "$state" in
      combat)
        capture_one "$s" home-combat-ready
        echo "$(date -u +%FT%TZ) visual-ready $s home combat probes=$probes" >> "$run_dir/actions.log"
        return 0
        ;;
      rule-modal)
        ((rule_taps < 3)) || fail "Rule Update modal did not close after 3 UI-confirmed taps on $s"
        capture_one "$s" rule-update-modal
        tap_one_native "$s" 540 1740 rule-update-accept
        rule_taps=$((rule_taps + 1)); sleep 2
        ;;
      notices)
        ((notice_taps < 3)) || fail "Notices modal did not close after 3 UI-confirmed taps on $s"
        capture_one "$s" notices-modal
        tap_one_native "$s" 540 2220 notices-close
        notice_taps=$((notice_taps + 1)); sleep 2
        ;;
      unexpected-modal)
        capture_one "$s" unexpected-home-modal
        fail "$s Home screen has a modal that is not the recognized Notices overlay; stopping without an unverified tap"
        ;;
      *) sleep 5 ;;
    esac
  done
}
stop_monitors() {
  local p
  if ((monitor_count > 0)); then
    for p in "${monitors[@]}"; do kill "$p" 2>/dev/null || true; done
  fi
}
finish() {
  local code s raw out bytes i
  code=$?; trap - EXIT; stop_monitors
  if [[ "$result" == failed && "$code" -eq 0 ]]; then code=1; fi
  if ((launched)) && [[ "$result" == failed ]]; then
    for s in "${serials[@]}"; do
      raw="$run_dir/.failure-$s-raw.png"; out="$run_dir/failure-$s-720.png"
      if [[ -x "$adb" ]] && python3 - "$adb" "$s" "$raw" <<'PY'
import subprocess, sys
try:
    with open(sys.argv[3], 'wb') as output:
        subprocess.run([sys.argv[1], '-s', sys.argv[2], 'exec-out', 'screencap', '-p'],
                       stdout=output, stderr=subprocess.DEVNULL, timeout=8, check=True)
except (OSError, subprocess.SubprocessError):
    raise SystemExit(1)
PY
      then
        if [[ -s "$raw" ]]; then sips --resampleWidth 720 "$raw" --out "$out" >/dev/null 2>&1 || true; fi
      fi
      rm -f "$raw"
      "$adb" -s "$s" logcat -d -v threadtime > "$run_dir/logcat-$s-full.txt" 2>/dev/null || true
    done
  fi
  if [[ -n "$api_log" && "$api_offset" -ge 0 && -f "$api_log" ]]; then
    bytes="$(wc -c < "$api_log" | tr -d ' ')"
    if ((bytes >= api_offset)); then tail -c +$((api_offset + 1)) "$api_log" > "$run_dir/api-log-delta.txt"; fi
  fi
  {
    echo "run_id=$run_id"; echo "result=$result"; echo "phase=$phase"; echo "failure=$failure"
    echo "api_port=$port"; echo "api_listener_pids=$listener_start"; echo "boot_index_host_10.0.2.2=$boot_status"
    echo "identity_check=$identity_check"; echo "profile_identity=$profile_identity"; echo "battle_id=$battle_id"; echo "human_count=$human_count"; echo "bot_count=$bot_count"
    echo "entry_span_seconds=$entry_span"; echo "movement_and_attack=gestures-sent-manual-visual-review-required"
    echo "roster=one-720px-screenshot-per-serial-after-stage3; manual-visual-review-required"; echo "prime_control=$prime_control"; echo "evidence=$run_dir"; printf '\nserial\tplayer_id\tplayer_name\n'
    for i in "${!serials[@]}"; do printf '%s\t%s\t%s\n' "${serials[$i]}" "${players[$i]}" "${names[$i]:-}"; done
  } > "$run_dir/summary.txt"
  rm -f "$lock/pid"; rmdir "$lock" 2>/dev/null || true
  exit "$code"
}
trap finish EXIT

phase=api-listener-preflight
listener_start="$(listener_pids || true)"
[[ -n "$listener_start" ]] || fail "no listener on TCP $port"
phase=boot-index-preflight
dd if=/dev/zero of="$run_dir/boot-probe.bin" bs=32 count=1 2>/dev/null
boot_status="$(curl -sS --max-time 8 --http1.1 -X POST -H 'Host: 10.0.2.2' \
  -H 'Content-Type: application/octet-stream' --data-binary "@$run_dir/boot-probe.bin" \
  -D "$run_dir/boot-headers.txt" -o "$run_dir/boot-response.bin" -w '%{http_code}' \
  "http://127.0.0.1:$port/boot/index")" || fail "POST /boot/index failed"
[[ "$boot_status" == 200 ]] || fail "POST /boot/index Host 10.0.2.2 returned $boot_status"
grep -Eiq '^x-app-status-code:[[:space:]]*0' "$run_dir/boot-headers.txt" || fail "boot status header missing"
check_listener

[[ "$api_log" = /* ]] || api_log="$repo/$api_log"
[[ -f "$api_log" ]] || fail "API log not found: $api_log"
api_offset="$(wc -c < "$api_log" | tr -d ' ')"
phase=device-preflight
for i in "${!serials[@]}"; do
  s="${serials[$i]}"; p="${players[$i]}"
  [[ "$("$adb" -s "$s" get-state 2>/dev/null || true)" == device ]] || fail "$s not ready"
  [[ "$("$adb" -s "$s" shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" == 1 ]] || fail "$s not booted"
  [[ "$("$adb" -s "$s" shell pm path "$package" 2>/dev/null | head -n1)" == package:* ]] || fail "app missing on $s"
  player_file="$repo/src/KickFlight.BootstrapApi/data/users/$p.json"
  [[ -f "$player_file" ]] || fail "player file missing for $p"
  info="$(python3 - "$player_file" "$p" <<'PY'
import json, sys
with open(sys.argv[1], encoding='utf-8') as f: data=json.load(f)
pid=str(data.get('PlayerId', data.get('UserId', '')))
print(pid+'\t'+str(data.get('UserName', '')))
PY
)" || fail "invalid player JSON for $p"
  IFS=$'\t' read -r file_id name <<< "$info"
  [[ "$file_id" == "$p" ]] || fail "player file $p reports a different ID ($file_id)"
  [[ "$name" =~ ^[A-Za-z0-9_-]{1,10}$ ]] || fail "player $p needs a saved non-empty name of at most 10 safe characters"
  [[ -f "$repo/src/KickFlight.BootstrapApi/data/users/_identities.json" ]] || fail "identity index missing"
  python3 - "$repo/src/KickFlight.BootstrapApi/data/users/_identities.json" "$p" <<'PY' || fail "identity index does not map a UUID to player $p"
import json, sys
with open(sys.argv[1], encoding='utf-8') as f: data=json.load(f)
target=sys.argv[2]
def walk(value):
    if isinstance(value, dict):
        for v in value.values(): yield from walk(v)
    elif isinstance(value, list):
        for v in value: yield from walk(v)
    else: yield str(value)
raise SystemExit(0 if target in set(walk(data)) else 1)
PY
  if ((${#names[@]} > 0)); then
    for previous_name in "${names[@]}"; do [[ "$previous_name" != "$name" ]] || fail "duplicate player name $name"; done
  fi
  names+=("$name")
done
registry="$base/multi-run-player-registry.tsv"; touch "$registry"
# Keep this append-only as an audit trail. IDs can enter successive rooms;
# uniqueness is enforced within the current run and by fresh per-serial logs.
for i in "${!serials[@]}"; do printf '%s\t%s\t%s\n' "$run_id" "${players[$i]}" "${serials[$i]}" >> "$registry"; done

phase=launch
for s in "${serials[@]}"; do "$adb" -s "$s" shell am force-stop "$package" >/dev/null; "$adb" -s "$s" logcat -c; done
for s in "${serials[@]}"; do
  : > "$run_dir/logcat-$s.txt"
  "$adb" -s "$s" logcat -v time Unity:D Repro:D CRASH:E AndroidRuntime:E libc:E DEBUG:E '*:S' >> "$run_dir/logcat-$s.txt" 2>&1 &
  monitors+=("$!")
  monitor_count=$((monitor_count + 1))
done
launched=1
for i in "${!serials[@]}"; do
  s="${serials[$i]}"
  launch_time="$(date -u +%FT%TZ)"
  "$adb" -s "$s" shell am start -n "$activity" > "$run_dir/launch-$s.txt" 2>&1 || fail "launch command failed on $s"
  echo "$launch_time am-start $s" >> "$run_dir/actions.log"
  if ((launch_stagger > 0 && i + 1 < ${#serials[@]})); then
    echo "$(date -u +%FT%TZ) launch-stagger-${launch_stagger}s-after-$s" >> "$run_dir/actions.log"
    sleep "$launch_stagger"
  fi
done

write_screen_probe
write_profile_probe "$run_dir/profile-gate.py"
wait_scene TitleScene "$timeout"
for s in "${serials[@]}"; do wait_visual "$s" title ready 120; done
# TitleScene can be logged over LOADING; this visual gate is per device.
tap_native_all 540 1200 start
wait_any_scene DownloadScene HomeScene 180
for s in "${serials[@]}"; do
  latest_scene_line="$(grep -E 'DownloadScene|HomeScene' "$run_dir/logcat-$s.txt" | tail -n1 || true)"
  if [[ "$latest_scene_line" == *DownloadScene* ]]; then
    phase=download-visual-gate
    download_deadline=$((SECONDS + 60)); download_state=other-screen; download_confirmed=0
    while ((SECONDS < download_deadline)); do
      live_check
      latest_scene_line="$(grep -E 'DownloadScene|HomeScene' "$run_dir/logcat-$s.txt" | tail -n1 || true)"
      if [[ "$latest_scene_line" == *HomeScene* ]]; then
        download_state=home-scene
        break
      elif [[ "$latest_scene_line" != *DownloadScene* ]]; then
        fail "unexpected latest scene marker while checking DownloadScene on $s"
      fi
      download_state="$(visual_state "$s" download)" || fail "cannot inspect current DownloadScene on $s"
      if [[ "$download_state" == download-ready ]]; then
        capture_one "$s" download-current
        final_download_state="$(python3 "$run_dir/screen-probe.py" download "$run_dir/download-current-$s-720.png")" || fail "cannot inspect saved DownloadScene checkpoint on $s"
        if [[ "$final_download_state" == download-ready ]]; then
          echo "$(date -u +%FT%TZ) download-screen-confirmed $s state=$final_download_state" >> "$run_dir/actions.log"
          tap_one_native "$s" 766 1420 download-confirm
          download_confirmed=1
          break
        fi
        rm -f "$run_dir/download-current-$s-720.png"
        download_state="$final_download_state"
      fi
      sleep 2
    done
    if ((download_confirmed)); then
      :
    elif [[ "$download_state" == home-scene ]]; then
      echo "$(date -u +%FT%TZ) download-confirm-skipped $s latest=HomeScene" >> "$run_dir/actions.log"
    else
      live_check
      latest_scene_line="$(grep -E 'DownloadScene|HomeScene' "$run_dir/logcat-$s.txt" | tail -n1 || true)"
      if [[ "$latest_scene_line" == *HomeScene* ]]; then
        echo "$(date -u +%FT%TZ) download-confirm-skipped $s latest=HomeScene" >> "$run_dir/actions.log"
      else
        capture_one "$s" download-current
        final_download_state="$(python3 "$run_dir/screen-probe.py" download "$run_dir/download-current-$s-720.png")" || fail "cannot inspect final DownloadScene checkpoint on $s"
        if [[ "$latest_scene_line" == *DownloadScene* && "$final_download_state" == download-ready ]]; then
          echo "$(date -u +%FT%TZ) download-screen-confirmed $s state=$final_download_state after-bounded-poll" >> "$run_dir/actions.log"
          tap_one_native "$s" 766 1420 download-confirm
          download_confirmed=1
        else
          fail "DownloadScene remained current but its screen did not match the confirmation checkpoint after 60 seconds on $s; inspect $run_dir/download-current-$s-720.png (last visual state: $final_download_state)"
        fi
      fi
    fi
  elif [[ "$latest_scene_line" == *HomeScene* ]]; then
    echo "$(date -u +%FT%TZ) download-confirm-skipped $s latest=HomeScene" >> "$run_dir/actions.log"
  else
    fail "no current HomeScene or DownloadScene marker on $s after the scene wait"
  fi
done
wait_scene HomeScene 480
phase=verify-player-profile
for i in "${!serials[@]}"; do verify_player_profile "${serials[$i]}" "${players[$i]}"; done
profile_identity=verified-per-serial
phase=home-visual-gates
for s in "${serials[@]}"; do
  wait_home_combat "$s"
done
tap_native_all 820 1515 combat
phase=wait-correlated-matchmaking
wait_api_gate stage3 "$timeout"
identity_check=api-verified-same-battle-id-human-membership-and-stage3-visual-review-required
capture_all matching-roster-after-stage3
if ((prime_control)); then
  echo "$(date -u +%FT%TZ) prime-control opt-in after single Stage 3 checkpoint" >> "$run_dir/actions.log"
  tap_native_all 540 350 prime-once
fi
wait_scene GameScene "$timeout"
phase=game-settle; end=$((SECONDS + settle))
while ((SECONDS < end)); do live_check; sleep 5; done
capture_all "gamescene-after-${settle}s"
phase=human-gameplay-probes
printf 'Review each before/after screenshot. Mark a player verified only if the avatar/radar reacts to movement, an attack effect/action is visible, and the same client remains in GameScene.\n' > "$run_dir/manual-gameplay-checklist.md"
for i in "${!serials[@]}"; do
  s="${serials[$i]}"; p="${players[$i]}"; name="${names[$i]}"
  capture_one "$s" gameplay-before
  # These gestures come from the existing gameplay probe. Their visual effect
  # is still a human criterion; sending input alone is never a pass.
  swipe_game_scaled "$s" 540 1200 540 900 600 movement
  sleep 2; live_check; capture_one "$s" gameplay-after-movement
  swipe_game_scaled "$s" 110 1740 110 1380 400 slot-1-attack
  sleep 2; live_check; capture_one "$s" gameplay-after-attack
printf -- '- [ ] %s / %s (%s): roster row is visibly present in the single post-Stage3 screenshot; avatar or radar changes after movement; attack response visible; client stays in GameScene. If that screenshot shows loading/GameScene instead of the roster, mark roster unverified. Evidence: matching-roster-after-stage3-%s-720.png, gameplay-before-%s-720.png, gameplay-after-movement-%s-720.png, gameplay-after-attack-%s-720.png\n' \
    "$s" "$p" "$name" "$s" "$s" "$s" "$s" >> "$run_dir/manual-gameplay-checklist.md"
done
result=passed
echo "Reached GameScene; human roster and gameplay still require evidence review. Evidence: $run_dir"
