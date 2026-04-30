using System.Text.Json.Nodes;
using FluentAssertions;
using Forge.Core.Workspace;

namespace Forge.Core.Tests;

public class CanonicalHasherTests
{
  [Fact]
  public void Hash_produces_64_char_lowercase_hex()
  {
    var node = new JsonObject { ["a"] = 1 };
    var hash = CanonicalHasher.Hash(node);
    hash.Should().HaveLength(64);
    hash.Should().MatchRegex("^[0-9a-f]{64}$");
  }

  [Fact]
  public void Hash_is_stable_for_same_input()
  {
    var node = new JsonObject { ["a"] = 1, ["b"] = 2 };
    var hash1 = CanonicalHasher.Hash(node);
    var hash2 = CanonicalHasher.Hash(node);
    hash1.Should().Be(hash2);
  }

  [Fact]
  public void Hash_is_stable_under_key_reordering()
  {
    var node1 = new JsonObject { ["a"] = 1, ["b"] = 2 };
    var node2 = new JsonObject { ["b"] = 2, ["a"] = 1 };
    var hash1 = CanonicalHasher.Hash(node1);
    var hash2 = CanonicalHasher.Hash(node2);
    hash1.Should().Be(hash2);
  }

  [Fact]
  public void Hash_differs_for_different_values()
  {
    var node1 = new JsonObject { ["a"] = 1 };
    var node2 = new JsonObject { ["a"] = 2 };
    var hash1 = CanonicalHasher.Hash(node1);
    var hash2 = CanonicalHasher.Hash(node2);
    hash1.Should().NotBe(hash2);
  }

  [Fact]
  public void Canonicalize_sorts_object_keys()
  {
    var node = new JsonObject { ["z"] = 1, ["a"] = 2, ["m"] = 3 };
    var canonical = CanonicalHasher.Canonicalize(node);
    var keys = ((JsonObject)canonical).Select(kv => kv.Key).ToList();
    keys.Should().BeInAscendingOrder();
  }

  [Fact]
  public void Hash_handles_nested_objects()
  {
    var node = new JsonObject
    {
      ["outer"] = new JsonObject { ["inner"] = 42 }
    };
    var hash = CanonicalHasher.Hash(node);
    hash.Should().HaveLength(64);
  }

  [Fact]
  public void Hash_handles_arrays()
  {
    var node = new JsonObject
    {
      ["items"] = new JsonArray(1, 2, 3)
    };
    var hash = CanonicalHasher.Hash(node);
    hash.Should().HaveLength(64);
  }
}
