using System.Text.Json;
using Microsoft.Extensions.AI;

namespace EdgeKit.Services.Agent;

internal sealed class AgentRuntimeFunction : AIFunction
{
    private static readonly JsonElement StringReturnSchema = JsonSerializer.SerializeToElement(new { type = "string" });

    private readonly string _name;
    private readonly string _description;
    private readonly JsonElement _jsonSchema;
    private readonly JsonElement? _returnJsonSchema;
    private readonly JsonSerializerOptions _jsonSerializerOptions;
    private readonly Func<AIFunctionArguments, CancellationToken, Task<string>> _invoke;

    public AgentRuntimeFunction(
        string name,
        string description,
        JsonElement jsonSchema,
        JsonSerializerOptions jsonSerializerOptions,
        Func<AIFunctionArguments, CancellationToken, Task<string>> invoke,
        JsonElement? returnJsonSchema = null)
    {
        _name = name;
        _description = description;
        _jsonSchema = jsonSchema.ValueKind == JsonValueKind.Undefined
            ? AgentToolSchemas.EmptyObjectSchema
            : jsonSchema.Clone();
        _returnJsonSchema = returnJsonSchema?.Clone() ?? StringReturnSchema.Clone();
        _jsonSerializerOptions = jsonSerializerOptions;
        _invoke = invoke;
    }

    public override string Name => _name;

    public override string Description => _description;

    public override JsonElement JsonSchema => _jsonSchema;

    public override JsonElement? ReturnJsonSchema => _returnJsonSchema;

    public override JsonSerializerOptions JsonSerializerOptions => _jsonSerializerOptions;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        => await _invoke(arguments ?? new AIFunctionArguments(), cancellationToken).ConfigureAwait(false);
}
