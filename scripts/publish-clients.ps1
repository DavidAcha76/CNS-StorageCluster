$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

dotnet restore
dotnet publish src/CNS.StorageCluster.Client -c Release -r win-x64 --self-contained true -o publish/client-windows
dotnet publish src/CNS.StorageCluster.Client -c Release -r linux-x64 --self-contained true -o publish/client-linux

Write-Host "Clientes publicados en publish/client-windows y publish/client-linux"
