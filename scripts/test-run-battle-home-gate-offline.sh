#!/usr/bin/env bash
set -Eeuo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd -P)"
runner="$repo/scripts/run-battle-entry-multi.sh"
tmp="$(mktemp -d "${TMPDIR:-/tmp}/kf-home-gate.XXXXXX")"
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/bin" "$tmp/run"

# Load the production Home gate and its scope-sensitive helpers without running
# the runner's preflight, API calls, or ADB setup.
awk '
  /^fail\(\) \{/ || /^check_listener\(\) \{/ || /^get_size\(\) \{/ ||
  /^capture_one\(\) \{/ || /^visual_state\(\) \{/ || /^tap_one_native\(\) \{/ ||
  /^live_check\(\) \{/ || /^wait_home_combat\(\) \{/ { copy=1; depth=0 }
  copy { print; opens=gsub(/\{/, "{"); closes=gsub(/\}/, "}"); depth+=opens-closes; if (depth==0) copy=0 }
' "$runner" > "$tmp/functions.sh"

cat > "$tmp/bin/adb" <<'SH'
#!/usr/bin/env bash
serial="$2"
shift 2
if [[ "$1 $2" == "shell pidof" ]]; then exit 0; fi
if [[ "$1 $2" == "shell wm" ]]; then echo 'Physical size: 1080x2340'; exit 0; fi
if [[ "$1 $2 $3" == "exec-out screencap -p" ]]; then printf '%s' "$serial"; exit 0; fi
if [[ "$1 $2 $3" == "shell input tap" ]]; then printf '%s\t%s\n' "$serial" "$5" >> "$TAPS"; exit 0; fi
exit 0
SH
chmod +x "$tmp/bin/adb"
cat > "$tmp/bin/python3" <<'SH'
#!/usr/bin/env bash
if [[ "$1" == "$RUN_DIR/screen-probe.py" ]]; then
  serial="$(cat "$3")"
  count_file="$RUN_DIR/probe-count-$serial"
  count=0; [[ ! -f "$count_file" ]] || count="$(cat "$count_file")"
  count=$((count + 1)); echo "$count" > "$count_file"
  if ((count == 1)); then echo notices; else echo combat; fi
  exit 0
fi
exec /usr/bin/python3 "$@"
SH
chmod +x "$tmp/bin/python3"
cat > "$tmp/bin/sips" <<'SH'
#!/usr/bin/env bash
while (($#)); do if [[ "$1" == --out ]]; then : > "$2"; break; fi; shift; done
SH
chmod +x "$tmp/bin/sips"

export PATH="$tmp/bin:$PATH" RUN_DIR="$tmp/run" TAPS="$tmp/taps"
run_dir="$RUN_DIR"; adb="$tmp/bin/adb"; package=world.global.fenix
serials=(5554 5556 5558); listener_start=offline; phase=home-visual-gates
source "$tmp/functions.sh"
listener_pids() { echo offline; }
for s in "${serials[@]}"; do
  : > "$run_dir/logcat-$s.txt"
done
for s in "${serials[@]}"; do
  wait_home_combat "$s"
done

for serial in "${serials[@]}"; do
  grep -Fq "$serial" "$TAPS" || { echo "Home modal was not tapped for $serial" >&2; exit 1; }
  grep -Fq "visual-ready $serial home combat" "$run_dir/actions.log" || { echo "Home combat gate missing for $serial" >&2; exit 1; }
  [[ -f "$run_dir/home-combat-ready-$serial-720.png" ]] || { echo "Home gate screenshot missing for $serial" >&2; exit 1; }
done
[[ "$(wc -l < "$TAPS" | tr -d ' ')" == 3 ]] || { echo 'expected exactly one modal tap per device' >&2; exit 1; }
echo 'PASS: each of three serials passed its Home visual gate and received one UI-confirmed modal tap'
