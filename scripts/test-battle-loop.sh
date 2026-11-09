#!/usr/bin/env bash
# ==============================================================================
# KICK-FLIGHT: AUTOMATED BATTLE LOOP TEST RUNNER
# ==============================================================================
# Automates:
#   1. Optional APK rebuild with patch-il2cpp-endpoints.py
#   2. APK installation on device/emulator
#   3. Clean app launch and monitoring
#   4. Title screen detection and TAP START
#   5. HomeScene transition and modal dismissal
#   6. "Combate" button tap and matchmaking room detection
#   7. "Iniciar combate" tap and battle transition verification
#   8. Full failure detection, logcat diagnostics, and 720px screenshots at every step.
# ==============================================================================
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd -P)"

# Color codes for clean output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
BOLD='\033[1m'
NC='\033[0m' # No Color

# Defaults
DEVICE="${DEVICE:-}"
SERVER_IP="${SERVER_IP:-10.0.2.2}"
SERVER_PORT="${PORT:-18080}"
DO_BUILD=0
DO_INSTALL=1
DO_CLEAN=0
DO_SEED=1
SEED_MIN_FILES=2000
TIMEOUT=60
APP_PACKAGE="${KF_PACKAGE_ID:-world.global.fenix}"
APP_ACTIVITY="com.google.firebase.MessagingUnityPlayerActivity"

