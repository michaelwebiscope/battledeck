# Start all Naval Archive services (run at boot or manually)
# Use: & C:\inetpub\start-all-services.ps1

$ErrorActionPreference = "Continue"
$appcmd = $env:windir + "\System32\inetsrv\appcmd.exe"
if (!(Test-Path $appcmd)) { $appcmd = $env:windir + "\SysNative\inetsrv\appcmd.exe" }

# IIS sites
if (Test-Path $appcmd) {
    & $appcmd start site NavalArchive-API 2>$null
    & $appcmd start site NavalArchive-Web 2>$null
    & $appcmd recycle apppool /apppool.name:NavalArchive-API 2>$null
    foreach ($site in @("NavalArchive-Gateway", "NavalArchive-Auth", "NavalArchive-User", "NavalArchive-Catalog", "NavalArchive-Inventory", "NavalArchive-Basket")) {
        & $appcmd start site $site 2>$null
    }
}

# Windows services
$svcs = @("NavalArchiveWeb", "NavalArchiveImagePopulator", "NavalArchivePayment", "NavalArchiveCard", "NavalArchiveCart",
    "NavalArchiveOrder", "NavalArchivePaymentChain",
    "NavalArchiveShipping", "NavalArchiveNotification")
foreach ($s in $svcs) {
    $svc = Get-Service -Name $s -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne "Running") {
        Start-Service -Name $s -ErrorAction SilentlyContinue
    }
}
