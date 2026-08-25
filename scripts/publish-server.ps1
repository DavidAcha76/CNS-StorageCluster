$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

dotnet restore
dotnet publish src/CNS.StorageCluster.Server -c Release -r win-x64 --self-contained true -o publish/server-windows
dotnet publish src/CNS.StorageCluster.Server -c Release -r linux-x64 --self-contained true -o publish/server-linux
Write-Host "Servidor publicado en publish/server-windows y publish/server-linux"
Write-Host "Socket TCP esperado: distribuidos.hermesoft.com:5050"
