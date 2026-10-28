@echo off
rem Startet ReplayClip und beendet vorher alle anderen laufenden Versionen (inkl. FFmpeg).
start "" "%~dp0ReplayClip.exe" --replace
