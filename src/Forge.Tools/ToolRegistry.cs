using Forge.Core.Exceptions;

namespace Forge.Tools;

public sealed class ToolRegistry
{
  private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

  public void Register(ITool tool) => _tools[tool.Name] = tool;

  public ITool? Get(string name) => _tools.GetValueOrDefault(name);

  public ITool Require(string name) =>
    Get(name) ?? throw new ConfigException($"Unknown tool: {name}");

  public IReadOnlyList<ToolDescriptor> List() =>
    _tools.Values.Select(t => new ToolDescriptor(t.Name, t.Description)).ToList();
}
