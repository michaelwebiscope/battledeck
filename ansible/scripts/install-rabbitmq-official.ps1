$ErrorActionPreference = 'Stop'
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')

Get-Process choco -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
sc.exe stop RabbitMQ 2>$null

$temp = 'C:\Temp\rabbitmq-install'
New-Item -ItemType Directory -Force -Path $temp, 'C:\RabbitMQ\data', 'C:\RabbitMQ\log' | Out-Null
[Environment]::SetEnvironmentVariable('RABBITMQ_BASE', 'C:\RabbitMQ', 'Machine')
$env:RABBITMQ_BASE = 'C:\RabbitMQ'

$otpExe = Join-Path $temp 'otp_win64_26.2.5.exe'
$rmqExe = Join-Path $temp 'rabbitmq-server-3.13.7.exe'

if (-not (Test-Path $otpExe)) {
    Invoke-WebRequest -Uri 'https://github.com/erlang/otp/releases/download/OTP-26.2.5/otp_win64_26.2.5.exe' -OutFile $otpExe -UseBasicParsing
}
if (-not (Test-Path $rmqExe)) {
    Invoke-WebRequest -Uri 'https://github.com/rabbitmq/rabbitmq-server/releases/download/v3.13.7/rabbitmq-server-3.13.7.exe' -OutFile $rmqExe -UseBasicParsing
}

Start-Process -FilePath $otpExe -ArgumentList '/S' -Wait
Start-Process -FilePath $rmqExe -ArgumentList '/S' -Wait

[Environment]::SetEnvironmentVariable('ERLANG_HOME', 'C:\Program Files\Erlang OTP', 'Machine')
$env:ERLANG_HOME = 'C:\Program Files\Erlang OTP'

$serverDir = Get-ChildItem 'C:\Program Files\RabbitMQ Server' -Directory | Where-Object { $_.Name -like 'rabbitmq_server-*' } | Select-Object -First 1
$sbin = Join-Path $serverDir.FullName 'sbin'
& "$sbin\rabbitmq-service.bat" stop 2>$null
& "$sbin\rabbitmq-service.bat" remove 2>$null
& "$sbin\rabbitmq-service.bat" install
& "$sbin\rabbitmq-service.bat" start
Start-Sleep 35

if (Get-NetTCPConnection -LocalPort 5672 -State Listen -ErrorAction SilentlyContinue) {
    Write-Output 'AMQP OK'
} else {
    Write-Output 'AMQP FAIL'
    (Get-Service RabbitMQ).Status
}
