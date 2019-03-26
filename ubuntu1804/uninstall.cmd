@echo off
pushd "%~dp0"

set TARGET_DIR=%SYSTEMDRIVE%\Distro\Ubuntu1804Custom
wsl --unregister Ubuntu1804Custom
if exist %TARGET_DIR% rmdir /s /q %TARGET_DIR%

:exit
popd
@echo on