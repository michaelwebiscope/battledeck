$ErrorActionPreference = 'Stop'
choco uninstall rabbitmq -y --remove-dependencies
choco uninstall erlang -y
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
choco install rabbitmq -y --version=3.13.7
[Environment]::SetEnvironmentVariable('RABBITMQ_BASE', 'C:\RabbitMQ', 'Machine')
$serverDir = Get-ChildItem 'C:\Program Files\RabbitMQ Server' -Directory | Where-Object { $_.Name -like 'rabbitmq_server-*' } | Select-Object -First 1
$sbin = Join-Path $serverDir.FullName 'sbin'
& "$sbin\rabbitmq-service.bat" stop 2>$null
& "$sbin\rabbitmq-service.bat" remove 2>$null
& "$sbin\rabbitmq-service.bat" install
& "$sbin\rabbitmq-service.bat" start
Start-Sleep 30
$conn = Get-NetTCPConnection -LocalPort 5672 -State Listen -ErrorAction SilentlyContinue
if ($conn) { Write-Output 'AMQP OK' } else { Write-Output 'AMQP FAIL' }
