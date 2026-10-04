using System.Collections.Concurrent;
using Canary.Runner.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RadiCorder.Features.Program;
using RadiCorder.Logics.ApiClients;
using RadiCorder.Logics.BackgroundServices;
using RadiCorder.Logics.DependencyInjection;
using RadiCorder.Logics.Domain.Recording;
using RadiCorder.Logics.Interfaces;
using RadiCorder.Logics.Logics.ProgramScheduleLogic;
using RadiCorder.Logics.Logics.RadikoLogic;
using RadiCorder.Logics.Logics.StationLogic;
using RadiCorder.Logics.Models.Enums;
using RadiCorder.Logics.Options;
using RadiCorder.Logics.RdbContext;
using RadiCorder.Logics.Services;

namespace Canary.Runner.Hosting;

/// <summary>
/// 本体の共通DIで業務サービスを構築し、確認用DBと録音用プロキシを管理する。
/// </summary>
internal sealed class LogicContext(WebApplication application, AsyncServiceScope scope, string workRoot) : IAsyncDisposable
{
    internal RadioDbContext DbContext => scope.ServiceProvider.GetRequiredService<RadioDbContext>();
    internal IAppConfigurationService Config => scope.ServiceProvider.GetRequiredService<IAppConfigurationService>();
    internal ConcurrentDictionary<string, string> RadikoStationDic => Config.RadikoStationDic;
    internal RadikoUniqueProcessLogic RadikoLogic => scope.ServiceProvider.GetRequiredService<RadikoUniqueProcessLogic>();
    internal IRadikoApiClient RadikoApiClient => scope.ServiceProvider.GetRequiredService<IRadikoApiClient>();
    internal IRadiruApiClient RadiruApiClient => scope.ServiceProvider.GetRequiredService<IRadiruApiClient>();
    internal StationLobLogic StationLobLogic => scope.ServiceProvider.GetRequiredService<StationLobLogic>();
    internal ProgramScheduleLobLogic ProgramScheduleLobLogic => scope.ServiceProvider.GetRequiredService<ProgramScheduleLobLogic>();
    internal IMediaTranscodeService MediaTranscodeService => scope.ServiceProvider.GetRequiredService<IMediaTranscodeService>();
    internal string FfmpegLogDirectory => Path.Combine(workRoot, "logs");
    internal IServiceProvider Services => scope.ServiceProvider;
    internal IServiceProvider RootServices => application.Services;

    internal IRecordingSource GetRecordingSource(RadioServiceKind serviceKind) =>
        scope.ServiceProvider.GetRequiredService<IEnumerable<IRecordingSource>>().Single(source => source.CanHandle(serviceKind));

    internal static async Task<LogicContext> CreateAsync(
        string radikoUserId,
        string radikoPassword,
        Action<IServiceCollection>? configureServices = null,
        string radiruAreaId = "JP13",
        string radiruStationId = "r1",
        string? databaseSnapshotPath = null,
        bool startProxy = true)
    {
        // 実行ごとにDBを分け、保持DBもコピー側だけで更新する。
        var workRoot = Path.Combine(Path.GetTempPath(), "radicorder-canary", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);
        var databasePath = Path.Combine(workRoot, "canary.db");
        try
        {
            if (databaseSnapshotPath is not null)
            {
                // 保存元には書き込まず、SQLiteのバックアップAPIで実行専用DBへ復元する。
                CanaryStateStore.CopyDatabase(databaseSnapshotPath, databasePath);
            }
        }
        catch
        {
            DeleteWorkDirectory(workRoot);
            throw;
        }
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RadiCorder:LogDirectory"] = Path.Combine(workRoot, "logs")
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<RadioDbContext>(options =>
            options.UseSqlite($"Data Source={databasePath};Pooling=False"));
        builder.Services.Configure<StorageOptions>(options =>
        {
            options.RecordFileSaveFolder = Path.Combine(workRoot, "record");
            options.TemporaryFileSaveFolder = Path.Combine(workRoot, "temp");
        });
        builder.Services.Configure<ExternalServiceOptions>(options =>
        {
            options.ExternalServiceUserAgent = "RadiCorder.Logic.Canary/0.1";
            options.RadiruApiMinRequestIntervalMs = 0;
            options.RadiruApiRequestJitterMs = 0;
        });
        builder.Services.AddOptions<RadikoOptions>();
        builder.Services.AddOptions<MonitoringOptions>();
        builder.Services.AddOptions<AutomationOptions>();
        builder.Services.AddOptions<ReleaseOptions>();
        builder.Services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();
        builder.Services.AddSingleton<IAppConfigurationService, AppConfigurationService>();
        builder.Services.AddSingleton<ILocalApplicationUrlService, LocalApplicationUrlService>();
        builder.Services.AddSingleton<IRadikoProxyTicketService, RadikoProxyTicketService>();
        builder.Services.AddSingleton<IRecordingScheduleWakeup, RecordingScheduleWakeup>();
        builder.Services.AddCanaryEventPublishers();
        builder.Services.AddRadiCorderLogics();
        builder.Services.AddSingleton(new RadiruProbeTarget(
            CanaryInputs.NormalizeRadiruAreaKey(radiruAreaId), radiruStationId,
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, CanaryInputs.ResolveJapanTimeZone()).DateTime)));
        builder.Services.AddScoped<RadiruApiClient>();
        builder.Services.Replace(ServiceDescriptor.Scoped<IRadiruApiClient, CanaryRadiruApiClient>());
        configureServices?.Invoke(builder.Services);

        WebApplication? application = null;
        AsyncServiceScope scope = default;
        var scopeCreated = false;
        try
        {
            application = builder.Build();
            scope = application.Services.CreateAsyncScope();
            scopeCreated = true;
            var context = new LogicContext(application, scope, workRoot);
            if (databaseSnapshotPath is not null)
            {
                var unknown = (await context.DbContext.Database.GetAppliedMigrationsAsync())
                    .Except(context.DbContext.Database.GetMigrations()).ToList();
                if (unknown.Count > 0)
                {
                    throw new InvalidDataException("保存DBに現在の本体が認識しないmigrationが含まれています。");
                }
            }
            await context.DbContext.Database.MigrateAsync();
            if (!string.IsNullOrWhiteSpace(radikoUserId) && !string.IsNullOrWhiteSpace(radikoPassword))
            {
                // 資格情報は本体と同じ保護処理で保存し、実行終了時に確認用DBごと削除する。
                await context.Config.UpdateRadikoCredentialsAsync(radikoUserId, radikoPassword);
            }

            if (startProxy)
            {
                application.MapGroup("/api/programs").MapRadikoStreamingEndpoints();
                await application.StartAsync();
                var addresses = application.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()?.Addresses ?? application.Urls;
                context.Services.GetRequiredService<ILocalApplicationUrlService>().SetCandidateUrls(addresses);
            }
            return context;
        }
        catch
        {
            if (scopeCreated)
            {
                await scope.DisposeAsync();
            }
            if (application is not null)
            {
                await application.DisposeAsync();
            }
            DeleteWorkDirectory(workRoot);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await application.StopAsync();
        }
        finally
        {
            try
            {
                await scope.DisposeAsync();
            }
            finally
            {
                await application.DisposeAsync();
                DeleteWorkDirectory(workRoot);
            }
        }
    }

    private static void DeleteWorkDirectory(string workRoot)
    {
        // 後処理の失敗でチェック結果を変更しない。
        try
        {
            Directory.Delete(workRoot, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
