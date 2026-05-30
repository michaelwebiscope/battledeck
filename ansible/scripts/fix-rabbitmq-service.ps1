$ErrorActionPreference = 'Stop'
$base = 'C:\RabbitMQ'
New-Item -ItemType Directory -Force -Path $base, "$base\data", "$base\log" | Out-Null
[Environment]::SetEnvironmentVariable('RABBITMQ_BASE', $base, 'Machine')
$serverDir = Get-ChildItem 'C:\Program Files\RabbitMQ Server' -Directory | Where-Object { $_.Name -like 'rabbitmq_server-*' } | Select-Object -First 1
if (-not $serverDir) { throw 'RabbitMQ server directory not found' }
$sbin = Join-Path $serverDir.FullName 'sbin'
& "$sbin\rabbitmq-service.bat" stop 2>$null
Start-Sleep 3
& "$sbin\rabbitmq-service.bat" remove 2>$null
& "$sbin\rabbitmq-service.bat" install
& "$sbin\rabbitmq-service.bat" start
Start-Sleep 25
$conn = Get-NetTCPConnection -LocalPort 5672 -State Listen -ErrorAction SilentlyContinue
if ($conn) {
    Write-Output 'AMQP OK'
} else {
    Write-Output 'AMQP FAIL'
    Get-ChildItem "$base\log" -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Output "=== $($_.Name) ==="
        Get-Content $_.FullName -Tail 20
    }
}
