$ErrorActionPreference = 'Stop'
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')

sc.exe stop RabbitMQ 2>$null
choco uninstall rabbitmq -y -n --skip-autouninstaller --force 2>$null
choco uninstall erlang -y -n --skip-autouninstaller --force 2>$null
Remove-Item 'C:\Program Files\RabbitMQ Server' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item 'C:\Program Files\Erlang OTP' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item 'C:\ProgramData\chocolatey\lib\rabbitmq' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item 'C:\ProgramData\chocolatey\lib\erlang' -Recurse -Force -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Force -Path 'C:\RabbitMQ\data', 'C:\RabbitMQ\log' | Out-Null
[Environment]::SetEnvironmentVariable('RABBITMQ_BASE', 'C:\RabbitMQ', 'Machine')
$env:RABBITMQ_BASE = 'C:\RabbitMQ'

choco install erlang --version=26.2.5.5 -y
choco install rabbitmq --version=3.13.7 -y

[Environment]::SetEnvironmentVariable('ERLANG_HOME', 'C:\Program Files\Erlang OTP', 'Machine')
$env:ERLANG_HOME = 'C:\Program Files\Erlang OTP'

$erl = Get-ChildItem "$env:ERLANG_HOME\erts-*" -Directory | Sort-Object Name -Descending | Select-Object -First 1
Write-Output "Using Erlang runtime: $($erl.Name)"

$serverDir = Get-ChildItem 'C:\Program Files\RabbitMQ Server' -Directory | Where-Object { $_.Name -like 'rabbitmq_server-*' } | Select-Object -First 1
$sbin = Join-Path $serverDir.FullName 'sbin'
& "$sbin\rabbitmq-service.bat" install
& "$sbin\rabbitmq-service.bat" start
Start-Sleep 30

if (Get-NetTCPConnection -LocalPort 5672 -State Listen -ErrorAction SilentlyContinue) {
    Write-Output 'AMQP OK'
} else {
    Write-Output 'AMQP FAIL'
    & "$sbin\rabbitmq-server.bat" 2>&1 | Select-Object -First 25
}
