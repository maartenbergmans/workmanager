@echo off
wsl --cd "%~dp0" -e bash -lc "deploytool default push"
