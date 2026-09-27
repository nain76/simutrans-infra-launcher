@echo off
rem Run Enable-Https.ps1 as administrator
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','\"%~dp0Enable-Https.ps1\"')"
