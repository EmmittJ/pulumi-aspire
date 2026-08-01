# 🔍 Spike: Native Aspire Environment Step Adoption

**Status:** ✅ Go (all three environments)
**Aspire version:** 13.4.6 (`Aspire.Hosting.Kubernetes` 13.4.6-preview.1.26319.6)
**Spike code:** `tests/EmmittJ.Aspire.Hosting.Pulumi.NativeAdoptionSpike.Tests` (the suppression mechanism has since been promoted to production as `NativePipelineStepAdoption` + `PulumiStepSuppressionSelector`; the test project remains as the pinned step catalogue and end-to-end adoption proof)

## 🎯 Question

Can we reliably neutralize the deploy/destroy execution steps of Aspire's **native** compute environments
(Azure Container Apps, Kubernetes, Docker Compose) and splice Pulumi-owned pipeline steps into the same
slots — while the native environment's prepare/publish phases still run and fully materialize the
provisioning model we intend to walk?

This gates the adopt-and-traverse architecture: users configure native environments
(`AddAzureContainerAppEnvironment`, etc.), attach Pulumi with a `PublishAsPulumi(...)`-style decorator, and
Pulumi becomes the execution engine over the model the native environment produced.

## ✅ Answer

**Yes, for all three environments.** The spike executes the full Aspire pipeline in-process with every
native execution step suppressed and a stub "Pulumi backend" step spliced into the deploy slot. The stub
observes the complete provisioning model (Bicep templates and `DeploymentTargetAnnotation`s for ACA,
deployment targets for Kubernetes and Docker Compose), and the pipeline completes cleanly.

## 📋 Step catalogue

### Azure Container Apps (`AddAzureContainerAppEnvironment`)

Adding the environment adds two supporting resources: an implicit `AzureEnvironmentResource` (owns
login/provisioning/destroy) and an `AzureContainerRegistryResource` (ACR).

| Step | Owner | DependsOn | RequiredBy | Tags | Classification |
| --- | --- | --- | --- | --- | --- |
| `azure-prepare-resources` | AzureEnvironmentResource | — | `before-start` | — | 🧩 modeling — keep |
| `publish-{azureEnv}` | AzureEnvironmentResource | `publish-prereq` | `publish` | — | 🧩 modeling — keep |
| `validate-azure-login` | AzureEnvironmentResource | `deploy-prereq` | `deploy` | — | 🔥 execution — suppress |
| `create-provisioning-context` | AzureEnvironmentResource | `deploy-prereq`, `validate-azure-login` | `deploy` | — | 🔥 execution — suppress |
| `provision-azure-bicep-resources` | AzureEnvironmentResource | `deploy-prereq`, `create-provisioning-context` | `deploy` | `provision-infra` | 🔥 execution — suppress |
| `destroy-azure-{azureEnv}` | AzureEnvironmentResource | `destroy-prereq` | `destroy` | — | 🔥 execution — suppress |
| `provision-{acr}` | AzureContainerRegistryResource | `create-provisioning-context` | `provision-azure-bicep-resources` | `provision-infra` | 🔥 execution — suppress |
| `login-to-acr-{acr}` | AzureContainerRegistryResource | — | `push-prereq` | `acr-login` | ⚠️ execution — suppress/replace (Pulumi backend must supply registry credentials for push) |
| `provision-{acaEnv}` | AzureContainerAppEnvironmentResource | `create-provisioning-context` | `provision-azure-bicep-resources` | `provision-infra` | 🔥 execution — suppress |
| `prepare-azure-container-apps-{acaEnv}` | AzureContainerAppEnvironmentResource | `azure-prepare-resources`, `validate-compute-environments` | `before-start` | — | 🧩 modeling — keep (creates `DeploymentTargetAnnotation`s) |
| `print-dashboard-url-{acaEnv}` | AzureContainerAppEnvironmentResource | `provision-azure-bicep-resources` | `deploy` | `print-summary` | ℹ️ cosmetic — replace with Pulumi summary |

Suppression selector used: tag `provision-infra`, tag `acr-login`, name prefix `destroy-azure-`, plus
`validate-azure-login` and `create-provisioning-context` by name.

### Kubernetes (`AddKubernetesEnvironment`)

Single environment resource; no supporting resources.

| Step | DependsOn | RequiredBy | Tags | Classification |
| --- | --- | --- | --- | --- |
| `prepare-deployment-targets-{env}` | `validate-compute-environments` | `before-start` | — | 🧩 modeling — keep |
| `publish-{env}` | `publish-prereq` | `publish` | — | 🧩 modeling — keep (writes manifests the walker can also read) |
| `check-helm-prereqs-{env}` | — | — | — | 🔥 execution — suppress (Helm CLI not needed) |
| `prepare-{env}` | `publish`, `build`, `check-helm-prereqs-{env}` | — | — | ⚠️ borderline — prepares Helm values; suppressible (spike suppressed only Helm steps and it ran harmlessly) |
| `helm-deploy-{env}` | `prepare-{env}` | `deploy` | `helm-deploy` | 🔥 execution — suppress |
| `print-{env}-instructions` | `helm-deploy-{env}` | `deploy` | `print-summary` | ℹ️ cosmetic — replace |
| `destroy-helm-{env}` | `destroy-prereq` | `destroy` | — | 🔥 execution — suppress |
| `helm-uninstall-{env}` | `check-helm-prereqs-{env}` | — | `helm-uninstall` | 🔥 execution — suppress |

