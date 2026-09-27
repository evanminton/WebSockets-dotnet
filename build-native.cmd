@echo off
rem Builds the native C library and checks it from C. See build-native.ps1. Usage: build-native.cmd [win-x64^|win-arm64]
if "%~1"=="" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-native.ps1"
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-native.ps1" -Rid %1
)
