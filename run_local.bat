@echo off
setlocal

REM Ejecuta GENM_WEBSCRAPPER sin argumentos.
REM El propio programa administra la persistencia y solicita los datos.

set "PROJECT_DIR=%~dp0"
set "EXE_RELEASE=%PROJECT_DIR%bin\Release\GENM_WEBSCRAPPER.exe"
set "EXE_DEBUG=%PROJECT_DIR%bin\Debug\GENM_WEBSCRAPPER.exe"
set "SLN=%PROJECT_DIR%GENM_WEBSCRAPPER.sln"
set "EXE="

REM Preferir siempre Release; usar Debug solo si Release no existe.
if exist "%EXE_RELEASE%" set "EXE=%EXE_RELEASE%"
if not defined EXE if exist "%EXE_DEBUG%" set "EXE=%EXE_DEBUG%"

REM Si no existe ningún ejecutable, compilar Release.
if not defined EXE (
    echo No se encontro el ejecutable. Compilando Release...
    if not exist "%SLN%" (
        echo ERROR: No se encontro la solucion: %SLN%
        exit /b 1
    )
    msbuild "%SLN%" /p:Configuration=Release /p:Platform="Any CPU"
    if errorlevel 1 exit /b 1
    if not exist "%EXE_RELEASE%" (
        echo ERROR: La compilacion no genero: %EXE_RELEASE%
        exit /b 1
    )
    set "EXE=%EXE_RELEASE%"
)

echo Ejecutando sin argumentos: %EXE%
"%EXE%"
set "EXIT_CODE=%ERRORLEVEL%"

echo.
echo Proceso finalizado con codigo %EXIT_CODE%.
exit /b %EXIT_CODE%
