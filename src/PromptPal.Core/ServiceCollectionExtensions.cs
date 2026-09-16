using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PromptPal.Core.Data;
using PromptPal.Core.Services;

namespace PromptPal.Core;

public static class ServiceCollectionExtensions
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static IServiceCollection AddPromptPalCore(this IServiceCollection services)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite(DatabaseInitializer.GetConnectionString()),
            ServiceLifetime.Scoped);

        // 服务注册
        services.AddScoped<IPromptService, PromptService>();
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<ITagService, TagService>();
        services.AddScoped<IDbTransferService, DbTransferService>();

        return services;
    }

    /// <summary>在应用启动时调用，确保数据库存在</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static async Task InitializeDatabaseAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await DatabaseInitializer.InitializeAsync(db);
    }
}
