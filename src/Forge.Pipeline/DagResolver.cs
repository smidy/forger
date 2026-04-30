using Forge.Core.Exceptions;

namespace Forge.Pipeline;

public sealed class ResolvedDag
{
  public required List<List<StageConfig>> Groups { get; init; }
}

public static class DagResolver
{
  public static ResolvedDag Resolve(PipelineConfig config)
  {
    var stages = config.Stages.ToDictionary(s => s.Id, StringComparer.Ordinal);
    foreach (var s in config.Stages)
    {
      foreach (var d in s.DependsOn)
      {
        if (!stages.ContainsKey(d))
        {
          throw new ConfigException($"Stage '{s.Id}' depends on unknown stage '{d}'.");
        }
      }
    }

    var remaining = new HashSet<string>(stages.Keys, StringComparer.Ordinal);
    var groups = new List<List<StageConfig>>();
    while (remaining.Count > 0)
    {
      var ready = remaining.Where(id => stages[id].DependsOn.All(d => !remaining.Contains(d))).ToList();
      if (ready.Count == 0)
      {
        throw new PipelineException("Pipeline has a cycle or unsatisfiable dependencies.");
      }

      groups.Add(ready.Select(id => stages[id]).ToList());
      foreach (var id in ready)
      {
        remaining.Remove(id);
      }
    }

    return new ResolvedDag { Groups = groups };
  }
}
