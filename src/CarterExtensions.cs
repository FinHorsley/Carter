namespace Carter
{
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Threading.Tasks;
    using FluentValidation;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using static OpenApi.CarterOpenApi;

    public static class CarterExtensions
    {
        /// <summary>
        /// Adds Carter to the specified <see cref="IApplicationBuilder"/>.
        /// </summary>
        /// <param name="builder">The <see cref="IApplicationBuilder"/> to configure.</param>
        /// <param name="options">A <see cref="CarterOptions"/> instance.</param>
        /// <returns>A reference to this instance after the operation has completed.</returns>
        public static IApplicationBuilder UseCarter(this IApplicationBuilder builder, CarterOptions options = null)
        {
            var diagnostics = builder.ApplicationServices.GetService<CarterDiagnostics>();

            var loggerFactory = builder.ApplicationServices.GetService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger(typeof(CarterDiagnostics));

            diagnostics.LogDiscoveredCarterTypes(logger);

            ApplyGlobalBeforeHook(builder, options, loggerFactory.CreateLogger("Carter.GlobalBeforeHook"));

            ApplyGlobalAfterHook(builder, options, loggerFactory.CreateLogger("Carter.GlobalAfterHook"));

            return builder.UseRouting(endpointRouteBuilder =>
            {
                var routeMetaData = new Dictionary<(string verb, string path), RouteMetaData>();

                //Create a "startup scope" to resolve modules from
                using (var scope = builder.ApplicationServices.CreateScope())
                {
                    var statusCodeHandlers = scope.ServiceProvider.GetServices<IStatusCodeHandler>().ToList();

                    //Get all instances of CarterModule to fetch and register declared routes
                    foreach (var module in scope.ServiceProvider.GetServices<CarterModule>())
                    {
                        var moduleLogger = scope.ServiceProvider
                            .GetService<ILoggerFactory>()
                            .CreateLogger(module.GetType());

                        routeMetaData = routeMetaData.Concat(module.RouteMetaData).ToDictionary(x => x.Key, x => x.Value);

                        foreach (var descriptor in module.Routes)
                        {
                            endpointRouteBuilder.MapVerbs(descriptor.Key.path, CreateRouteHandler(descriptor.Key.path, module, statusCodeHandlers, moduleLogger, descriptor.Value),
                                new List<string> { descriptor.Key.verb });
                        }
                    }
                }

                endpointRouteBuilder.MapGet("openapi", BuildOpenApiResponse(options, routeMetaData));
            });
        }

        private static RequestDelegate CreateRouteHandler(string path, CarterModule module, IEnumerable<IStatusCodeHandler> statusCodeHandlers, ILogger logger, RequestDelegate routeHandler)
        {
            return async ctx =>
            {
                bool shouldContinue = true;

                if (module.Before != null)
                {
                    foreach (var beforeDelegate in module.Before.GetInvocationList())
                    {
                        
                        var beforeTask = (Task<bool>)beforeDelegate.DynamicInvoke(ctx);
                        shouldContinue = await beforeTask;
                        if (!shouldContinue)
                        {
                            break;
                        }
                    }
                }

                if (shouldContinue)
                {
                    // run the route handler
                    logger.LogDebug("Executing module route handler for {Method} /{Path}", ctx.Request.Method, path);
                    await routeHandler(ctx);

                    // run after handler
                    if (module.After != null)
                    {
                        await module.After(ctx);
                    }
                }

                // run status code handler
                var scHandler = statusCodeHandlers.FirstOrDefault(x => x.CanHandle(ctx.Response.StatusCode));
                if (scHandler != null)
                {
                    await scHandler.Handle(ctx);
                }
            };
        }

        private static void ApplyGlobalAfterHook(IApplicationBuilder builder, CarterOptions options, ILogger logger)
        {
            if (options?.After != null)
            {
                builder.Use(async (ctx, next) =>
                {
                    await next();
                    logger.LogDebug("Executing global after hook");
                    await options.After(ctx);
                });
            }
        }

        private static void ApplyGlobalBeforeHook(IApplicationBuilder builder, CarterOptions options, ILogger logger)
        {
            if (options?.Before != null)
            {
                builder.Use(async (ctx, next) =>
                {
                    logger.LogDebug("Executing global before hook");

                    var carryOn = await options.Before(ctx);
                    if (carryOn)
                    {
                        logger.LogDebug("Executing next handler after global before hook");
                        await next();
                    }
                });
            }
        }

        /// <summary>
        /// Adds Carter to the specified <see cref="IServiceCollection"/>.
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> to add Carter to.</param>
        /// <param name="assemblyCatalog">Optional <see cref="DependencyContextAssemblyCatalog"/> containing assemblies to add to the services collection. If not provided, the default catalog of assemblies is added, which includes Assembly.GetEntryAssembly.</param>
        public static void AddCarter(this IServiceCollection services, DependencyContextAssemblyCatalog assemblyCatalog = null)
        {
            assemblyCatalog = assemblyCatalog ?? new DependencyContextAssemblyCatalog();

            var assemblies = assemblyCatalog.GetAssemblies();

            CarterDiagnostics diagnostics = new CarterDiagnostics();
            services.AddSingleton(diagnostics);

            var validators = assemblies.SelectMany(ass => ass.GetTypes())
                .Where(typeof(IValidator).IsAssignableFrom)
                .Where(t => !t.GetTypeInfo().IsAbstract);

            foreach (var validator in validators)
            {
                diagnostics.AddValidator(validator);
                services.AddSingleton(typeof(IValidator), validator);
            }

            services.AddSingleton<IValidatorLocator, DefaultValidatorLocator>();

            services.AddRouting();

            var modules = assemblies.SelectMany(x => x.GetTypes()
                .Where(t =>
                    !t.IsAbstract &&
                    typeof(CarterModule).IsAssignableFrom(t) &&
                    t != typeof(CarterModule) &&
                    t.IsPublic
                ));

            foreach (var module in modules)
            {
                diagnostics.AddModule(module);
                services.AddScoped(module);
                services.AddScoped(typeof(CarterModule), module);
            }

            var schs = assemblies.SelectMany(x => x.GetTypes().Where(t => typeof(IStatusCodeHandler).IsAssignableFrom(t) && t != typeof(IStatusCodeHandler)));
            foreach (var sch in schs)
            {
                diagnostics.AddStatusCodeHandler(sch);
                services.AddScoped(typeof(IStatusCodeHandler), sch);
            }

            var responseNegotiators = assemblies.SelectMany(x => x.GetTypes()
                .Where(t =>
                    !t.IsAbstract &&
                    typeof(IResponseNegotiator).IsAssignableFrom(t) &&
                    t != typeof(IResponseNegotiator) &&
                    t != typeof(DefaultJsonResponseNegotiator)
                ));

            foreach (var negotiator in responseNegotiators)
            {
                diagnostics.AddResponseNegotiator(negotiator);
                services.AddSingleton(typeof(IResponseNegotiator), negotiator);
            }

            services.AddSingleton<IResponseNegotiator, DefaultJsonResponseNegotiator>();
        }
    }
}
