using System;
using System.Collections.Generic;
using System.Reflection;
using BuildingBlocks.Caching;
using BuildingBlocks.Exception;
using BuildingBlocks.Logging;
using BuildingBlocks.Validation;
using Hellang.Middleware.ProblemDetails;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MovieSearch.Application;
using Newtonsoft.Json;
using Microsoft.AspNetCore.Http;
using MovieSearch.Application.Services.Clients;
using MovieSearch.Core;
using BuildingBlocks.Resiliency;
using BuildingBlocks.Security;
using BuildingBlocks.Security.ApiKey;
using BuildingBlocks.Security.ApiKey.Authorization;
using Google;
using Microsoft.AspNetCore.Authorization;
using MovieSearch.Infrastructure.Security;
using MovieSearch.Infrastructure.Services.Clients.MovieDb;
using MovieSearch.Infrastructure.Services.Clients.Video;

namespace MovieSearch.Infrastructure
{
    public static class Extensions
    {
        public static IServiceCollection AddInfrastructure(this IServiceCollection services,
            IConfiguration configuration)
        {
            services.AddSingleton<IMovieDbServiceClient, TMDBServiceClient>();
            services.AddSingleton<IVideoServiceClient, YoutubeVideoServiceClient>();

            services.AddCustomValidators(typeof(ApplicationRoot).Assembly);

            services.AddAutoMapper(typeof(ApplicationRoot).Assembly, typeof(InfrastructureRoot).Assembly);

            services.AddMediatR(typeof(ApplicationRoot).Assembly);

            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(RequestValidationBehavior<,>))
                .AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));

            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(CachingBehavior<,>))
                .AddScoped(typeof(IPipelineBehavior<,>), typeof(InvalidateCachingBehavior<,>));

            services.AddCachingRequestPolicies(new List<Assembly>
            {
                typeof(ApplicationRoot).Assembly
            });
            services.AddEasyCaching(options => { options.UseInMemory(configuration, "mem"); });

            services.AddProblemDetails(x =>
            {
                // Control when an exception is included
                x.IncludeExceptionDetails = (ctx, _) =>
                {
                    // Fetch services from HttpContext.RequestServices
                    var env = ctx.RequestServices.GetRequiredService<IHostEnvironment>();
                    return env.IsDevelopment() || env.IsStaging();
                };
                x.Map<AppException>(ex => new ProblemDetails
                {
                    Title = "Application rule broken",
                    Status = StatusCodes.Status409Conflict,
                    Detail = ex.Message,
                    Type = "https://somedomain/application-rule-validation-error",
                });
                // Exception will produce and returns from our FluentValidation RequestValidationBehavior
                x.Map<ValidationException>(ex => new ProblemDetails
                {
                    Title = "input validation rules broken",
                    Status = StatusCodes.Status400BadRequest,
                    Detail = JsonConvert.SerializeObject(ex.ValidationResultModel.Errors),
                    Type = "https://somedomain/input-validation-rules-error",
                });
                x.Map<BadRequestException>(ex => new ProblemDetails
                {
                    Title = "bad request exception",
                    Status = StatusCodes.Status400BadRequest,
                    Detail = ex.Message,
                    Type = "https://somedomain/bad-request-error",
                });
                x.Map<NotFoundException>(ex => new ProblemDetails
                {
                    Title = "not found exception",
                    Status = StatusCodes.Status404NotFound,
                    Detail = ex.Message,
                    Type = "https://somedomain/not-found-error",
                });
                x.Map<ApiException>(ex => new ProblemDetails
                {
                    Title = "api server exception",
                    Status = StatusCodes.Status500InternalServerError,
                    Detail = ex.Message,
                    Type = "https://somedomain/api-server-error",
                });
                x.MapToStatusCode<ArgumentNullException>(StatusCodes.Status400BadRequest);
                x.Map<GoogleApiException>(googleApiException =>
                {
                    if (googleApiException.Error.Code == StatusCodes.Status403Forbidden)
                    {
                        return new ProblemDetails
                        {
                            Title = "youtube api forbidden exception",
                            Status = StatusCodes.Status403Forbidden,
                            Detail = googleApiException.Error.Message,
                            Type = "https://somedomain/forbiden",
                        };
                    }

                    if (googleApiException.Error.Code == StatusCodes.Status400BadRequest)
                    {
                        return new ProblemDetails
                        {
                            Title = "youtube api bad request exception",
                            Status = StatusCodes.Status400BadRequest,
                            Detail = googleApiException.Error.Message,
                            Type = "https://somedomain/bad-request-error",
                        };
                    }

                    return new ProblemDetails();
                });
            });

            return services;
        }

        public static void UseInfrastructure(this IApplicationBuilder app, IWebHostEnvironment env)
        {
            app.UseProblemDetails();
        }

        public static IServiceCollection AddCustomHttpClients(this IServiceCollection services,
            IConfiguration configuration, string movieDbSectionName = "TMDBOptions",
            string pollySectionName = "PolicyConfig")
        {
            var movieDbOptions = configuration.GetSection(movieDbSectionName).Get<TMDBOptions>();

            services.AddHttpClient(nameof(TMDBServiceClient), config =>
            {
                config.BaseAddress = new Uri(movieDbOptions.BaseApiAddress);
                config.Timeout = new TimeSpan(0, 0, 30);
                config.DefaultRequestHeaders.Clear();
            }).AddCustomPolicyHandlers(configuration, pollySectionName);

            return services;
        }

        public static IServiceCollection AddCustomApiKeyAuthentication(this IServiceCollection services)
        {
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = ApiKeyAuthenticationOptions.DefaultScheme;
                    options.DefaultChallengeScheme = ApiKeyAuthenticationOptions.DefaultScheme;
                })
                .AddApiKeySupport(options => { });

            services.AddAuthorization(options =>
            {
                options.AddPolicy(Policies.OnlyCustomers,
                    policy => policy.Requirements.Add(new OnlyCustomersRequirement()));
                options.AddPolicy(Policies.OnlyAdmins, policy => policy.Requirements.Add(new OnlyAdminsRequirement()));
                options.AddPolicy(Policies.OnlyThirdParties,
                    policy => policy.Requirements.Add(new OnlyThirdPartiesRequirement()));
            });


            services.AddSingleton<IAuthorizationHandler, OnlyCustomersAuthorizationHandler>();
            services.AddSingleton<IAuthorizationHandler, OnlyAdminsAuthorizationHandler>();
            services.AddSingleton<IAuthorizationHandler, OnlyThirdPartiesAuthorizationHandler>();

            services.AddSingleton<IGetApiKeyQuery, InMemoryGetApiKeyQuery>();

            return services;
        }
    }
}