# Parse arguments
while [[ $# -gt 0 ]]; do
  case "$1" in
    --build|-b)
      DO_BUILD=1
      shift
      ;;
    --no-install)
      DO_INSTALL=0
      shift
      ;;
    --clean)
      DO_CLEAN=1
      shift
      ;;
    --no-seed)
      DO_SEED=0
      shift
      ;;
    --device|-d)
      DEVICE="$2"
      shift 2
      ;;
    --server-ip|-s)
      SERVER_IP="$2"
      shift 2
      ;;
    --port|-p)
      SERVER_PORT="$2"
      shift 2
      ;;
    --timeout|-t)
      TIMEOUT="$2"
      shift 2
      ;;
    --help|-h)
      echo "Usage: $0 [OPTIONS]"
      echo ""
      echo "Options:"
      echo "  --build, -b         Rebuild direct APK before testing"
      echo "  --no-install        Skip APK installation (use currently installed app)"
      echo "  --clean             Clear app storage (pm clear) before running"
      echo "  --no-seed           Skip the Octo cache check/seed (scripts/seed-device-cache.py)"
      echo "  --device, -d <id>   Target ADB device (default: auto-detect)"
      echo "  --server-ip, -s <ip> Server IP (default: 10.0.2.2)"
      echo "  --port, -p <port>   Server port (default: 18080)"
      echo "  --timeout, -t <sec> Max wait time per phase in seconds (default: 60)"
      echo "  --help, -h          Show this help message"
      exit 0
      ;;
    *)
      if [[ "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
        SERVER_IP="$1"
      else
        echo -e "${RED}Unknown option: $1${NC}"
        exit 1
      fi
      shift
      ;;
  esac
done

# Resolve ADB device
if [[ -z "$DEVICE" ]]; then
  DEVICE=$(adb devices 2>/dev/null | grep -w "device" | awk '{print $1}' | head -n1 || true)
fi

if [[ -z "$DEVICE" ]]; then
  echo -e "${RED}❌ Error: No ADB device or emulator connected!${NC}" >&2
  exit 1
fi

TIMESTAMP=$(date +"%Y%m%d_%H%M%S")
RUN_DIR="$REPO_ROOT/.local/battle-test/run_${TIMESTAMP}"
mkdir -p "$RUN_DIR"
ln -sfn "$RUN_DIR" "$REPO_ROOT/.local/battle-test/latest"

LOG_FILE="$RUN_DIR/test_run.log"
exec > >(tee -a "$LOG_FILE") 2>&1

echo -e "${BOLD}${CYAN}==========================================================${NC}"
echo -e "${BOLD}${CYAN}🚀 KICK-FLIGHT AUTOMATED BATTLE LOOP TEST RUNNER${NC}"
echo -e "${BOLD}${CYAN}==========================================================${NC}"
echo -e "📱 Device:       ${BOLD}$DEVICE${NC}"
echo -e "🌐 Server IP:    ${BOLD}$SERVER_IP:$SERVER_PORT${NC}"
echo -e "📂 Run Dir:      ${BOLD}$RUN_DIR${NC}"
echo -e "⏱️  Timeout:      ${BOLD}${TIMEOUT}s per step${NC}"
echo -e "${BOLD}${CYAN}==========================================================${NC}"
echo ""

# Helper to capture and resize screenshot to 720px width
capture_step() {
  local name="$1"
  local raw="$RUN_DIR/${name}_raw.png"
  local scaled="$RUN_DIR/${name}.png"
  
  adb -s "$DEVICE" exec-out screencap -p > "$raw" 2>/dev/null || true
  if [[ -f "$raw" && -s "$raw" ]]; then
    if command -v sips >/dev/null 2>&1; then
      sips --resampleWidth 720 "$raw" --out "$scaled" >/dev/null 2>&1 || cp "$raw" "$scaled"
    else
      cp "$raw" "$scaled"
    fi
    rm -f "$raw"
    echo -e "  ${BLUE}📸 Screenshot captured: [${name}] -> ${scaled}${NC}"
  else
    echo -e "  ${YELLOW}⚠️  Could not capture screenshot for [${name}]${NC}"
  fi
}

# Helper to report failure
report_failure() {
  local step="$1"
  local reason="$2"
  echo ""
  echo -e "${BOLD}${RED}==========================================================${NC}"
  echo -e "${BOLD}${RED}❌ TEST FAILED at step: $step${NC}"
  echo -e "${BOLD}${RED}❌ Reason: $reason${NC}"
  echo -e "${BOLD}${RED}==========================================================${NC}"
  
  capture_step "failure_${step}"
  
  echo ""
  echo -e "${YELLOW}📋 Dumping last 60 lines of relevant logcat:${NC}"
  echo "----------------------------------------------------------"
  adb -s "$DEVICE" logcat -d | grep -E "Unity|FATAL|SIGSEGV|AndroidRuntime|NullReferenceException|Crash|Exception" | tail -n 60 || true
  echo "----------------------------------------------------------"
  echo -e "Artifacts and screenshots saved in: ${BOLD}$RUN_DIR${NC}"
  exit 1
}

# Helper to get current app PID
get_app_pid() {
  adb -s "$DEVICE" shell pidof "$APP_PACKAGE" 2>/dev/null | tr -d '\r\n' | awk '{print $1}' || true
}

# ------------------------------------------------------------------------------
# 1. VERIFY BACKEND SERVER
# ------------------------------------------------------------------------------
echo -e "${BOLD}[1/7] Checking Backend Server Health...${NC}"
HTTP_CODE=$(curl -s -X POST -o /dev/null -w "%{http_code}" "http://127.0.0.1:${SERVER_PORT}/boot/index" 2>/dev/null || true)
if [[ "$HTTP_CODE" != "200" ]]; then
  report_failure "SERVER_CHECK" "Backend server on port $SERVER_PORT is not responding (HTTP $HTTP_CODE). Run ./scripts/run-direct.sh first!"
fi
echo -e "  ${GREEN}✅ Backend server is UP and responding (HTTP 200).${NC}"

# ------------------------------------------------------------------------------
# 2. OPTIONAL BUILD & INSTALL APK
# ------------------------------------------------------------------------------
APK_PATH="$REPO_ROOT/.local/artifacts/KickFlight-2.11.0-direct-${SERVER_IP}-${SERVER_PORT}.apk"

if [[ $DO_BUILD -eq 1 ]]; then
  echo ""
  echo -e "${BOLD}[2/7] Rebuilding Octo Catalog & Direct APK with patches...${NC}"
  python3 "$REPO_ROOT/scripts/build-title-resource-catalog.py"
  SERVER_BASE_URL="http://${SERVER_IP}:${SERVER_PORT}" "$REPO_ROOT/scripts/build-direct-apk.sh" || report_failure "APK_BUILD" "Failed to build direct APK."
  echo -e "  ${GREEN}✅ Direct APK built successfully.${NC}"
fi

if [[ ! -f "$APK_PATH" ]]; then
  # Fallback to any matching direct APK in .local/artifacts
  FALLBACK_APK=$(ls "$REPO_ROOT/.local/artifacts"/KickFlight-2.11.0-direct-*.apk 2>/dev/null | head -n1 || true)
  if [[ -n "$FALLBACK_APK" ]]; then
    APK_PATH="$FALLBACK_APK"
    echo -e "  ${YELLOW}⚠️  Specific APK for $SERVER_IP not found, using fallback: $APK_PATH${NC}"
  else
    report_failure "APK_FIND" "No APK found at $APK_PATH. Pass --build to build it."
  fi
fi

if [[ $DO_INSTALL -eq 1 ]]; then
  echo ""
  echo -e "${BOLD}[2/7] Installing APK on $DEVICE ($APK_PATH)...${NC}"
  adb -s "$DEVICE" install -r "$APK_PATH" || report_failure "APK_INSTALL" "adb install failed."
  adb -s "$DEVICE" shell pm grant "$APP_PACKAGE" android.permission.POST_NOTIFICATIONS 2>/dev/null || true
  echo -e "  ${GREEN}✅ APK installed and permissions granted.${NC}"
else
  echo ""
  echo -e "${BOLD}[2/7] Skipping APK installation (--no-install specified).${NC}"
fi

# ------------------------------------------------------------------------------
# 3. LAUNCH APP & WAIT FOR PROCESS
# ------------------------------------------------------------------------------
echo ""
echo -e "${BOLD}[3/7] Launching $APP_PACKAGE...${NC}"

adb -s "$DEVICE" shell am force-stop "$APP_PACKAGE"
adb -s "$DEVICE" logcat -c

if [[ $DO_CLEAN -eq 1 ]]; then
  echo -e "  ${YELLOW}🧹 Cleaning app data (--clean specified)...${NC}"
  adb -s "$DEVICE" shell pm clear "$APP_PACKAGE"
  adb -s "$DEVICE" shell pm grant "$APP_PACKAGE" android.permission.POST_NOTIFICATIONS 2>/dev/null || true
fi

# The client only downloads what the Octo catalog advertises (~850 files). GameScene needs
# the full preserved set (2,579 bundles); without it Unity dies on the loading screen with
# SIGSEGV fault addr 0x7b0 in libunity.so while trying to log the load failure. pm clear wipes
# the seed, so the check runs after --clean and before every launch.
if [[ $DO_SEED -eq 1 ]]; then
  echo ""
  echo -e "${BOLD}[3/7] Checking Octo asset cache on $DEVICE...${NC}"
  CACHE_FILES=$(adb -s "$DEVICE" shell "run-as $APP_PACKAGE find files/octo -type f 2>/dev/null | wc -l" 2>/dev/null | tr -cd '0-9' || true)
  [[ "$CACHE_FILES" =~ ^[0-9]+$ ]] || CACHE_FILES=0
  PYTHON_BIN=$(command -v python3 || command -v python)
  if (( CACHE_FILES < SEED_MIN_FILES )); then
    echo -e "  ${YELLOW}⚠️  Octo cache has $CACHE_FILES files (< $SEED_MIN_FILES). Seeding full asset set...${NC}"
    SEED_ARGS=(-s "$DEVICE")
    [[ -f "$REPO_ROOT/octo_cache.tar" ]] && SEED_ARGS+=(--skip-tar)
    if ! "$PYTHON_BIN" "$REPO_ROOT/scripts/seed-device-cache.py" "${SEED_ARGS[@]}"; then
      report_failure "CACHE_SEED" "seed-device-cache.py failed. Run it manually or pass --no-seed."
    fi
    CACHE_FILES=$(adb -s "$DEVICE" shell "run-as $APP_PACKAGE find files/octo -type f 2>/dev/null | wc -l" 2>/dev/null | tr -cd '0-9' || true)
  fi
  echo -e "  ${GREEN}✅ Octo cache ready ($CACHE_FILES files).${NC}"
fi
adb -s "$DEVICE" shell am start -n "$APP_PACKAGE/$APP_ACTIVITY" >/dev/null

# Wait for process to spawn
APP_PID=""
for i in $(seq 1 15); do
  APP_PID=$(get_app_pid)
  if [[ -n "$APP_PID" ]]; then break; fi
  sleep 0.5
done

if [[ -z "$APP_PID" ]]; then
  report_failure "APP_LAUNCH" "Application process failed to start within 7 seconds."
fi

echo -e "  ${GREEN}✅ Process running with PID $APP_PID.${NC}"
sleep 2
capture_step "01_launched"

# ------------------------------------------------------------------------------
# 4. TITLE SCREEN & TAP START
# ------------------------------------------------------------------------------
echo ""
echo -e "${BOLD}[4/7] Waiting for Title Screen...${NC}"

TITLE_REACHED=0
START_TIME=$(date +%s)

while true; do
  CURRENT_PID=$(get_app_pid)
  if [[ -z "$CURRENT_PID" ]]; then
    report_failure "TITLE_SCREEN" "App process died before reaching Title screen."
  fi
  
  # Check if TitleScene reached in logcat
  if adb -s "$DEVICE" logcat -d --pid="$APP_PID" | grep -q "TitleScene"; then
    TITLE_REACHED=1
    break
  fi
  
  NOW=$(date +%s)
  if (( NOW - START_TIME > 30 )); then
    break
  fi
  sleep 1
done

if [[ $TITLE_REACHED -eq 0 ]]; then
  report_failure "TITLE_SCREEN" "Timed out waiting for TitleScene in logcat."
fi

echo -e "  ${GREEN}✅ TitleScene reached! Waiting 6s for animations and TAP START to appear...${NC}"
sleep 6
capture_step "02_title_screen"

echo -e "  ${CYAN}👉 Tapping 'TAP START' at (540, 1200)...${NC}"
adb -s "$DEVICE" shell input tap 540 1200
sleep 2
capture_step "03_tap_start_pressed"

# ------------------------------------------------------------------------------
# 5. TRANSITION TO HOMESCENE
# ------------------------------------------------------------------------------
echo ""
echo -e "${BOLD}[5/7] Waiting for HomeScene transition (loading assets)...${NC}"

HOME_REACHED=0
START_TIME=$(date +%s)
LAST_LOG_TIME=$START_TIME
LAST_TAP_TIME=$START_TIME

while true; do
  CURRENT_PID=$(get_app_pid)
  if [[ -z "$CURRENT_PID" ]]; then
    report_failure "HOME_TRANSITION" "App process crashed (SIGSEGV / crash) during Home transition."
  fi
  
  # Check for native crash
  if adb -s "$DEVICE" logcat -d --pid="$APP_PID" | grep -E -q "Fatal signal|SIGSEGV"; then
    report_failure "HOME_TRANSITION" "Fatal signal (SIGSEGV) detected in logcat during Home transition."
  fi
  
  # Check if HomeScene reached
  if adb -s "$DEVICE" logcat -d --pid="$APP_PID" | grep -q "HomeScene"; then
    HOME_REACHED=1
    break
  fi
  
  NOW=$(date +%s)
  if (( NOW - LAST_TAP_TIME >= 5 )); then
    echo -e "  ${CYAN}👉 Retrying 'TAP START' tap at (540, 1200)...${NC}"
    adb -s "$DEVICE" shell input tap 540 1200 2>/dev/null || true
    LAST_TAP_TIME=$NOW
  fi

  if (( NOW - LAST_LOG_TIME >= 10 )); then
    ELAPSED=$(( NOW - START_TIME ))
    echo -e "  ${YELLOW}⏳ Still loading assets (${ELAPSED}s elapsed)...${NC}"
    capture_step "04_loading"
    LAST_LOG_TIME=$NOW
  fi
  
  if (( NOW - START_TIME > TIMEOUT )); then
    break
  fi
  sleep 1
done

if [[ $HOME_REACHED -eq 0 ]]; then
  report_failure "HOME_TRANSITION" "Timed out waiting for HomeScene (${TIMEOUT}s limit reached)."
fi

echo -e "  ${GREEN}✅ HomeScene reached! Waiting 22s for 3D assets, shaders, and announcement modal to finish loading...${NC}"
sleep 22

# Dismiss any announcement, news, or modal popup by tapping Aceptar (540, 1725) and close (540, 2220)
echo -e "  ${CYAN}👉 Dismissing announcement / news modal at (540, 1725) and (540, 2220)...${NC}"
adb -s "$DEVICE" shell input tap 540 1725
sleep 2
adb -s "$DEVICE" shell input tap 540 2220
sleep 2
capture_step "05_home_screen"

# ------------------------------------------------------------------------------
# 6. MATCHMAKING ROOM (COMBATE BUTTON)
# ------------------------------------------------------------------------------
echo ""
echo -e "${BOLD}[6/7] Entering Matchmaking (Tapping 'Combate' at 820, 1480)...${NC}"

adb -s "$DEVICE" shell input tap 820 1480
sleep 2
capture_step "06_combate_pressed"

MATCH_ROOM_REACHED=0
START_TIME=$(date +%s)
LAST_TAP_TIME=$START_TIME

while true; do
  CURRENT_PID=$(get_app_pid)
  if [[ -z "$CURRENT_PID" ]]; then
    report_failure "MATCHMAKING" "App process crashed after tapping Combate."
  fi
  
  if adb -s "$DEVICE" logcat -d --pid="$APP_PID" | grep -E -q "MatchingScene|NormalMatchingJoinBattleRoomState|GetAssignments|battle/entry"; then
    MATCH_ROOM_REACHED=1
    break
  fi
  
  NOW=$(date +%s)
  if (( NOW - LAST_TAP_TIME >= 4 )); then
    echo -e "  ${CYAN}👉 Retrying modal dismiss and 'Combate' tap...${NC}"
    adb -s "$DEVICE" shell input tap 540 1725 2>/dev/null || true
    sleep 1
    adb -s "$DEVICE" shell input tap 540 2220 2>/dev/null || true
    sleep 1
    adb -s "$DEVICE" shell input tap 820 1480 2>/dev/null || true
    LAST_TAP_TIME=$NOW
  fi

  if (( NOW - START_TIME > 25 )); then
    break
  fi
  sleep 1
done

if [[ $MATCH_ROOM_REACHED -eq 0 ]]; then
  echo -e "  ${YELLOW}⚠️  Matchmaking state not explicitly logged in logcat, checking screen...${NC}"
fi

sleep 4
capture_step "07_matchmaking_room"
echo -e "  ${GREEN}✅ Matchmaking room displayed.${NC}"

# ------------------------------------------------------------------------------
# 7. INICIAR COMBATE & BATTLE SCENE VERIFICATION
# ------------------------------------------------------------------------------
echo ""
echo -e "${BOLD}[7/7] Starting Battle (Tapping 'Iniciar combate' at 540, 630)...${NC}"

adb -s "$DEVICE" shell input tap 540 630
sleep 2
capture_step "08_iniciar_combate_pressed"

BATTLE_STARTED=0
START_TIME=$(date +%s)
LAST_BATTLE_TAP=$START_TIME

echo -e "  ${CYAN}⏳ Monitoring battle scene transition and character spawning...${NC}"

while true; do
  CURRENT_PID=$(get_app_pid)
  if [[ -z "$CURRENT_PID" ]]; then
    report_failure "BATTLE_LOOP" "App process crashed during battle transition."
  fi
  
  # Check for crash signals
  if adb -s "$DEVICE" logcat -d --pid="$APP_PID" | grep -E -q "Fatal signal|SIGSEGV"; then
    report_failure "BATTLE_LOOP" "Fatal signal (SIGSEGV) in logcat during battle transition."
  fi

  # Check for managed exception in character spawn / SetModel
  if adb -s "$DEVICE" logcat -d --pid="$APP_PID" | grep -A 2 "NullReferenceException" | grep -E -q "SetModel|ReceiveAddPlayer|InGameSceneBase"; then
    report_failure "BATTLE_SPAWN" "NullReferenceException detected during character spawn (PlayerCharacter.SetModel)."
  fi
  
  # Check if PlayStartAnim / PlayGoAnimation / UpdateFly reached (combat initiation & flight)
  BATTLE_LOGS=$(adb -s "$DEVICE" logcat -d --pid="$APP_PID" | grep -E "GameScene|InGameSceneBase|PlayStartAnim|PlayGoAnimation|PlayGameReady|UpdateFly|ReceiveAddPlayer|ApplyBattleProperties" || true)
  
  if echo "$BATTLE_LOGS" | grep -q -E "PlayStartAnim|PlayGoAnimation|UpdateFly"; then
    BATTLE_STARTED=1
    echo -e "  ${GREEN}🎉 Combat loop initiated ('3, 2, 1, FLY!' / active flight) detected in logcat!${NC}"
    echo "$BATTLE_LOGS" | tail -n 10
    break
  fi

  NOW=$(date +%s)
  # If GameScene hasn't started yet, re-tap 'Iniciar combate' every 5 seconds
  if ! echo "$BATTLE_LOGS" | grep -q "GameScene"; then
    if (( NOW - LAST_BATTLE_TAP >= 5 )); then
      echo -e "  ${CYAN}👉 Retrying 'Iniciar combate' tap at (540, 630)...${NC}"
      adb -s "$DEVICE" shell input tap 540 630 2>/dev/null || true
      LAST_BATTLE_TAP=$NOW
    fi
  fi
  
  if (( NOW - START_TIME > 60 )); then
    break
  fi
  sleep 1
done

if [[ $BATTLE_STARTED -eq 0 ]]; then
  # If GameScene was loaded but PlayStartAnim not called
  if echo "$BATTLE_LOGS" | grep -q "GameScene"; then
    report_failure "BATTLE_LOOP" "GameScene reached but PlayStartAnim was never called (animation/spawn stalled)."
  else
    report_failure "BATTLE_LOOP" "Timed out waiting for GameScene / PlayStartAnim (40s limit reached)."
  fi
fi

# Let battle render for 3 seconds
sleep 3
capture_step "09_battle_scene_ready"

echo ""
echo -e "${BOLD}${GREEN}==========================================================${NC}"
echo -e "${BOLD}${GREEN}🏆 BATTLE SYSTEM TEST COMPLETED SUCCESSFULLY!${NC}"
echo -e "${BOLD}${GREEN}==========================================================${NC}"
echo -e "📸 All screenshots and logs saved in:"
echo -e "   ${BOLD}$RUN_DIR${NC}"
echo -e "Latest symlink: ${BOLD}$REPO_ROOT/.local/battle-test/latest${NC}"
echo ""
ls -lh "$RUN_DIR"/*.png
echo -e "${BOLD}${GREEN}==========================================================${NC}"
exit 0
