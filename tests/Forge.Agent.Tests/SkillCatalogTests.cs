using FluentAssertions;
using Forge.Agent;

namespace Forge.Agent.Tests;

public class SkillCatalogTests
{
  [Fact]
  public void Format_includes_sorted_names()
  {
    var skills = new[]
    {
      new SkillEntry("zebra", "z", ""),
      new SkillEntry("alpha", "a", "")
    };
    var text = SkillCatalog.Format(skills, SkillCatalog.DefaultMaxCatalogChars, null);
    text.Should().Contain("alpha");
    text.Should().Contain("zebra");
    text.IndexOf("alpha", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("zebra", StringComparison.Ordinal));
  }

  [Fact]
  public void Format_truncates_entry_count_to_fit_budget()
  {
    var skills = Enumerable.Range(0, 50)
      .Select(i => new SkillEntry($"skill-{i}", new string('x', 400), ""))
      .ToList();
    var text = SkillCatalog.Format(skills, maxChars: 1200, trace: null);
    text.Length.Should().BeLessThanOrEqualTo(1200);
    text.Should().Contain("skill-");
  }
}
