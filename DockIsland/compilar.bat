@echo off
echo Compilando DockIsland...
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishReadyToRun=true -o publish
echo.
echo Pronto! Executavel em: publish\DockIsland.exe
pause
