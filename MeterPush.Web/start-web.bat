@echo off
rem Starts the Monthly Meter Push web page (address and access code are in appsettings.json)
cd /d "%~dp0"
dotnet bin\Release\net7.0\MeterPush.Web.dll
