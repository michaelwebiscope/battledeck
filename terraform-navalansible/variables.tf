variable "azure_region" {
  description = "Azure region for resources"
  type        = string
  default     = "eastus"
}

variable "resource_group_name" {
  description = "Existing resource group name (leave empty to create one)"
  type        = string
  default     = ""
}

variable "vm_admin_username" {
  description = "Admin username for Windows VM"
  type        = string
  default     = "azureadmin"
  sensitive   = true
}

variable "vm_admin_password" {
  description = "Admin password for Windows VM"
  type        = string
  sensitive   = true
}

variable "vm_size" {
  description = "Azure VM size"
  type        = string
  default     = "Standard_B2s"
}

variable "restrict_to_my_ip" {
  description = "Restrict WinRM/RDP/HTTP access to the IP that runs terraform apply"
  type        = bool
  default     = true
}

variable "allowed_ip" {
  description = "Override: use this IP instead of auto-detection"
  type        = string
  default     = ""
}

variable "use_app_service" {
  description = "Use App Service instead of VM (reserved)"
  type        = bool
  default     = false
}

variable "environment" {
  description = "Environment label (prod, staging, etc.)"
  type        = string
  default     = "prod"
}

variable "project_name" {
  description = "Project name for resource naming"
  type        = string
  default     = "navalansible"
}

variable "github_repo_url" {
  description = "GitHub repo URL for clone/bootstrap"
  type        = string
  default     = ""
}

variable "github_repo_branch" {
  description = "GitHub repo branch"
  type        = string
  default     = "main"
}

variable "github_token" {
  description = "GitHub PAT for private repos"
  type        = string
  default     = ""
  sensitive   = true
}

variable "bootstrap_trigger" {
  description = "Bump to force bootstrap re-run"
  type        = string
  default     = "1"
}

variable "newrelic_license_key" {
  description = "New Relic APM license key (optional)"
  type        = string
  default     = ""
  sensitive   = true
}
