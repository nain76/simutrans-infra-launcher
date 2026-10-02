@echo off
rem Run Rename-ServerList.ps1 as administrator (rename the server list and pakset folders to hard-to-guess names)
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','\"%~dp0Rename-ServerList.ps1\"')"
