# --- VM-based deployment (when use_app_service = false) ---

data "azurerm_client_config" "current" {}

# Detect IPv4 of machine running terraform (Azure NSG requires IPv4, not IPv6)
data "http" "my_ip" {
  count = var.use_app_service ? 0 : 1
  url   = "https://api.ipify.org"
  request_headers = {
    Accept = "text/plain"
  }
}

locals {
  # Source IP for NSG: your IPv4 when restrict_to_my_ip, else allow all. Azure NSG requires IPv4 (no IPv6).
  _detected_ip = trimspace(data.http.my_ip[0].response_body)
  _use_ip      = var.allowed_ip != "" ? var.allowed_ip : (can(regex("^[0-9.]+$", local._detected_ip)) ? local._detected_ip : null)
  vm_source_prefix = var.use_app_service ? "*" : (
    var.restrict_to_my_ip && local._use_ip != null ? "${local._use_ip}/32" : "*"
  )
}

# Storage account for bootstrap script (self-contained, no external hosting needed)
resource "azurerm_storage_account" "bootstrap" {
  count                    = var.use_app_service ? 0 : (var.vm_auto_bootstrap ? 1 : 0)
  name                     = "navalarch${substr(md5("${local.rg_name}-${local.name_prefix}"), 0, 6)}"
  resource_group_name       = local.rg_name
  location                  = var.azure_region
  account_tier              = "Standard"
  account_replication_type  = "LRS"
  min_tls_version           = "TLS1_2"
  allow_nested_items_to_be_public = false
}

resource "azurerm_storage_container" "scripts" {
  count                 = length(azurerm_storage_account.bootstrap) > 0 ? 1 : 0
  name                  = "scripts"
  storage_account_name  = azurerm_storage_account.bootstrap[0].name
  container_access_type = "private"
}

resource "azurerm_storage_blob" "setup_script" {
  count                  = length(azurerm_storage_container.scripts) > 0 ? 1 : 0
  name                   = "setup-vm.ps1"
  storage_account_name   = azurerm_storage_account.bootstrap[0].name
  storage_container_name = azurerm_storage_container.scripts[0].name
  type                   = "Block"
  source_content         = templatefile("${path.module}/../scripts/setup-vm.ps1.tpl", {
    repo_url             = var.github_repo_url
    repo_branch           = var.github_repo_branch
    repo_token            = var.github_token
    bootstrap_trigger     = var.bootstrap_trigger
  })
}

resource "azurerm_virtual_network" "main" {
  count               = var.use_app_service ? 0 : 1
  name                = "${local.name_prefix}-vnet"
  address_space       = ["10.0.0.0/16"]
  location            = var.azure_region
  resource_group_name = local.rg_name
}

resource "azurerm_subnet" "main" {
  count                = var.use_app_service ? 0 : 1
  name                 = "internal"
  resource_group_name  = local.rg_name
  virtual_network_name = azurerm_virtual_network.main[0].name
  address_prefixes     = ["10.0.1.0/24"]
}

resource "azurerm_subnet_network_security_group_association" "main" {
  count                     = var.use_app_service ? 0 : 1
  subnet_id                 = azurerm_subnet.main[0].id
  network_security_group_id = azurerm_network_security_group.main[0].id
}

resource "azurerm_public_ip" "main" {
  count               = var.use_app_service ? 0 : 1
  name                = "${local.name_prefix}-pip"
  location            = var.azure_region
  resource_group_name = local.rg_name
  allocation_method   = "Static"
}

resource "azurerm_network_interface" "main" {
  count               = var.use_app_service ? 0 : 1
  name                = "${local.name_prefix}-nic"
  location            = var.azure_region
  resource_group_name = local.rg_name

  ip_configuration {
    name                          = "internal"
    subnet_id                     = azurerm_subnet.main[0].id
    private_ip_address_allocation = "Dynamic"
    public_ip_address_id          = azurerm_public_ip.main[0].id
  }
}

resource "azurerm_network_security_group" "main" {
  count               = var.use_app_service ? 0 : 1
  name                = "${local.name_prefix}-nsg"
  location            = var.azure_region
  resource_group_name = local.rg_name

  # Only RDP, HTTP, HTTPS. All other ports closed (API, Payment, Card, Cart, etc. are internal).
  security_rule {
    name                       = "AllowRDP"
    priority                   = 100
    direction                  = "Inbound"
    access                     = "Allow"
    protocol                   = "Tcp"
    source_port_range          = "*"
    destination_port_range     = "3389"
    source_address_prefix      = local.vm_source_prefix
    destination_address_prefix = "*"
  }
  security_rule {
    name                       = "AllowHTTP"
    priority                   = 110
    direction                  = "Inbound"
    access                     = "Allow"
    protocol                   = "Tcp"
    source_port_range          = "*"
    destination_port_range     = "80"
    source_address_prefix      = local.vm_source_prefix
    destination_address_prefix = "*"
  }
  security_rule {
    name                       = "AllowHTTPS"
    priority                   = 120
    direction                  = "Inbound"
    access                     = "Allow"
    protocol                   = "Tcp"
    source_port_range          = "*"
    destination_port_range     = "443"
    source_address_prefix      = local.vm_source_prefix
    destination_address_prefix = "*"
  }
}

