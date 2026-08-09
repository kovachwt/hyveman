using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hyveman.Protocol;

/// <summary>
/// Minimal JSON Schema draft-07 validator covering the keyword subset used by
/// docs/schemas/protocol-v1.json: $ref (local), type (incl. arrays and null),
/// required, properties, additionalProperties, const, enum, pattern,
/// minLength/maxLength, minimum/maximum, minItems/maxItems, items, oneOf and
/// boolean schemas. Runs in the protocol's forward-compatible mode: unknown
/// instance members are allowed (additionalProperties defaults to true) and
/// unknown schema keywords are ignored (PROTOCOL.md §3 additive rule; §6.7).
/// </summary>
public sealed class SchemaValidator
{
    private readonly JsonObject _root;

    private SchemaValidator(JsonObject root) => _root = root;

    public static SchemaValidator FromJson(string json) =>
        new(JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("Schema root must be an object"));

    /// <summary>True when the instance matches the schema.</summary>
    public bool IsValid(string schemaId, JsonElement instance)
    {
        var schema = Resolve(schemaId);
        return schema is not null && Validate(schema, JsonNode.Parse(instance.GetRawText()) ?? JsonNode.Parse("null")!, "/").Count == 0;
    }

    /// <summary>All validation errors; empty when valid.</summary>
    public IReadOnlyList<string> Validate(string schemaId, string instanceJson)
    {
        var schema = Resolve(schemaId);
        if (schema is null) return [$"unknown schema id {schemaId}"];
        var node = JsonNode.Parse(instanceJson) ?? JsonNode.Parse("null")!;
        if (node is null) return ["invalid JSON"];
        return Validate(schema, node, "/");
    }

    private JsonNode? Resolve(string schemaId)
    {
        if (schemaId == "#") return _root;
        if (schemaId.StartsWith("#/definitions/", StringComparison.Ordinal))
        {
            if (_root["definitions"] is not JsonObject defs) return null;
            var parts = schemaId["#/definitions/".Length..].Split('/');
            JsonNode? cur = defs;
            foreach (var p in parts)
            {
                if (cur is not JsonObject obj || !obj.TryGetPropertyValue(p, out cur)) return null;
            }
            return cur;
        }
        return null;
    }

