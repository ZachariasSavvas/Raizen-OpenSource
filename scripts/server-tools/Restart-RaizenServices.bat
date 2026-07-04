@echo off
setlocal

echo.
echo  ============================================================
echo    Raizen Services Restart
echo  ============================================================
echo.

:: Require admin
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo  ERROR: This script must be run as Administrator.
    pause
    exit /b 1
)

:: ── Stop (Web first — it depends on API) ─────────────────────────────────────
echo  Stopping RaizenWeb...
sc stop RaizenWeb >nul 2>&1
call :WaitFor RaizenWeb STOPPED 15
echo.

echo  Stopping RaizenApi...
sc stop RaizenApi >nul 2>&1
call :WaitFor RaizenApi STOPPED 15
echo.

:: ── Start ────────────────────────────────────────────────────────────────────
echo  Starting RaizenApi...
sc start RaizenApi >nul 2>&1
call :WaitFor RaizenApi RUNNING 30
echo.

echo  Starting RaizenWeb...
sc start RaizenWeb >nul 2>&1
call :WaitFor RaizenWeb RUNNING 30
echo.

:: ── Final status ─────────────────────────────────────────────────────────────
echo  ============================================================
echo  Final status:

for %%s in (RaizenApi RaizenWeb) do (
    for /f "tokens=4" %%t in ('sc query %%s ^| findstr "        STATE"') do (
        if "%%t"=="RUNNING" (
            echo    %%s  :  RUNNING  ^(OK^)
        ) else (
            echo    %%s  :  !! %%t  - check Event Viewer for details
        )
    )
)
echo  ============================================================
echo.
pause
exit /b

:: ─────────────────────────────────────────────────────────────────────────────
:WaitFor   service  state  seconds
set /a _tries=0
:_loop
set /a _tries+=1
sc query "%~1" | findstr "        STATE" | findstr /i "%~2" >nul 2>&1
if %errorlevel%==0 (
    echo    %~1  ->  %~2
    exit /b 0
)
if %_tries% geq %~3 (
    echo    %~1  ->  timed out waiting for %~2
    exit /b 1
)
timeout /t 1 /nobreak >nul
goto :_loop
