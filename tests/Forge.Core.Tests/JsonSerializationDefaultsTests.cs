using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Forge.Core.Json;

namespace Forge.Core.Tests;

/// <summary>
/// Regression coverage for F-J (<c>docs/plans/eval-context-auto-compaction-fixes.md</c>):
/// the tool-facing <see cref="JsonSerializationDefaults"/> options must not HTML-escape
/// <c>&lt;</c>, <c>&gt;</c>, <c>+</c>, or <c>&amp;</c>. The tool-call <c>content</c> field is
/// a STRING embedded inside the chat-completions request body, so the LLM reads it as
/// literal text. If the default <c>JavaScriptEncoder.Default</c> leaks through,
/// <c>read_file</c> → <c>apply_patch</c> roundtrips break because the model echoes back
/// <c>\u003C</c> sequences that no longer match the on-disk content.
/// </summary>
public class JsonSerializationDefaultsTests
{
  [Theory]
  [InlineData('<')]
  [InlineData('>')]
  [InlineData('+')]
  [InlineData('&')]
  [InlineData('=')]
  [InlineData('\'')]
  public void CamelCaseTool_does_not_escape_html_unsafe_chars(char ch)
  {
    var payload = new { content = $"namespace Foo {{ var x = \"{ch}bit{ch}\"; }}" };

    var json = JsonSerializer.Serialize(payload, JsonSerializationDefaults.CamelCaseTool);

    json.Should().Contain(ch.ToString(), "the literal character must round-trip so apply_patch context lines still match the on-disk file");
    json.Should().NotContain($"\\u00{(int)ch:X2}", "HTML-safe escaping leaks \\uXXXX sequences the model treats as literal text");
  }

  [Fact]
  public void ToJsonString_with_CamelCaseTool_does_not_escape_angle_brackets()
  {
    var node = new JsonObject { ["content"] = "List<Foo> xs = new(); var b = a + 1;" };

    var json = node.ToJsonString(JsonSerializationDefaults.CamelCaseTool);

    json.Should().Contain("List<Foo>");
    json.Should().Contain("a + 1");
    json.Should().NotContain("\\u003C");
    json.Should().NotContain("\\u003E");
    json.Should().NotContain("\\u002B");
  }

  [Fact]
  public void Trace_options_do_not_escape_html_unsafe_chars()
  {
    var evt = new { code = "if (x < y) { y += 1; }" };
    var json = JsonSerializer.Serialize(evt, JsonSerializationDefaults.Trace);
    json.Should().Contain("if (x < y)", "trace.jsonl is consumed by analyser tooling that expects literal punctuation");
  }
}
