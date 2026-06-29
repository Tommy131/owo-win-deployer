Get-Process WinDeploy -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
dotnet build src/WinDeploy.App/WinDeploy.App.csproj -c Debug
if ($LASTEXITCODE -eq 0) {
    Start-Process "src\WinDeploy.App\bin\Debug\net10.0-windows10.0.19041.0\WinDeploy.exe"
}
