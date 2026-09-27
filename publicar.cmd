@echo off
rem Genera un unico WanMonitor.exe (requiere .NET 11 SDK; el exe necesita el .NET 11 Desktop Runtime).
rem Para un exe que no dependa del runtime instalado, cambia --self-contained false por true (pesa ~70 MB).

rem El monitor bloquea su propio exe: hay que cerrarlo antes de publicar encima.
taskkill /IM WanMonitor.exe /F >nul 2>&1 && echo Monitor cerrado para poder publicar.
timeout /t 2 /nobreak >nul

dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
if errorlevel 1 (
  echo.
  echo Fallo la publicacion. Si el error es "GenerateBundle" o "Manifest state",
  echo el exe seguia bloqueado: cierra WanMonitor, borra bin y obj, y repite.
  pause
  exit /b 1
)

echo.
echo Listo: dist\WanMonitor.exe
start "" "%~dp0dist\WanMonitor.exe"
pause
