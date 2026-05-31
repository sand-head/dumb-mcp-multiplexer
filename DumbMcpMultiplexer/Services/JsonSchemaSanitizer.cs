using System.Text.Json;

namespace DumbMcpMultiplexer.Services;

/// <summary>
/// Sanitizes JSON Schemas from upstream MCP servers to ensure compatibility
/// with strict clients (e.g. Home Assistant) that require every schema object
/// to have an explicit "type" field and do not support $ref/$defs.
///
/// Known HA limitations addressed:
/// - Empty {} schemas (no type) inside anyOf/oneOf/allOf
/// - $ref/$defs references (must be inlined/resolved)
/// </summary>
public static class JsonSchemaSanitizer
{
    /// <summary>
    /// Recursively walks a JSON Schema element and fixes known compatibility issues:
    /// - Resolves $ref references using $defs definitions
    /// - Removes empty {} entries (typeless "any" schemas) from anyOf/oneOf/allOf
    /// - If only one entry remains after removal, collapses the combinator into the parent
    /// - Strips $defs from the output (no longer needed after inlining)
    /// </summary>
    public static JsonElement Sanitize(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return schema;

        // Extract $defs for ref resolution
        Dictionary<string, JsonElement>? defs = null;
        if (schema.TryGetProperty("$defs", out var defsElement) && defsElement.ValueKind == JsonValueKind.Object)
        {
            defs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var def in defsElement.EnumerateObject())
            {
                defs[def.Name] = def.Value;
            }
        }

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            WriteObject(writer, schema, defs, skipDefs: true);
        }

        ms.Position = 0;
        using var doc = JsonDocument.Parse(ms);
        return doc.RootElement.Clone();
    }

    private static void WriteObject(Utf8JsonWriter writer, JsonElement obj, Dictionary<string, JsonElement>? defs, bool skipDefs = false)
    {
        // If this object is a $ref, resolve it
        if (obj.TryGetProperty("$ref", out var refValue) && refValue.ValueKind == JsonValueKind.String)
        {
            var resolved = ResolveRef(refValue.GetString()!, defs);
            if (resolved is not null)
            {
                // Merge any sibling properties (like "description") from the $ref-containing object
                // with the resolved definition
                var hasSiblings = false;
                foreach (var prop in obj.EnumerateObject())
                {
                    if (prop.Name != "$ref")
                    {
                        hasSiblings = true;
                        break;
                    }
                }

                if (hasSiblings && resolved.Value.ValueKind == JsonValueKind.Object)
                {
                    // Write merged object: resolved props + sibling props
                    writer.WriteStartObject();
                    // Write resolved properties first
                    foreach (var prop in resolved.Value.EnumerateObject())
                    {
                        WriteSanitizedProperty(writer, prop, defs);
                    }
                    // Then write sibling properties that aren't already present
                    foreach (var prop in obj.EnumerateObject())
                    {
                        if (prop.Name == "$ref") continue;
                        if (!HasProperty(resolved.Value, prop.Name))
                        {
                            WriteSanitizedProperty(writer, prop, defs);
                        }
                    }
                    writer.WriteEndObject();
                }
                else
                {
                    WriteObject(writer, resolved.Value, defs);
                }
                return;
            }
        }

        writer.WriteStartObject();

        // Pre-compute collapsed properties to avoid duplicates
        var collapsedProps = GetCollapsedProperties(obj);

        foreach (var prop in obj.EnumerateObject())
        {
            // Strip $defs from output (already resolved inline)
            if (skipDefs && prop.Name == "$defs")
                continue;

            // Skip $ref (already handled above - if we get here, resolution failed)
            if (prop.Name == "$ref")
                continue;

            if (IsCombinatorKeyword(prop.Name) && prop.Value.ValueKind == JsonValueKind.Array)
            {
                WriteSanitizedCombinator(writer, obj, prop.Name, prop.Value, collapsedProps, defs);
            }
            else if (collapsedProps is not null && collapsedProps.Contains(prop.Name))
            {
                // Skip — will be written by the collapsed combinator entry
                continue;
            }
            else
            {
                WriteSanitizedProperty(writer, prop, defs);
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteSanitizedProperty(Utf8JsonWriter writer, JsonProperty prop, Dictionary<string, JsonElement>? defs)
    {
        writer.WritePropertyName(prop.Name);

        if (prop.Value.ValueKind == JsonValueKind.Object)
        {
            if (prop.Name == "properties")
                WritePropertiesObject(writer, prop.Value, defs);
            else
                WriteObject(writer, prop.Value, defs);
        }
        else if (prop.Value.ValueKind == JsonValueKind.Array && prop.Name == "items")
        {
            // items can be an array of schemas
            writer.WriteStartArray();
            foreach (var item in prop.Value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                    WriteObject(writer, item, defs);
                else
                    item.WriteTo(writer);
            }
            writer.WriteEndArray();
        }
        else
        {
            prop.Value.WriteTo(writer);
        }
    }

    /// <summary>
    /// Pre-computes which property names will be injected by a collapsed combinator,
    /// so we can skip those in the main property loop to avoid duplicates.
    /// </summary>
    private static HashSet<string>? GetCollapsedProperties(JsonElement obj)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (!IsCombinatorKeyword(prop.Name) || prop.Value.ValueKind != JsonValueKind.Array)
                continue;

            var validEntries = new List<JsonElement>();
            foreach (var entry in prop.Value.EnumerateArray())
            {
                if (!IsEmptySchema(entry))
                    validEntries.Add(entry);
            }

            if (validEntries.Count == 1 && validEntries[0].ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entryProp in validEntries[0].EnumerateObject())
                    names.Add(entryProp.Name);
                return names;
            }
        }

        return null;
    }

    private static void WritePropertiesObject(Utf8JsonWriter writer, JsonElement properties, Dictionary<string, JsonElement>? defs)
    {
        writer.WriteStartObject();
        foreach (var prop in properties.EnumerateObject())
        {
            writer.WritePropertyName(prop.Name);
            if (prop.Value.ValueKind == JsonValueKind.Object)
                WriteObject(writer, prop.Value, defs);
            else
                prop.Value.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static void WriteSanitizedCombinator(
        Utf8JsonWriter writer,
        JsonElement parent,
        string keyword,
        JsonElement array,
        HashSet<string>? collapsedProps,
        Dictionary<string, JsonElement>? defs)
    {
        // Collect non-empty entries
        var validEntries = new List<JsonElement>();
        foreach (var entry in array.EnumerateArray())
        {
            if (!IsEmptySchema(entry))
                validEntries.Add(entry);
        }

        if (validEntries.Count == 0)
        {
            // All entries were empty — don't constrain the type
            // Skip writing the combinator keyword entirely
            return;
        }

        if (validEntries.Count == 1 && collapsedProps is not null)
        {
            // Collapse: merge the single remaining entry's properties into the parent level
            var entry = validEntries[0];
            if (entry.ValueKind == JsonValueKind.Object)
            {
                foreach (var entryProp in entry.EnumerateObject())
                {
                    // Only write properties that won't conflict with already-written parent properties
                    if (!HasProperty(parent, entryProp.Name) || collapsedProps.Contains(entryProp.Name))
                    {
                        WriteSanitizedProperty(writer, entryProp, defs);
                    }
                }
            }
            return;
        }

        // Multiple valid entries remain — keep the combinator but recurse into each entry
        writer.WritePropertyName(keyword);
        writer.WriteStartArray();
        foreach (var entry in validEntries)
        {
            if (entry.ValueKind == JsonValueKind.Object)
                WriteObject(writer, entry, defs);
            else
                entry.WriteTo(writer);
        }
        writer.WriteEndArray();
    }

    private static JsonElement? ResolveRef(string refPath, Dictionary<string, JsonElement>? defs)
    {
        if (defs is null) return null;

        // Handle "#/$defs/Name" format
        const string prefix = "#/$defs/";
        if (refPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            var defName = refPath[prefix.Length..];
            if (defs.TryGetValue(defName, out var resolved))
                return resolved;
        }

        return null;
    }

    private static bool IsCombinatorKeyword(string name) =>
        name is "anyOf" or "oneOf" or "allOf";

    private static bool IsEmptySchema(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var _ in element.EnumerateObject())
            return false;

        return true;
    }

    private static bool HasProperty(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return false;
        return obj.TryGetProperty(propertyName, out _);
    }
}
