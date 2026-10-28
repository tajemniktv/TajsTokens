using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Services;

/// <summary>Explicit local data import, never executable feed content or an automatic source-selection action.</summary>
public static class ApiPriceCatalogFileReader
{
    public static async Task<ApiPriceImportAttempt> ReadAsync(string sourceKey, string path, CancellationToken token)
    {
        ApiPriceImportAttempt Failure(ApiPriceImportState state, string issue) => new(sourceKey, DateTimeOffset.UtcNow, state, null, issue);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
            if (stream.Length is <= 0 or > 512 * 1024) return Failure(ApiPriceImportState.Invalid, "price-file-size-bound");
            byte[] bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes, token);
            if (stream.ReadByte() != -1) return Failure(ApiPriceImportState.Invalid, "price-file-changed");
            return Parse(sourceKey, bytes);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Failure(ApiPriceImportState.Unavailable, "price-file-unavailable"); }
    }

    public static ApiPriceImportAttempt Parse(string sourceKey, byte[] bytes)
    {
        ApiPriceImportAttempt Failure(ApiPriceImportState state, string issue) => new(sourceKey, DateTimeOffset.UtcNow, state, null, issue);
        try
        {
            if (bytes.Length is <= 0 or > 512 * 1024) return Failure(ApiPriceImportState.Invalid, "price-file-size-bound");
            using var json = JsonDocument.Parse(bytes); var root = json.RootElement;
            ApiPriceCatalogSnapshot snapshot;
            string? contract = root.GetProperty("Contract").GetString();
            if (contract == ApiPriceCatalogSnapshot.Version)
            {
                RequireTimestamp(root.GetProperty("RetrievedAtUtc"));
                foreach (var rate in root.GetProperty("Rates").EnumerateArray())
                    foreach (string field in new[] { "EffectiveFromUtc", "EffectiveToUtc" })
                        if (rate.TryGetProperty(field, out var time)) RequireTimestamp(time);
                snapshot = root.Deserialize<ApiPriceCatalogSnapshot>() ?? throw new JsonException();
                if (root.TryGetProperty("SnapshotId", out var id) && id.GetString() != snapshot.SnapshotId) throw new JsonException();
            }
            else if (contract == "api-price-reference-candidate/v1")
            {
                string Text(string key) => root.GetProperty(key).GetString()!;
                DateTimeOffset? Time(string key)
                {
                    if (!root.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
                    RequireTimestamp(value);
                    return DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.None);
                }
                var context = new ApiPriceReferenceContext(Text("Product"), Text("Tier"), Text("ContextBand"), Text("CacheWritePolicy"));
                var rateArray = root.GetProperty("Rates");
                if (rateArray.ValueKind != JsonValueKind.Array || rateArray.GetArrayLength() > ApiPriceCatalogSnapshot.MaximumRates / 5) throw new JsonException();
                var sources = root.GetProperty("Sources").EnumerateArray().Select(x => x.GetString()!).ToList();
                var rates = new List<ApiPriceRate>();
                string[] fields = ["UncachedInput", "CacheRead", "CacheWrite", "NonReasoningOutput", "ReasoningOutput"];
                foreach (var model in rateArray.EnumerateArray())
                {
                    string? source = model.TryGetProperty("Source", out var s) ? s.GetString() : null;
                    if (source is not null) sources.Add(source);
                    for (int i = 0; i < 5; i++)
                        rates.Add(new(new(model.GetProperty("Model").GetString()!, context.Product, context.Tier, context.ContextBand),
                            (TtTokenCategory)i, i == 2 ? context.CacheWritePolicy : null,
                            model.TryGetProperty(fields[i], out var amount) && amount.ValueKind != JsonValueKind.Null ? amount.GetDecimal() : null,
                            Time("EffectiveFromUtc"), Time("EffectiveToUtc"), source));
                }
                snapshot = new(ApiPriceCatalogSnapshot.Version, ApiPriceOrigin.OfficialCurated, "official-curated-candidate", Time("LocallyCollectedAtUtc") ?? throw new JsonException(),
                    Text("Currency"), Text("RateUnit"), sources.Distinct(StringComparer.Ordinal).ToArray(), rates, context,
                    Convert.ToHexStringLower(SHA256.HashData(bytes)));
            }
            else return Failure(ApiPriceImportState.Invalid, "unsupported-price-contract");
            return new(sourceKey, DateTimeOffset.UtcNow, ApiPriceImportState.Accepted, snapshot, null);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { return Failure(ApiPriceImportState.Invalid, "invalid-price-schema"); }
    }
    private static void RequireTimestamp(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return;
        string text = value.GetString()!; int t = text.IndexOf('T');
        if (t < 0 || !(text.EndsWith('Z') || text.LastIndexOf('+') > t || text.LastIndexOf('-') > t))
            throw new JsonException("An explicit timestamp offset is required; dates do not invent UTC instants.");
    }
}
