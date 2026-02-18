# Naval Archive — Terraform & Puppet

Automated deployment with **Terraform** (infrastructure) and **Puppet** (configuration). No manual setup.

---

## Quick Start (Azure App Service — Zero Config)

```bash
# 1. Login to Azure
az login

# 2. Create infrastructure
cd terraform
terraform init
terraform plan
terraform apply

# 3. Deploy the app (use output values)
./scripts/deploy.sh $(terraform output -raw resource_group) $(terraform output -raw api_app_name) $(terraform output -raw web_app_name)

# Or on Windows:
# .\scripts\deploy.ps1 -ResourceGroup (terraform output -raw resource_group) -ApiAppName (terraform output -raw api_app_name) -WebAppName (terraform output -raw web_app_name)
```

Your app will be at:
- **Web:** `https://navalarchive-prod-web.azurewebsites.net`
- **API:** `https://navalarchive-prod-api.azurewebsites.net`

---

## Variables

| Variable | Default | Description |
|----------|---------|--------------|
| `azure_region` | eastus | Azure region |
| `resource_group_name` | "" | **Existing** resource group (required if you can't create RGs) |
| `environment` | prod | Environment name |
| `project_name` | navalarchive | Resource name prefix |
| `use_app_service` | true | Use App Service (true) or VM (false) |
| `vm_auto_bootstrap` | true | Run setup script on VM first boot |
| `github_repo_url` | "" | GitHub repo for full deploy (clone, build, deploy). Empty = bootstrap only |
| `github_repo_branch` | main | Branch to clone |

**Can't create resource groups?** Set `resource_group_name = "your-existing-rg"` and `use_app_service = false` to deploy a VM into an existing RG.

For VM deployment, also set `vm_admin_username`, `vm_admin_password`, `vm_size`.

---

## VM + Auto-Bootstrap (Windows Server)

When `use_app_service = false`, Terraform creates a Windows VM and **automatically runs the setup script** on first boot (via Azure Custom Script Extension). No RDP or manual steps required.

### Fully automated (recommended)

1. Set `github_repo_url` in `terraform.tfvars` to your public repo:
   ```hcl
   github_repo_url   = "https://github.com/your-org/battledeck"
   github_repo_branch = "main"
   ```

2. Run Terraform:
   ```bash
   terraform init && terraform apply
   ```

3. Wait ~15–20 minutes for the VM to boot and the script to complete (Chocolatey, .NET, Node, IIS, clone, build, deploy).

4. Open `http://<vm_public_ip>` — the app should be live.

### Bootstrap only

Leave `github_repo_url` empty. The script installs IIS, .NET 8, Node.js, and creates directories. Deploy the app via your CI/CD pipeline.

---

## File Layout

```
terraform/
  main.tf      # App Service resources
  vm.tf        # VM resources (when use_app_service=false)
  variables.tf
  outputs.tf

puppet/
  manifests/
    site.pp
    navalarchive.pp

scripts/
  setup-vm.ps1.tpl   # Single bootstrap script (Bootstrap or Fix mode)
  deploy.ps1         # Deploy to App Service (Windows)
  deploy.sh          # Deploy to App Service (Mac/Linux)
```
