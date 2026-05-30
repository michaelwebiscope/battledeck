$ErrorActionPreference = 'Stop'
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')

# RabbitMQ 3.13.x requires Erlang 26.2.x
choco install erlang --version=26.2.5.5 -y --force

[Environment]::SetEnvironmentVariable('ERLANG_HOME', 'C:\Program Files\Erlang OTP', 'Machine')
$env:ERLANG_HOME = 'C:\Program Files\Erlang OTP'
[Environment]::SetEnvironmentVariable('RABBITMQ_BASE', 'C:\RabbitMQ', 'Machine')
$env:RABBITMQ_BASE = 'C:\RabbitMQ'

$serverDir = Get-ChildItem 'C:\Program Files\RabbitMQ Server' -Directory | Where-Object { $_.Name -like 'rabbitmq_server-*' } | Select-Object -First 1
if (-not $serverDir) { throw 'RabbitMQ server not installed' }
$sbin = Join-Path $serverDir.FullName 'sbin'

& "$sbin\rabbitmq-service.bat" stop 2>$null
Start-Sleep 2
& "$sbin\rabbitmq-service.bat" remove 2>$null
& "$sbin\rabbitmq-service.bat" install
& "$sbin\rabbitmq-service.bat" start

Start-Sleep 25
if (Get-NetTCPConnection -LocalPort 5672 -State Listen -ErrorAction SilentlyContinue) {
    Write-Output 'AMQP OK'
} else {
    Write-Output 'AMQP FAIL'
    Get-ChildItem C:\RabbitMQ\log -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Output "--- $($_.Name) ---"
        Get-Content $_.FullName -Tail 20
    }
    (Get-Service RabbitMQ).Status
}
