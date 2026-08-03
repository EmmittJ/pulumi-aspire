# 🔍 Spike: Translation Source for the Azure → Pulumi azure-native Mapping

**Status:** ✅ Go (construct graph is the primary source)
**Aspire version:** 13.4.6 (Azure.Provisioning 1.5.0, Pulumi SDK 3.107.2, Pulumi.AzureNative 3.19.0)
**Spike code:** validated via scratch harness against `AddAzureContainerAppEnvironment` and
`AddAzureAppServiceEnvironment`; the faithfulness check has been promoted into the pinned fixture tests in
`tests/EmmittJ.Aspire.Hosting.Pulumi.Tests`

## 🎯 Question

Which representation of the deployment targets that Aspire's **native** Azure environments materialize
(`AzureProvisioningResource` / `AzureBicepResource`) can the translation layer consume to map ARM resource
types onto Pulumi `azure-native` resources?

Candidates, in preference order from the plan:

1. the Azure.Provisioning construct graph (typed, no parsing),
2. the compiled template → ARM JSON document,
3. escape hatch: provision the compiled template server-side via the azure-native `Deployment` resource.

## ✅ Answer

**The construct graph is reachable, complete, and faithful — it is the translation source.** ARM-JSON
translation is unnecessary for everything Aspire 13.4.6 emits; the `Deployment` escape hatch remains the
documented degraded path for plain `AzureBicepResource`s that carry only a static template.

### 🔨 The capture seam

`AzureProvisioningResource.GetBicepTemplateFile()` is equivalent to:

1. `new AzureResourceInfrastructure(resource, resource.Name)` — the class is **sealed** with an
   **internal** constructor;
2. invoke the public `ConfigureInfrastructure` callback;
3. private `EnsureParametersAlign(infra)` — adds a `ProvisioningParameter(key, typeof(string))` for every
   `resource.Parameters` key not already declared, marking it `IsSecure` when the parameter value is a
   secret `ParameterResource` or a `BicepSecretOutputReference`;
4. `infra.Build(resource.ProvisioningBuildOptions).Compile()`.

The capture replicates this exact sequence, using
`[UnsafeAccessor(UnsafeAccessorKind.Constructor)]` for step 1 and a re-implementation of step 3
(the logic is small and pinned by tests). Two rules are load-bearing:

- ⚠️ **`Build()` must run before the graph is walked.** `Build()` mutates constructs in-place — most
  importantly it assigns the default names of unnamed resources
  (e.g. `take('acaenvacr${uniqueString(resourceGroup().id)}', 50)`).
- ✅ **Faithfulness is verifiable.** Compiling the captured infrastructure produces output byte-identical
  to `resource.GetBicepTemplateString()`, for the ACA environment, the App Service environment, ACR, and
  every `{resource}-containerapp` / `{resource}-website` deployment target. The pinned fixtures keep
  asserting this on every run, so an Aspire bump that changes the seam fails CI instead of mistranslating.

### 📋 What the graph exposes

`Infrastructure.GetProvisionableResources()` yields, per construct:

| Construct | Key members |
| --- | --- |
| `ProvisioningParameter` | `BicepIdentifier`, `IsSecure`, `Value` |
| `ProvisioningVariable` | `BicepIdentifier`, `Value` |
| `ProvisioningOutput` | `BicepIdentifier`, `Value` |
| `ProvisionableResource` | `BicepIdentifier`, `ResourceType` (e.g. `Microsoft.App/containerApps`), `ResourceVersion` (e.g. `2025-07-01`), `IsExistingResource`, `DependsOn`, `ProvisionableProperties` |

Each property is an `IBicepValue` with `Kind` (`Unset`/`Literal`/`Expression`), `Self.BicepPath` (the ARM
property path, e.g. `["properties","configuration"]`), `IsSecure`, `IsOutput`, and `Compile()` producing a
`BicepExpression` AST — a closed, public node set (literals, object/array, identifier, member/index,
function call, interpolated string, binary/conditional/unary). Properties with `Kind == Unset` or
`IsOutput == true` are skipped; everything else translates from the AST. No Bicep text parsing anywhere.

### 📊 Catalogue of what Aspire 13.4.6 emits (pinned by fixtures)

ARM types across ACA + App Service environments and their targets:

- `Microsoft.ContainerRegistry/registries@2025-04-01`
- `Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30`
- `Microsoft.Authorization/roleAssignments@2022-04-01`
- `Microsoft.OperationalInsights/workspaces@2025-02-01`
- `Microsoft.App/managedEnvironments@2025-07-01`
- `Microsoft.App/managedEnvironments/dotNetComponents@2025-10-02-preview` (child; `parent` property)
- `Microsoft.App/containerApps@2025-07-01`
- `Microsoft.Web/serverfarms@2025-03-01`
- `Microsoft.Web/sites@2025-03-01`
- `Microsoft.Web/sites/sitecontainers@2025-03-01` (child)
- `Microsoft.Web/sites/config@2025-03-01` (child, `slotConfigNames`)

Expression surface: `resourceGroup().location` / `.id`, `uniqueString`, `take`, `toLower`, `guid`,
`subscriptionResourceId`, string interpolation, symbolic member reads (`acr.properties.loginServer`),
`listKeys().primarySharedKey` (→ azure-native `getSharedKeys` invoke), `existing` re-declarations
(→ azure-native `get*` invokes), and Output-valued object **keys**
(`userAssignedIdentities: { '${mi_id}': {} }` — the whole dictionary lifts into a Pulumi `Output`).

Deployment-target `resource.Parameters` values are `BicepOutputReference` (environment outputs),
`ContainerImageReference` (filled by the native push step), and `ParameterResource` (secrets → wrapped via
`Output.CreateSecret`) — all already handled by the core value-resolution logic.

### 🔑 Pulumi-side feasibility (verified)

- `new CustomResource(token, name, DictionaryResourceArgs, options)` is public; the serializer accepts
  nested `Output`s anywhere, including dictionary values — one untyped wrapper covers every ARM type.
- There is **no** public API to read arbitrary outputs from an untyped `CustomResource`; property reads on
  translated resources go through untyped invokes
  (`Deployment.Instance.Invoke<...>("azure-native:module:getXxx", ..., new InvokeOutputOptions { DependsOn = { resource } })`).
- Unversioned tokens (`azure-native:app:ContainerApp`) resolve to the provider's curated default API
  version; explicit-version pins stay available through the mapper's override table when the curated
  version drifts from what Aspire emits.

## ⚠️ Fallback and escape hatch

- **Plain `AzureBicepResource`** (static template, no construct graph): not emitted by the ACA/App Service
  environments themselves, but user-added bicep resources exist in the wild. These take the escape hatch:
  provision the compiled template server-side via the azure-native
  `azure-native:resources:Deployment` resource, logged loudly as a degraded path. ARM-JSON
  translation (candidate 2) is therefore **not** implemented — the construct graph covers the
  translation-worthy surface and the escape hatch covers the rest without hard-blocking anyone.
- **Untranslatable constructs** inside a graph fail fast with an error naming the resource, the property
  path, and the offending construct — never a silent mistranslation.

## 🧪 Risk pinning

Everything above is Aspire-version-sensitive, so the same strategy as the step catalogue applies: the
fixture tests pin the exact construct graphs (types, versions, parameters, outputs, expression shapes)
that 13.4.6 materializes and assert capture faithfulness byte-for-byte. An Aspire upgrade that moves the
seam or widens the expression surface fails CI with a readable diff.