    private List<string> Validate(JsonNode? schemaNode, JsonNode? instance, string path)
    {
        var errors = new List<string>();
        // .NET 10 STJ represents JSON null as a C# null reference everywhere
        // (JsonNode.Parse("null") and JsonValue.Create(null) both return null,
        // and null object members / array elements surface as null). So a null
        // `instance` IS the JSON null literal — TypeMatches handles it; never
        // dereference instance without a null check.
        if (schemaNode is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b))
            {
                if (!b) errors.Add($"{path}: boolean schema false");
            }
            return errors;
        }
        if (schemaNode is not JsonObject schema) return errors;

        if (schema.TryGetPropertyValue("$ref", out var refNode) && refNode is JsonValue rv && rv.TryGetValue<string>(out var refId))
        {
            var target = Resolve(refId);
            if (target is null) { errors.Add($"{path}: unresolved $ref {refId}"); return errors; }
            return Validate(target, instance, path);
        }

        if (schema.TryGetPropertyValue("type", out var typeNode))
        {
            var ok = typeNode is JsonValue tv && tv.TryGetValue<string>(out var t)
                ? TypeMatches(t, instance)
                : typeNode is JsonArray ta && ta.Any(n => n is JsonValue x && x.TryGetValue<string>(out var s) && TypeMatches(s, instance));
            if (!ok) errors.Add($"{path}: type mismatch");
        }

        if (schema.TryGetPropertyValue("const", out var constNode))
        {
            if (!JsonNode.DeepEquals(constNode, instance)) errors.Add($"{path}: value does not equal const");
        }

        if (schema.TryGetPropertyValue("enum", out var enumNode) && enumNode is JsonArray arr)
        {
            if (!arr.Any(n => JsonNode.DeepEquals(n, instance))) errors.Add($"{path}: value not in enum");
        }

        if (instance is JsonObject obj)
        {
            if (schema.TryGetPropertyValue("required", out var reqNode) && reqNode is JsonArray req)
            {
                foreach (var r in req)
                {
                    if (r is JsonValue rv2 && rv2.TryGetValue<string>(out var name) && !obj.ContainsKey(name))
                        errors.Add($"{path}: missing required property '{name}'");
                }
            }
            if (schema.TryGetPropertyValue("properties", out var propsNode) && propsNode is JsonObject props)
            {
                foreach (var (name, propSchema) in props)
                {
                    if (obj.TryGetPropertyValue(name, out var child))
                        errors.AddRange(Validate(propSchema, child!, $"{path}{name}"));
                }
            }
            if (schema.TryGetPropertyValue("additionalProperties", out var apNode) && apNode is JsonValue apv
                && apv.TryGetValue<bool>(out var allow) && !allow)
            {
                if (schema.TryGetPropertyValue("properties", out var props2) && props2 is JsonObject props3)
                {
                    foreach (var (name, _) in obj)
                    {
                        if (!props3.ContainsKey(name)) errors.Add($"{path}: unknown property '{name}' not allowed");
                    }
                }
            }
            else if (apNode is JsonObject apSchema)
            {
                foreach (var (name, child) in obj)
                    errors.AddRange(Validate(apSchema, child!, $"{path}{name}"));
            }
        }

        if (instance is JsonArray arr2)
        {
            if (schema.TryGetPropertyValue("minItems", out var minNode) && minNode is JsonValue minv
                && minv.TryGetValue<int>(out var min) && arr2.Count < min)
                errors.Add($"{path}: fewer than {min} items");
            if (schema.TryGetPropertyValue("maxItems", out var maxNode) && maxNode is JsonValue maxv
                && maxv.TryGetValue<int>(out var max) && arr2.Count > max)
                errors.Add($"{path}: more than {max} items");
            if (schema.TryGetPropertyValue("items", out var itemsNode))
            {
                for (var i = 0; i < arr2.Count; i++)
                    errors.AddRange(Validate(itemsNode, arr2[i]!, $"{path}{i}/"));
            }
        }

        if (instance is JsonValue scalar)
        {
            if (scalar.TryGetValue<string>(out var s))
            {
                if (schema.TryGetPropertyValue("minLength", out var minL) && minL is JsonValue minLv
                    && minLv.TryGetValue<int>(out var ml) && s.Length < ml)
                    errors.Add($"{path}: string shorter than {ml}");
                if (schema.TryGetPropertyValue("maxLength", out var maxL) && maxL is JsonValue maxLv
                    && maxLv.TryGetValue<int>(out var xl) && s.Length > xl)
                    errors.Add($"{path}: string longer than {xl}");
                if (schema.TryGetPropertyValue("pattern", out var pat) && pat is JsonValue patv
                    && patv.TryGetValue<string>(out var p) && !Regex.IsMatch(s, p))
                    errors.Add($"{path}: string does not match pattern");
            }
            if (scalar.TryGetValue<double>(out var d))
            {
                if (schema.TryGetPropertyValue("minimum", out var minNode) && minNode is JsonValue minv
                    && minv.TryGetValue<double>(out var mn) && d < mn)
                    errors.Add($"{path}: value below minimum {mn}");
                if (schema.TryGetPropertyValue("maximum", out var maxNode) && maxNode is JsonValue maxv
                    && maxv.TryGetValue<double>(out var mx) && d > mx)
                    errors.Add($"{path}: value above maximum {mx}");
            }
        }

        if (schema.TryGetPropertyValue("oneOf", out var oneOfNode) && oneOfNode is JsonArray oneOf)
        {
            var matches = 0;
            var branchFailures = new List<string>();
            foreach (var branch in oneOf)
            {
                var branchErrors = Validate(branch, instance, path);
                if (branchErrors.Count == 0) matches++;
                else branchFailures.Add($"branch[{oneOf.IndexOf(branch)}]: {string.Join("; ", branchErrors.Take(3))}");
            }
            if (matches != 1)
            {
                errors.Add($"{path}: matches {matches} oneOf branches (expected exactly 1)");
                errors.AddRange(branchFailures.Take(matches == 0 ? 6 : 2));
            }
        }

        return errors;
    }

    private static bool TypeMatches(string type, JsonNode instance) => type switch
    {
        "object" => instance is JsonObject,
        "array" => instance is JsonArray,
        "string" => instance is JsonValue v && v.TryGetValue<string>(out _),
        "integer" => instance is JsonValue v2 && v2.TryGetValue<double>(out var d) && d == Math.Truncate(d),
        "number" => instance is JsonValue v3 && v3.TryGetValue<double>(out _),
        "boolean" => instance is JsonValue v4 && v4.TryGetValue<bool>(out _),
        "null" => instance is null || instance.GetValueKind() == JsonValueKind.Null,
        _ => true,
    };
}
