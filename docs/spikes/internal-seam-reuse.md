# 🔍 Spike: Internal-Seam Reuse of the Default Azure Environments

**Status:** 🚀 Promoted to production — this is the shipped architecture (`EmmittJ.Aspire.Hosting.Pulumi.Azure.Seams` + `UsePulumiProvisioning`); the caveats below were accepted deliberately. See [ARCHITECTURE.md](../ARCHITECTURE.md).
**Aspire version:** 13.4.6
**Spike code:** `tests/EmmittJ.Aspire.Hosting.Pulumi.InternalSeamSpike.Tests` (retained as the pinned end-to-end proof)

## 🎯 Question

How little code can we get away with while reusing as much of the default Aspire Azure environments as
possible? Specifically: instead of suppressing the native execution steps and splicing Pulumi-owned steps
into the pipeline (the adopt-and-traverse architecture proven in
[native-environment-step-adoption](native-environment-step-adoption.md)), can the default Azure Container
Apps environment run **completely unmodified** — every native step executing — with Pulumi intercepting at
a lower boundary?

This is a boundary-pushing experiment. The internal classes involved haven't shipped as public API, and we
are deliberately **not** filing an upstream ask to open them up — the goal is to see what's possible today.

## ✅ Answer

**Yes.** The entire "integration" collapses to **six `Services.Replace` calls** plus one Pulumi-backed
implementation of Aspire's internal `IBicepProvisioner` interface. The spike executes the native ACA
pipeline in-process — prepare, publish wiring, login validation, provisioning-context creation,
per-resource provision steps, ACR login, deployment-target provisioning — with **zero steps suppressed**,
fully offline. Every Bicep template the native environment materializes (the ACA environment, its ACR, and
the per-compute-resource deployment target) flows through the swapped provisioner with parameters already
resolved by Aspire's own internal `BicepUtilities`.

No reflection is involved, and no upstream changes are needed: compile-time access to the internals comes
from an `InternalsVisibleTo` grant Aspire already ships.

## 🔑 The access mechanism: IVT + public signing

`Aspire.Hosting.Azure` grants `InternalsVisibleTo` to `Aspire.Hosting.Azure.Tests` (and to
`Aspire.Hosting.Azure.ContainerRegistry` and `Aspire.Hosting.Foundry`) with Aspire's OSS public key. The
spike assembly claims that identity:

- `<AssemblyName>Aspire.Hosting.Azure.Tests</AssemblyName>`
- `<SignAssembly>true</SignAssembly>` + `<PublicSign>true</PublicSign>` against `aspire-public.snk`, a
  key blob containing **only the public key** extracted from the IVT attribute's hex string (no private
  key exists in this repo — CoreCLR skips strong-name signature validation, so public signing satisfies
  the IVT identity check).

Result: full compile-time access to `Aspire.Hosting.Azure` internals. Note the boundary: only
`Aspire.Hosting.Azure` grants IVT — `Aspire.Hosting` and `Aspire.Hosting.Azure.AppContainers` internals
remain inaccessible (and were not needed).

## 📋 Seam catalogue

`AzureProvisionerExtensions.AddAzureProvisioning` registers every provisioning service with
`TryAddSingleton`, so all of them are cleanly replaceable via `Services.Replace`:

| Seam | Accessibility | Native role | Spike replacement |
| --- | --- | --- | --- |
| `ITokenCredentialProvider` | public | Azure credential chain | static fake credential |
| `IUserPrincipalProvider` | internal | resolves deploying principal from token | fixed `UserPrincipal` |
| `IArmClientProvider` | internal | ARM SDK facade (subscription/tenant/RG graph) | in-memory ARM graph |
| `IAcrLoginService` | internal | `docker login` to ACR | recording fake |
| `IBicepProvisioner` | internal | **deploys a template to ARM** | **the Pulumi seam** |
| `IBicepCompiler` | internal | shells out to `bicep` CLI | no-op (Pulumi consumes the model, not ARM JSON) |
| `IProvisioningContextProvider` | internal | builds `ProvisioningContext` | native impl reused as-is |
| `ISecretClientProvider` | internal | Key Vault secret clients | not needed for ACA |

The critical seam is `IBicepProvisioner`: `AzureBicepResource.ProvisionAzureBicepResourceAsync` (the
action behind every native `provision-{name}` step, including deployment targets) resolves it from DI at
execution time. Swapping it makes Pulumi the execution engine for **every** template the native
environment produces, while the native steps keep their ordering, reporting UI, and state handling.

