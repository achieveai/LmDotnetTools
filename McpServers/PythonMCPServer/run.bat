@echo off
setlocal
cd /d %~dp0

REM Activate the virtual environment
call .venv/Scripts/activate.bat

python src/server.py %*