terraform {
  required_version = ">= 1.0"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 3.0"
    }
    http = {
      source  = "hashicorp/http"
      version = "~> 3.0"
    }
  }
}

provider "azurerm" {
  features {}
}

data "azurerm_resource_group" "main" {
  count = var.resource_group_name != "" ? 1 : 0
  name  = var.resource_group_name
}

locals {
  name_prefix = "navalansible"
  rg_name     = var.resource_group_name != "" ? data.azurerm_resource_group.main[0].name : azurerm_resource_group.main[0].name
}
