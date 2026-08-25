$hostName = "distribuidos.hermesoft.com"
Write-Host "Resolución DNS:"
Resolve-DnsName $hostName
Write-Host "`nPrueba del socket TCP 5050:"
Test-NetConnection $hostName -Port 5050
