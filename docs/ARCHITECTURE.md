# Architecture

This document describes the internal structure of the Pulumi integration for maintainers. It complements the README by focusing on lifecycle and extension points rather than setup or end-user usage.

Pulumi Aspire deploys Aspire applications to the cloud using Pulumi's [Automation API](https://www.pulumi.com/docs/guides/automation-api/) through **internal-seam reuse**, validated by the [internal-seam reuse spike](spikes/internal-seam-reuse.md): the default Azure provisioning pipeline runs **completely unmodified** — prepare, publish wiring, login validation, provisioning-context creation (subscription/resource-group prompts and state), per-resource provision ordering and reporting, and the container registry login — and Pulumi is swapped in as the *execution engine* behind it. The one seam every native `provision-*` step resolves at execution time, the internal `IBicepProvisioner`, is replaced so each template flows to Pulumi with parameters already resolved by Aspire's own machinery.

## Packages

| Package | Responsibility |
| --- | --- |
| `EmmittJ.Aspire.Hosting.Pulumi` | Provider-agnostic core: the Automation API runner (`PulumiRunner`) and naming validation. |
| `EmmittJ.Aspire.Hosting.Pulumi.Azure` | The user-facing `UsePulumiProvisioning` extension, the Pulumi execution engine (`PulumiTemplateProvisioner`), and the Bicep → azure-native translation core. |
| `EmmittJ.Aspire.Hosting.Pulumi.Azure.Seams` | The internal-seam boundary: compiles with the assembly identity `Aspire.Hosting.Azure.Tests` (see below) to access `Aspire.Hosting.Azure` internals, and confines that access behind a fully public contract (`IAzureTemplateProvisioner`, `AzureTemplateDeployment`). |

## The seam boundary (`…Azure.Seams`)

`Aspire.Hosting.Azure` grants `InternalsVisibleTo` to its own test assembly, `Aspire.Hosting.Azure.Tests`, signed with the Aspire OSS public key. The Seams project takes that assembly identity (`AssemblyName` + `PublicSign` with the public-key-only `aspire-public.snk` — CoreCLR skips strong-name validation) and therefore compiles against Aspire's internals. Everything it exposes is public and internal-free:

| Type | Role |
| --- | --- |
| `AzureProvisioningSeams.UseTemplateProvisioner` | `Services.Replace`s the internal `IBicepProvisioner` with an adapter over the supplied `IAzureTemplateProvisioner`, and the internal `IBicepCompiler` with a no-op (the seam consumes the provisioning model, not compiled ARM JSON, so the `bicep` CLI is not required). Aspire registers every provisioning service with `TryAddSingleton`, so the replacement is order-independent. |
| `AzureTemplateDeployment` | One template the pipeline asked to provision: the `AzureBicepResource`, its parameters resolved by Aspire's own internal `BicepUtilities` (ARM deployment-parameter format), the deployment scope, and the provisioning-context values. |
| `AzureDeploymentContext` | The native `create-provisioning-context` step's values as plain strings: subscription, tenant, resource group, location, principal. |
| `IAzureTemplateProvisioner` | The pluggable execution engine. Contract: back-propagate deployed outputs into `AzureBicepResource.Outputs` and complete `ProvisioningTaskCompletionSource` before returning; outputs named `*_ID` must be valid ARM resource ids (native steps parse them with `ResourceIdentifier`). |

The adapter (`SeamBicepProvisioner`) mirrors the native provisioner's pre-deployment work — well-known parameter population (principal id/name/type, location), `BicepUtilities.SetParametersAsync`/`SetScopeAsync`, and Key Vault secret-resolver wiring after provisioning — so an engine plugged in behind it observes exactly what an ARM deployment would.

### Caveats (accepted deliberately)

- **Assembly-identity hack**: the Seams assembly impersonates Aspire's own test assembly. Aspire internals can change in any release; the compile breaks loudly on upgrade (which is the desired failure mode), and the mock-engine fixture tests pin the observable behavior.
- **One per process**: two assemblies named `Aspire.Hosting.Azure.Tests` cannot be loaded together, so test projects consuming the shim must not carry that identity themselves.

## The Pulumi execution engine

`UsePulumiProvisioning(options?)` (on `IDistributedApplicationBuilder`) is the entire user-facing surface: a no-op in run mode, otherwise it registers `PulumiRunner` and plugs `PulumiTemplateProvisioner` into the seam. There is nothing to model — the environment stays a completely standard `AddAzureContainerAppEnvironment` — and `aspire deploy` keeps its normal flow, including the interactive subscription/resource-group/location prompts, because the native `create-provisioning-context` step still owns them (and creates the resource group via ARM; the translation deliberately does not manage a resource-group resource).

`PulumiTemplateProvisioner` maintains a **cumulative single-stack model**: every template handed to `ProvisionAsync` (environment resources first, then each compute resource's deployment target — ordering guaranteed by the native pipeline's step graph) is appended, and one `pulumi up` runs against a single stack whose inline program translates the whole model so far. Pulumi's diffing makes each successive up incremental — already-deployed resources no-op — so the stack always reflects the full application and remains independently usable (`pulumi preview`, `pulumi destroy`, drift detection). Concurrent provision steps serialize on a gate, since ups against one stack cannot overlap.

