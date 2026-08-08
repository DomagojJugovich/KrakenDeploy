# Step Catalog Plan — KrakenDeploy vs Octopus Built-in Steps

> Version: 1.0. Date: 2026-07-30. Status: Active.

Source of truth for Octopus built-in steps: network export at
`D:\_DOWNLOADS\KrakenDeploy\octopus_json_result_builtin_AND_ALL_tasks.txt`
(55 step types captured from a live Octopus instance).

## Current coverage (8 handlers)

| Step type | Handler | Notes |
|---|---|---|
| `Octopus.TentaclePackage` | `Steps.OctopusTentaclePackage` | Deploy a Package (full Octopus shape) |
| `Octopus.Script` / `Kraken.Script` | `Steps.Script` | Multi-language inline script |
| `Octopus.IIS` / `Kraken.IIS` | `Steps.KrakenIis` | IIS site/app-pool deploy |
| `Octopus.WindowsService` | `Steps.OctopusWindowsService` | Windows service install/config |
| `Octopus.Manual` | `Steps.Manual` | Auto-approve (real pause = WP3) |
| `Octopus.SubstituteVariables` | `Steps.SubstituteVariables` | Octostache in-file substitution |
| `Octopus.JsonConfigurationVariables` | `Steps.JsonConfigurationVariables` | JSON config variable substitution |
| `Octopus.DeployRelease` | `Server.Transport/DeployReleaseStepRunner` | Server-side child deployment |

## Implementation phases

### Batch 1 — Quick wins (current)

| Step | Type string | Size | Notes |
|---|---|---|---|
| Health Check | `Octopus.HealthCheck` | S | HTTP/TCP probe with retries; extracts probe logic from KrakenIis |
| Transfer Package | `Octopus.TransferPackage` | S | Offline drop step; `OfflineDropBundleBuilder` exists server-side |
| Deploy Package UI | `Kraken.DeployPackage` (alias) | S | Simplified schema over existing TentaclePackage handler |

### Batch 2 — Docker

| Step | Type string | Size |
|---|---|---|
| Run a Docker container | `Octopus.DockerRun` | M (shared CLI wrapper) |
| Stop a Docker container | `Octopus.DockerStop` | S |
| Create a Docker network | `Octopus.DockerNetwork` | S |

### Batch 3 — Kubernetes (XL)

`Octopus.KubernetesDeployRawYaml`, `Octopus.KubernetesDeployContainers`,
`Octopus.KubernetesDeployService`, `Octopus.KubernetesDeployIngress`,
`Octopus.KubernetesDeployConfigMap`, `Octopus.KubernetesDeploySecret`,
`Octopus.Kubernetes.Kustomize`, `Octopus.HelmChartUpgrade`,
`Octopus.KubernetesRunScript`

Shared kubectl/helm CLI wrapper + kubeconfig credential model.

### Batch 4 — AWS (L)

`Octopus.AwsUploadS3`, `Octopus.AwsCreateS3`, `Octopus.AwsRunCloudFormation`,
`Octopus.AwsApplyCloudFormationChangeSet`, `Octopus.AwsDeleteCloudFormation`,
`aws-ecs`, `aws-ecs-update-service`, `Octopus.AwsRunScript`

Shared AWS credential model + SDK. S3 example in `examples/` as starting point.

### Batch 5 — Azure (L)

`Octopus.AzureWebApp`, `Octopus.AzureAppService`, `Octopus.AzurePowerShell`,
`Octopus.AzureResourceGroup`, `deploy-a-bicep-template`

Shared Azure credential model + SDK.

### Batch 6 — Java / Tomcat / WildFly (L)

`Octopus.JavaArchive`, `Octopus.TomcatDeploy`, `Octopus.TomcatState`,
`Octopus.TomcatDeployCertificate`, `Octopus.WildFlyDeploy`,
`Octopus.WildFlyState`, `Octopus.WildFlyCertificateDeploy`,
`Octopus.JavaDeployCertificate`

Depends on WP15 (certificates library) for cert-deploy steps.

### Batch 7 — Terraform (M)

`Octopus.TerraformApply`, `Octopus.TerraformPlan`,
`Octopus.TerraformDestroy`, `Octopus.TerraformPlanDestroy`

Shared terraform CLI wrapper.

### Batch 8 — Misc (S–M each)

`Octopus.Email`, `Octopus.Nginx`, `Octopus.Certificate.Import`, `Octopus.Vhd`

Certificate.Import depends on WP15.

### Skipped (niche / not relevant for v1)

`Octopus.AzureServiceFabricApp`, `Octopus.AzureServiceFabricPowerShell`,
`Octopus.GoogleCloudScripting`, `Octopus.ArgoCDUpdateImageTags`,
`Octopus.ArgoCDUpdateManifests`, `Octopus.JiraIntegration.ServiceDeskAction`

## Design decisions

- All new steps follow the step-package model (`steps/KrakenDeploy.Steps.*`).
- Octopus config key names preserved for import compatibility.
- `RequiresPackage = false` for probe/transfer steps that don't need a package.
- Shared CLI wrappers (docker, kubectl, terraform) live in `Steps.Common`.
- Community library steps (600+) remain importable via `StepTemplateCatalogService` — no reimplementation.
