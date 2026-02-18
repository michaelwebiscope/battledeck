variable "azure_region" {
  description = "Azure region for resources"
  type        = string
  default     = "eastus"
}

variable "resource_group_name" {
  description = "Existing resource group name (leave empty to create one - requires permissions)"
  type        = string
  default     = ""
}

variable "environment" {
  description = "Environment name (e.g. dev, staging, prod)"
  type        = string
  default     = "prod"
}

variable "project_name" {
  description = "Project name used in resource naming"
  type        = string
  default     = "navalarchive"
}

# --- App Service (managed) ---
variable "use_app_service" {
  description = "Use Azure App Service instead of VM (recommended for zero-config)"
  type        = bool
  default     = true
}

# --- VM (when use_app_service = false) ---
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

# --- VM auto-bootstrap (runs setup script on first boot) ---
variable "vm_auto_bootstrap" {
  description = "Run bootstrap/setup script automatically on VM creation"
  type        = bool
  default     = true
}

variable "github_repo_url" {
  description = "GitHub repo URL for full deploy (e.g. https://github.com/user/battledeck). If empty, only bootstrap runs (IIS, .NET, Node installed)."
  type        = string
  default     = ""
}

variable "github_repo_branch" {
  description = "Branch to clone when deploying from GitHub"
  type        = string
  default     = "main"
}

variable "github_token" {
  description = "GitHub personal access token (required for private repos). Create at https://github.com/settings/tokens"
  type        = string
  default     = ""
  sensitive   = true
}

variable "newrelic_license_key" {
  description = "New Relic license key for Node.js APM (leave empty to skip New Relic)"
  type        = string
  default     = ""
  sensitive   = true
}
