using System.Reflection;
using System.Text.Json;
using Forge.Core.Types;

namespace Forge.Core.Pricing;

public sealed class PricingTable
{
  private readonly Dictionary<string, ModelPricing> _byModel = new(StringComparer.OrdinalIgnoreCase);

  public static async Task<PricingTable> LoadOrCreateDefaultAsync(string forgeHome, CancellationToken ct = default)
  {
    var path = Path.Combine(forgeHome, "pricing.json");
    if (!File.Exists(path))
    {
      Directory.CreateDirectory(forgeHome);
      await ExtractEmbeddedDefaultAsync(path, ct).ConfigureAwait(false);
    }

    await using var fs = File.OpenRead(path);
    var doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct).ConfigureAwait(false);
    var table = new PricingTable();
    foreach (var p in doc.RootElement.EnumerateObject())
    {
      var input = p.Value.GetProperty("input_per_mtok_usd").GetDecimal();
      var output = p.Value.GetProperty("output_per_mtok_usd").GetDecimal();
      table._byModel[p.Name] = new ModelPricing(input, output);
    }

    return table;
  }

  private static async Task ExtractEmbeddedDefaultAsync(string path, CancellationToken ct)
  {
    var asm = Assembly.GetExecutingAssembly();
    await using var res = asm.GetManifestResourceStream("Forge.Core.default-pricing.json")
                     ?? throw new InvalidOperationException("Missing embedded default-pricing.json");
    await using var fs = File.Create(path);
    await res.CopyToAsync(fs, ct).ConfigureAwait(false);
  }

  public decimal CostUsd(string model, Usage usage)
  {
    if (!_byModel.TryGetValue(model, out var p))
    {
      return 0m;
    }

    var inCost = usage.PromptTokens / 1_000_000m * p.InputPerMtokUsd;
    var outCost = usage.CompletionTokens / 1_000_000m * p.OutputPerMtokUsd;
    return inCost + outCost;
  }

  private readonly record struct ModelPricing(decimal InputPerMtokUsd, decimal OutputPerMtokUsd);
}
