@echo off
pushd "%~dp0"

curl https://aka.ms/wsl-ubuntu-1804 -L -o install.zip
powershell -Command Expand-Archive -Force install.zip
set TARGET_DIR=%SYSTEMDRIVE%\Distro\Ubuntu1804Custom
if not exist %TARGET_DIR% mkdir %TARGET_DIR%
wsl --import Ubuntu1804Custom %TARGET_DIR% .\install\install.tar.gz

:exit
del install.zip
rd /s /q install

popd
@echo on