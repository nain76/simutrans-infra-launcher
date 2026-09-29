@echo off
rem Run Manage-SigningKey.ps1 as administrator (show the verification code, back up or restore the signing key)
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','\"%~dp0Manage-SigningKey.ps1\"')"
