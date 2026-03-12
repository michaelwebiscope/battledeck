# Navalansible — Terraform + Ansible

Terraform provisions Azure infrastructure and installs WinRM. Ansible configures the VM and deploys Naval Archive using **native Ansible tasks** (no monolithic PowerShell script).

## Quick Start

```bash
# 1. Login to Azure
az login

# 2. Create terraform.tfvars (copy from terraform.tfvars.example)
cd terraform-navalansible
cp terraform.tfvars.example terraform.tfvars
# Edit terraform.tfvars: set vm_admin_password

# 3. Install Ansible + Windows support
pip install pywinrm ansible
ansible-galaxy collection install -r ansible/requirements.yml

# 4. Deploy (Terraform + Ansible)
export VM_ADMIN_PASSWORD="YourPassword"  # Same as terraform.tfvars
./scripts/deploy-navalansible.sh
```

## Manual Steps

```bash
# 1. Terraform
cd terraform-navalansible
terraform init
terraform apply -var="vm_admin_password=YourPass"

# 2. Wait ~3 min for WinRM bootstrap

# 3. Ansible
cd ansible
ansible-playbook playbooks/site.yml \
  -e "ansible_host=$(cd ../terraform-navalansible && terraform output -raw vm_public_ip)" \
  -e "vm_admin_password=YourPass"
```

## Layout

- `terraform-navalansible/` — VM, VNet, NSG, storage, WinRM bootstrap
- `ansible/` — Inventory, playbooks, templates
- `scripts/configure-winrm.ps1.tpl` — Minimal bootstrap (runs ConfigureRemotingForAnsible.ps1)
- `scripts/deploy-navalansible.sh` — One-command deploy

## NSG Ports

- 3389 RDP
- 5986 WinRM HTTPS (Ansible)
- 80 HTTP
- 443 HTTPS
