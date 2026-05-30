Get-Service RabbitMQ -ErrorAction SilentlyContinue | Format-List Status, StartType
Write-Output '--- ports ---'
netstat -ano | findstr ':5672'
Write-Output '--- logs ---'
Get-ChildItem C:\RabbitMQ\log -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Output "LOG $($_.Name)"
    Get-Content $_.FullName -Tail 30
}
$ctl = Get-ChildItem 'C:\Program Files\RabbitMQ Server' -Recurse -Filter rabbitmqctl.bat -ErrorAction SilentlyContinue | Select-Object -First 1
if ($ctl) {
    Write-Output '--- status ---'
    & $ctl.FullName status 2>&1
}
