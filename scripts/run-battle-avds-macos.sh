#!/usr/bin/env bash
set -Eeuo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
home="$repo/.local/avd"; group= profiles= port_csv= ids= names= api_log= api_port=18080 apk=
gpu=host cores=2 boot_timeout=300 run_id="$(date -u +%Y%m%dT%H%M%SZ)"
onboard=0 manual=0 start_api=0 start_photon=0 prepare=0 dry=0 stagger=0
adb="${ADB:-}" emulator="${EMULATOR:-}" docker="${DOCKER:-}" api_pid= phase=args failure= run_dir=
avds=() ports=() serials=() started=() avd_pids=() device_ids=() logs=()
usage(){ cat <<'HELP'
Usage: scripts/run-battle-avds-macos.sh --group 3|4|5 [--player-ids CSV | --onboard --names CSV] [options]
Profiles: 3=kf_match3_20260924_a-c; 4=kf_match4_20260924_a,c,d,e; 5=kf_match5_20260924_a-e.
Ports default 5554,5556,5558,5560,5562.
Options: --profiles CSV --ports CSV --onboard --names CSV --manual-checkpoints
 --apk FILE (install with -r and verify SHA-256) --api-log FILE --start-api-if-needed --start-photon-if-needed --api-port PORT
 --gpu MODE --cores N --boot-timeout SEC --run-id NAME --launch-stagger SEC
 --prepare-only (1-5 AVDs, no battle) --dry-run --validate-only -h
No wipe, snapshot load/save, pm clear or emulator shutdown. AVDs/API are child processes.
HELP
}
die(){ failure="$*"; echo "ERROR [$phase] $failure" >&2; exit 1; }
readcsv(){ local v="$1" dest="$2"; IFS=, read -r -a "$dest" <<< "$v"; }
while (($#)); do case "$1" in
 --group) group="$2"; shift 2;; --profiles) profiles="$2"; shift 2;; --ports) port_csv="$2"; shift 2;;
 --player-ids) ids="$2"; shift 2;; --names) names="$2"; shift 2;; --onboard) onboard=1; shift;;
 --manual-checkpoints) manual=1; shift;; --api-log) api_log="$2"; shift 2;;
 --start-api-if-needed) start_api=1; shift;; --start-photon-if-needed) start_photon=1; shift;;
 --api-port) api_port="$2"; shift 2;; --apk) apk="$2"; shift 2;; --gpu) gpu="$2"; shift 2;; --cores) cores="$2"; shift 2;;
 --boot-timeout) boot_timeout="$2"; shift 2;; --run-id) run_id="$2"; shift 2;; --launch-stagger) stagger="$2"; shift 2;;
 --prepare-only) prepare=1; shift;; --dry-run|--validate-only) dry=1; shift;;
 -h|--help) usage; exit 0;; *) die "argumento desconocido $1";; esac; done