resource "azurerm_windows_virtual_machine" "main" {
  count                            = var.use_app_service ? 0 : 1
  name                             = "${local.name_prefix}-vm"
  computer_name                    = "navalarchive-vm"
  resource_group_name              = local.rg_name
  location                         = var.azure_region
  size                             = var.vm_size
  admin_username                   = var.vm_admin_username
  admin_password                   = var.vm_admin_password
  vm_agent_platform_updates_enabled = true

  network_interface_ids = [
    azurerm_network_interface.main[0].id
  ]

  os_disk {
    caching              = "ReadWrite"
    storage_account_type = "Standard_LRS"
  }

  source_image_reference {
    publisher = "MicrosoftWindowsServer"
    offer     = "WindowsServer"
    sku       = "2019-Datacenter"
    version   = "latest"
  }
}

# Custom Script Extension: runs setup script on first boot (bootstrap + optional deploy)
# lifecycle: don't re-run on every apply (bootstrap takes 45-60 min). Use refresh-web-az.sh for quick updates.
resource "azurerm_virtual_machine_extension" "bootstrap" {
  count                = var.use_app_service ? 0 : (var.vm_auto_bootstrap && length(azurerm_storage_blob.setup_script) > 0 ? 1 : 0)
  name                 = "NavalArchiveBootstrap"
  virtual_machine_id    = azurerm_windows_virtual_machine.main[0].id
  publisher            = "Microsoft.Compute"
  type                 = "CustomScriptExtension"
  type_handler_version  = "1.10"

  lifecycle {
    ignore_changes = [settings]
  }

  settings = jsonencode({
    fileUris         = ["https://${azurerm_storage_account.bootstrap[0].name}.blob.core.windows.net/${azurerm_storage_container.scripts[0].name}/${azurerm_storage_blob.setup_script[0].name}"]
    commandToExecute = "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe -ExecutionPolicy Bypass -NoProfile -File ${azurerm_storage_blob.setup_script[0].name}"
    timestamp        = var.bootstrap_trigger
  })

  protected_settings = jsonencode({
    storageAccountName = azurerm_storage_account.bootstrap[0].name
    storageAccountKey  = azurerm_storage_account.bootstrap[0].primary_access_key
  })
}

# Website refresh - runs on every terraform apply -auto-approve (~5-7 min)
# Pushes NavalArchive.Web to GitHub first so VM gets latest code
# Waits for bootstrap to finish so IIS app pool/site exist before refresh
resource "null_resource" "refresh_web" {
  count = var.use_app_service ? 0 : 1

  depends_on = [azurerm_virtual_machine_extension.bootstrap]

  triggers = {
    run = timestamp()
  }

  provisioner "local-exec" {
    command     = "cd ${path.module}/.. && git add NavalArchive.Web NavalArchive.Api NavalArchive.ImagePopulator scripts/refresh-web.ps1 scripts/populate-images.js .github/workflows/populate-images.yml && (git diff --staged --quiet || git commit -m 'deploy: sync web and API to VM') && git push"
    interpreter = ["bash", "-c"]
  }

  provisioner "local-exec" {
    command = "az vm run-command invoke --resource-group ${local.rg_name} --name ${azurerm_windows_virtual_machine.main[0].name} --command-id RunPowerShellScript --scripts \"Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/michaelwebiscope/battledeck/main/scripts/refresh-web.ps1?t=$RANDOM' -OutFile 'C:\\Windows\\Temp\\refresh-web.ps1' -UseBasicParsing -Headers @{ 'Cache-Control'='no-cache' }; powershell -ExecutionPolicy Bypass -File 'C:\\Windows\\Temp\\refresh-web.ps1' -RepoUrl '${replace(var.github_repo_url, ".git", "")}' -RepoBranch '${var.github_repo_branch}'\""
    interpreter = ["bash", "-c"]
  }
}

# VM output for Puppet/bootstrap (only when VM is created)
output "vm_name" {
  description = "VM name (for az vm run-command)"
  value       = length(azurerm_windows_virtual_machine.main) > 0 ? azurerm_windows_virtual_machine.main[0].name : null
}

output "vm_public_ip" {
  description = "VM public IP (for RDP and Puppet apply)"
  value       = length(azurerm_public_ip.main) > 0 ? azurerm_public_ip.main[0].ip_address : null
}

output "allowed_source_ip" {
  description = "IP allowed to access the website (when restrict_to_my_ip = true)"
  value       = var.use_app_service ? null : local.vm_source_prefix
}
