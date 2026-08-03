// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Primitives;
using Pulumi;

namespace EmmittJ.Aspire.Hosting.Pulumi.Azure;

/// <summary>
/// Translates compiled Azure.Provisioning bicep expressions into Pulumi input values: literals pass
/// through, identifier references resolve through the template's symbol table (parameters, variables,
/// resources), symbolic resource references become Pulumi output property paths (implicit dependency
/// ordering), and the bicep functions Aspire emits get deterministic .NET implementations over Pulumi
/// output combinators. Unsupported constructs fail with errors naming the template, construct, and
/// property path.
/// </summary>
internal sealed partial class BicepExpressionTranslator(
    AzureTranslationContext context,
    string templateName,
    Output<string> resourceGroupName,
    IReadOnlyDictionary<string, BicepSymbol> symbols)
{
    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex InterpolatedKeyPattern();

    private string _constructName = "<template>";
    private string _propertyPath = "<value>";

    /// <summary>Translates <paramref name="expression"/> in the context of the named construct/property (for errors).</summary>
    public object? Translate(BicepExpression expression, string constructName, string propertyPath)
    {
        _constructName = constructName;
        _propertyPath = propertyPath;
        return Materialize(TranslateCore(expression));
    }

    private object? Materialize(object? value) => value is SymbolAccess access ? access.Materialize(this) : value;

    private object? TranslateCore(BicepExpression expression) => expression switch
    {
        StringLiteralExpression s => s.Value,
        IntLiteralExpression i => i.Value,
        BoolLiteralExpression b => b.Value,
        NullLiteralExpression => null,
        ObjectExpression o => TranslateObject(o),
        ArrayExpression a => new List<object?>(a.Values.Select(v => Materialize(TranslateCore(v)))),
        IdentifierExpression id => ResolveIdentifier(id.Name),
        MemberExpression m => TranslateMember(m),
        IndexExpression ix => TranslateIndex(ix),
        FunctionCallExpression f => TranslateFunctionCall(f),
        InterpolatedStringExpression istr => TranslateInterpolatedString(istr),
        _ => throw Fail($"bicep expression '{expression}' of kind '{expression.GetType().Name}' is not supported"),
    };

    private object TranslateObject(ObjectExpression expression)
    {
        var literal = new Dictionary<string, object?>();
        var lifted = new List<(Output<string> Key, object? Value)>();

        foreach (var property in expression.Properties)
        {
            var value = Materialize(TranslateCore(property.Value));
            var key = TranslateObjectKey(property.Name);
            if (key is string literalKey)
            {
                literal[literalKey] = value;
            }
            else
            {
                lifted.Add(((Output<string>)key, value));
            }
        }

        if (lifted.Count == 0)
        {
            return literal;
        }

        // Output-valued keys (e.g. userAssignedIdentities keyed by an identity's resource id) force the
        // whole dictionary to be lifted into a single Output. The lifted value must use the immutable
        // dictionary form the Pulumi serializer's upfront type check accepts.
        var keys = Output.All(lifted.Select(p => p.Key));
        return keys.Apply(resolved =>
        {
            var combined = literal.ToImmutableDictionary(
                static p => p.Key,
                static p => AzureNativeResource.NormalizeValue(p.Value));
            for (var i = 0; i < resolved.Length; i++)
            {
                combined = combined.SetItem(resolved[i], AzureNativeResource.NormalizeValue(lifted[i].Value));
            }

            return combined;
        });
    }

    private object TranslateObjectKey(string name)
    {
        if (!name.Contains("${", StringComparison.Ordinal))
        {
            return name;
        }

        var parts = new List<object>();
        var position = 0;
        foreach (Match match in InterpolatedKeyPattern().Matches(name))
        {
            if (match.Index > position)
            {
                parts.Add(name[position..match.Index]);
            }

            parts.Add(AsStringOutput(Materialize(ResolveIdentifier(match.Groups[1].Value))));
            position = match.Index + match.Length;
        }

        if (position < name.Length)
        {
            parts.Add(name[position..]);
        }

        return ConcatToStringOutput(parts);
    }

    private object? ResolveIdentifier(string name)
    {
        if (!symbols.TryGetValue(name, out var symbol))
        {
            throw Fail($"identifier '{name}' does not resolve to a parameter, variable, or resource in the template");
        }

        return symbol switch
        {
            ParameterSymbol p => p.Value,
            VariableSymbol v => v.GetValue(this),
            ResourceSymbol r => new SymbolAccess(r, ImmutableList<string>.Empty, ViaListKeys: false),
            _ => throw Fail($"identifier '{name}' resolved to an unsupported symbol kind '{symbol.GetType().Name}'"),
        };
    }

    private object? TranslateMember(MemberExpression expression)
    {
        var target = TranslateCore(expression.Value);
        return AccessMember(target, expression.Member, expression.Value);
    }

    private object? TranslateIndex(IndexExpression expression)
    {
        var index = Materialize(TranslateCore(expression.Index));
        if (index is not string memberName)
        {
            throw Fail($"index expression with non-string index '{index}' is not supported");
        }

        var target = TranslateCore(expression.Value);
        return AccessMember(target, memberName, expression.Value);
    }

    private object? AccessMember(object? target, string member, BicepExpression targetExpression)
    {
        switch (target)
        {
            case SymbolAccess access:
                return access with { Path = access.Path.Add(member) };
            case ResourceGroupAccess:
                return member switch
                {
                    "location" => context.Location,
                    "name" => resourceGroupName,
                    "id" => Output.Format($"/subscriptions/{context.SubscriptionId}/resourceGroups/{resourceGroupName}"),
                    _ => throw Fail($"resourceGroup().{member} is not supported"),
                };
            case Dictionary<string, object?> dictionary:
                return dictionary.TryGetValue(member, out var entry)
                    ? entry
                    : throw Fail($"member '{member}' was not found on object expression '{targetExpression}'");
            default:
                throw Fail($"member access '.{member}' on '{targetExpression}' (translated to '{target?.GetType().Name ?? "null"}') is not supported");
        }
    }

    private object? TranslateFunctionCall(FunctionCallExpression expression)
    {
        // Method-style calls: <resource>.listKeys().primarySharedKey etc.
        if (expression.Function is MemberExpression method)
        {
            var target = TranslateCore(method.Value);
            if (target is SymbolAccess { Path.Count: 0 } access && method.Member is "listKeys" or "listCredentials")
            {
                return access with { ViaListKeys = true };
            }

            throw Fail($"method call '{method.Member}(...)' on '{method.Value}' is not supported");
        }

        if (expression.Function is not IdentifierExpression function)
        {
            throw Fail($"function call target '{expression.Function}' is not supported");
        }

        var arguments = expression.Arguments;
        switch (function.Name)
        {
            case "resourceGroup" when arguments.Length == 0:
                return ResourceGroupAccess.Instance;
            case "uniqueString":
                return ApplyStrings(arguments, static values => ArmFunctions.UniqueString(values));
            case "guid":
                return ApplyStrings(arguments, static values => ArmFunctions.CreateGuid(values));
            case "toLower" when arguments.Length == 1:
                return ApplyStrings(arguments, static values => values[0].ToLowerInvariant());
            case "toUpper" when arguments.Length == 1:
                return ApplyStrings(arguments, static values => values[0].ToUpperInvariant());
            case "take" when arguments.Length == 2:
            {
                var source = AsStringOutput(Materialize(TranslateCore(arguments[0])));
                var count = Materialize(TranslateCore(arguments[1]));
                if (count is not int length)
                {
                    throw Fail($"take(...) with a non-literal length '{count}' is not supported");
                }

                return source.Apply(value => ArmFunctions.Take(value, length));
            }
            case "subscriptionResourceId" when arguments.Length >= 2:
            {
                // subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '<guid>') and friends:
                // /subscriptions/{sub}/providers/{type}/{name...}
                var parts = arguments.Select(a => AsStringOutput(Materialize(TranslateCore(a)))).ToArray();
                var suffix = Output.All(parts).Apply(values =>
                    $"/providers/{values[0]}/{string.Join('/', values.Skip(1))}");
                return Output.Format($"/subscriptions/{context.SubscriptionId}{suffix}");
            }
            case "string" when arguments.Length == 1:
                return AsStringOutput(Materialize(TranslateCore(arguments[0])));
            default:
                throw Fail($"bicep function '{function.Name}' with {arguments.Length} argument(s) is not supported");
        }
    }

    private Output<string> ApplyStrings(BicepExpression[] arguments, Func<string[], string> transform)
    {
        var parts = arguments.Select(a => AsStringOutput(Materialize(TranslateCore(a))));
        return Output.All(parts).Apply(values => transform([.. values]));
    }

    private object TranslateInterpolatedString(InterpolatedStringExpression expression)
    {
        var parts = expression.Values.Select(v => Materialize(TranslateCore(v))).ToList();
        return ConcatToStringOutput(parts);
    }

    private object ConcatToStringOutput(IReadOnlyList<object?> parts)
    {
        if (parts.All(static p => p is string))
        {
            return string.Concat(parts.Cast<string>());
        }

        return Output.All(parts.Select(AsStringOutput)).Apply(static values => string.Concat(values));
    }

    internal Output<string> AsStringOutput(object? value) => value switch
    {
        Output<string> output => output,
        string s => Output.Create(s),
        Output<object> output => output.Apply(v => Stringify(v)),
        _ => Output.Create(Stringify(value)),
    };

    private string Stringify(object? value) => value switch
    {
        null => throw Fail("a null value cannot be used where a string is required"),
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => throw Fail($"a value of type '{value.GetType().Name}' cannot be converted to a string"),
    };

    internal AzureProvisioningTranslationException Fail(string detail) =>
        AzureProvisioningTranslationException.ForConstruct(templateName, _constructName, _propertyPath, detail);

    private sealed class ResourceGroupAccess
    {
        public static readonly ResourceGroupAccess Instance = new();
    }
}

