using System.ComponentModel.DataAnnotations;
using Kasa.Api.Domain;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kasa.Api.Infrastructure;

public sealed class MongoOptions
{
    [Required]
    public string ConnectionString { get; set; } = "";

    [Required]
    public string Database { get; set; } = "kasa";

    /// <summary>MongoDB deletes archived responses older than this by itself (a TTL index).</summary>
    [Range(1, 3650)]
    public int RawResponseDays { get; set; } = 90;
}

/// <summary>What a bank call is about, attached to the request so the archive knows whose response it is.</summary>
public sealed record RawResponseTag(Guid CompanyId, Guid ConnectionId, BankCode Bank, string Operation)
{
    public static readonly HttpRequestOptionsKey<RawResponseTag> Key = new("kasa.raw-response");
}

/// <summary>
/// Keeps every bank data response as it arrived, so "what exactly did the bank send that day?" has an answer.
/// MongoDB because these are documents of any shape that are written once, read rarely and expire on their own.
/// Like Redis it is optional: when MongoDB is down the response is not archived and the sync carries on.
/// </summary>
public sealed class RawResponseArchive(IMongoClient client, IOptions<MongoOptions> options, TimeProvider clock, ILogger<RawResponseArchive> log)
{
    // Larger responses are cut: the archive is for reading by a person, not for replaying.
    private const int MaxBodyChars = 256 * 1024;
    private readonly IMongoCollection<BsonDocument> _responses = client.GetDatabase(options.Value.Database).GetCollection<BsonDocument>("bank_raw_responses");
    private int _indexesReady;

    public IMongoCollection<BsonDocument> Responses => _responses;

    public static IServiceCollection Add(IServiceCollection services)
    {
        services.AddOptions<MongoOptions>().BindConfiguration("Mongo").ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<IMongoClient>(sp =>
        {
            var settings = MongoClientSettings.FromConnectionString(sp.GetRequiredService<IOptions<MongoOptions>>().Value.ConnectionString);
            // A missing MongoDB costs a bank call one second, not thirty (the driver's default).
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(1);
            settings.ConnectTimeout = TimeSpan.FromSeconds(1);
            return new MongoClient(settings);
        });
        services.AddSingleton<RawResponseArchive>();
        services.AddTransient<RawResponseHandler>();
        return services;
    }

    public async Task SaveAsync(RawResponseTag tag, HttpRequestMessage request, HttpResponseMessage response, string body, CancellationToken ct)
    {
        try
        {
            await EnsureIndexesAsync(ct);
            await _responses.InsertOneAsync(new BsonDocument
            {
                ["companyId"] = tag.CompanyId.ToString(),
                ["connectionId"] = tag.ConnectionId.ToString(),
                ["bank"] = tag.Bank.ToString(),
                ["operation"] = tag.Operation,
                // Path and query only: the request headers carry the bearer token and are never stored.
                ["request"] = request.RequestUri?.PathAndQuery ?? "",
                ["status"] = (int)response.StatusCode,
                ["contentType"] = response.Content.Headers.ContentType?.ToString() ?? "",
                ["body"] = body.Length <= MaxBodyChars ? body : body[..MaxBodyChars],
                ["truncated"] = body.Length > MaxBodyChars,
                ["receivedAt"] = clock.GetUtcNow().UtcDateTime,
            }, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            log.LogWarning("MongoDB is not reachable; the {Bank} {Operation} response was not archived: {Error}", tag.Bank, tag.Operation, ex.Message);
        }
    }

    private async Task EnsureIndexesAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _indexesReady) == 1)
        {
            return;
        }

        // Creating an index that already exists is a no-op, so two threads doing it at once is harmless.
        await _responses.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("receivedAt"),
                new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(options.Value.RawResponseDays), Name = "ttl_receivedAt" }),
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("connectionId").Descending("receivedAt"),
                new CreateIndexOptions { Name = "connection_receivedAt" }),
        ], ct);
        Volatile.Write(ref _indexesReady, 1);
    }
}

/// <summary>Sits in the bank HttpClients. Only requests carrying a <see cref="RawResponseTag"/> are archived; token calls carry none.</summary>
public sealed class RawResponseHandler(RawResponseArchive archive) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        if (request.Options.TryGetValue(RawResponseTag.Key, out var tag))
        {
            // Buffering lets the client read the body again after the archive has.
            await response.Content.LoadIntoBufferAsync(ct);
            await archive.SaveAsync(tag, request, response, await response.Content.ReadAsStringAsync(ct), ct);
        }

        return response;
    }
}
