using FootLook.Core.Interfaces;
using FootLook.Core.Options;
using FootLook.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace FootLook.Data.Extensions;

public static class FootLookDataServiceCollectionExtensions
{
    public static IServiceCollection AddFootLookMongoRepository(
        this IServiceCollection services)
    {
        services.AddSingleton<ICaptureRepository, MongoCaptureRepository>();

        return services;
    }
}