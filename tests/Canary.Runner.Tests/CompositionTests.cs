using Canary.Runner.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RadiCorder.Logics.Application;
using RadiCorder.Logics.Domain.Station;
using RadiCorder.Logics.Infrastructure.Station;
using RadiCorder.Logics.Infrastructure.Recording;
using RadiCorder.Logics.Interfaces;
using RadiCorder.Logics.Logics;
using RadiCorder.Logics.Logics.RecordJobLogic;
using RadiCorder.Logics.Models.Enums;
using RadiCorder.Logics.Services;
using RadiCorder.Logics.Services.Streaming;

namespace Canary.Runner.Tests;

[TestFixture]
public class CompositionTests
{
    [Test]
    public async Task 共通DIで本体の部品を構築し常駐処理と外部通信を開始しない()
    {
        var handler = new RejectNetworkHandler();
        await using var context = await LogicContext.CreateAsync("", "", services =>
        {
            services.AddHttpClient(HttpClientNames.Radiko).ConfigurePrimaryHttpMessageHandler(() => handler);
            services.AddHttpClient(HttpClientNames.Radiru).ConfigurePrimaryHttpMessageHandler(() => handler);
        });
        Assert.That(context.Config, Is.TypeOf<AppConfigurationService>());
        Assert.That(context.Services.GetRequiredService<IStationRepository>(), Is.TypeOf<StationRepository>());
        Assert.That(context.GetRecordingSource(RadioServiceKind.Radiko), Is.TypeOf<RadikoRecordingSource>());
        Assert.That(context.GetRecordingSource(RadioServiceKind.Radiru), Is.TypeOf<RadiruRecordingSource>());
        Assert.That(context.Services.GetRequiredService<StartupTask>(), Is.Not.Null);
        Assert.That(context.Services.GetRequiredService<RecordJobLobLogic>(), Is.Not.Null);
        Assert.That(context.Services.GetRequiredService<RadikoPlaylistClient>(), Is.Not.Null);
        Assert.That(context.RootServices.GetServices<IHostedService>()
            .Any(service => service.GetType().Namespace == "RadiCorder.Logics.BackgroundServices"), Is.False);
        Assert.That(handler.Requests, Is.Zero);
        Assert.That(context.DbContext.Database.HasPendingModelChanges(), Is.False);
    }

    [Test]
    public async Task 資格情報は本体の設定サービスで保護し確認用DBを終了時に削除する()
    {
        var context = await LogicContext.CreateAsync("canary-offline-user", "canary-offline-password");
        var databasePath = context.DbContext.Database.GetDbConnection().DataSource;
        try
        {
            var credentials = await context.Config.TryGetRadikoCredentialsAsync();
            Assert.That(credentials, Is.EqualTo((true, "canary-offline-user", "canary-offline-password")));
            Assert.That(File.Exists(databasePath), Is.True);
        }
        finally
        {
            await context.DisposeAsync();
        }
        Assert.That(Directory.Exists(Path.GetDirectoryName(databasePath)), Is.False);
    }

    [Test]
    public async Task 実行ごとに確認用DBを分ける()
    {
        await using var first = await LogicContext.CreateAsync("", "");
        await using var second = await LogicContext.CreateAsync("", "");
        Assert.That(first.DbContext.Database.GetDbConnection().DataSource,
            Is.Not.EqualTo(second.DbContext.Database.GetDbConnection().DataSource));
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new InvalidOperationException("Unexpected external request.");
        }
    }
}
