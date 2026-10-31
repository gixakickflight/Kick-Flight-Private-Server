@echo off
setlocal
cd /d "%~dp0"
echo Starting Kick Flight Badges Server on http://127.0.0.1:8766 ...
python tools\badges\server.py --port 8766
