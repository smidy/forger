using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Forge.Core.Types;

namespace Forge.Tools;

public sealed class WebSearchInput
{
  public required string Query { get; init; }
}

public sealed class WebSearchResultItem
{
  public required string Url { get; init; }
  public required string Title { get; init; }
  public required string Snippet { get; init; }
}

public sealed class WebSearchOutput
{
  public required IReadOnlyList<WebSearchResultItem> Results { get; init; }
}

public sealed class WebSearchTool : ToolBase<WebSearchInput, WebSearchOutput>
{
  private readonly IHttpClientFactory _httpFactory;

  public WebSearchTool(IHttpClientFactory httpFactory) => _httpFactory = httpFactory;

  public override string Name => "web_search";
  public override string Description => "Search the web (Brave API when FORGE_SEARCH_BRAVE_KEY is set).";

  protected override async Task<WebSearchOutput> ExecuteCoreAsync(WebSearchInput input, ToolContext ctx, CancellationToken cancellationToken)
  {
    var provider = Environment.GetEnvironmentVariable("FORGE_SEARCH_PROVIDER") ?? "brave";
    if (!string.Equals(provider, "brave", StringComparison.OrdinalIgnoreCase))
    {
      throw new InvalidOperationException($"Search provider '{provider}' is not supported in v1.");
    }

    var key = Environment.GetEnvironmentVariable("FORGE_SEARCH_BRAVE_KEY");
    if (string.IsNullOrEmpty(key))
    {
      throw new InvalidOperationException("FORGE_SEARCH_BRAVE_KEY is not set.");
    }

    var http = _httpFactory.CreateClient("forge_search");
    var url = "https://api.search.brave.com/res/v1/web/search?q=" + Uri.EscapeDataString(input.Query);
    using var req = new HttpRequestMessage(HttpMethod.Get, url);
    req.Headers.TryAddWithoutValidation("X-Subscription-Token", key);
    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    using var resp = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);
    resp.EnsureSuccessStatusCode();
    using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
    var node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    var results = new List<WebSearchResultItem>();
    var web = node?["web"]?["results"] as JsonArray;
    if (web is not null)
    {
      foreach (var item in web)
      {
        if (item is not JsonObject o)
        {
          continue;
        }

        results.Add(new WebSearchResultItem
        {
          Url = o["url"]?.GetValue<string>() ?? "",
          Title = o["title"]?.GetValue<string>() ?? "",
          Snippet = o["description"]?.GetValue<string>() ?? ""
        });
      }
    }

    return new WebSearchOutput { Results = results };
  }
}
