@echo off
pushd "%~dp0"

wsl --export Ubuntu1604Custom %userprofile%\Ubuntu1604Custom-backup.tar.gz

:exit
popd
@echo on