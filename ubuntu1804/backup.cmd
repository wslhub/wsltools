@echo off
pushd "%~dp0"

wsl --export Ubuntu1804Custom %userprofile%\Ubuntu1804Custom-backup.tar.gz

:exit
popd
@echo on