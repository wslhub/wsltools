@echo off
pushd "%~dp0"

curl https://aka.ms/wsl-ubuntu-1604 -L -o install.zip
powershell -Command Expand-Archive -Force install.zip
set TARGET_DIR=%SYSTEMDRIVE%\Distro\Ubuntu1604Custom
if not exist %TARGET_DIR% mkdir %TARGET_DIR%
wsl --import Ubuntu1604Custom %TARGET_DIR% .\install\install.tar.gz

:exit
del install.zip
rd /s /q install

popd
@echo on