## 📝 Findings

### 1. The provisioner contract is small and self-contained

`IBicepProvisioner` has two methods: `ConfigureResourceAsync` (return `false` to always provision — this
is where Pulumi's own state/checksum story replaces Aspire's deployment-state reuse) and
`GetOrCreateResourceAsync(resource, provisioningContext, ct)`. To satisfy the downstream pipeline the
implementation must honor one contract: **back-propagate outputs into `resource.Outputs`** so later
templates' `BicepOutputReference` parameters resolve. The spike substitutes deterministic placeholders;
production substitutes Pulumi stack outputs.

### 2. Aspire's own parameter resolution becomes reusable

The internal static `BicepUtilities.SetParametersAsync` / `SetScopeAsync` — Aspire's own
`ReferenceExpression`/`BicepOutputReference`/`ParameterResource` resolution — is directly callable from
the seam. The production integration currently re-implements this logic in `PulumiValueResolver`; the
seam approach deletes that duplication entirely.

### 3. Outputs must be ARM-resource-id shaped

Native follow-up steps parse certain outputs with `Azure.Core.ResourceIdentifier` (e.g. the ACA
portal-link step reads `.SubscriptionId` off `AZURE_CONTAINER_APPS_ENVIRONMENT_ID`). Placeholder or real
outputs for `*_ID` values must be valid ARM resource IDs
(`/subscriptions/{sub}/resourceGroups/{rg}/providers/...`).

### 4. Pipeline phasing matters — and two native steps are not idempotent

Deployment targets don't exist until the before-start phase's `prepare-azure-container-apps-*` step runs,
but step factories are evaluated when the pipeline resolves. The real `aspire deploy` phases execution
(before-start runs to completion on a cloned pipeline first). The in-process harness mirrors this with
three filtered executions via `PipelineOptions.Step`: `before-start` → `push` → `deploy`. Running the
full DAG twice instead crashes: `PrepareDeploymentTargetsAsync` duplicates `DeploymentTargetAnnotation`s
(and `GetDeploymentTargetAnnotation` throws on duplicates), and the publish step's
`WriteAzureArtifactsOutputAsync` re-adds bicep parameters. Any host that drives the pipeline in-process
must respect this phasing; the CLI already does.

### 5. What the spike proves end-to-end

With the six seams swapped and nothing suppressed, the offline pipeline run:

- creates the resource group through the fake ARM graph (`create-provisioning-context` ran for real);
- provisions `acaenv`, `acaenv-acr`, and `web-containerapp` through the Pulumi seam, with the
  deployment target's parameters resolved from the environment outputs the seam back-propagated;
- executes the native `login-to-acr-*` step against the swapped `IAcrLoginService` using the registry
  endpoint output the seam produced.

## 💡 Recommendation

**This recommendation has been carried out**: the seam approach replaced the adopt-and-traverse
architecture — step suppression (`NativePipelineStepAdoption` + `PulumiStepSuppressionSelector`), the
spliced deploy step, and `PulumiValueResolver` were deleted in favour of the `…Azure.Seams` shim
(`AzureProvisioningSeams` + `IAzureTemplateProvisioner`) and the Pulumi-backed `PulumiTemplateProvisioner`
behind `UsePulumiProvisioning`.

Production caveats that were weighed and accepted:

- ⚠️ **Identity hack**: shipping a NuGet package whose assembly is named `Aspire.Hosting.Azure.Tests`
  and public-signed with Aspire's key is test-grade, not product-grade. Two assemblies with that identity
  cannot coexist in one app (e.g. alongside Aspire's real test assembly), and the trick silently breaks
  if Aspire drops or renames the IVT grant.
- ⚠️ **Version fragility**: internals carry no compatibility promise; every Aspire bump can break
  compilation. (This is also true, to a lesser degree, of the step-name/tag coupling in the suppression
  approach.)
- 🎯 **Granularity shift**: the seam receives one template at a time, which naturally maps to one Pulumi
  stack update per template (or incremental resource registration inside a single stack run) rather than
  one aggregated program. The destroy path would similarly ride the native `destroy-azure-*` step.
- ℹ️ Since this library hasn't shipped, the pragmatic path is: keep the spike as the pinned proof of the
  minimal boundary, and if the seam economics stay this favorable, revisit whether the suppression +
  translation frontend can be collapsed onto it (or whether upstream would take a public seam once we
  have evidence it's the right shape).
