$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

dotnet restore
dotnet build CNS.StorageCluster.sln -c Release

dotnet publish src/CNS.StorageCluster.Server -c Release -r win-x64 --self-contained true -o publish/server-windows
dotnet publish src/CNS.StorageCluster.Server -c Release -r linux-x64 --self-contained true -o publish/server-linux
dotnet publish src/CNS.StorageCluster.Client -c Release -r win-x64 --self-contained true -o publish/client-windows
dotnet publish src/CNS.StorageCluster.Client -c Release -r linux-x64 --self-contained true -o publish/client-linux

Write-Host "Publicación terminada:"
Write-Host "  publish/server-windows"
Write-Host "  publish/server-linux"
Write-Host "  publish/client-windows"
Write-Host "  publish/client-linux"
Write-Host "Los clientes apuntan a distribuidos.hermesoft.com:5050"
