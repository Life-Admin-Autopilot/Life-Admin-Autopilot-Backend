using Life_Admin_Autopilot.DAL.Configurations;
using Life_Admin_Autopilot.DAL.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.DAL.Extensions
{
    public static class MongoDbExtensions
    {
        public static IServiceCollection AddMongoDb(
                this IServiceCollection services,
                IConfiguration configuration)
        {
            services
                .AddOptions<MongoDbSettings>()
                .Bind(configuration.GetSection(MongoDbSettings.SectionName));

            services.AddSingleton<IMongoClient>(serviceProvider =>
            {
                var settings = serviceProvider
                    .GetRequiredService<IOptions<MongoDbSettings>>()
                    .Value;

                return new MongoClient(settings.ConnectionString);
            });

            services.AddSingleton<IMongoDatabase>(
            serviceProvider =>
            {
                var mongoClient = serviceProvider.GetRequiredService<IMongoClient>();

                var settings =serviceProvider.GetRequiredService<IOptions<MongoDbSettings>>().Value;

                return mongoClient.GetDatabase(settings.DatabaseName);
            });

            services.AddScoped<IUserTaskRepository, UserTaskRepository>();

            services.AddScoped<IDocumentRepository,DocumentRepository>();

            services.AddScoped<IContentChunksRepository, ContentChunksRepository>();

            return services;
        }
    }
}
