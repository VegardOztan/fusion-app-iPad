﻿using System.Reflection;
using Api.Authentication;
using Api.Controllers;
using Api.Database;
using Api.Database.Models;
using Api.Extensions;
using Api.Filters;
using Api.Services;
using Api.Utilities;
using Equinor.TI.CommonLibrary.Client;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;
using Microsoft.OpenApi.Models;

namespace Api
{
    public class Startup
    {
        public Startup(IConfiguration configuration, IWebHostEnvironment env)
        {
            _env = env;
            Configuration = configuration;
            // Adding Audience as Common Library needs it.
            Configuration.GetSection("AzureAd")["Audience"] = Configuration.GetSection("AzureAd")["ClientId"];
        }

        public IConfiguration Configuration { get; }
        private readonly IWebHostEnvironment _env;

        private const string MyAllowSpecificOrigins = "_myAllowSpecificOrigins";

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers(o =>
            {
                o.Conventions.Add(new ActionHidingConvention(_env.IsDevelopment()));
            });

            // Security Setup
            #region Security Setup

            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApi(Configuration.GetSection("AzureAd"))
            .EnableTokenAcquisitionToCallDownstreamApi()
                .AddDownstreamWebApi("WbsService", Configuration.GetSection("WbsApi"))
            .AddDistributedTokenCaches();

            services.AddAuthorization(options =>
            {
                options.FallbackPolicy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .RequireRole(Tools.GetRoles(Configuration))
                    .Build();

                // Database access is also available through special roles for other applications
                options.AddPolicy("DatabasePolicy", new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .RequireRole(Tools.GetRoles(Configuration).Concat(Tools.GetDatabaseRoles(Configuration)))
                    .Build());
            });

            services.AddCors(options =>
            {
                options.AddPolicy(name: MyAllowSpecificOrigins, builder =>
                {
                    builder.WithOrigins(
                            "http://localhost:3000",
                            "https://*.equinor.com")
                    .AllowAnyHeader()
                    .WithExposedHeaders(QueryStringParameters.PaginationHeader)
                    .AllowAnyMethod()
                    .SetIsOriginAllowedToAllowWildcardSubdomains();
                });
            });

            #endregion

            // Swagger integration
            #region Integrate Swagger
            services.AddSwaggerGen(c =>
            {
                // Add documentation for health-endpoint
                c.DocumentFilter<HealthCheckFilter>();
                // Add implicit flow authentication to Swagger
                c.AddSecurityDefinition("oauth2", new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.OAuth2,

                    Flows = new OpenApiOAuthFlows
                    {
                        Implicit = new OpenApiOAuthFlow
                        {
                            TokenUrl = new Uri($"{Configuration["AzureAd:Instance"]}/{Configuration["AzureAd:TenantId"]}/oauth2/token"),
                            AuthorizationUrl = new Uri($"{Configuration["AzureAd:Instance"]}/{Configuration["AzureAd:TenantId"]}/oauth2/authorize")
                        }
                    }
                });
                c.AddSecurityRequirement(new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecurityScheme()
                        {
                            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "oauth2" }
                        },
                        Array.Empty<string>()
                    }
                });
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "iPad", Version = "v1" });

                // Make swagger use xml comments from functions
                string xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
                string xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
                c.IncludeXmlComments(xmlPath);
            });
            #endregion

            //HttpClient for WBS API
            services.AddHttpClient(WbsService.ClientName, httpClient =>
            {
                httpClient.BaseAddress = new Uri(Configuration["WbsApi:BaseUrl"]);
                httpClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", Configuration["WbsApi:SubscriptionKey"]);
            });

            // Common Library Integration
            #region Common Library Integration
            string commonLibTokenConnection = CommonLibraryService.BuildTokenConnectionString(
                Configuration["AzureAd:ClientId"],
                Configuration["AzureAd:TenantId"],
                Configuration["AzureAd:ClientSecret"]);

            // Service options is of singleton type because it should be the same for all services
            // TODO: Verify if this should be singleton
            services.AddSingleton(typeof(CommonLibraryClientOptions),
                _ => new CommonLibraryClientOptions { TokenProviderConnectionString = commonLibTokenConnection });

            // Service is of singleton type because it should be the same for all requests
            // TODO: Verify if this should be singleton
            services.AddSingleton(typeof(CommonLibraryService), typeof(CommonLibraryService));

            // Controller is scoped because a new instance should be initialized for each request
            services.AddScoped(typeof(CommonLibraryController), typeof(CommonLibraryController));
            #endregion

            //WBS API Integration
            #region WBS API Integration

            services.AddScoped(typeof(WbsService), typeof(WbsService));

            // Controller is scoped because a new instance should be initialized for each request
            services.AddScoped(typeof(WbsController), typeof(WbsController));
            #endregion

            // Database connection
            services.AddSqlDbContext<DatabaseContext>(Configuration.GetConnectionString("iPadDatabase"))
                .AddAccessTokenSupport()
                .AddSqlTokenProvider<SqlTokenProvider>(ServiceLifetime.Singleton);

            // Database access is Scoped because it depends on SqlDbContext which is also scoped
            services.AddScoped<IPadDatabaseAccess, IPadDatabaseAccess>();

            services.AddApplicationInsightsTelemetry();

            services.AddHealthChecks().AddDbContextCheck<DatabaseContext>();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "iPad v1");
                c.OAuthAppName("Fusion-iPad");
                c.OAuthClientId(Configuration["AzureAd:ClientId"]);
                c.OAuthAdditionalQueryStringParams(new Dictionary<string, string>
                    { { "resource", $"{Configuration["AzureAd:ClientId"]}" } });
            });

            app.UseRouting();

            app.UseCors(MyAllowSpecificOrigins);

            app.UseAuthentication();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapHealthChecks(HealthCheckFilter.HealthCheckEndpoint);
            });
        }
    }
}
