// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Reflection;
using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Primitives;
using Microsoft.Extensions.Logging;
using Pulumi;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure;

/// <summary>
/// Translates one Aspire Azure template — the construct graph an <see cref="AzureProvisioningResource"/>
/// materializes — into Pulumi azure-native resources inside the current Pulumi program:
/// <list type="number">
/// <item>captures the built construct graph (<see cref="AzureProvisioningGraphCapture"/>),</item>
/// <item>resolves every template parameter asynchronously (known parameters from ambient client config,
/// <see cref="BicepOutputReference"/>s wired in-memory from previously translated templates, everything
/// else through <see cref="PulumiValueResolver"/>),</item>
/// <item>translates each declared resource in dependency order onto an untyped
/// <see cref="AzureNativeResource"/> (or a <c>get*</c> invoke for <c>existing</c> declarations), grouped
/// under one component resource per template so previews read per-Aspire-resource,</item>
/// <item>translates the template outputs into live Pulumi outputs and back-propagates their deployed
/// values into <see cref="AzureBicepResource.Outputs"/> so Aspire's <see cref="BicepOutputReference"/>s
/// resolve without a provisioning context.</item>
/// </list>
/// </summary>
internal sealed class AzureProvisioningTemplateTranslator
{
    private readonly AzureTranslationContext _context;
    private readonly string _templateName;
    private readonly AzureBicepResource _bicepResource;
    private readonly Dictionary<string, BicepSymbol> _symbols = [];

    private AzureProvisioningTemplateTranslator(AzureTranslationContext context, string templateName, AzureBicepResource bicepResource)
    {
        _context = context;
        _templateName = templateName;
        _bicepResource = bicepResource;
    }

    /// <summary>
    /// Translates the given Aspire Azure resource's template into Pulumi resources and registers the result
    /// on the translation context.
    /// </summary>
    /// <param name="context">The shared translation context.</param>
    /// <param name="resource">The Aspire resource carrying the template (environment or deployment target).</param>
    /// <param name="templateName">The name to group the translated resources under.</param>
    public static async Task<TranslatedAzureTemplate> TranslateAsync(
        AzureTranslationContext context,
        AzureBicepResource resource,
        string? templateName = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resource);

        if (resource is not AzureProvisioningResource provisioning)
        {
            throw new AzureProvisioningTranslationException(
                $"Resource '{resource.Name}' ({resource.GetType().Name}) is a plain bicep template without an " +
                "Azure.Provisioning construct graph, so it cannot be translated to azure-native resources. " +
                "Wrap it in an AzureProvisioningResource, or provision it out-of-band.");
        }

