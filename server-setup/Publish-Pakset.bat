@echo off
rem Run Publish-Pakset.ps1 as administrator with the settings saved by Setup-Server.bat
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','\"%~dp0Publish-Pakset.ps1\"')"