/// <summary>A named symbol (parameter, variable, or resource) in a translated template's symbol table.</summary>
internal abstract class BicepSymbol(string identifier)
{
    public string Identifier { get; } = identifier;
}

/// <summary>A template parameter resolved (asynchronously, before translation) to a Pulumi input value.</summary>
internal sealed class ParameterSymbol(string identifier, object? value) : BicepSymbol(identifier)
{
    public object? Value { get; } = value;
}

/// <summary>A template variable, translated lazily on first use and cached.</summary>
internal sealed class VariableSymbol(string identifier, BicepExpression expression) : BicepSymbol(identifier)
{
    private bool _translated;
    private object? _value;

    public object? GetValue(BicepExpressionTranslator translator)
    {
        if (!_translated)
        {
            _value = translator.Translate(expression, Identifier, "<variable>");
            _translated = true;
        }

        return _value;
    }
}

/// <summary>
/// A resource declared in the template: its type mapping, resolved ARM name, the created Pulumi resource
/// (null for <c>existing</c> resources), and lazily-invoked state reads for symbolic property references.
/// </summary>
internal sealed class ResourceSymbol(
    string identifier,
    string armType,
    string? apiVersion,
    AzureNativeTypeMapping mapping,
    bool isExisting) : BicepSymbol(identifier)
{
    private Output<ImmutableDictionary<string, object>>? _state;
    private Output<ImmutableDictionary<string, object>>? _keys;

    public string ArmType { get; } = armType;
    public string? ApiVersion { get; } = apiVersion;
    public AzureNativeTypeMapping Mapping { get; } = mapping;
    public bool IsExisting { get; } = isExisting;

    /// <summary>The resolved ARM resource name.</summary>
    public required Output<string> Name { get; set; }

    /// <summary>The created Pulumi resource; null for <c>existing</c> resources.</summary>
    public AzureNativeResource? Resource { get; set; }

    /// <summary>The invoke arguments (name, parent, resource group) used for state reads.</summary>
    public required Dictionary<string, object?> InvokeArgs { get; set; }

    public string TemplateName { get; set; } = string.Empty;

    /// <summary>The ARM resource id: the created resource's id, or a state read for existing resources.</summary>
    public Output<string> Id => Resource is not null
        ? Resource.Id
        : ReadState(["id"], "id");

    public Output<string> ReadState(IReadOnlyList<string> path, string propertyPath)
    {
        // azure-native flattens the ARM 'properties' envelope into the invoke result's top level.
        var effective = path.Count > 0 && path[0] == "properties" ? path.Skip(1).ToArray() : [.. path];
        return GetState().Apply(state => WalkState(state, effective, propertyPath));
    }

    public Output<string> ReadKeys(IReadOnlyList<string> path, string propertyPath)
    {
        var token = AzureNativeTypeCatalog.GetListKeysInvokeToken(ArmType)
            ?? throw AzureProvisioningTranslationException.ForConstruct(
                TemplateName, Identifier, propertyPath, $"no listKeys invoke is mapped for ARM type '{ArmType}'");
        _keys ??= InvokeState(token);
        return _keys.Apply(state => WalkState(state, path, propertyPath));
    }

    private Output<ImmutableDictionary<string, object>> GetState()
    {
        if (Mapping.GetInvokeToken is null)
        {
            throw AzureProvisioningTranslationException.ForConstruct(
                TemplateName, Identifier, "<state>", $"no get* invoke is mapped for ARM type '{ArmType}'");
        }

        return _state ??= InvokeState(Mapping.GetInvokeToken);
    }

    private Output<ImmutableDictionary<string, object>> InvokeState(string token)
    {
        var args = new DictionaryInvokeArgs(InvokeArgs.ToImmutableDictionary(
            static p => p.Key,
            static p => p.Value));
        var options = new InvokeOutputOptions { Version = AzureNativeResource.ProviderVersion };
        if (Resource is not null)
        {
            options.DependsOn = new InputList<global::Pulumi.Resource> { Resource };
        }

        return global::Pulumi.Deployment.Instance.Invoke<ImmutableDictionary<string, object>>(token, args, options);
    }

    private string WalkState(ImmutableDictionary<string, object> state, IReadOnlyList<string> path, string propertyPath)
    {
        object? current = state;
        foreach (var segment in path)
        {
            current = current switch
            {
                ImmutableDictionary<string, object> map when map.TryGetValue(segment, out var next) => next,
                IReadOnlyDictionary<string, object> map when map.TryGetValue(segment, out var next) => next,
                _ => throw AzureProvisioningTranslationException.ForConstruct(
                    TemplateName, Identifier, propertyPath,
                    $"property segment '{segment}' was not found reading '{string.Join('.', path)}' from the state of '{ArmType}'"),
            };
        }

        return current switch
        {
            string s => s,
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => throw AzureProvisioningTranslationException.ForConstruct(
                TemplateName, Identifier, propertyPath,
                $"state property '{string.Join('.', path)}' of '{ArmType}' is not a scalar value"),
        };
    }
}

/// <summary>
/// An in-flight symbolic reference to a resource: the symbol plus the member path accessed so far.
/// Materialized into a Pulumi output when the value is consumed.
/// </summary>
internal sealed record SymbolAccess(ResourceSymbol Symbol, ImmutableList<string> Path, bool ViaListKeys)
{
    public object Materialize(BicepExpressionTranslator translator)
    {
        var propertyPath = Path.Count == 0 ? Symbol.Identifier : $"{Symbol.Identifier}.{string.Join('.', Path)}";
        if (ViaListKeys)
        {
            return Symbol.ReadKeys(Path, propertyPath);
        }

        return Path switch
        {
            // A bare resource reference (e.g. a role assignment scope) is its ARM resource id.
            [] => Symbol.Id,
            ["id"] => Symbol.Id,
            ["name"] => Symbol.Name,
            _ => Symbol.ReadState(Path, propertyPath),
        };
    }
}
