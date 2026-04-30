using System.Text;
using System.Text.Json.Nodes;
using Forge.Core.Trace;

namespace Forge.Agent;

public static class SkillCatalog
{
  public const int DefaultMaxCatalogChars = 8000;

  public static string Build(ITraceSink? trace)
  {
    var skills = SkillScanner.Discover(trace);
    return Format(skills, DefaultMaxCatalogChars, trace);
  }

  public static string Format(IReadOnlyList<SkillEntry> skills, int maxChars, ITraceSink? trace)
  {
    if (skills.Count == 0)
    {
      return "";
    }

    var ordered = skills.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    var total = ordered.Count;
    while (ordered.Count > 0)
    {
      var text = BuildText(ordered);
      if (text.Length <= maxChars)
      {
        if (ordered.Count < total)
        {
          trace?.Trace(new GenericTraceEvent
          {
            Payload = new JsonObject
            {
              ["reason"] = "skills_catalog_truncated",
              ["max_chars"] = maxChars,
              ["skills_kept"] = ordered.Count,
              ["skills_total"] = total
            }
          });
        }

        return text;
      }

      ordered.RemoveAt(ordered.Count - 1);
    }

    return "";
  }

  private static string BuildText(IReadOnlyList<SkillEntry> ordered)
  {
    var sb = new StringBuilder();
    sb.AppendLine("## Available Agent Skills (catalog)");
    sb.AppendLine();
    sb.AppendLine(
      "Use filesystem tools to read each skill’s `SKILL.md` when you need full instructions (progressive disclosure).");
    sb.AppendLine();
    foreach (var s in ordered)
    {
      sb.Append("- **").Append(s.Name).Append("**: ").AppendLine(s.Description);
    }

    return sb.ToString();
  }
}
