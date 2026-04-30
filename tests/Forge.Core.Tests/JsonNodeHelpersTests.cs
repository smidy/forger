using System.Text.Json.Nodes;
using FluentAssertions;
using Forge.Core.Json;

namespace Forge.Core.Tests;

public class JsonNodeHelpersTests
{
  [Fact]
  public void Int_returns_null_when_node_is_null()
  {
    JsonNodeHelpers.Int(null).Should().BeNull();
  }

  [Fact]
  public void Int_returns_value_when_node_is_int()
  {
    JsonNode node = JsonValue.Create(48);
    JsonNodeHelpers.Int(node).Should().Be(48);
  }

  [Fact]
  public void Int_coerces_long_to_int_within_range()
  {
    // YamlDotNet / Yaml2JsonNode produces numbers as long. Without coercion
    // max_iterations: 48 in YAML becomes a long and TryGetValue<int> fails.
    JsonNode node = JsonValue.Create(48L);
    JsonNodeHelpers.Int(node).Should().Be(48);
  }

  [Fact]
  public void Int_returns_null_when_long_overflows_int()
  {
    JsonNode node = JsonValue.Create((long)int.MaxValue + 1);
    JsonNodeHelpers.Int(node).Should().BeNull();
  }

  [Fact]
  public void Int_returns_null_when_long_underflows_int()
  {
    JsonNode node = JsonValue.Create((long)int.MinValue - 1);
    JsonNodeHelpers.Int(node).Should().BeNull();
  }

  [Fact]
  public void Int_coerces_decimal_to_int_when_integral()
  {
    // Yaml2JsonNode stores YAML integers as System.Decimal.
    JsonNode node = JsonValue.Create(48m);
    JsonNodeHelpers.Int(node).Should().Be(48);
  }

  [Fact]
  public void Int_returns_null_for_decimal_with_fraction()
  {
    JsonNode node = JsonValue.Create(48.5m);
    JsonNodeHelpers.Int(node).Should().BeNull();
  }

  [Fact]
  public void Int_returns_null_for_string_node()
  {
    JsonNode node = JsonValue.Create("48");
    JsonNodeHelpers.Int(node).Should().BeNull();
  }
}
