@echo off
pushd "%~dp0"

wsl --distribution Ubuntu1604Custom

:exit
popd
@echo on