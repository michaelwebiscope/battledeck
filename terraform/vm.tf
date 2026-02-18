# --- VM-based deployment (when use_app_service = false) ---

data "azurerm_client_config" "current" {}

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
    newrelic_license_key  = var.newrelic_license_key
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

  security_rule {
    name                       = "HTTP"
    priority                   = 100
    direction                  = "Inbound"
    access                     = "Allow"
    protocol                   = "Tcp"
    source_port_range          = "*"
    destination_port_range    = "80"
    source_address_prefix      = "*"
    destination_address_prefix = "*"
  }

  security_rule {
    name                       = "HTTPS"
    priority                   = 110
    direction                  = "Inbound"
    access                     = "Allow"
    protocol                   = "Tcp"
    source_port_range          = "*"
    destination_port_range    = "443"
    source_address_prefix      = "*"
    destination_address_prefix = "*"
  }

  security_rule {
    name                       = "API"
    priority                   = 115
    direction                  = "Inbound"
    access                     = "Allow"
    protocol                   = "Tcp"
    source_port_range          = "*"
    destination_port_range    = "5000"
    source_address_prefix      = "*"
    destination_address_prefix = "*"
  }

  security_rule {
    name                       = "RDP"
    priority                   = 120
    direction                  = "Inbound"
    access                     = "Allow"
    protocol                   = "Tcp"
    source_port_range          = "*"
    destination_port_range    = "3389"
    source_address_prefix      = "*"
    destination_address_prefix = "*"
  }
}

resource "azurerm_windows_virtual_machine" "main" {
  count               = var.use_app_service ? 0 : 1
  name                = "${local.name_prefix}-vm"
  computer_name       = "navalarchive-vm"
  resource_group_name = local.rg_name
  location            = var.azure_region
  size                = var.vm_size
  admin_username      = var.vm_admin_username
  admin_password      = var.vm_admin_password

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
resource "azurerm_virtual_machine_extension" "bootstrap" {
  count                = var.use_app_service ? 0 : (var.vm_auto_bootstrap && length(azurerm_storage_blob.setup_script) > 0 ? 1 : 0)
  name                 = "NavalArchiveBootstrap"
  virtual_machine_id    = azurerm_windows_virtual_machine.main[0].id
  publisher            = "Microsoft.Compute"
  type                 = "CustomScriptExtension"
  type_handler_version  = "1.10"

  settings = jsonencode({
    fileUris         = ["https://${azurerm_storage_account.bootstrap[0].name}.blob.core.windows.net/${azurerm_storage_container.scripts[0].name}/${azurerm_storage_blob.setup_script[0].name}"]
    commandToExecute = "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe -ExecutionPolicy Bypass -NoProfile -File ${azurerm_storage_blob.setup_script[0].name}"
    timestamp        = timestamp()
  })

  protected_settings = jsonencode({
    storageAccountName = azurerm_storage_account.bootstrap[0].name
    storageAccountKey  = azurerm_storage_account.bootstrap[0].primary_access_key
  })
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