### Docker Compose (`AddDockerComposeEnvironment`)

| Step | DependsOn | RequiredBy | Tags | Classification |
| --- | --- | --- | --- | --- |
| `prepare-deployment-targets-{env}` | `validate-compute-environments` | `before-start` | — | 🧩 modeling — keep |
| `publish-{env}` | — | `publish` | — | 🧩 modeling — keep |
| `prepare-{env}` | `validate-compute-environments`, `publish`, `build` | — | — | ⚠️ borderline — same note as k8s |
| `docker-compose-up-{env}` | `prepare-{env}` | `deploy` | `docker-compose-up` | 🔥 execution — suppress |
| `destroy-compose-{env}` | `destroy-prereq` | `destroy` | — | 🔥 execution — suppress |
| `docker-compose-down-{env}` | — | — | `docker-compose-down` | 🔥 execution — suppress |

## 🔧 The suppression mechanism

Two API facts drove the design (both verified against 13.4.6):

1. `PipelineStep.Action` is **init-only** — a step's action cannot be mutated after creation.
2. `PipelineConfigurationContext.Steps` is **init-only** — a `PipelineConfigurationAnnotation` callback can
   rewire edges (`GetSteps(...).DependsOn(...)/.RequiredBy(...)`) but cannot remove or replace steps.

Therefore suppression happens **at builder time, before build**, using only public APIs:

- For each adopted native resource, remove its `PipelineStepAnnotation`s and re-add wrappers whose factory
  calls the original factory, then substitutes **same-name clones with no-op actions** for the steps
  matching the suppression selector (see `PipelineSpikeHarness.WrapNativeSteps`).
- Because clones keep the original name, tags, `DependsOnSteps`, and `RequiredBySteps`, the step graph stays
  identical: every other step schedules exactly as before and graph validation is untouched.
- The Pulumi backend's steps are spliced via `builder.Pipeline.AddStep(...)` into the standard slots
  (`dependsOn: push` + `before-start`, `requiredBy: deploy`), mirroring the existing
  `PulumiEnvironmentResource` shape.

### ⚠️ Finding: explicit `before-start` dependency required

The pipeline scheduler runs independent steps concurrently. A spliced deploy step that only depends on
`push` can start **while `prepare-azure-container-apps-*` (which attaches `DeploymentTargetAnnotation`s) is
still running**, observing a half-materialized model. Adding `before-start` to the spliced step's
`DependsOnSteps` fixes the race deterministically. The real backend must do the same (today's
`PulumiEnvironmentResource` deploy step should also carry this edge once it adopts native environments).

## 👁️ Prepare/publish integrity findings

- **ACA**: with all execution steps suppressed, `azure-prepare-resources` and
  `prepare-azure-container-apps-*` still fully materialize the model: the environment and ACR expose
  Bicep via `AzureBicepResource.GetBicepTemplateFile()`, and the compute resource carries a
  `DeploymentTargetAnnotation` whose target (`web-containerapp`, an `AzureProvisioningResource`) has its own
  Bicep with a container-image parameter. **No provisioning context, Azure login, or subscription resolution
  is needed for the model to materialize** — those live entirely in the suppressed steps.
- **Kubernetes / Compose**: `prepare-deployment-targets-*` attaches deployment targets without Helm or a
  Docker daemon; `publish-*` writes artifacts normally.
- **Build/push ordering**: framework build steps (`build-compute` tag) schedule independently of the
  suppressed steps; a spliced step depending on `push` orders correctly after them. In the spike, build/push
  were no-op'd only because the sandbox lacks docker/az — in production they keep running under Aspire.
- **ACR login (`login-to-acr-*`, requiredBy `push-prereq`)**: suppressing it means the Pulumi backend must
  provide registry credentials before Aspire's push step runs. This is the one place the backend must
  *replace* behavior rather than merely skip it (the existing registry pre-stack flow already solves this;
  alternatively provision ACR via a first Pulumi phase, then let the native login logic be re-implemented
  against Pulumi outputs).

## 🛡️ Experimental API surface this depends on

| API | Diagnostic | Used for |
| --- | --- | --- |
| `PipelineStepAnnotation`, `PipelineStep`, `builder.Pipeline.AddStep`, `WellKnownPipelineSteps/Tags` | `ASPIREPIPELINES001` | wrap/suppress/splice |
| `DeploymentTargetAnnotation`, `IComputeEnvironmentResource` | `ASPIRECOMPUTE001/002` | walking targets |
| `AzureBicepResource.GetBicepTemplateFile`, `AzureEnvironmentResource` | `ASPIREAZURE001` (partly) | Azure model extraction |
| Step **names/tags** of native environments | none (strings) | suppression selectors |

The step names/tags are the fragile part: they are undocumented strings. The catalogue tests in the spike
project pin them so an Aspire version bump fails CI with the exact diff instead of silently deploying twice.

## 🚀 Recommendation

**Go.** Proceed with:

1. Generic `PulumiBackendResource` + `PublishAsPulumi(...)` decorator implementing the wrap-at-builder-time
   suppression (promote `WrapNativeSteps` from spike code to production code with per-provider suppression
   selectors).
2. Keep per-provider selectors data-driven (names/tags lists) and covered by the pinned catalogue tests.
3. Include `before-start` in the backend deploy step's dependencies.
4. Resolve the ACR-login/push-credential seam in the Azure frontend design (registry-first phase or
   credential injection).
