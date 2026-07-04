@echo off
setlocal EnableDelayedExpansion

echo.
echo  ============================================================
echo    Raizen Admin Password Reset
echo  ============================================================
echo.
echo  This script will:
echo    1. Remove ALL admin accounts from the database
echo    2. Restart the RaizenWeb service
echo    3. The service will re-create the default account:
echo.
echo         Username :  Admin
echo         Password :  Admin
echo.
echo    You will be forced to set a new password on first login.
echo.
echo  WARNING: Any additional admin accounts you created will also
echo  be removed. Note them down before proceeding if needed.
echo.

:: Require admin
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo  ERROR: This script must be run as Administrator.
    pause
    exit /b 1
)

set /p CONFIRM= Type YES to continue:
if /i not "%CONFIRM%"=="YES" (
    echo  Cancelled.
    pause
    exit /b 0
)
echo.

:: ── Read connection string from appsettings ───────────────────────────────────
set CFG=C:\Program Files\Raizen\Server\Web\appsettings.Production.json
if not exist "%CFG%" (
    echo  ERROR: Config not found at:
    echo    %CFG%
    echo  Is Raizen Server installed?
    pause
    exit /b 1
)

echo  Reading database connection from config...

:: Parse connection string using PowerShell
for /f "delims=" %%L in ('powershell -NoProfile -Command ^
    "$cfg = Get-Content '%CFG%' | ConvertFrom-Json; ^
     $cs = $cfg.ConnectionStrings.Default; ^
     $parts = @{}; ^
     foreach($p in $cs -split ';') { ^
         $kv = $p -split '=',2; ^
         if($kv.Length -eq 2){ $parts[$kv[0].Trim()] = $kv[1].Trim() } ^
     }; ^
     Write-Output ('HOST=' + $parts['Host']); ^
     Write-Output ('PORT=' + $parts['Port']); ^
     Write-Output ('USER=' + $parts['Username']); ^
     Write-Output ('PASS=' + $parts['Password']); ^
     Write-Output ('DB='   + $parts['Database'])"') do (
    set "%%L"
)

if "!HOST!"=="" (
    echo  ERROR: Could not parse connection string from config.
    pause
    exit /b 1
)

echo    Host : !HOST!:!PORT!
echo    DB   : !DB! / !USER!
echo.

:: ── Find psql ─────────────────────────────────────────────────────────────────
set PSQL=
for /d %%d in ("C:\Program Files\PostgreSQL\*") do (
    if exist "%%d\bin\psql.exe" set PSQL=%%d\bin\psql.exe
)

if "!PSQL!"=="" (
    echo  ERROR: psql.exe not found in C:\Program Files\PostgreSQL\
    echo  Make sure PostgreSQL is installed.
    pause
    exit /b 1
)

:: ── Delete all admin accounts ─────────────────────────────────────────────────
echo  Clearing admin accounts...

set PGPASSWORD=!PASS!
"!PSQL!" -h !HOST! -p !PORT! -U !USER! -d !DB! -c "DELETE FROM admin_users;" >nul 2>&1

if %errorlevel% neq 0 (
    echo.
    echo  ERROR: Could not connect to the database or delete failed.
    echo  Check the connection details above and try again.
    pause
    exit /b 1
)

echo    Done.
echo.

:: ── Restart RaizenWeb so it reseeds the default admin ────────────────────────
echo  Restarting RaizenWeb...
sc stop RaizenWeb >nul 2>&1
timeout /t 5 /nobreak >nul
sc start RaizenWeb >nul 2>&1

:: Wait for RUNNING (up to 30s)
set /a _t=0
:_wait
set /a _t+=1
sc query RaizenWeb | findstr "        STATE" | findstr "RUNNING" >nul 2>&1
if %errorlevel%==0 goto :_done
if %_t% geq 30 (
    echo  WARNING: RaizenWeb did not reach RUNNING state within 30 seconds.
    echo  Check Services or Event Viewer.
    goto :_finish
)
timeout /t 1 /nobreak >nul
goto :_wait

:_done
echo    RaizenWeb is RUNNING.

:_finish
echo.
echo  ============================================================
echo    Reset complete.
echo.
echo    Log in at the admin portal with:
echo      Username :  Admin
echo      Password :  Admin
echo.
echo    You will be required to set a new password immediately.
echo  ============================================================
echo.
pause
exit /b 0
