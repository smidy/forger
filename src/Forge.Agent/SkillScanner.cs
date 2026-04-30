using System.Text.Json.Nodes;
using Forge.Core.Filesystem;
using Forge.Core.Json;
using Forge.Core.Trace;

namespace Forge.Agent;

public sealed record SkillEntry(string Name, string Description, string SkillMdPath);

public static class SkillScanner
{
  /// <summary>
  /// Discovers skills in precedence order (lowest first; later overwrites on name collision).
  /// User <c>.agents</c> → user <c>.claude</c> → project <c>.agents</c> → project <c>.claude</c>.
  /// </summary>
  public static IReadOnlyList<SkillEntry> Discover(ITraceSink? trace)
  {
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var cwd = RuntimePaths.ProcessStartedDirectory;
    var scans = new (string Root, string Label)[]
    {
      (Path.Combine(home, ".agents", "skills"), "user.agents"),
      (Path.Combine(home, ".claude", "skills"), "user.claude"),
      (Path.Combine(cwd, ".agents", "skills"), "project.agents"),
      (Path.Combine(cwd, ".claude", "skills"), "project.claude")
    };

    var byName = new Dictionary<string, SkillEntry>(StringComparer.OrdinalIgnoreCase);
    foreach (var (root, _) in scans)
    {
      if (!Directory.Exists(root))
      {
        continue;
      }

      foreach (var dir in Directory.GetDirectories(root))
      {
        var skillFile = Path.Combine(dir, "SKILL.md");
        if (!File.Exists(skillFile))
        {
          continue;
        }

        try
        {
          var text = File.ReadAllText(skillFile);
          if (!TryParseFrontmatter(text, out var name, out var description))
          {
            trace?.Trace(new GenericTraceEvent
            {
              Payload = new JsonObject
              {
                ["reason"] = "skill_frontmatter_invalid",
                ["path"] = skillFile
              }
            });
            continue;
          }

          byName[name] = new SkillEntry(name, description, skillFile);
        }
        catch (Exception ex)
        {
          trace?.Trace(new GenericTraceEvent
          {
            Payload = new JsonObject
            {
              ["reason"] = "skill_scan_error",
              ["path"] = skillFile,
              ["error"] = ex.Message
            }
          });
        }
      }
    }

    return byName.Values
      .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
      .ToList();
  }

  private static bool TryParseFrontmatter(string fileText, out string name, out string description)
  {
    name = "";
    description = "";
    using var r = new StringReader(fileText);
    if (r.ReadLine() is not { } first || first.Trim() != "---")
    {
      return false;
    }

    var yaml = new System.Text.StringBuilder();
    while (true)
    {
      var line = r.ReadLine();
      if (line is null)
      {
        return false;
      }

      if (line.Trim() == "---")
      {
        break;
      }

      yaml.AppendLine(line);
    }

    JsonNode json;
    try
    {
      json = YamlFront.ParseToJson(yaml.ToString());
    }
    catch
    {
      return false;
    }

    if (json is not JsonObject o)
    {
      return false;
    }

    name = JsonNodeHelpers.Str(o["name"]).Trim();
    description = JsonNodeHelpers.Str(o["description"]).Trim();
    return !string.IsNullOrEmpty(name);
  }
}
