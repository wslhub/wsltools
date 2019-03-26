@echo off
pushd "%~dp0"

wsl --distribution Ubuntu1804Custom

:exit
popd
@echo on