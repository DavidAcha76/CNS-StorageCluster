$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "La base de datos se crea automáticamente al iniciar el servidor mediante EF Core EnsureCreatedAsync()."
Write-Host "Restaurando paquetes y compilando para validar la configuración..."
dotnet restore
dotnet build CNS.StorageCluster.sln
Write-Host "Ahora ejecuta: dotnet run --project src/CNS.StorageCluster.Server"
