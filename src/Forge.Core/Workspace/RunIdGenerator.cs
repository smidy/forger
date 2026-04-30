namespace Forge.Core.Workspace;

public static class RunIdGenerator
{
  public static string Generate(string pipelineName)
  {
    var slug = string.Concat(pipelineName.Select(c => char.IsLetterOrDigit(c) ? c : '-')).Trim('-');
    if (string.IsNullOrEmpty(slug))
    {
      slug = "run";
    }

    var ts = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
    var id = Guid.NewGuid().ToString("N")[..8];
    return $"{slug}-{ts}-{id}";
  }
}
