using FootLook.Core.Interfaces;
using FootLook.Core.Services;
using FootLook.Core.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FootLook.Core.Options;
using FootLook.Core.Sinks;
using FootLook.Core.Security;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FootLook.Core.Extensions
{
    public static class FootLookServiceCollectionExtensions
    {
        public static IServiceCollection AddFootLook(this IServiceCollection services, Action<FootLookOptions>? configure = null)
        {
            var options = new FootLookOptions();

            configure?.Invoke(options);

            services.AddSingleton(options);
            services.AddSingleton<CaptureRuntimeState>();

            // Register the ShadowQueue as a singleton service, meaning there will be only one instance of it throughout the application's lifetime.
            services.AddSingleton<IShadowQueue, ShadowQueue>();

            //adding concrete sinks to the service collection, so that they can be resolved and used by the ShadowBackgroundWorker to process captured requests from the queue.
            //By registering multiple implementations of IShadowSink, we can have different ways of handling the captured data, such as writing it to a file or storing it in memory.
            services.AddSingleton<FileSink>();
            services.AddSingleton<InMemorySink>();
            services.AddSingleton<CaptureEvents>();
            services.AddSingleton<PrivacyAuditStore>();
            services.AddSingleton<CaptureReliabilityState>();
            services.AddSingleton<FootLookDeveloperExperienceService>();
            services.AddSingleton<ProductOutcomeMetricsService>();
            services.AddSingleton<IShadowCaptureStore>(provider => provider.GetRequiredService<InMemorySink>());
            services.AddSingleton<CaptureHistoryService>();
            // Stateless (no instance fields, every method is a pure function of its
            // parameters) - safe and cheap to share as a singleton instead of the
            // endpoints newing one up per request.
            services.AddSingleton<CaptureIdentityResolver>();
            //services.AddSingleton<MongoSink>();


            //add a composite sink that combines multiple IShadowSink implementations, allowing the captured requests to be processed by all registered sinks. This way,
            //when a request is captured, it can be written to a file and stored in memory simultaneously, providing flexibility in how the captured data is handled and stored.
            services.AddSingleton<IShadowSink>(provide =>
            {
                var sinks = new IShadowSink[]
                {
                   provide.GetRequiredService<FileSink>(),
                   provide.GetRequiredService<InMemorySink>()
                };

                return new CompositeSink(
                    sinks,
                    provide.GetRequiredService<FootLookOptions>(),
                    provide.GetRequiredService<CaptureReliabilityState>(),
                    provide.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CompositeSink>>());
            });
            // Register the ShadowBackgroundWorker as a hosted service, which will run in the background and process captured requests from the queue.
            services.AddHostedService<ShadowBackgroundWorker>();

            // TryAdd so a host can register its own account store before AddFootLook.
            services.TryAddSingleton<IFootLookUserStore, JsonFileUserStore>();
            services.AddSingleton<ObservationSessionStore>();
            services.AddSingleton<FootLookTokenService>();
            // Validates Microsoft (Entra ID) ID tokens for POST {EndpointBasePath}/auth/microsoft. TryAdd so a
            // host (or a test) can supply its own, e.g. with different key retrieval.
            services.TryAddSingleton(provider => new MicrosoftIdTokenValidator(
                provider.GetRequiredService<FootLookOptions>().Microsoft,
                provider.GetService<Microsoft.Extensions.Logging.ILogger<MicrosoftIdTokenValidator>>()));

            // A named scheme (not the default) so registering FootLook auth never changes
            // an app's own default authentication behavior if it already has one.
            services.AddAuthentication()
                .AddJwtBearer(FootLookAuthDefaults.SchemeName, _ => { });

            // JwtBearerOptions needs the signing key from FootLookTokenService, which isn't
            // available yet when AddJwtBearer's own configure delegate runs above - this
            // named-options + DI form runs once the container can resolve it.
            services.AddOptions<JwtBearerOptions>(FootLookAuthDefaults.SchemeName)
                .Configure<FootLookTokenService, FootLookOptions, ObservationSessionStore>((jwtOptions, tokenService, footLookOptions, sessions) =>
                {
                    jwtOptions.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = FootLookAuthDefaults.Issuer,
                        ValidateAudience = true,
                        ValidAudience = FootLookAuthDefaults.Audience,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = tokenService.SigningKey,
                        ClockSkew = TimeSpan.FromSeconds(30),
                    };

                    var hubPath = footLookOptions.EndpointBasePath.TrimEnd('/') + "/live";

                    jwtOptions.Events = new JwtBearerEvents
                    {
                        // Browsers can't set a custom Authorization header on a WebSocket
                        // upgrade request, so the SignalR hub accepts the token via query
                        // string instead - but only for the hub path, never for REST calls.
                        OnMessageReceived = context =>
                        {
                            var token = context.Request.Query[footLookOptions.TokenQueryParameterName];
                            if (!string.IsNullOrEmpty(token) &&
                                context.HttpContext.Request.Path.StartsWithSegments(hubPath, StringComparison.OrdinalIgnoreCase))
                            {
                                context.Token = token;
                            }

                            return Task.CompletedTask;
                        },
                        // A correctly signed, unexpired token is not enough: its observation
                        // session must still be open. Logging out (or a host restart) ends the
                        // session, and that has to invalidate the token immediately rather
                        // than leaving it usable until it expires on its own.
                        OnTokenValidated = context =>
                        {
                            var sessionId = context.Principal?.GetSessionId();
                            if (!sessions.IsActive(sessionId))
                            {
                                context.Fail("The FootLook session has ended.");
                            }

                            return Task.CompletedTask;
                        },
                        OnChallenge = context =>
                        {
                            context.HandleResponse();
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            context.Response.ContentType = "application/json";
                            return context.Response.WriteAsync("{\"message\":\"Authentication required\"}");
                        },
                        OnForbidden = context =>
                        {
                            context.Response.StatusCode = StatusCodes.Status403Forbidden;
                            context.Response.ContentType = "application/json";
                            return context.Response.WriteAsync("{\"message\":\"This action requires an admin FootLook account.\"}");
                        }
                    };
                });

            services.AddAuthorizationBuilder()
                .AddPolicy(FootLookAuthDefaults.UserPolicy, policy => policy
                    .AddAuthenticationSchemes(FootLookAuthDefaults.SchemeName)
                    .RequireAuthenticatedUser())
                .AddPolicy(FootLookAuthDefaults.AdminPolicy, policy => policy
                    .AddAuthenticationSchemes(FootLookAuthDefaults.SchemeName)
                    .RequireClaim(FootLookAuthDefaults.AdminClaimType, "true"));

            return services;

            #region Validation of FootLookOptions
            // Removing this reliance for now since it is for a database sink, we can add it back later for phase 2. For now, we will just use the in-memory and file sinks.
            //services.AddSingleton<IShadowSink>(provider =>
            //{
            //    var options = provider.GetRequiredService<FootLookOptions>();

            //    var sinks = new List<IShadowSink>
            //{
            //     provider.GetRequiredService<InMemorySink>(),
            //     provider.GetRequiredService<FileSink>()
            //};

            //    if (options.UseMongoSink)
            //    {
            //        sinks.Add(provider.GetRequiredService<MongoSink>());
            //    }

            //    return new CompositeSink(sinks);
            //});
            #endregion
        }
    }
}
