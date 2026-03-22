$ErrorActionPreference = "Continue"

# Windows services (sc.exe services — NOT scheduled tasks)
$services = @(
    "NavalArchiveCard",
    "NavalArchiveCart",
    "NavalArchiveOrder",
    "NavalArchivePaymentChain",
    "NavalArchiveShipping",
    "NavalArchiveNotification"
)

# Scheduled tasks (Go + Java — use task state, not Get-Service)
$scheduledTasks = @(
    "NavalArchiveAccount",
    "NavalArchivePayment",
    "NavalArchiveImagePopulator"
)

$endpoints = @(
    @{ Name = "Frontend";       Url = "http://localhost:3000/" },
    @{ Name = "Backend API";    Url = "http://localhost:5000/health" },
    @{ Name = "GoAccount";      Url = "http://localhost:5005/health" },
    @{ Name = "GoPayment";      Url = "http://localhost:5001/health" },
    @{ Name = "Card";           Url = "http://localhost:5002/api/Card/health" },
    @{ Name = "Cart";           Url = "http://localhost:5003/api/Cart/items/healthcheck" },
    @{ Name = "ImagePopulator"; Url = "http://localhost:5099/health" },
    @{ Name = "Gateway";        Url = "http://localhost:5010/health" },
    @{ Name = "Auth";           Url = "http://localhost:5011/health" },
    @{ Name = "User";           Url = "http://localhost:5012/health" },
    @{ Name = "Catalog";        Url = "http://localhost:5013/health" },
    @{ Name = "Inventory";      Url = "http://localhost:5014/health" },
    @{ Name = "Basket";         Url = "http://localhost:5015/health" },
    @{ Name = "Order";          Url = "http://localhost:5016/health" },
    @{ Name = "PaymentChain";   Url = "http://localhost:5017/health" },
    @{ Name = "Shipping";       Url = "http://localhost:5018/health" },
    @{ Name = "Notification";   Url = "http://localhost:5019/health" }
)

$issues = New-Object System.Collections.Generic.List[string]

foreach ($name in $services) {
    $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
    if (-not $svc) {
        $issues.Add("Missing service: $name")
        continue
    }
    if ($svc.Status -ne "Running") {
        try {
            Start-Service -Name $name -ErrorAction Stop
            Start-Sleep -Seconds 2
            $svc.Refresh()
            if ($svc.Status -ne "Running") {
                $issues.Add("Service not running: $name (status: $($svc.Status))")
            }
        } catch {
            $issues.Add("Service start failed: $name ($($_.Exception.Message))")
        }
    }
}

foreach ($taskName in $scheduledTasks) {
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if (-not $task) {
        $issues.Add("Missing scheduled task: $taskName")
        continue
    }
    if ($task.State -notin @("Running", "Ready")) {
        Start-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    }
}

foreach ($ep in $endpoints) {
    try {
        $resp = Invoke-WebRequest -Uri $ep.Url -UseBasicParsing -TimeoutSec 6 -ErrorAction Stop
        if ($resp.StatusCode -lt 200 -or $resp.StatusCode -ge 300) {
            $issues.Add("Endpoint unhealthy: $($ep.Name) ($($ep.Url)) -> $($resp.StatusCode)")
        }
    } catch {
        $issues.Add("Endpoint unreachable: $($ep.Name) ($($ep.Url))")
    }
}

$logPath = "C:\inetpub\health-check.log"
$ts = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
if ($issues.Count -eq 0) {
    Add-Content -Path $logPath -Value "[$ts] OK - all services and endpoints healthy"
} else {
    Add-Content -Path $logPath -Value "[$ts] FAIL - $($issues.Count) issue(s)"
    foreach ($issue in $issues) {
        Add-Content -Path $logPath -Value "  - $issue"
    }
}
