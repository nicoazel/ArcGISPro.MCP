using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace ArcGISProMCP.Core.Operations;

/// <summary>
/// Typed factory methods for the bounded JSON Schema subset understood by
/// <see cref="OperationArgumentValidator"/>. Every schema produced here is
/// self-checked against the validator when it is built, so a schema that uses an
/// unsupported or malformed keyword fails at startup instead of at invocation time.
/// The subset has no union types (<c>oneOf</c>, <c>anyOf</c>, type arrays), so there
/// is deliberately no Nullable/OneOf factory; use <see cref="Any"/> for values that
/// may be null.
/// </summary>
public static partial class JsonSchemas
{
    /// <summary>
    /// Builds an object schema. The result always
    /// carries <c>properties</c>, <c>required</c> and <c>additionalProperties</c>
    /// (false by default).
    /// </summary>
    [SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Mirrors the JSON Schema type keyword.")]
    public static JsonElement Object(
        (string Name, JsonElement Schema)[] properties,
        string[]? required = null,
        bool additionalProperties = false,
        int? minProperties = null,
        int? maxProperties = null,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(properties);
        required ??= [];
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, schema) in properties)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Property names must be non-empty.", nameof(properties));
            if (!names.Add(name))
                throw new ArgumentException($"Property '{name}' is declared more than once.", nameof(properties));
            RequireDefined(schema, $"property '{name}'");
        }
        var requiredNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in required)
        {
            if (name is null || !names.Contains(name))
                throw new ArgumentException($"Required property '{name}' is not declared in properties.", nameof(required));
            if (!requiredNames.Add(name))
                throw new ArgumentException($"Required property '{name}' is listed more than once.", nameof(required));
        }
        CheckCount(minProperties, nameof(minProperties));
        CheckCount(maxProperties, nameof(maxProperties));
        CheckRange(minProperties, maxProperties, nameof(minProperties));

        return Build(writer =>
        {
            WriteDescription(writer, description);
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");
            foreach (var (name, schema) in properties)
            {
                writer.WritePropertyName(name);
                schema.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.WriteStartArray("required");
            foreach (var name in required)
                writer.WriteStringValue(name);
            writer.WriteEndArray();
            writer.WriteBoolean("additionalProperties", additionalProperties);
            WriteOptional(writer, "minProperties", minProperties);
            WriteOptional(writer, "maxProperties", maxProperties);
        });
    }

    /// <summary>Builds a string schema with optional length, pattern and enum constraints.</summary>
    [SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Mirrors the JSON Schema type keyword.")]
    public static JsonElement String(
        int? minLength = null,
        int? maxLength = null,
        string? pattern = null,
        IEnumerable<string>? enumValues = null,
        string? description = null)
    {
        CheckCount(minLength, nameof(minLength));
        CheckCount(maxLength, nameof(maxLength));
        CheckRange(minLength, maxLength, nameof(minLength));
        var values = enumValues?.ToArray();
        if (values is not null)
        {
            if (values.Length == 0)
                throw new ArgumentException("Enum must declare at least one value.", nameof(enumValues));
            if (values.Any(v => v is null))
                throw new ArgumentException("Enum values must not be null.", nameof(enumValues));
            if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
                throw new ArgumentException("Enum values must be unique.", nameof(enumValues));
        }

        return Build(writer =>
        {
            WriteDescription(writer, description);
            writer.WriteString("type", "string");
            if (values is not null)
            {
                writer.WriteStartArray("enum");
                foreach (var value in values)
                    writer.WriteStringValue(value);
                writer.WriteEndArray();
            }
            WriteOptional(writer, "minLength", minLength);
            WriteOptional(writer, "maxLength", maxLength);
            if (pattern is not null)
                writer.WriteString("pattern", pattern);
        });
    }

    /// <summary>Shorthand for a string schema restricted to the given values.</summary>
    public static JsonElement Enum(params string[] values) => String(enumValues: values);

    /// <summary>Builds an integer schema with optional inclusive bounds.</summary>
    [SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Mirrors the JSON Schema type keyword.")]
    public static JsonElement Integer(long? minimum = null, long? maximum = null, string? description = null)
    {
        if (minimum is { } min && maximum is { } max && min > max)
            throw new ArgumentOutOfRangeException(nameof(minimum), "minimum must not exceed maximum.");
        return Build(writer =>
        {
            WriteDescription(writer, description);
            writer.WriteString("type", "integer");
            if (minimum is { } lower) writer.WriteNumber("minimum", lower);
            if (maximum is { } upper) writer.WriteNumber("maximum", upper);
        });
    }

    /// <summary>Builds a number schema with optional inclusive and exclusive bounds.</summary>
    public static JsonElement Number(
        double? minimum = null,
        double? maximum = null,
        double? exclusiveMinimum = null,
        double? exclusiveMaximum = null,
        string? description = null)
    {
        foreach (var (value, name) in new[] { (minimum, nameof(minimum)), (maximum, nameof(maximum)), (exclusiveMinimum, nameof(exclusiveMinimum)), (exclusiveMaximum, nameof(exclusiveMaximum)) })
            if (value is { } bound && !double.IsFinite(bound))
                throw new ArgumentOutOfRangeException(name, "Numeric bounds must be finite.");
        if (minimum is { } min && maximum is { } max && min > max)
            throw new ArgumentOutOfRangeException(nameof(minimum), "minimum must not exceed maximum.");
        if (exclusiveMinimum is { } xmin && exclusiveMaximum is { } xmax && xmin >= xmax)
            throw new ArgumentOutOfRangeException(nameof(exclusiveMinimum), "exclusiveMinimum must be less than exclusiveMaximum.");
        return Build(writer =>
        {
            WriteDescription(writer, description);
            writer.WriteString("type", "number");
            if (minimum is { } a) writer.WriteNumber("minimum", a);
            if (maximum is { } b) writer.WriteNumber("maximum", b);
            if (exclusiveMinimum is { } c) writer.WriteNumber("exclusiveMinimum", c);
            if (exclusiveMaximum is { } d) writer.WriteNumber("exclusiveMaximum", d);
        });
    }

    /// <summary>Builds a boolean schema.</summary>
    public static JsonElement Boolean(string? description = null) => Build(writer =>
    {
        WriteDescription(writer, description);
        writer.WriteString("type", "boolean");
    });

    /// <summary>Builds an array schema. Omitting <paramref name="items"/> leaves items unconstrained.</summary>
    public static JsonElement Array(
        JsonElement? items = null,
        int? minItems = null,
        int? maxItems = null,
        bool uniqueItems = false,
        string? description = null)
    {
        if (items is { } itemSchema)
            RequireDefined(itemSchema, nameof(items));
        CheckCount(minItems, nameof(minItems));
        CheckCount(maxItems, nameof(maxItems));
        CheckRange(minItems, maxItems, nameof(minItems));
        return Build(writer =>
        {
            WriteDescription(writer, description);
            writer.WriteString("type", "array");
            if (items is { } schema)
            {
                writer.WritePropertyName("items");
                schema.WriteTo(writer);
            }
            WriteOptional(writer, "minItems", minItems);
            WriteOptional(writer, "maxItems", maxItems);
            if (uniqueItems)
                writer.WriteBoolean("uniqueItems", true);
        });
    }

    /// <summary>Builds an unconstrained schema that accepts any JSON value, including null.</summary>
    public static JsonElement Any(string? description = null) => Build(writer => WriteDescription(writer, description));

    /// <summary>
    /// Describes the wire shape of <see cref="OperationResult"/> as serialized with
    /// <see cref="JsonSerializerDefaults.Web"/> (camelCase, nulls written):
    /// success, data, errorCode, message, workspaceRevision, notices[], resources[].
    /// When <paramref name="data"/> is null the data member is unconstrained. When a
    /// data schema is supplied it describes the payload of a successful result; the
    /// bounded schema subset has no unions, so a failed result (data = null) does not
    /// validate against a typed envelope.
    /// </summary>
    public static JsonElement ResultEnvelope(JsonElement? data)
    {
        var notice = Object(
            [
                ("code", String()),
                ("message", String()),
                ("severity", String()),
            ],
            ["code", "message", "severity"]);
        var resource = Object(
            [
                ("uri", String(minLength: 1)),
                ("mimeType", String()),
                ("name", Any("Optional display name; null when absent.")),
            ],
            ["uri", "mimeType", "name"]);
        return Object(
            [
                ("success", Boolean()),
                ("data", data ?? Any("Operation-specific payload; null on failure.")),
                ("errorCode", Any("Stable error code string on failure; null on success.")),
                ("message", Any("Human-readable failure message; null on success.")),
                ("workspaceRevision", String()),
                ("notices", Array(notice)),
                ("resources", Array(resource)),
            ],
            ["success", "data", "errorCode", "message", "workspaceRevision", "notices", "resources"]);
    }

    private static JsonElement Build(Action<Utf8JsonWriter> writeBody)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writeBody(writer);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        var schema = document.RootElement.Clone();
        SelfCheck(schema);
        return schema;
    }

    /// <summary>
    /// Validates only the schema shape: the schema is wrapped as an optional property
    /// of an object schema and validated against an empty object, so the only issues
    /// that can be reported are schema-shape issues.
    /// </summary>
    private static void SelfCheck(JsonElement schema)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");
            writer.WritePropertyName("schema");
            schema.WriteTo(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        using var wrapper = JsonDocument.Parse(buffer.WrittenMemory);
        var issues = OperationArgumentValidator.Validate(EmptyObjectValue, wrapper.RootElement);
        if (issues.Count > 0)
            throw new ArgumentException(
                "Schema is not supported by OperationArgumentValidator: " +
                string.Join("; ", issues.Select(issue => $"{issue.Path.Replace("$.schema", "$", StringComparison.Ordinal)}: {issue.Message}")));
    }

    private static readonly JsonElement EmptyObjectValue = Parse("{}");

    private static void RequireDefined(JsonElement schema, string name)
    {
        if (schema.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException($"Schema for {name} is not initialized.", name);
    }

    private static void CheckCount(int? value, string name)
    {
        if (value is < 0)
            throw new ArgumentOutOfRangeException(name, "Value must be non-negative.");
    }

    private static void CheckRange(int? minimum, int? maximum, string name)
    {
        if (minimum is { } min && maximum is { } max && min > max)
            throw new ArgumentOutOfRangeException(name, "Minimum must not exceed maximum.");
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } number)
            writer.WriteNumber(name, number);
    }

    private static void WriteDescription(Utf8JsonWriter writer, string? description)
    {
        if (description is not null)
            writer.WriteString("description", description);
    }
}
