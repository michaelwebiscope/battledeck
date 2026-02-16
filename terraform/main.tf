terraform {
  required_version = ">= 1.0"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 3.0"
    }
  }
}

provider "azurerm" {
  features {}
}

locals {
  name_prefix = "${var.project_name}-${var.environment}"
}

# --- Resource Group (only if not using existing) ---
resource "azurerm_resource_group" "main" {
  count    = var.resource_group_name == "" ? 1 : 0
  name     = "${local.name_prefix}-rg"
  location = var.azure_region
}

data "azurerm_resource_group" "main" {
  count = var.resource_group_name != "" ? 1 : 0
  name  = var.resource_group_name
}

locals {
  rg_id       = var.resource_group_name != "" ? data.azurerm_resource_group.main[0].id : azurerm_resource_group.main[0].id
  rg_name     = var.resource_group_name != "" ? data.azurerm_resource_group.main[0].name : azurerm_resource_group.main[0].name
  rg_location = var.resource_group_name != "" ? data.azurerm_resource_group.main[0].location : azurerm_resource_group.main[0].location
}

# --- App Service Plan ---
resource "azurerm_service_plan" "main" {
  count               = var.use_app_service ? 1 : 0
  name                = "${local.name_prefix}-plan"
  resource_group_name = local.rg_name
  location            = local.rg_location
  os_type             = "Windows"
  sku_name            = "B1"
}

# --- API App Service ---
resource "azurerm_windows_web_app" "api" {
  count               = var.use_app_service ? 1 : 0
  name                = "${local.name_prefix}-api"
  resource_group_name = local.rg_name
  location            = local.rg_location
  service_plan_id     = azurerm_service_plan.main[0].id

  site_config {
    application_stack {
      dotnet_version = "v8.0"
    }
    always_on = true
  }

  app_settings = {
    "ASPNETCORE_ENVIRONMENT" = "Production"
  }
}

# --- Web App Service (Node.js) ---
resource "azurerm_windows_web_app" "web" {
  count               = var.use_app_service ? 1 : 0
  name                = "${local.name_prefix}-web"
  resource_group_name = local.rg_name
  location            = local.rg_location
  service_plan_id     = azurerm_service_plan.main[0].id

  site_config {
    application_stack {
      node_version = "~18"
    }
    always_on = true
  }

  app_settings = {
    "API_URL" = "https://${azurerm_windows_web_app.api[0].default_hostname}"
    "PORT"    = "8080"
    "WEBSITE_NODE_DEFAULT_VERSION" = "~18"
  }
}
