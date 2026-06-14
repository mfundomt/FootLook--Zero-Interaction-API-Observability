using FootLook.Core.Interfaces;
using FootLook.Core.Services;
using FootLook.Core.Queue;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FootLook.Core.Options;
using FootLook.Core.Sinks;
using System.ComponentModel.DataAnnotations;

namespace FootLook.Core.Extensions
{
    public static class FootLookServiceCollectionExtensions
    {
        public static IServiceCollection AddFootLook(this IServiceCollection services, Action<FootLookOptions>? configure = null)
        {
            var options = new FootLookOptions();

            configure?.Invoke(options);

            services.AddSingleton(options);

            // Register the ShadowQueue as a singleton service, meaning there will be only one instance of it throughout the application's lifetime.
            services.AddSingleton<IShadowQueue, ShadowQueue>();

            //adding concrete sinks to the service collection, so that they can be resolved and used by the ShadowBackgroundWorker to process captured requests from the queue.
            //By registering multiple implementations of IShadowSink, we can have different ways of handling the captured data, such as writing it to a file or storing it in memory.
            services.AddSingleton<FileSink>();
            services.AddSingleton<InMemorySink>();
            services.AddSingleton<CaptureEvents>();
            services.AddSingleton<IShadowCaptureStore>(provider => provider.GetRequiredService<InMemorySink>());
            services.AddSingleton<CaptureHistoryService>();
            services.AddSingleton<MongoSink>();


            //add a composite sink that combines multiple IShadowSink implementations, allowing the captured requests to be processed by all registered sinks. This way,
            //when a request is captured, it can be written to a file and stored in memory simultaneously, providing flexibility in how the captured data is handled and stored.
            services.AddSingleton<IShadowSink>(provide =>
            {
                var sinks = new IShadowSink[]
                {
                   provide.GetRequiredService<FileSink>(),
                   provide.GetRequiredService<InMemorySink>()
                };

                return new CompositeSink(sinks);
            });

            services.AddSingleton<IShadowSink>(provider =>
            {
                var options = provider.GetRequiredService<FootLookOptions>();

                var sinks = new List<IShadowSink>
            {
                 provider.GetRequiredService<InMemorySink>(),
                 provider.GetRequiredService<FileSink>()
            };

                if (options.UseMongoSink)
                {
                    sinks.Add(provider.GetRequiredService<MongoSink>());
                }

                return new CompositeSink(sinks);
            });
            // Register the ShadowBackgroundWorker as a hosted service, which will run in the background and process captured requests from the queue.
            services.AddHostedService<ShadowBackgroundWorker>();
            return services;

        }
    }
}
