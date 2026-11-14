@echo off
setlocal
cd /d "%~dp0"
title Criar instalador do DockIsland

echo.
echo [1/3] Compilando o DockIsland (pre-compilado, abre rapido)...
echo       Inclui o .NET junto: o instalador funciona em qualquer Windows 10/11 x64.
if exist publish_setup rmdir /s /q publish_setup
dotnet publish DockIsland.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:DebugType=none -p:DebugSymbols=false -o publish_setup
if errorlevel 1 goto erro

echo.
echo [2/3] Procurando o Inno Setup...
set "ISCC="
call :achar
if defined ISCC goto compilar

echo       Nao encontrado. Instalando via winget (pode pedir confirmacao)...
winget install --id JRSoftware.InnoSetup -e --silent --accept-package-agreements --accept-source-agreements
call :achar
if defined ISCC goto compilar

echo.
echo Nao consegui instalar o Inno Setup sozinho.
echo Baixe em https://jrsoftware.org/isdl.php, instale e rode este arquivo de novo.
goto erro

:compilar
echo       Usando: %ISCC%
echo.
echo [3/3] Gerando o instalador...
"%ISCC%" DockIsland.iss
if errorlevel 1 goto erro

echo.
echo Pronto! Instalador em: instalador\DockIsland-Setup.exe
explorer instalador
pause
exit /b 0

:achar
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if exist "%LocalAppData%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LocalAppData%\Programs\Inno Setup 6\ISCC.exe"
exit /b 0

:erro
echo.
echo Algo deu errado. Veja as mensagens acima.
pause
exit /b 1
