using Microsoft.Extensions.DependencyInjection;
using Refit;

namespace Episteme.Client;

public static class EpistemeClientRegistration
{
    public static IHttpClientBuilder AddEpistemeClient(this IServiceCollection services, Uri baseAddress) =>
        services.AddRefitClient<IEpistemeApi>()
            .ConfigureHttpClient(client => client.BaseAddress = baseAddress);
}
