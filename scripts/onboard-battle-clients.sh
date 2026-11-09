#!/usr/bin/env bash
set -Eeuo pipefail

# Complete first-run onboarding on already booted AVDs. This script never
# installs APKs, clears app data/cache, or wipes/restarts an emulator.
repo="$(cd "$(dirname "$0")/.." && pwd -P)"
serial_csv=""; names_csv=""; name_prefix="K$(date -u +%j%H%M)"
run_id="$(date -u +%Y%m%dT%H%M%SZ)"; port="${HTTP_PORT:-18080}"
adb="${ADB:-}"; timeout=240; manual_checkpoints=0
package=world.global.fenix
activity=world.global.fenix/com.google.firebase.MessagingUnityPlayerActivity
users_dir="$repo/src/KickFlight.BootstrapApi/data/users"
base="$repo/.local/battle-test"; phase=preflight; result=failed; failure=""
run_dir=""; lock=""; listener_start=""; boot_status=not-run
ocr_bin=""; swiftc_bin="$(command -v swiftc 2>/dev/null || true)"; ocr_mode=""
if [[ -n "$swiftc_bin" ]]; then
  ocr_mode="vision"
elif command -v tesseract >/dev/null 2>&1; then
  ocr_bin="$(command -v tesseract)"; ocr_mode="tesseract"
fi
serials=(); names=(); player_ids=(); widths=(); heights=(); monitors=(); monitored_serials=()

usage() {
  cat <<USAGE
Usage: $0 --serials S1[,S2,...] [options]

Options:
  --serials CSV          Required; 1-5 booted AVD serials.
  --names CSV            Explicit unique names, one per serial (letters/digits).
  --name-prefix TEXT     Prefix for generated names (default: UTC time based).
  --api-port PORT        Local API listener port (default: HTTP_PORT or 18080).
  --users-dir PATH       User JSON store (default: src/KickFlight.BootstrapApi/data/users).
  --adb PATH             ADB executable (or set ADB).
  --timeout SECONDS      Scene timeout (default: 240).
  --run-id NAME          Evidence folder suffix (default: UTC timestamp).
  --manual-checkpoints   Require an interactive review of each 720px checkpoint.
  --trust-coordinate-ui  Alias for --manual-checkpoints (legacy option name).
  -h, --help             Show this help.

Apple Vision is preferred when swiftc is available; Tesseract is the fallback.
If OCR cannot run, --manual-checkpoints requires an operator to review each
saved 720px checkpoint before continuing; the script never assumes a UI state.
Screenshots are saved at 720 px wide under .local/battle-test/onboard-<run-id>/.
On success, players.tsv and RUNNER_ARGS are ready for run-battle-entry-multi.sh.
The helper verifies each Load user profile(id=...), user JSON and _identities.json.
USAGE
}
fail() { failure="$*"; echo "ERROR [$phase] $failure" >&2; exit 1; }

while (($#)); do
  case "$1" in
    --serials) serial_csv="${2:?missing --serials value}"; shift 2 ;;
    --names) names_csv="${2:?missing --names value}"; shift 2 ;;
    --name-prefix) name_prefix="${2:?missing --name-prefix value}"; shift 2 ;;
    --api-port) port="${2:?missing --api-port value}"; shift 2 ;;
    --users-dir) users_dir="${2:?missing --users-dir value}"; shift 2 ;;
    --adb) adb="${2:?missing --adb value}"; shift 2 ;;
    --timeout) timeout="${2:?missing --timeout value}"; shift 2 ;;
    --run-id) run_id="${2:?missing --run-id value}"; shift 2 ;;
    --manual-checkpoints|--trust-coordinate-ui) manual_checkpoints=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; fail "unknown argument $1" ;;
  esac
done

