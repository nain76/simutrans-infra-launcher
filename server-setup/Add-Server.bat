@echo off
rem Run Add-Server.ps1 as administrator
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','\"%~dp0Add-Server.ps1\"')"
