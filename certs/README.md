# Cloudflare Origin Certificate

Place your Cloudflare Origin certificate files here for HTTPS between Cloudflare and your origin server.

## Files

| File | Description |
|------|-------------|
| `cloudflare-origin.pem` | Origin certificate (from Cloudflare Dashboard → SSL/TLS → Origin Server) |
| `cloudflare-origin-key.pem` | Private key (generated when you create the Origin cert) |

## Setup

1. In Cloudflare Dashboard: **SSL/TLS** → **Origin Server** → **Create Certificate**
2. Download or copy the certificate and private key
3. Save them as `cloudflare-origin.pem` and `cloudflare-origin-key.pem` in this folder

## For IIS (Windows VM)

IIS uses PFX format. Convert PEM to PFX:

```bash
openssl pkcs12 -export -out cloudflare-origin.pfx \
  -inkey cloudflare-origin-key.pem \
  -in cloudflare-origin.pem \
  -passout pass:
```

Then import the PFX into the Windows certificate store and bind it to your IIS site. The setup script currently uses a self-signed cert; you can replace it with the Cloudflare Origin cert for proper validation when proxied through Cloudflare.

## Security

- `cloudflare-origin.pem`, `cloudflare-origin-key.pem`, and `*.pfx` are in `.gitignore` — never commit real certificates.