[[ -n "$serial_csv" ]] || { usage >&2; fail "--serials is required"; }
[[ "$run_id" =~ ^[A-Za-z0-9._-]+$ ]] || fail "invalid run ID"
[[ "$timeout" =~ ^[0-9]+$ ]] && ((timeout >= 30 && timeout <= 1800)) || fail "timeout must be 30-1800 seconds"
[[ "$port" =~ ^[0-9]+$ ]] && ((port > 0 && port <= 65535)) || fail "invalid API port"
[[ "$name_prefix" =~ ^[A-Za-z0-9]{1,8}$ ]] || fail "name prefix must be 1-8 letters/digits"
IFS=',' read -r -a serials <<< "$serial_csv"
(( ${#serials[@]} >= 1 && ${#serials[@]} <= 5 )) || fail "requires 1-5 serials"
if [[ -n "$names_csv" ]]; then IFS=',' read -r -a names <<< "$names_csv"; fi
if [[ -n "$names_csv" ]] && (( ${#names[@]} != ${#serials[@]} )); then fail "name count must match serial count"; fi
for i in "${!serials[@]}"; do
  s="${serials[$i]}"
  [[ "$s" =~ ^[A-Za-z0-9._:-]+$ ]] || fail "invalid serial '$s'"
  for j in "${!serials[@]}"; do
    if ((j < i)); then [[ "${serials[$j]}" != "$s" ]] || fail "repeated serial $s"; fi
  done
  if [[ -z "$names_csv" ]]; then
    suffix="$(printf '%s' ABCDE | cut -c $((i + 1)))"
    names+=("${name_prefix}${suffix}")
  fi
  [[ "${names[$i]}" =~ ^[A-Za-z0-9]{1,10}$ ]] || fail "invalid name '${names[$i]}' (use 1-10 letters/digits)"
  for j in "${!names[@]}"; do
    if ((j < i)); then [[ "${names[$j]}" != "${names[$i]}" ]] || fail "repeated name ${names[$i]}"; fi
  done
done

if [[ -z "$adb" ]]; then
  [[ ! -x "$repo/.local/android-sdk/platform-tools/adb" ]] || adb="$repo/.local/android-sdk/platform-tools/adb"
fi
[[ -n "$adb" ]] || adb="$(command -v adb || true)"
[[ -x "$adb" ]] || fail "adb not found; set ADB or pass --adb"
for tool in curl lsof sips jq; do command -v "$tool" >/dev/null 2>&1 || fail "$tool is required"; done
if [[ "$users_dir" != /* ]]; then users_dir="$repo/$users_dir"; fi
if ((manual_checkpoints)); then ocr_mode="manual"; fi
if ((manual_checkpoints == 0)) && [[ -z "$ocr_bin" && -z "$swiftc_bin" ]]; then
  fail "Apple Vision (swiftc) or Tesseract is required for UI gates; use --manual-checkpoints for interactive review"
fi
[[ -d "$users_dir" ]] || fail "user JSON directory not found: $users_dir"
[[ -f "$users_dir/_identities.json" ]] || fail "identity index not found: $users_dir/_identities.json"

mkdir -p "$base"
run_dir="$base/onboard-$run_id"
[[ ! -e "$run_dir" ]] || fail "evidence directory exists: $run_dir"
mkdir "$run_dir"
mkdir "$base/.onboard-lock" 2>/dev/null || fail "another onboarding run is active"
lock="$base/.onboard-lock"; echo "$$" > "$lock/pid"

if [[ "$ocr_mode" == vision ]]; then
  cat > "$run_dir/vision-ocr.swift" <<'SWIFT'
import AppKit
import Foundation
import Vision

guard CommandLine.arguments.count == 2,
      let image = NSImage(contentsOfFile: CommandLine.arguments[1]) else {
    fputs("usage: vision-ocr IMAGE.png\n", stderr)
    exit(2)
}
var rect = CGRect(origin: .zero, size: image.size)
guard let cgImage = image.cgImage(forProposedRect: &rect, context: nil, hints: nil) else {
    fputs("cannot decode screenshot\n", stderr)
    exit(2)
}
let request = VNRecognizeTextRequest()
request.recognitionLevel = .accurate
request.usesLanguageCorrection = true
request.automaticallyDetectsLanguage = true
do {
    try VNImageRequestHandler(cgImage: cgImage).perform([request])
    for item in request.results ?? [] {
        if let text = item.topCandidates(1).first?.string { print(text) }
    }
} catch {
    fputs("Vision OCR failed: \(error)\n", stderr)
    exit(1)
}
SWIFT
  mkdir -p "$run_dir/swift-module-cache"
  if "$swiftc_bin" -j 1 -module-cache-path "$run_dir/swift-module-cache" -O \
      -framework AppKit -framework Vision "$run_dir/vision-ocr.swift" -o "$run_dir/vision-ocr"; then
    ocr_bin="$run_dir/vision-ocr"
  elif command -v tesseract >/dev/null 2>&1; then
    ocr_mode="tesseract"; ocr_bin="$(command -v tesseract)"
    echo "Apple Vision compile failed; falling back to Tesseract." >&2
  elif ((manual_checkpoints)); then
    ocr_mode="manual"; ocr_bin=""
    echo "Apple Vision compile failed; using interactive 720px checkpoints." >&2
  else
    fail "could not compile Apple Vision OCR helper; rerun with --manual-checkpoints for interactive UI review"
  fi
fi

listener_pids() { lsof -nP -t -iTCP:"$port" -sTCP:LISTEN 2>/dev/null | sort -u; }
check_listener() {
  now="$(listener_pids || true)"
  [[ -n "$now" ]] || fail "API listener missing on TCP $port"
  [[ "$now" == "$listener_start" ]] || fail "API listener PID changed during onboarding"
}
get_dimensions() {
  local serial="$1" line size
  line="$("$adb" -s "$serial" shell wm size 2>/dev/null | tail -n 1 | tr -d '\r')"
  size="${line##* }"
  [[ "$size" =~ ^([0-9]+)x([0-9]+)$ ]] || fail "cannot read wm size for $serial: $line"
  echo "${BASH_REMATCH[1]} ${BASH_REMATCH[2]}"
}
assert_healthy() {
  local serial="$1" log="$run_dir/logcat-$serial.txt"
  [[ -f "$log" ]] || return 0
  if grep -Eiq 'Scudo|SIGABRT|signal 6 \(SIGABRT\)|SIGSEGV|signal 11 \(SIGSEGV\)|Fatal signal|FATAL EXCEPTION' "$log"; then
    fail "$serial emitted Scudo/SIGABRT/SIGSEGV/fatal exception (see $log)"
  fi
  return 0
}
tap() {
  local serial="$1" x="$2" y="$3" label="$4" i w="" h="" sx sy
  for i in "${!serials[@]}"; do
    if [[ "${serials[$i]}" == "$serial" ]]; then w="${widths[$i]}"; h="${heights[$i]}"; break; fi
  done
  [[ -n "$w" && -n "$h" ]] || fail "screen dimensions missing for $serial"
  sx="$(((x * w + 540) / 1080))"; sy="$(((y * h + 1170) / 2340))"
  assert_healthy "$serial"
  "$adb" -s "$serial" shell input tap "$sx" "$sy" >/dev/null || fail "tap $label failed on $serial"
  printf '%s\t%s\t%s@%s,%s\n' "$(date -u +%FT%TZ)" "$serial" "$label" "$sx" "$sy" >> "$run_dir/actions.tsv"
}
capture() {
  local serial="$1" label="$2" raw="$run_dir/.$label-$serial-raw.png" out="$run_dir/$label-$serial-720.png"
  assert_healthy "$serial"
  "$adb" -s "$serial" exec-out screencap -p > "$raw" || fail "screencap failed on $serial at $label"
  sips --resampleWidth 720 "$raw" --out "$out" >/dev/null 2>&1 || fail "sips failed on $serial"
  rm -f "$raw"
  if [[ -n "$ocr_bin" ]]; then
    if [[ "$ocr_mode" == tesseract ]]; then
      "$ocr_bin" "$out" stdout 2>/dev/null > "$run_dir/$label-$serial-ocr.txt" || fail "OCR failed on $serial at $label"
    else
      "$ocr_bin" "$out" > "$run_dir/$label-$serial-ocr.txt" || fail "OCR failed on $serial at $label"
    fi
  fi
  echo "$(date -u +%FT%TZ) $serial $label $out" >> "$run_dir/checkpoints.log"
}
screen_has() {
  local serial="$1" label="$2" pattern="$3" ocr="$run_dir/$label-$serial-ocr.txt"
  if ((manual_checkpoints)); then
    [[ -t 0 ]] || fail "manual UI checkpoint requires an interactive terminal"
    echo "Review $run_dir/$label-$serial-720.png"
    while true; do
      printf 'Does this image show /%s/? [sí/no] ' "$pattern"
      read -r answer || fail "manual review ended before confirming $label for $serial"
      case "$answer" in
        y|Y|yes|YES|s|S|si|SI|sí|SÍ) return 0 ;;
        n|N|no|NO) return 1 ;;
        *) echo "Answer sí or no; a blank response does not approve the checkpoint." ;;
      esac
    done
  fi
  [[ -f "$ocr" ]] || fail "missing OCR output for $serial/$label"
  grep -Eiq "$pattern" "$ocr"
}
wait_scene() {
  local serial="$1" marker="$2" seconds="$3" deadline
  deadline=$((SECONDS + seconds))
  while ((SECONDS < deadline)); do
    assert_healthy "$serial"
    grep -Fq "$marker" "$run_dir/logcat-$serial.txt" && return 0
    sleep 1
  done
  capture "$serial" "timeout-${marker}"
  fail "$serial did not reach $marker within ${seconds}s"
}
wait_ime() {
  local serial="$1" want="$2" seconds="$3" deadline state
  deadline=$((SECONDS + seconds))
  while ((SECONDS < deadline)); do
    assert_healthy "$serial"
    state="$("$adb" -s "$serial" shell dumpsys input_method 2>/dev/null | grep -E 'mInputShown=' | tail -n 1 | tr -d '\r')"
    if [[ "$want" == shown && "$state" =~ mInputShown=true ]]; then return 0; fi
    if [[ "$want" == hidden && "$state" =~ mInputShown=false ]]; then return 0; fi
    sleep 1
  done
  echo "$serial IME did not become $want" >&2
  # A timeout is recoverable by the caller, which can inspect the saved UI and
  # retry focus. Fatal logcat signatures still exit through assert_healthy.
  return 1
}
unique_name_in_store() {
  local name="$1" file value
  for file in "$users_dir"/*.json; do
    [[ -f "$file" && "$file" != "$users_dir/_identities.json" ]] || continue
    value="$(jq -r '.UserName // empty' "$file" 2>/dev/null || true)"
    [[ "$value" != "$name" ]] || return 1
  done
  return 0
}
finish() {
  local code=$? serial raw scaled id name
  trap - EXIT
  for pid in "${monitors[@]}"; do kill "$pid" 2>/dev/null || true; done
  if [[ -n "$run_dir" ]]; then
    for serial in "${monitored_serials[@]}"; do
      "$adb" -s "$serial" logcat -d -v time > "$run_dir/logcat-$serial-final.txt" 2>/dev/null || true
      if ((code != 0)); then
        raw="$run_dir/.$serial-failure-raw.png"; scaled="$run_dir/failure-$serial-720.png"
        "$adb" -s "$serial" exec-out screencap -p > "$raw" 2>/dev/null || true
        if [[ -s "$raw" ]]; then sips --resampleWidth 720 "$raw" --out "$scaled" >/dev/null 2>&1 || true; fi
        rm -f "$raw"
      fi
    done
    {
      echo "result=$result"; echo "phase=$phase"; echo "failure=$failure"
      echo "api_listener_pids=$listener_start"; echo "boot_index_host_10.0.2.2=$boot_status"
      echo "evidence=$run_dir"; printf '\nserial\tplayer_id\tname\n'
      for i in "${!serials[@]}"; do
        id="${player_ids[$i]:-unverified}"; name="${names[$i]:-unknown}"
        printf '%s\t%s\t%s\n' "${serials[$i]}" "$id" "$name"
      done
    } > "$run_dir/summary.txt"
  fi
  if [[ -n "$lock" ]]; then rm -f "$lock/pid"; rmdir "$lock" 2>/dev/null || true; fi
  exit "$code"
}
trap finish EXIT

phase=api-listener-preflight
listener_start="$(listener_pids || true)"
[[ -n "$listener_start" ]] || fail "no API listener on TCP $port"
dd if=/dev/zero of="$run_dir/boot-probe.bin" bs=32 count=1 2>/dev/null
boot_status="$(curl -sS --max-time 8 --http1.1 -X POST -H 'Host: 10.0.2.2' \
  -H 'Content-Type: application/octet-stream' --data-binary "@$run_dir/boot-probe.bin" \
  -D "$run_dir/boot-headers.txt" -o "$run_dir/boot-response.bin" -w '%{http_code}' \
  "http://127.0.0.1:$port/boot/index")" || fail "POST /boot/index failed"
[[ "$boot_status" == 200 ]] || fail "POST /boot/index Host 10.0.2.2 returned $boot_status"
grep -Eiq '^x-app-status-code:[[:space:]]*0' "$run_dir/boot-headers.txt" || fail "boot status header missing"
check_listener

phase=device-preflight
for i in "${!serials[@]}"; do
  s="${serials[$i]}"
  [[ "$("$adb" -s "$s" get-state 2>/dev/null || true)" == device ]] || fail "$s is not ADB-ready"
  [[ "$("$adb" -s "$s" shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" == 1 ]] || fail "$s is not booted"
  [[ "$("$adb" -s "$s" shell pm path "$package" 2>/dev/null | head -n 1)" == package:* ]] || fail "Kick Flight is not installed on $s"
  dims="$(get_dimensions "$s")"; read -r w h <<< "$dims"; widths+=("$w"); heights+=("$h")
done
for name in "${names[@]}"; do unique_name_in_store "$name" || fail "name '$name' already exists in user JSON"; done
printf '%s\tpreflight-ok\t%s\n' "$(date -u +%FT%TZ)" "$serial_csv" > "$run_dir/actions.tsv"

phase=onboarding
for i in "${!serials[@]}"; do
  s="${serials[$i]}"; name="${names[$i]}"
  phase="launch-$s"
  "$adb" -s "$s" logcat -c >/dev/null 2>&1 || fail "cannot clear diagnostic log buffer on $s"
  monitored_serials+=("$s")
  : > "$run_dir/logcat-$s.txt"
  "$adb" -s "$s" logcat -v time > "$run_dir/logcat-$s.txt" 2>&1 & monitors+=("$!")
  "$adb" -s "$s" shell am start -n "$activity" > "$run_dir/launch-$s.txt" 2>&1 || fail "could not open app on $s"
  wait_scene "$s" TitleScene "$timeout"
  phase="title-$s"; sleep 8; assert_healthy "$s"; capture "$s" title
  if screen_has "$s" title 'Got[[:space:]]+it'; then
    tap "$s" 915 681 got-it
    sleep 1; capture "$s" title-after-got-it
    screen_has "$s" title-after-got-it 'TAP[[:space:]]+START|Tap[[:space:]]+Start' || fail "$s did not show TAP START after Got it"
  elif screen_has "$s" title 'TAP[[:space:]]+START|Tap[[:space:]]+Start'; then
    :
  else
    fail "$s title screen shows neither Got it nor TAP START"
  fi
  phase="tap-start-$s"; tap "$s" 540 1200 tap-start
  wait_scene "$s" DownloadScene "$timeout"
  sleep 2; capture "$s" download
  screen_has "$s" download 'Download|Descarg(a|ar)' || fail "$s DownloadScene lacks its Download control"
  phase="tap-download-$s"; tap "$s" 766 1420 download
  wait_scene "$s" HomeScene "$timeout"
  phase="home-stabilize-$s"; sleep 8; assert_healthy "$s"; capture "$s" home-tooltip
  screen_has "$s" home-tooltip 'Enter your name|change your name later' || fail "$s Home name tooltip was not visible"
  if screen_has "$s" home-tooltip 'Enter your name'; then
    phase="advance-home-tooltip-$s"; tap "$s" 972 247 tooltip-next
    sleep 1; capture "$s" home-tooltip-next
    screen_has "$s" home-tooltip-next 'change your name later|settings' || fail "$s did not reach the second Home tooltip"
  else
    screen_has "$s" home-tooltip 'change your name later|settings' || fail "$s Home name tooltip state was ambiguous"
  fi
  phase="open-name-dialog-$s"; tap "$s" 540 1260 name-selector
  sleep 2; capture "$s" name-dialog
  screen_has "$s" name-dialog 'Enter your name|Placeholder' || fail "$s name dialog did not open"
  if wait_ime "$s" shown 8; then
    :
  else
    phase="retry-focus-name-$s"; capture "$s" name-ime-retry
    screen_has "$s" name-ime-retry 'Enter your name|Placeholder' || fail "$s name field was not visible for IME retry"
    tap "$s" 540 1230 name-field-retry
    wait_ime "$s" shown 8 || fail "$s keyboard did not appear after retrying name-field focus"
  fi
  capture "$s" name-ime
  "$adb" -s "$s" shell input text "$name" >/dev/null || fail "could not type name on $s"
  sleep 1; capture "$s" name-entered
  screen_has "$s" name-entered "$name" || fail "$s screenshot does not show $name"
  phase="ime-ok-$s"; tap "$s" 996 2175 ime-ok
  wait_ime "$s" hidden 12 || fail "$s keyboard did not close after IME OK"
  sleep 1; capture "$s" name-ready
  screen_has "$s" name-ready "$name" || fail "$s name dialog lost $name"
  screen_has "$s" name-ready 'Accept' || fail "$s name dialog does not show Accept"
  phase="accept-name-$s"; tap "$s" 540 1415 accept-name
  sleep 2; assert_healthy "$s"; check_listener

  phase="verify-identity-$s"; deadline=$((SECONDS + 45)); player_id=""
  while ((SECONDS < deadline)); do
    assert_healthy "$s"
    player_id="$(sed -nE 's/.*Load user profile\(id=([0-9]+)\).*/\1/p' "$run_dir/logcat-$s.txt" | tail -n 1)"
    if [[ -n "$player_id" && -f "$users_dir/$player_id.json" ]] &&
      jq -e --arg name "$name" --arg id "$player_id" '(.UserName==$name) and (.HasName==true) and ((.PlayerId|tostring)==$id)' "$users_dir/$player_id.json" >/dev/null 2>&1 &&
      jq -e --arg id "$player_id" '[to_entries[] | select((.value|tostring)==$id)] | length==1' "$users_dir/_identities.json" >/dev/null 2>&1; then
      break
    fi
    sleep 1
  done
  [[ -n "$player_id" && -f "$users_dir/$player_id.json" ]] || fail "$s lacks Load user profile(id=...) or user JSON"
  jq -e --arg name "$name" --arg id "$player_id" '(.UserName==$name) and (.HasName==true) and ((.PlayerId|tostring)==$id)' "$users_dir/$player_id.json" >/dev/null || fail "$s JSON does not match $player_id/$name"
  jq -e --arg id "$player_id" '[to_entries[] | select((.value|tostring)==$id)] | length==1' "$users_dir/_identities.json" >/dev/null || fail "$s player ID $player_id is absent or duplicated in _identities.json"
  for previous in "${player_ids[@]}"; do [[ "$previous" != "$player_id" ]] || fail "duplicate player ID $player_id"; done
  player_ids+=("$player_id")
  printf '%s\t%s\t%s\n' "$s" "$player_id" "$name" >> "$run_dir/players.tsv"
  echo "$(date -u +%FT%TZ) verified $s $player_id $name" >> "$run_dir/actions.tsv"
done

phase=complete; result=verified
serials_out="$(IFS=,; echo "${serials[*]}")"
players_out="$(IFS=,; echo "${player_ids[*]}")"
names_out="$(IFS=,; echo "${names[*]}")"
{
  echo "SERIALS=$serials_out"
  echo "PLAYER_IDS=$players_out"
  echo "NAMES=$names_out"
  echo "RUNNER_ARGS=--serials $serials_out --player-ids $players_out"
} | tee "$run_dir/runner-args.txt"
echo "Verified mapping: $run_dir/players.tsv"
