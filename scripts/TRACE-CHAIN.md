# Distributed Trace Chain (10 Microservices)

## Chain

```
Gateway (5010) → Auth (5011) → User (5012) → Catalog (5013) → Inventory (5014)
  → Basket (5015) → Order (5016) → Payment (5017) → Shipping (5018) → Notification (5019)
```

## Trigger a distributed trace

```bash
curl http://localhost:5010/trace
```

Each service calls the next via HTTP. With New Relic machine-level agent + `NEW_RELIC_APP_NAME` in registry per service, you get a 10-span distributed trace.

## Services

| Service    | Port | Role              |
|-----------|------|-------------------|
| Gateway   | 5010 | API entry point   |
| Auth      | 5011 | Token validation  |
| User      | 5012 | User profile      |
| Catalog   | 5013 | Product catalog   |
| Inventory | 5014 | Stock levels      |
| Basket    | 5015 | Cart operations   |
| Order     | 5016 | Order creation    |
| Payment   | 5017 | Payment processing|
| Shipping  | 5018 | Shipping quotes   |
| Notification | 5019 | Send notifications |

## New Relic registry (per service)

```powershell
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services"
$services = @("NavalArchiveGateway","NavalArchiveAuth","NavalArchiveUser","NavalArchiveCatalog","NavalArchiveInventory","NavalArchiveBasket","NavalArchiveOrder","NavalArchivePaymentChain","NavalArchiveShipping","NavalArchiveNotification")
foreach ($svc in $services) {
    Set-ItemProperty -Path "$regPath\$svc" -Name "Environment" -Value @("NEW_RELIC_APP_NAME=$svc") -Type MultiString -Force
    Restart-Service $svc -Force
}
```