The inline program (`BuildProgramAsync`) creates one `AzureTranslationContext` from the provisioning-context values, translates each deployment in order, and exports every template output as a `{template}_{output}` stack output. Exporting is load-bearing: it roots the applies that back-propagate deployed values into `AzureBicepResource.Outputs` and complete the provisioning gate — which is exactly what downstream *native* steps consume (the real `login-to-acr-*` step reads the back-propagated registry endpoint, and later templates' `BicepOutputReference` parameters resolve through Aspire's own `BicepUtilities`).

### The destroy path

`UsePulumiProvisioning` also splices a `destroy-pulumi-stack` step into the standard destroy slots (`destroy-prereq` → `destroy`) and, via a pipeline-configuration callback, orders every native `destroy-azure-*` step after it. On `aspire destroy`, Pulumi therefore tears down each stack resource first — through `pulumi destroy`, while the resources still exist, leaving the stack's state empty instead of stale — and the native step then deletes the resource group itself via ARM (the resource group is created by the native `create-provisioning-context` step and deliberately not Pulumi-managed). The step targets exactly the project/stack the deploy path resolves (`PulumiStackNameResolver`), skips cleanly when the stack doesn't exist or is already empty, and mirrors the native destroy step's confirmation contract: it prompts through `IInteractionService`, honors `--yes` (`PipelineOptions.SkipConfirmation`), and refuses to destroy without a confirmation channel.

### Stack naming

The Pulumi project defaults to the AppHost's application name (sanitized to Pulumi's allowed character set) and the stack to the deployment environment name, lower-cased (precedence: `--environment` > `DOTNET_ENVIRONMENT` > `ASPIRE_ENVIRONMENT`) — matching how Aspire partitions deployment state per environment. `PulumiProvisioningOptions.ProjectName`/`StackName` pin either explicitly; both are validated against Pulumi's naming rules.

## Translation core

`AzureProvisioningTemplateTranslator` translates one template — the construct graph an `AzureProvisioningResource` materializes, captured by `AzureProvisioningGraphCapture` — into Pulumi azure-native resources inside the current program (see the [translation-source spike](spikes/azure-provisioning-translation-source.md)):

1. **Parameters** resolve from, in order: a `BicepOutputReference` to a template translated earlier in the same program run (wired **in-memory** as a live `Output`, the fast path that keeps the dependency graph inside Pulumi); the Aspire-resolved ARM-format value carried by the deployment (strings, numbers, booleans, arrays, objects — wrapped with `Output.CreateSecret` when the parameter is declared secure); the provisioning-context fallback for declared-but-unvalued known parameters (principal id/name/type, location); or the parameter's default-value expression.
2. **Resources** translate in dependency order (identifier references, parent links, `dependsOn`) onto untyped `AzureNativeResource`s via `AzureNativeTypeCatalog` token mapping, grouped under one component resource per template; `existing` declarations become lazy `get*` invokes. Scope overrides (templates targeting an existing resource group) win over the provisioning context's resource group, per template.
3. **Outputs** translate to live `Output<string>`s and back-propagate into the Aspire model as described above.

`BicepExpressionTranslator` handles the compiled bicep expression language Aspire emits (literals, interpolation, member/index access, `uniqueString`/`guid`/`take`/`toLower`/`subscriptionResourceId`/…, `listKeys`), materializing symbolic resource references as output property paths or state-read invokes. `PulumiProvisioningOptions.ConfigureResource` is the per-resource escape hatch for Pulumi-only concerns (providers, aliases, protect flags, property overrides).

## Testing strategy

- `EmmittJ.Aspire.Hosting.Pulumi.Tests` — wiring tests assert the seam replacement by service-type full name (no internals access, exactly like a consumer), and translation fixture tests materialize the real ACA provisioning model in-process (the pipeline's `before-start` slice), hand-build resolved deployments the way the seam does, and pin the translated resource set under the Pulumi mock engine. An Aspire version bump that changes what the environment emits fails here with a readable diff.
- `EmmittJ.Aspire.Hosting.Pulumi.InternalSeamSpike.Tests` — the pinned end-to-end proof that the full native pipeline runs unsuppressed with the seams swapped (offline ARM graph, recording provisioner). This project itself carries the `Aspire.Hosting.Azure.Tests` identity, so it cannot reference the Seams shim; it duplicates the seam mechanics by design.

## Adding a provider frontend

The seam approach is Azure-specific by nature (it plugs into `Aspire.Hosting.Azure`'s provisioning pipeline). A future provider frontend would follow the same recipe against that provider's execution seams: find the internal boundary its native steps resolve at execution time, confine the internals access to a dedicated shim assembly with a public contract, and implement the contract over `PulumiRunner`. The Azure package is the only frontend implemented in this repository today.

## Roadmap

1. ✅ Internal-seam boundary (`…Azure.Seams`): public `IAzureTemplateProvisioner` contract over the internal `IBicepProvisioner`/`IBicepCompiler` seams, validated by the internal-seam reuse spike.
2. ✅ Pulumi execution engine: `UsePulumiProvisioning` + `PulumiTemplateProvisioner` with the cumulative single-stack model.
3. ✅ Bicep → azure-native translation core consuming Aspire-resolved parameters, pinned by mock-engine fixture tests over the real ACA provisioning model.
4. ⏸️ Additional Azure compute environments (App Service) ride the same seam — the pipeline and seam are environment-agnostic; translation coverage for their templates is validated as needed.

## References

- [Aspire](https://aspire.dev/)
- [Pulumi Automation API](https://www.pulumi.com/docs/guides/automation-api/)
