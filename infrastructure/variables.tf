variable "environment" {
  description = "Environment name (e.g., dev, sbx, prd)"
  type        = string
}

variable "location" {
  description = "Azure region for all resources"
  type        = string
  default     = "swedencentral"
}

variable "project" {
  description = "Project name"
  type        = string
}

variable "ai_capacity" {
  type        = number
  description = "TPM capacity for the model deployment (in thousands of tokens per minute)."
  default     = 30
}

variable "ai_foundry_project_managers" {
  type        = list(string)
  description = "Entra ID object IDs to grant the built-in 'Foundry Project Manager' role on the AI Foundry project."
  default     = []
}

variable "agent_invoker_principal_ids" {
  type        = list(string)
  description = "Entra ID object IDs (e.g. the GitHub Actions identity used by the issue triage trigger) to grant the built-in 'Foundry User' role on the AI Foundry project, so they can invoke hosted agents."
  default     = []
}

variable "key_vault_secrets_readers" {
  type        = list(string)
  description = "Entra ID object IDs (e.g. the hosted agent's identity) to grant 'Key Vault Secrets User' on the agent Key Vault."
  default     = []
}
