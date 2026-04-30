using Forge.Tools.Docker;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Tools;

public static class BuiltInToolsRegistration
{
  public static IServiceCollection AddForgeBuiltInTools(this IServiceCollection services)
  {
    services.AddSingleton<FetchUrlTool>();
    services.AddSingleton<WebSearchTool>();
    services.AddSingleton<AskCallerTool>();
    services.AddSingleton<NotifyCallerTool>();
    services.AddSingleton<RequestApprovalTool>();
    services.AddSingleton<LlmCompleteTool>();
    services.AddSingleton<IDockerCli>(_ => new DockerProcessCli());
    services.AddSingleton<IBashContainerLifecycle, DockerContainerLifecycle>();
    services.AddSingleton<BashTool>();
    services.AddHttpClient("forge_fetch", c => { c.Timeout = TimeSpan.FromSeconds(30); })
      .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
      {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5
      });
    services.AddHttpClient("forge_search", c => { c.Timeout = TimeSpan.FromSeconds(30); });
    services.AddSingleton(sp =>
    {
      var r = new ToolRegistry();
      r.Register(sp.GetRequiredService<FetchUrlTool>());
      r.Register(sp.GetRequiredService<AskCallerTool>());
      r.Register(sp.GetRequiredService<NotifyCallerTool>());
      r.Register(sp.GetRequiredService<RequestApprovalTool>());
      r.Register(sp.GetRequiredService<WebSearchTool>());
      r.Register(sp.GetRequiredService<LlmCompleteTool>());
      r.Register(sp.GetRequiredService<BashTool>());
      return r;
    });
    return services;
  }
}
