# Ejecutar PowerShell como Administrador en el equipo servidor.
$ErrorActionPreference = "Stop"

New-NetFirewallRule -DisplayName "CNS Storage Cluster TCP 5050" -Direction Inbound -Protocol TCP -LocalPort 5050 -Action Allow -ErrorAction SilentlyContinue
New-NetFirewallRule -DisplayName "CNS Storage Cluster Web 8080" -Direction Inbound -Protocol TCP -LocalPort 8080 -Action Allow -ErrorAction SilentlyContinue

Write-Host "Reglas creadas/verificadas para TCP 5050 y Web 8080."
