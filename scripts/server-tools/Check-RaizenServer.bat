@echo off
setlocal EnableDelayedExpansion

echo.
echo  ============================================================
echo    Raizen Server Health Check
echo  ============================================================
echo.

:: ── Services ─────────────────────────────────────────────────────────────────
echo  [SERVICES]

call :CheckService RaizenApi "Raizen API"
call :CheckService RaizenWeb "Raizen Web Portal"

:: PostgreSQL — name varies by version (postgresql-x64-16, etc.)
set PG_FOUND=0
for /f "tokens=1" %%s in ('sc query type^= all state^= all ^| findstr /i "postgresql"') do set PG_FOUND=1
if "%PG_FOUND%"=="1" (
    echo    PostgreSQL        :  RUNNING
) else (
    echo    PostgreSQL        :  !! NOT DETECTED
)

echo.

:: ── Ports ────────────────────────────────────────────────────────────────────
echo  [PORTS]

netstat -an | findstr ":5001 " | findstr "LISTENING" >nul 2>&1
if %errorlevel%==0 (echo    Port 5001 ^(API^)  :  LISTENING) else (echo    Port 5001 ^(API^)  :  !! NOT LISTENING)

netstat -an | findstr ":5002 " | findstr "LISTENING" >nul 2>&1
if %errorlevel%==0 (echo    Port 5002 ^(Web^)  :  LISTENING) else (echo    Port 5002 ^(Web^)  :  !! NOT LISTENING)

echo.

:: ── Files ────────────────────────────────────────────────────────────────────
echo  [FILES]

set INSTALL=C:\Program Files\Raizen\Server

call :CheckFile "%INSTALL%\Api\Raizen.Server.Api.exe"      "API executable"
call :CheckFile "%INSTALL%\Api\appsettings.Production.json" "API config"
call :CheckFile "%INSTALL%\Web\Raizen.Server.Web.exe"      "Web executable"
call :CheckFile "%INSTALL%\Web\appsettings.Production.json" "Web config"

if exist "%INSTALL%\Packages\RaizenEndpoint.msi" (
    echo    Agent MSI         :  OK
) else (
    echo    Agent MSI         :  not present ^(optional^)
)

echo.

:: ── Firewall ─────────────────────────────────────────────────────────────────
echo  [FIREWALL]

netsh advfirewall firewall show rule name="Raizen API" >nul 2>&1
if %errorlevel%==0 (echo    Raizen API rule   :  EXISTS) else (echo    Raizen API rule   :  !! MISSING)

netsh advfirewall firewall show rule name="Raizen Web Portal" >nul 2>&1
if %errorlevel%==0 (echo    Raizen Web rule   :  EXISTS) else (echo    Raizen Web rule   :  !! MISSING)

echo.

:: ── DB connectivity (via web appsettings) ────────────────────────────────────
echo  [DATABASE]

set CFG=%INSTALL%\Web\appsettings.Production.json
if not exist "%CFG%" (
    echo    Connection string :  !! config file not found
    goto :skipdb
)

powershell -NoProfile -Command ^
  "$cfg = Get-Content '%CFG%' | ConvertFrom-Json; $cs = $cfg.ConnectionStrings.Default; Write-Host '   Connection str   :  ' $cs"

:: Try to find psql and do a quick ping
set PSQL=
for /d %%d in ("C:\Program Files\PostgreSQL\*") do (
    if exist "%%d\bin\psql.exe" set PSQL=%%d\bin\psql.exe
)

if "%PSQL%"=="" (
    echo    DB ping           :  psql not found - cannot test
    goto :skipdb
)

powershell -NoProfile -Command ^
  "$cfg = Get-Content '%CFG%' | ConvertFrom-Json; $cs = $cfg.ConnectionStrings.Default; ^
   $parts = @{}; foreach($p in $cs -split ';') { $kv = $p -split '=',2; if($kv.Length -eq 2){ $parts[$kv[0].Trim()] = $kv[1].Trim() } }; ^
   $env:PGPASSWORD = $parts['Password']; ^
   $result = & '%PSQL%' -h $parts['Host'] -p $parts['Port'] -U $parts['Username'] -d $parts['Database'] -c 'SELECT 1' -t 2>&1; ^
   if ($LASTEXITCODE -eq 0) { Write-Host '   DB ping           :  OK' } else { Write-Host '   DB ping           :  !! FAILED -' $result }"

:skipdb
echo.
echo  ============================================================
echo.
pause
exit /b

:: ─────────────────────────────────────────────────────────────────────────────
:CheckService
sc query "%~1" >nul 2>&1
if %errorlevel% neq 0 (
    echo    %-17s :  !! NOT INSTALLED
    exit /b
)
for /f "tokens=4" %%s in ('sc query "%~1" ^| findstr "        STATE"') do (
    if "%%s"=="RUNNING" (
        echo    %~2:  RUNNING
    ) else (
        echo    %~2:  !! %%s
    )
)
exit /b

:CheckFile
if exist %1 (
    echo    %-17s :  OK
) else (
    echo    %-17s :  !! MISSING
)
exit /b
