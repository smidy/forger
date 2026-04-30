using System.Net.Http.Headers;
using Forge.Core.Llm;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Llm;

public static class ServiceCollectionExtensions
{
  public static IServiceCollection AddLiteLlm(this IServiceCollection services, LiteLlmConfig config)
  {
    services.AddSingleton(config);
    services.AddHttpClient<ILlmClient, LiteLlmClient>((sp, http) =>
    {
      var c = sp.GetRequiredService<LiteLlmConfig>();
      http.BaseAddress = new Uri(c.BaseUrl.TrimEnd('/') + "/");
      http.Timeout = TimeSpan.FromSeconds(300);
      http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", c.ApiKey);
    });
    return services;
  }
}