[[ "$group" =~ ^[1-5]$ ]] || die "--group debe ser 1-5"
if ((prepare)); then [[ -z "$ids" && onboard == 0 ]] || die "prepare-only no usa IDs/onboarding"
else [[ "$group" =~ ^[345]$ ]] || die "battle requiere 3-5"
 if ((onboard)); then
  [[ -z "$ids" ]] || die "usa --onboard --names o --player-ids, no ambos";
 else
  readcsv "$ids" players
  [[ -n "$ids" && ${#players[@]} -eq group ]] || die "--player-ids obligatorio y en orden de serial (o usa --onboard --names)"
 fi
fi
if ((onboard)); then [[ -n "$names" ]] || die "--onboard requiere --names"; readcsv "$names" name_list; [[ ${#name_list[@]} -eq group ]] || die "nombres no coinciden"; fi
if [[ -n "$profiles" ]]; then readcsv "$profiles" avds; else case "$group" in
 3) avds=(kf_match3_20260924_a kf_match3_20260924_b kf_match3_20260924_c);;
 4) avds=(kf_match4_20260924_a kf_match4_20260924_c kf_match4_20260924_d kf_match4_20260924_e);;
 5) avds=(kf_match5_20260924_a kf_match5_20260924_b kf_match5_20260924_c kf_match5_20260924_d kf_match5_20260924_e);;
 *) die "group 1/2 prepare-only requires --profiles";; esac; fi
[[ ${#avds[@]} -eq group ]] || die "perfil count no coincide"
if [[ -n "$apk" ]]; then
 [[ -f "$apk" ]] || die "APK no existe: $apk"
 apk="$(cd "$(dirname "$apk")" && pwd -P)/$(basename "$apk")"
 apk_sha="$(shasum -a 256 "$apk" | awk '{print toupper($1)}')"
fi
if [[ -n "$port_csv" ]]; then readcsv "$port_csv" ports
elif [[ "$group" == 4 ]]; then ports=(5560 5564 5566 5570)
else for ((i=0;i<group;i++)); do ports+=( "$((5554+i*2))" ); done; fi
[[ ${#ports[@]} -eq group ]] || die "port count no coincide"
for i in "${!avds[@]}"; do
 [[ -f "$home/${avds[$i]}.ini" && -f "$home/${avds[$i]}.avd/config.ini" ]] || die "AVD no existe: ${avds[$i]}"
 [[ "${ports[$i]}" =~ ^[0-9]+$ ]] && ((ports[i]>=5554 && ports[i]<=5682 && ports[i]%2==0)) || die "puerto inválido"
 serials+=( "emulator-${ports[$i]}" )
 for j in "${!avds[@]}"; do ((j>=i)) || [[ "${avds[$i]}" != "${avds[$j]}" ]] || die "perfil repetido"; done
 for j in "${!ports[@]}"; do ((j>=i)) || [[ "${ports[$i]}" != "${ports[$j]}" ]] || die "puerto repetido"; done
done
if ((dry)); then echo "Plan group=$group prepare=$prepare GPU=$gpu AVD_HOME=$home${apk:+ APK=$apk SHA256=$apk_sha}"; for i in "${!avds[@]}"; do echo " ${avds[$i]} -> ${serials[$i]}"; done; exit 0; fi
[[ "$(uname -s)" == Darwin ]] || die "requiere macOS"
[[ -n "$adb" ]] || { [[ -x "$repo/.local/android-sdk/platform-tools/adb" ]] && adb="$repo/.local/android-sdk/platform-tools/adb" || adb="$(command -v adb || true)"; }
[[ -n "$emulator" ]] || { [[ -x "$repo/.local/android-sdk/emulator/emulator" ]] && emulator="$repo/.local/android-sdk/emulator/emulator" || emulator="$(command -v emulator || true)"; }
[[ -x "$adb" && -x "$emulator" ]] || die "adb/emulator no encontrados"
command -v curl >/dev/null && command -v lsof >/dev/null || die "curl y lsof requeridos"
if ((start_photon)); then docker="${docker:-$(command -v docker || true)}"; [[ -x "$docker" ]] || die "docker no encontrado"; fi
mkdir -p "$repo/.local/battle-test"; run_dir="$repo/.local/battle-test/avd-$run_id"; [[ ! -e "$run_dir" ]] || die "evidencia ya existe"; mkdir -p "$run_dir/emulators"
[[ -z "$api_log" || "$api_log" == /* ]] || api_log="$repo/$api_log"
for avd in "${avds[@]}"; do logs+=( "$run_dir/emulators/$avd.log" ); done
report(){ echo "Fallo. Estado por serial, evidencia=$run_dir" >&2; for i in "${!serials[@]}"; do
 s="$("$adb" devices 2>/dev/null|awk -v x="${serials[$i]}" '$1==x{print $2;exit}'||true)"; b=-; a=-
 if [[ "$s" == device ]]; then b="$("$adb" -s "${serials[$i]}" shell getprop sys.boot_completed 2>/dev/null|tr -d '\r'||true)"; a="$("$adb" -s "${serials[$i]}" shell settings get secure android_id 2>/dev/null|tr -d '\r'||true)"; fi
 echo " ${serials[$i]} AVD=${avds[$i]} state=${s:-absent} boot=$b id=$a started=${started[$i]:-no} log=${logs[$i]}" >&2; done
 [[ -z "$api_pid" ]] || echo "API pid=$api_pid log=$api_log" >&2; }
trap 'r=$?; if ((r)); then echo "FAILED [$phase]: ${failure:-exit $r}" >&2; report; fi' EXIT
listeners(){ { lsof -nP -iTCP:"$api_port" -sTCP:LISTEN -t 2>/dev/null || true; }|sort -u|tr '\n' ' '; }
container_running(){ "$docker" inspect -f '{{.State.Running}}' luxon-server 2>/dev/null | grep -qx true; }
ensure_photon_container(){
 phase=photon; docker="${docker:-$(command -v docker || true)}"; [[ -x "$docker" ]] || die "docker no encontrado para validar luxon-server"
 if ! container_running; then
  if ((start_photon)); then
   if "$docker" inspect luxon-server >/dev/null 2>&1; then "$docker" start luxon-server >"$run_dir/docker-start.log" 2>&1 || die "no se pudo iniciar luxon-server"
   else (cd "$repo" && "$docker" compose up -d luxon-server) >"$run_dir/compose.log" 2>&1 || die "Compose luxon-server falló"; fi
   for _ in {1..30}; do container_running && return 0; sleep 2; done
   die "luxon-server no quedó en estado running"
  else die "luxon-server detenido o ausente; añade --start-photon-if-needed"; fi
 fi
}
phase=api; listener="$(listeners)"
if [[ -z "$listener" ]] && ((start_api)); then
 ensure_photon_container
 [[ -n "$api_log" ]] || api_log="$run_dir/api-stdout.jsonl"
 mkdir -p "$(dirname "$api_log")"
 (cd "$repo" && exec env ENABLE_CAPTURE=true DIRECT_CLIENT_HOST=10.0.2.2 HTTP_PORT="$api_port" ./scripts/run-local.sh) >>"$api_log" 2>&1 & api_pid=$!
 for _ in {1..60}; do listener="$(listeners)"; [[ -n "$listener" ]] && break; kill -0 "$api_pid" 2>/dev/null || die "API child terminó: $api_log"; sleep 1; done
fi
[[ -n "$listener" ]] || die "sin listener API; usa --start-api-if-needed"
[[ -n "$api_log" && -f "$api_log" ]] || die "falta --api-log existente o usa --start-api-if-needed"
if ((start_photon)); then ensure_photon_container; fi
photon="$(curl -sS -m 5 -o "$run_dir/photon.json" -w '%{http_code}' "http://127.0.0.1:$api_port/health/photon" 2>/dev/null||true)"
[[ "$photon" == 200 ]] || die "Photon health=$photon"
boot_gate(){
 local suffix="$1" code
 dd if=/dev/zero of="$run_dir/$suffix-request.bin" bs=32 count=1 2>/dev/null
 code="$(curl -sS --max-time 10 --http1.1 -X POST -H 'Host: 10.0.2.2' -H 'Content-Type: application/octet-stream' \
  --data-binary "@$run_dir/$suffix-request.bin" -D "$run_dir/$suffix-headers.txt" -o "$run_dir/$suffix-response.bin" \
  -w '%{http_code}' "http://127.0.0.1:$api_port/boot/index" 2>/dev/null||true)"
 [[ "$code" == 200 ]] || die "POST boot/index Host 10.0.2.2=$code"
 grep -Eiq '^x-app-status-code:[[:space:]]*0' "$run_dir/$suffix-headers.txt" || die "boot/index sin x-app-status-code: 0"
 [[ -s "$run_dir/$suffix-response.bin" ]] || die "boot/index devolvió el cuerpo vacío"
}
boot_gate boot
export ANDROID_AVD_HOME="$home"; phase=adb; "$adb" start-server >"$run_dir/adb.log" 2>&1 || die "adb server falló"
state(){ "$adb" devices|awk -v x="$1" '$1==x{print $2;exit}'; }
for i in "${!avds[@]}"; do s="$(state "${serials[$i]}")"; if [[ "$s" == device ]]; then started+=(no); avd_pids+=(""); elif [[ -n "$s" ]]; then die "${serials[$i]} ADB state=$s"; else
 pgrep -f "emulator.*-avd[ =]${avds[$i]}([[:space:]]|$)" >/dev/null && die "AVD already running: ${avds[$i]}"
 pgrep -f "emulator.*-port[ =]${ports[$i]}([[:space:]]|$)" >/dev/null && die "port occupied ${ports[$i]}"
 "$emulator" -avd "${avds[$i]}" -port "${ports[$i]}" -gpu "$gpu" -cores "$cores" -no-snapshot-load -no-snapshot-save >"${logs[$i]}" 2>&1 & avd_pids+=("$!"); started+=(yes); fi; done
alive(){ for i in "${!avds[@]}"; do [[ "${started[$i]}" != yes ]] || kill -0 "${avd_pids[$i]}" 2>/dev/null || die "AVD child died ${avds[$i]} log=${logs[$i]}"; done
 [[ -z "$api_pid" ]] || kill -0 "$api_pid" 2>/dev/null || die "API child died $api_log"; }
phase=boot
for srl in "${serials[@]}"; do t=0; while ((t<boot_timeout)); do alive; s="$(state "$srl")"
 if [[ "$s" == device ]] && [[ "$("$adb" -s "$srl" shell getprop sys.boot_completed 2>/dev/null|tr -d '\r'||true)" == 1 ]]; then break; fi
 [[ -z "$s" || "$s" == offline || "$s" == device ]] || die "$srl state=$s"; sleep 2; t=$((t+2)); done; ((t<boot_timeout)) || die "boot timeout $srl"; done
phase=identity
for srl in "${serials[@]}"; do a="$("$adb" -s "$srl" shell settings get secure android_id 2>/dev/null|tr -d '\r'||true)"; [[ "$a" =~ ^[[:xdigit:]]+$ ]] || die "Android ID inválido $srl"; device_ids+=( "$a" ); done
for i in "${!device_ids[@]}"; do for j in "${!device_ids[@]}"; do ((j>=i)) || [[ "${device_ids[$i]}" != "${device_ids[$j]}" ]] || die "Android ID duplicado"; done; done
phase=gates; alive; [[ "$(listeners)" == "$listener" ]] || die "listener cambió"
photon="$(curl -sS -m 5 -o "$run_dir/photon-after.json" -w '%{http_code}' "http://127.0.0.1:$api_port/health/photon" 2>/dev/null||true)"; [[ "$photon" == 200 ]] || die "Photon post-boot=$photon"
boot_gate boot-after
if [[ -n "$apk" ]]; then
 phase=install
 for srl in "${serials[@]}"; do
  "$adb" -s "$srl" shell am force-stop world.global.fenix >/dev/null || die "no se pudo detener app en $srl"
  "$adb" -s "$srl" install -r "$apk" >"$run_dir/install-$srl.log" 2>&1 || die "install -r falló en $srl; ver $run_dir/install-$srl.log"
  installed_path="$("$adb" -s "$srl" shell pm path world.global.fenix | tr -d '\r' | sed -n '1s/^package://p')"
  [[ -n "$installed_path" ]] || die "pm path no devolvió APK instalada en $srl"
  device_sha="$("$adb" -s "$srl" shell sha256sum "$installed_path" | awk 'NR==1{print toupper($1)}' | tr -d '\r')"
  [[ "$device_sha" == "$apk_sha" ]] || die "SHA-256 APK instalada no coincide en $srl (local=$apk_sha device=$device_sha)"
 done
fi
if ((prepare)); then echo "Gates OK; al salir los procesos hijos pueden terminar. Use sesión persistente si deben vivir."; exit 0; fi
serial_csv="$(IFS=,; echo "${serials[*]}")"; phase=onboard
if ((onboard)); then args=(--serials "$serial_csv" --names "$names" --api-port "$api_port" --run-id "$run_id"); ((manual)) && args+=(--manual-checkpoints)
 "$repo/scripts/onboard-battle-clients.sh" "${args[@]}" || die "onboarding failed"
 battle_ids="$(awk -F '\t' 'NR>1 && $2 ~ /^[0-9]+$/ {printf "%s%s",sep,$2;sep=","}' "$repo/.local/battle-test/onboard-$run_id/players.tsv")"; [[ -n "$battle_ids" ]] || die "players.tsv no tiene IDs numéricos"
else battle_ids="$ids"; fi
phase=battle; alive
"$repo/scripts/run-battle-entry-multi.sh" --serials "$serial_csv" --player-ids "$battle_ids" --api-log "$api_log" --run-id "$run_id" --http-port "$api_port" --launch-stagger "$stagger" & runner_pid=$!
while kill -0 "$runner_pid" 2>/dev/null; do
 alive; [[ "$(listeners)" == "$listener" ]] || { kill -TERM "$runner_pid" 2>/dev/null || true; wait "$runner_pid" 2>/dev/null || true; die "listener API cambió durante la batalla"; }
 photon="$(curl -sS -m 5 -o "$run_dir/photon-watch.json" -w '%{http_code}' "http://127.0.0.1:$api_port/health/photon" 2>/dev/null||true)"
 [[ "$photon" == 200 ]] || { kill -TERM "$runner_pid" 2>/dev/null || true; wait "$runner_pid" 2>/dev/null || true; die "Photon health durante batalla=$photon"; }
 sleep 5
done
wait "$runner_pid" || die "battle runner failed"
summary="$repo/.local/battle-test/multi-$run_id/summary.txt"
[[ -f "$summary" ]] || die "battle runner did not write summary: $summary"
grep -qx 'result=passed' "$summary" || die "battle runner summary is not result=passed: $summary"
alive; echo "Battle completo; .local/battle-test/multi-$run_id"
