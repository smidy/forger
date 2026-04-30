using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Forge.Core.Workspace;

/// <summary>
/// Canonical JSON hashing and normalization.
/// </summary>
public static class CanonicalHasher
{
    /// <summary>
    /// SHA-256 hash of canonical JSON representation of <paramref name="node"/>, as lowercase hex.
    /// </summary>
    public static string Hash(JsonNode node)
    {
        var sorted = Canonicalize(node);
        var json = sorted.ToJsonString();
        var bytes = Encoding.UTF8.GetBytes(json);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>
    /// Canonical JSON — sorted object keys, minified whitespace, UTF-8 byte output.
    /// </summary>
    public static JsonNode Canonicalize(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var sorted = new JsonObject();
            foreach (var kv in obj.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                sorted[kv.Key] = kv.Value is null ? null : Canonicalize(kv.Value);
            }

            return sorted;
        }

        if (node is JsonArray arr)
        {
            var result = new JsonArray();
            foreach (var item in arr)
            {
                result.Add(item is null ? null : Canonicalize(item));
            }

            return result;
        }

        return node.DeepClone();
    }
}