        var translator = new AzureProvisioningTemplateTranslator(context, templateName ?? resource.Name, resource);
        var translated = await translator.TranslateCoreAsync(provisioning).ConfigureAwait(false);
        context.RegisterTemplate(resource, translated);
        return translated;
    }

    private async Task<TranslatedAzureTemplate> TranslateCoreAsync(AzureProvisioningResource provisioning)
    {
        var infrastructure = AzureProvisioningGraphCapture.Capture(provisioning);
        var constructs = infrastructure.GetProvisionableResources().ToList();
        var expressionTranslator = new BicepExpressionTranslator(_context, _templateName, _symbols);

        // Parameters resolve asynchronously first; resources then translate synchronously inside the program.
        foreach (var parameter in constructs.OfType<ProvisioningParameter>())
        {
            var value = await ResolveParameterAsync(parameter, expressionTranslator).ConfigureAwait(false);
            _symbols[parameter.BicepIdentifier] = new ParameterSymbol(parameter.BicepIdentifier, value);
        }

        foreach (var variable in constructs.OfType<ProvisioningVariable>())
        {
            if (variable is ProvisioningOutput or ProvisioningParameter)
            {
                continue;
            }

            _symbols[variable.BicepIdentifier] = new VariableSymbol(variable.BicepIdentifier, variable.Value.Compile());
        }

        var component = new ComponentResource("pulumi-aspire:azure:AspireResource", _templateName);
        var resources = new List<global::Pulumi.Resource>();

        foreach (var construct in OrderResources(constructs.OfType<ProvisionableResource>().ToList()))
        {
            var symbol = TranslateResource(construct, expressionTranslator, component);
            _symbols[construct.BicepIdentifier] = symbol;
            if (symbol.Resource is not null)
            {
                resources.Add(symbol.Resource);
            }
        }

        var outputs = new Dictionary<string, Output<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var output in constructs.OfType<ProvisioningOutput>())
        {
            var name = output.BicepIdentifier;
            var value = expressionTranslator.AsStringOutput(
                expressionTranslator.Translate(output.Value.Compile(), name, "<output>"));

            if (!_context.UseDeterministicPlaceholders)
            {
                // Back-propagate deployed values into Aspire's output dictionary so BicepOutputReference
                // resolution works without a provisioning context. The applies run because the caller
                // registers every template output as a stack output.
                value = value.Apply(v =>
                {
                    _bicepResource.Outputs[name] = v;
                    return v;
                });
            }

            outputs[name] = value;
        }

        return new TranslatedAzureTemplate(_templateName, component, resources, outputs);
    }

    private async Task<object?> ResolveParameterAsync(ProvisioningParameter parameter, BicepExpressionTranslator translator)
    {
        var name = parameter.BicepIdentifier;
        if (_bicepResource.Parameters.TryGetValue(name, out var value))
        {
            if (value is null)
            {
                return ResolveKnownParameter(name);
            }

            if (value is BicepOutputReference outputReference
                && _context.Templates.TryGetValue(outputReference.Resource, out var translated)
                && translated.Outputs.TryGetValue(outputReference.Name, out var inMemory))
            {
                // Environment output feeding a deployment-target parameter: wire the live output through
                // in-memory instead of a string round-trip via Outputs.
                return inMemory;
            }

            try
            {
                var resolved = await _context.ValueResolver.ResolveAsync(value).ConfigureAwait(false);
                return parameter.IsSecure && !resolved.IsSecret
                    ? Output.CreateSecret(resolved.Value)
                    : resolved.Value;
            }
            catch (Exception ex) when (_context.UseDeterministicPlaceholders && ex is not OperationCanceledException)
            {
                // Publish previews run before images are pushed and without deployed outputs; keep the
                // preview alive with a placeholder instead of failing the artifact.
                _context.Logger.LogWarning(
                    "Parameter '{Parameter}' of template '{Template}' could not be resolved for the preview: {Message}",
                    name, _templateName, ex.Message);
                return Output.Create($"<preview:{name}>");
            }
        }

        var compiled = ((IBicepValue)parameter.Value).Compile();
        if (compiled is not IdentifierExpression identifier || identifier.Name != name)
        {
            // The parameter has a default value expression (location, tags, ...); translate it.
            return translator.Translate(compiled, name, "<default>");
        }

        if (name == AzureBicepResource.KnownParameters.Location)
        {
            // Native deploys inject the location from the deployment context (it is deliberately excluded
            // from the Parameters back-fill); the translation supplies the target resource group's location.
            return _context.Location;
        }

        throw AzureProvisioningTranslationException.ForConstruct(
            _templateName, name, "<parameter>",
            "the template declares this parameter but the Aspire resource provides no value for it");
    }

    private object ResolveKnownParameter(string name) => name switch
    {
        "principalId" or "userPrincipalId" => _context.PrincipalId,
        "principalType" => Output.Create("User"),
        "location" => _context.Location,
        _ => throw AzureProvisioningTranslationException.ForConstruct(
            _templateName, name, "<parameter>",
            $"the known parameter '{name}' is not supported by the Pulumi translation"),
    };

    private ResourceSymbol TranslateResource(
        ProvisionableResource construct,
        BicepExpressionTranslator translator,
        ComponentResource component)
    {
        var identifier = construct.BicepIdentifier;
        var armType = construct.ResourceType;
        var apiVersion = construct.ResourceVersion;

        // Partition the construct's properties: the ARM name, the parent link, and everything else.
        BicepExpression? nameExpression = null;
        string? parentIdentifier = null;
        var properties = new List<(IReadOnlyList<string> Path, IBicepValue Value)>();

        foreach (var (_, value) in construct.ProvisionableProperties)
        {
            if (value.Kind == BicepValueKind.Unset || value.IsOutput)
            {
                continue;
            }

            var path = value.Self?.BicepPath ?? [];
            switch (path)
            {
                case ["name"]:
                    nameExpression = value.Compile();
                    break;
                case ["parent"]:
                    parentIdentifier = (value.Compile() as IdentifierExpression)?.Name
                        ?? throw AzureProvisioningTranslationException.ForConstruct(
                            _templateName, identifier, "parent", "the parent expression is not a resource reference");
                    break;
                case ["dependsOn"]:
                    break;
                default:
                    properties.Add((path, value));
                    break;
            }
        }

        if (nameExpression is null)
        {
            throw AzureProvisioningTranslationException.ForConstruct(
                _templateName, identifier, "name", "the resource declares no name");
        }

        var nameLiteral = (nameExpression as StringLiteralExpression)?.Value;
        var mapping = AzureNativeTypeCatalog.Map(armType, apiVersion, nameLiteral);
        var symbol = new ResourceSymbol(identifier, armType, apiVersion, mapping, construct.IsExistingResource)
        {
            Context = _context,
            TemplateName = _templateName,
            Name = translator.AsStringOutput(translator.Translate(nameExpression, identifier, "name")),
            InvokeArgs = [],
        };

        var parentSymbol = parentIdentifier is not null ? RequireResourceSymbol(parentIdentifier, identifier) : null;
        if (parentSymbol is null && mapping.ParentArgName is not null)
        {
            throw AzureProvisioningTranslationException.ForConstruct(
                _templateName, identifier, "parent",
                $"the child ARM type '{armType}' requires a parent resource, but none is declared");
        }

        symbol.InvokeArgs[mapping.NameArgName] = symbol.Name;
        if (parentSymbol is not null && mapping.ParentArgName is not null)
        {
            // For config-singleton children the parent argument shares the child's name argument
            // ('name' is the site name); the parent link wins by design.
            symbol.InvokeArgs[mapping.ParentArgName] = parentSymbol.Name;
        }

        if (mapping.RequiresResourceGroupName)
        {
            symbol.InvokeArgs["resourceGroupName"] = _context.ResourceGroupName;
        }

        if (construct.IsExistingResource)
        {
            return symbol;
        }

        var bag = new Dictionary<string, object?>(symbol.InvokeArgs);
        foreach (var (path, value) in properties)
        {
            var translated = translator.Translate(value.Compile(), identifier, string.Join('.', path));
            if (value.IsSecure && translated is Output<string> secureValue)
            {
                translated = Output.CreateSecret(secureValue);
            }

            // azure-native flattens the ARM 'properties' envelope into top-level inputs.
            var effective = path.Count > 1 && path[0] == "properties" ? path.Skip(1).ToArray() : [.. path];
            SetByPath(bag, effective, translated, identifier);
        }

        var options = new CustomResourceOptions { Parent = component };
        foreach (var dependency in construct.DependsOn)
        {
            if (RequireResourceSymbol(dependency.BicepIdentifier, identifier).Resource is { } dependencyResource)
            {
                options.DependsOn.Add(dependencyResource);
            }
        }

        _context.ConfigureResource?.Invoke(new AzureNativeResourceCustomizationContext(
            _templateName, identifier, armType, apiVersion, mapping.Token, bag, options));

        symbol.Resource = new AzureNativeResource(
            mapping.Token,
            $"{_templateName}-{identifier.Replace('_', '-')}",
            bag,
            options);
        return symbol;
    }

    private ResourceSymbol RequireResourceSymbol(string identifier, string referencedBy)
    {
        if (_symbols.TryGetValue(identifier, out var symbol) && symbol is ResourceSymbol resource)
        {
            return resource;
        }

        throw AzureProvisioningTranslationException.ForConstruct(
            _templateName, referencedBy, "<reference>",
            $"the referenced resource '{identifier}' has not been translated yet (unresolved ordering or cycle)");
    }

    private void SetByPath(Dictionary<string, object?> bag, IReadOnlyList<string> path, object? value, string identifier)
    {
        if (path.Count == 0)
        {
            throw AzureProvisioningTranslationException.ForConstruct(
                _templateName, identifier, "<property>", "a property with an empty ARM path cannot be translated");
        }

        var current = bag;
        for (var i = 0; i < path.Count - 1; i++)
        {
            if (!current.TryGetValue(path[i], out var next) || next is not Dictionary<string, object?> nested)
            {
                nested = [];
                current[path[i]] = nested;
            }

            current = nested;
        }

        current[path[^1]] = value;
    }

    /// <summary>
    /// Orders resource constructs so every symbolic reference points at an already-translated symbol:
    /// identifier references (data edges), parent links, and explicit DependsOn all count as edges.
    /// </summary>
    private List<ProvisionableResource> OrderResources(List<ProvisionableResource> constructs)
    {
        var byIdentifier = constructs.ToDictionary(c => c.BicepIdentifier);
        var edges = new Dictionary<string, HashSet<string>>();

        foreach (var construct in constructs)
        {
            var references = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (_, value) in construct.ProvisionableProperties)
            {
                if (value.Kind == BicepValueKind.Unset || value.IsOutput)
                {
                    continue;
                }

                CollectIdentifiers(value.Compile(), references);
            }

            foreach (var dependency in construct.DependsOn)
            {
                references.Add(dependency.BicepIdentifier);
            }

            references.IntersectWith(byIdentifier.Keys);
            references.Remove(construct.BicepIdentifier);
            edges[construct.BicepIdentifier] = references;
        }

        var ordered = new List<ProvisionableResource>(constructs.Count);
        var visited = new Dictionary<string, bool>();

        void Visit(string identifier)
        {
            if (visited.TryGetValue(identifier, out var done))
            {
                if (!done)
                {
                    throw new AzureProvisioningTranslationException(
                        $"Template '{_templateName}' contains a dependency cycle involving resource '{identifier}'.");
                }

                return;
            }

            visited[identifier] = false;
            foreach (var reference in edges[identifier].OrderBy(static r => r, StringComparer.Ordinal))
            {
                Visit(reference);
            }

            visited[identifier] = true;
            ordered.Add(byIdentifier[identifier]);
        }

        foreach (var construct in constructs)
        {
            Visit(construct.BicepIdentifier);
        }

        return ordered;
    }

    /// <summary>Collects every identifier referenced anywhere inside a compiled bicep expression tree.</summary>
    internal static void CollectIdentifiers(BicepExpression expression, HashSet<string> sink)
    {
        switch (expression)
        {
            case IdentifierExpression identifier:
                sink.Add(identifier.Name);
                return;
            case ObjectExpression obj:
                foreach (var property in obj.Properties)
                {
                    foreach (System.Text.RegularExpressions.Match match in
                        System.Text.RegularExpressions.Regex.Matches(property.Name, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}"))
                    {
                        sink.Add(match.Groups[1].Value);
                    }

                    CollectIdentifiers(property.Value, sink);
                }

                return;
        }

        // Generic structural walk: recurse into every property exposing nested expressions. This keeps the
        // collector correct for node shapes we do not pattern-match explicitly (binary, conditional, ...).
        foreach (var property in expression.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var value = property.GetValue(expression);
            switch (value)
            {
                case BicepExpression nested:
                    CollectIdentifiers(nested, sink);
                    break;
                case IEnumerable<BicepExpression> list:
                    foreach (var item in list)
                    {
                        CollectIdentifiers(item, sink);
                    }

                    break;
            }
        }
    }
}
