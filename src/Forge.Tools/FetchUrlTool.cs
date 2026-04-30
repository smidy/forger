using System.Text;
using Forge.Core.Types;

namespace Forge.Tools;

public sealed class FetchUrlInput
{
  public required string Url { get; init; }
}

public sealed class FetchUrlOutput
{
  public required int Status { get; init; }
  public required string ContentType { get; init; }
  public required string Body { get; init; }
}

public sealed class FetchUrlTool : ToolBase<FetchUrlInput, FetchUrlOutput>
{
  private readonly IHttpClientFactory _httpFactory;

  public FetchUrlTool(IHttpClientFactory httpFactory) => _httpFactory = httpFactory;

  public override string Name => "fetch_url";
  public override string Description => "HTTP GET a URL; returns status, content-type, and body text.";

  protected override async Task<FetchUrlOutput> ExecuteCoreAsync(FetchUrlInput input, ToolContext ctx, CancellationToken cancellationToken)
  {
    var http = _httpFactory.CreateClient("forge_fetch");
    using var req = new HttpRequestMessage(HttpMethod.Get, input.Url);
    using var resp = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);
    var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    var text = Encoding.UTF8.GetString(bytes);
    var ct = resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
    return new FetchUrlOutput
    {
      Status = (int)resp.StatusCode,
      ContentType = ct,
      Body = text
    };
  }
}
