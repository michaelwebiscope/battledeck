# Navalansible VM - Terraform provisions infra + minimal WinRM bootstrap for Ansible

data "azurerm_client_config" "current" {}

data "http" "my_ip" {
  url = "https://api.ipify.org"
  request_headers = {
    Accept = "text/plain"
  }
}

locals {
  _detected_ip   = trimspace(data.http.my_ip.response_body)
  _use_ip        = var.allowed_ip != "" ? var.allowed_ip : (can(regex("^[0-9.]+$", local._detected_ip)) ? local._detected_ip : null)
  source_prefix  = var.restrict_to_my_ip && local._use_ip != null ? "${local._use_ip}/32" : "*"
}

resource "azurerm_resource_group" "main" {
  count    = var.resource_group_name == "" ? 1 : 0
  name     = "${local.name_prefix}-rg"
  location = var.azure_region
}

# Storage for bootstrap script
resource "azurerm_storage_account" "bootstrap" {
  name                     = "navalansible${substr(md5(local.rg_name), 0, 6)}"
  resource_group_name       = local.rg_name
  location                  = var.azure_region
  account_tier              = "Standard"
  account_replication_type  = "LRS"
  min_tls_version           = "TLS1_2"
  allow_nested_items_to_be_public = false
}

resource "azurerm_storage_container" "scripts" {
  name                  = "scripts"
  storage_account_name   = azurerm_storage_account.bootstrap.name
  container_access_type = "private"
}

resource "azurerm_storage_blob" "winrm_script" {
  name                   = "ConfigureWinRM.ps1"
  storage_account_name    = azurerm_storage_account.bootstrap.name
  storage_container_name  = azurerm_storage_container.scripts.name
  type                   = "Block"
  source_content         = templatefile("${path.module}/../scripts/configure-winrm.ps1.tpl", {})
}

resource "azurerm_virtual_network" "main" {
  name                = "${local.name_prefix}-vnet"
  address_space       = ["10.0.0.0/16"]
  location            = var.azure_region
  resource_group_name = local.rg_name
}

resource "azurerm_subnet" "main" {
  name                 = "internal"
  resource_group_name   = local.rg_name
  virtual_network_name  = azurerm_virtual_network.main.name
  address_prefixes     = ["10.0.1.0/24"]
}

resource "azurerm_public_ip" "main" {
  name                = "${local.name_prefix}-pip"
  location            = var.azure_region
  resource_group_name = local.rg_name
  allocation_method   = "Static"
}

resource "azurerm_network_security_group" "main" {
  name                = "${local.name_prefix}-nsg"
  location            = var.azure_region
  resource_group_name = local.rg_name

  security_rule {
    name                       = "AllowRDP"
    priority                   = 100
    direction                  = "Inbound"
    access                     = "Allow"
    protocol                   = "Tcp"
    source_port_range          = "*"
    destination_port_range     = "3389"
    source_address_prefix      = local.source_prefix
    destination_address_prefix = "*"
  }
  security_rule {
    name                       = "AllowWinRM"
    priority                   = 105
    direction                  = "Inbound"
    access                     = "Allow"
    protocol                   = "Tcp"
    source_port_range          = "*"
    destination_port_range     = "5986"
    source_address_prefix      = local.source_prefix
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
    source_address_prefix      = local.source_prefix
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
    source_address_prefix      = local.source_prefix
    destination_address_prefix = "*"
  }
}

resource "azurerm_subnet_network_security_group_association" "main" {
  subnet_id                 = azurerm_subnet.main.id
  network_security_group_id = azurerm_network_security_group.main.id
}

resource "azurerm_network_interface" "main" {
  name                = "${local.name_prefix}-nic"
  location            = var.azure_region
  resource_group_name = local.rg_name

  ip_configuration {
    name                          = "internal"
    subnet_id                     = azurerm_subnet.main.id
    private_ip_address_allocation = "Dynamic"
    public_ip_address_id          = azurerm_public_ip.main.id
  }
}

resource "azurerm_windows_virtual_machine" "main" {
  name                             = "${local.name_prefix}-vm"
  computer_name                    = "navalansible-vm"
  resource_group_name              = local.rg_name
  location                         = var.azure_region
  size                             = var.vm_size
  admin_username                   = var.vm_admin_username
  admin_password                   = var.vm_admin_password
  vm_agent_platform_updates_enabled = true

  network_interface_ids = [azurerm_network_interface.main.id]

  os_disk {
    caching              = "ReadWrite"
    storage_account_type  = "Standard_LRS"
  }

  source_image_reference {
    publisher = "MicrosoftWindowsServer"
    offer     = "WindowsServer"
    sku       = "2019-Datacenter"
    version   = "latest"
  }
}

# Minimal bootstrap: configure WinRM so Ansible can connect
resource "azurerm_virtual_machine_extension" "winrm" {
  name                 = "ConfigureWinRM"
  virtual_machine_id   = azurerm_windows_virtual_machine.main.id
  publisher            = "Microsoft.Compute"
  type                 = "CustomScriptExtension"
  type_handler_version = "1.10"

  lifecycle {
    ignore_changes = [settings]
  }

  settings = jsonencode({
    fileUris         = ["https://${azurerm_storage_account.bootstrap.name}.blob.core.windows.net/${azurerm_storage_container.scripts.name}/${azurerm_storage_blob.winrm_script.name}"]
    commandToExecute = "powershell -ExecutionPolicy Bypass -NoProfile -File ${azurerm_storage_blob.winrm_script.name}"
  })

  protected_settings = jsonencode({
    storageAccountName = azurerm_storage_account.bootstrap.name
    storageAccountKey  = azurerm_storage_account.bootstrap.primary_access_key
  })
}
