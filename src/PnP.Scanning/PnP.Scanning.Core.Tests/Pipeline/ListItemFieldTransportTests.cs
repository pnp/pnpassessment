#nullable enable
using System.IdentityModel.Tokens.Jwt;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using PnP.Core;
using PnP.Core.Auth;
using PnP.Core.Model;
using PnP.Core.Model.SharePoint;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using PnP.Scanning.Core.ClassicPages.Analysis.Page;
using PnP.Scanning.Core.ClassicPages.Collection.List;
using PnP.Scanning.Core.ClassicPages.Collection.Page;
using PnP.Scanning.Core.ClassicPages.Contracts.List;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.ClassicPages.Contracts.Shared;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

/// <summary>Exercises real SDK list-stream mapping and exact backing-item capture with partially loaded user and lookup properties.</summary>
public sealed class ListItemFieldTransportTests
{
    /// <summary>Distinguishes omitted, explicit-null and returned display properties without losing the acquired item's other fields.</summary>
    [Theory]
    [InlineData(
        "{\"id\":\"7\",\"email\":\"editor@contoso.com\"}",
        false,
        true,
        "editor@contoso.com",
        "editor@contoso.com",
        7)]
    [InlineData("{\"id\":\"7\"}", false, false, null, null, 7)]
    [InlineData("{\"id\":\"7\",\"title\":null,\"email\":null}", true, true, null, null, 7)]
    [InlineData("{\"id\":\"7\",\"title\":\"Editor\",\"email\":\"\"}", true, true, "", "Editor", 7)]
    [InlineData("{\"id\":\"0\"}", false, false, null, null, 0)]
    [InlineData("{\"email\":\"editor@contoso.com\"}", false, true, "editor@contoso.com", "editor@contoso.com", null)]
    public async Task Real_partial_user_fields_preserve_available_metadata(
        string user,
        bool lookupReturned,
        bool emailReturned,
        string? email,
        string? modifiedBy,
        int? lookupId)
    {
        using FieldTransport transport = new(user);
        using ServiceProvider provider = Services(transport);
        using PnPContext context = await CreateContextAsync(provider);
        IList list = await LoadListAsync(context);
        DiscoveryEvidence page = new()
        {
            SiteUrl = FieldTransport.Site,
            WebUrl = "/sites/fields",
            Url = "/sites/fields/SitePages/Page.aspx",
            FileUniqueId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            SiteCollectionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ListId = FieldTransport.ListId,
            ListItemId = 7,
            RecordKey = "file",
        };
        FilePageEvidence result = await FilePageSourceReader.ReadFileItemMetadataAsync(
            list,
            page,
            skipUsers: false,
            CancellationToken.None);
        Assert.True(result.MetadataState.Succeeded);
        Assert.Equal("Page.aspx", result.Fields["FileLeafRef"].Text);
        Assert.Equal("Page", result.Fields["Title"].Text);
        Assert.Equal("7", result.Fields["ID"].Text);
        Assert.Equal("Json", result.Fields["Choices"].Type);
        Assert.Equal(
            new[] { "A", "B" },
            result.Fields["Choices"].Value.EnumerateArray().Select(value => value.GetString()));
        JsonElement captured = result.Fields["Editor"].Value;
        Assert.Equal(lookupId.HasValue, captured.TryGetProperty("LookupId", out JsonElement capturedId));
        if (lookupId.HasValue) Assert.Equal(lookupId, capturedId.GetInt32());
        Assert.Equal(lookupReturned, captured.TryGetProperty("LookupValue", out JsonElement lookup));
        Assert.Equal(emailReturned, captured.TryGetProperty("Email", out JsonElement capturedEmail));
        if (emailReturned) Assert.Equal(email, capturedEmail.GetString());
        if (lookupReturned && modifiedBy == null) Assert.Equal(JsonValueKind.Null, lookup.ValueKind);
        Assert.Equal(modifiedBy, FilePageProjection.ModifiedBy(result.Fields));
        Assert.Equal(captured.GetRawText(), Assert.IsType<JsonElement>(result.Fields["Editor"].ToValue()).GetRawText());
        Dictionary<string, SourceField> replay =
            JsonSerializer.Deserialize<Dictionary<string, SourceField>>(JsonSerializer.Serialize(result.Fields)) ??
            throw new InvalidDataException("Captured fields did not replay.");
        Assert.Equal(captured.GetRawText(), replay["Editor"].Value.GetRawText());
        Assert.Equal(modifiedBy, FilePageProjection.ModifiedBy(replay));
        Assert.Equal(19, result.Fields["Related"].Value.GetProperty("LookupId").GetInt32());
        Assert.False(result.Fields["Related"].Value.TryGetProperty("LookupValue", out _));
        Assert.Single(result.ReturnedItems!);
        Assert.Equal(1, transport.StreamRequests);
        Assert.Contains("Name='Editor'", transport.Query);
    }

    /// <summary>Preserves real lookup null/text/omission and never promotes the SDK's missing-ID sentinel to a native identity.</summary>
    [Theory]
    [InlineData("{\"lookupId\":19}", 19, false, null)]
    [InlineData("{\"lookupId\":19,\"lookupValue\":null}", 19, true, null)]
    [InlineData("{\"lookupId\":19,\"lookupValue\":\"Value\"}", 19, true, "Value")]
    [InlineData("{\"lookupId\":0,\"lookupValue\":\"Zero\"}", 0, true, "Zero")]
    [InlineData("{\"lookupValue\":null}", null, true, null)]
    public async Task Real_lookup_fields_preserve_property_availability(
        string lookup,
        int? lookupId,
        bool valueReturned,
        string? value)
    {
        using FieldTransport transport = new("{\"id\":\"7\"}", lookup);
        using ServiceProvider provider = Services(transport);
        using PnPContext context = await CreateContextAsync(provider);
        IList list = await LoadListAsync(context);
        ListItemFieldBatch batch = await ListItemContentReader.LoadItemFieldsAsync(
            list,
            ListItemQueryBuilder.ForFileItem(7, false),
            null,
            CancellationToken.None);
        SourceField field = Assert.Single(batch.Items)["Related"];
        Assert.Equal("Lookup", field.Type);
        Assert.Equal(lookupId.HasValue, field.Value.TryGetProperty("LookupId", out JsonElement capturedId));
        if (lookupId.HasValue) Assert.Equal(lookupId, capturedId.GetInt32());
        Assert.Equal(valueReturned, field.Value.TryGetProperty("LookupValue", out JsonElement capturedValue));
        if (valueReturned) Assert.Equal(value, capturedValue.GetString());
        Assert.Equal(field.Value.GetRawText(), Assert.IsType<JsonElement>(field.ToValue()).GetRawText());
    }

    /// <summary>Retains multi-choice order and Unicode while distinguishing missing, null and genuinely empty arrays through replay.</summary>
    [Theory]
    [InlineData(null, false, null)]
    [InlineData("null", true, null)]
    [InlineData("[]", true, new string[] { })]
    [InlineData("[\"A\",\"\\u4e2d\",\"A\"]", true, new[] { "A", "\u4e2d", "A" })]
    public async Task Real_multi_choice_fields_replay_without_missing_value_defaults(
        string? choices,
        bool returned,
        string[]? expected)
    {
        using FieldTransport transport = new("{\"id\":\"7\"}", choices: choices);
        using ServiceProvider provider = Services(transport);
        using PnPContext context = await CreateContextAsync(provider);
        IList list = await LoadListAsync(context);
        ListItemFieldBatch batch = await ListItemContentReader.LoadItemFieldsAsync(
            list,
            ListItemQueryBuilder.ForFileItem(7, false),
            null,
            CancellationToken.None);
        Dictionary<string, SourceField> fields =
            JsonSerializer.Deserialize<Dictionary<string, SourceField>>(
                JsonSerializer.Serialize(Assert.Single(batch.Items))) ??
            throw new InvalidDataException("Field replay failed.");
        Assert.Equal(returned, fields.TryGetValue("Choices", out SourceField? captured));
        if (!returned) return;
        Assert.NotNull(captured);
        if (expected == null)
            Assert.Null(captured.ToValue());
        else
            Assert.Equal(
                expected,
                Assert.IsType<JsonElement>(captured.ToValue()).EnumerateArray().Select(value => value.GetString()));
        Assert.Null(FilePageProjection.ModifiedBy(fields));
    }

    /// <summary>Only property-not-loaded is optional; unrelated SDK getter errors still fail acquisition explicitly.</summary>
    [Fact]
    public void Unexpected_lookup_getter_errors_are_not_suppressed()
    {
        Dictionary<string, object> fields = new() { ["Editor"] = new FailingLookup() };
        ClientException error = Assert.Throws<ClientException>(() => ListItemContentReader.Fields(fields));
        Assert.Equal(ErrorType.Unsupported, error.Error.Type);
    }

    /// <summary>Retains the SDK's unsigned counters losslessly within the existing signed-64-bit source contract.</summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public void Unsigned_SDK_scalars_round_trip_without_overflow(uint value)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Dictionary<string, SourceField> fields = ListItemContentReader.Fields(
                new Dictionary<string, object> { ["Counter"] = value });
            SourceField replay = JsonSerializer.Deserialize<SourceField>(JsonSerializer.Serialize(fields["Counter"])) ??
                                 throw new InvalidDataException("Unsigned field replay failed.");
            Assert.Equal("Int64", replay.Type);
            Assert.Equal((long)value, Assert.IsType<long>(replay.ToValue()));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>Creates the actual SDK with a strict offline read-only transport.</summary>
    private static ServiceProvider Services(FieldTransport transport)
    {
        ServiceCollection services = new();
        services.AddPnPCore(
            options => options.DisableTelemetry = true,
            configureSharePointRest: builder => builder.ConfigurePrimaryHttpMessageHandler(() => transport));
        return services.BuildServiceProvider();
    }

    /// <summary>Initializes a real SDK context using only synthetic app claims.</summary>
    private static Task<PnPContext> CreateContextAsync(ServiceProvider provider) =>
        provider.GetRequiredService<IPnPContextFactory>()
            .CreateAsync(
                new Uri(FieldTransport.Site),
                new ExternalAuthenticationProvider((_, _) => Task.FromResult(
                    new JwtSecurityTokenHandler().WriteToken(
                        new JwtSecurityToken(claims: [new Claim("roles", "Sites.Read.All")])))));

    /// <summary>Loads the SDK's required field schema in the same context as the subsequent exact-item read.</summary>
    private static Task<IList> LoadListAsync(PnPContext context) =>
        context.Web.Lists.GetByIdAsync(
            FieldTransport.ListId,
            value => value.Id,
            value => value.Title,
            value => value.RootFolder.QueryProperties(folder => folder.ServerRelativeUrl),
            value => value.Fields);

    /// <summary>Rejects every operation except identity/list metadata GETs and the native read-only list stream POST.</summary>
    private sealed class FieldTransport(
        string user,
        string lookup = "{\"lookupId\":19}",
        string? choices = "[\"A\",\"B\"]") : HttpMessageHandler
    {
        internal const string Site = "https://contoso.sharepoint.com/sites/fields";
        internal static readonly Guid ListId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        internal int StreamRequests { get; private set; }
        internal string? Query { get; private set; }

        /// <summary>Returns real list-stream JSON shapes, including id-only lookup values, without network access.</summary>
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri ?? throw new InvalidOperationException("The offline request has no URI.");
            string path = uri.AbsolutePath;
            if (uri.GetLeftPart(UriPartial.Authority) != "https://contoso.sharepoint.com")
                throw new InvalidOperationException("Unexpected offline authority.");
            string body;
            bool batch = path.EndsWith("/_api/$batch", StringComparison.OrdinalIgnoreCase);
            if (request.Method == HttpMethod.Post && (path.EndsWith(
                    "/RenderListDataAsStream",
                    StringComparison.OrdinalIgnoreCase) || batch))
            {
                StreamRequests++;
                HttpContent content = request.Content ??
                                      throw new InvalidOperationException("The stream request has no body.");
                Query = await content.ReadAsStringAsync(cancellationToken);
                if (batch)
                {
                    Match operation = Assert.Single(
                        Regex.Matches(Query, @"(?m)^POST ([^\r\n]+) HTTP/1\.1\r?$").Cast<Match>());
                    Assert.EndsWith(
                        "/RenderListDataAsStream",
                        operation.Groups[1].Value,
                        StringComparison.OrdinalIgnoreCase);
                    Query = Assert.Single(
                        Query.Split("\r\n"),
                        line => line.StartsWith("{\"parameters\":", StringComparison.Ordinal));
                }

                using JsonDocument parameters = JsonDocument.Parse(Query);
                Query = parameters.RootElement.GetProperty("parameters").GetProperty("ViewXml").GetString();
                body = "{\"Row\":[{\"ID\":\"7\",\"Title\":\"Page\",\"FileRef\":\"/sites/fields/SitePages/Page.aspx\"," +
                       "\"FileLeafRef\":\"Page.aspx\",\"ContentTypeId\":\"0x010108\"," + "\"Editor\":[" + user +
                       "],\"Related\":[" + lookup + "]" + (choices == null ? "" : ",\"Choices\":" + choices) + "}]}";
            }
            else if (request.Method == HttpMethod.Get &&
                     path.EndsWith("/_api/Site", StringComparison.OrdinalIgnoreCase))
                body =
                    "{\"Id\":\"11111111-1111-1111-1111-111111111111\",\"GroupId\":\"00000000-0000-0000-0000-000000000000\"}";
            else if (request.Method == HttpMethod.Get && path.EndsWith("/_api/Web", StringComparison.OrdinalIgnoreCase))
                body = JsonSerializer.Serialize(
                    new
                    {
                        Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        Url = Site,
                        RegionalSettings = new { TimeZone = new { Id = 2 } },
                    });
            else if (request.Method == HttpMethod.Get &&
                     (path.Contains(ListId.ToString(), StringComparison.OrdinalIgnoreCase) ||
                      (path.EndsWith("/_api/web/lists", StringComparison.OrdinalIgnoreCase) && Uri
                          .UnescapeDataString(uri.Query)
                          .Contains(ListId.ToString(), StringComparison.OrdinalIgnoreCase))))
            {
                body = JsonSerializer.Serialize(
                    new
                    {
                        Id = ListId,
                        Title = "Site Pages",
                        ListItemEntityTypeFullName = "SP.Data.SitePagesItem",
                        RootFolder = new { ServerRelativeUrl = "/sites/fields/SitePages" },
                        Fields = new[]
                        {
                            Field("ID", 1), Field("Title", 2), Field("FileRef", 2), Field("FileLeafRef", 2),
                            Field("ContentTypeId", 2), Field("Editor", 20), Field("Related", 7),
                            Field("Choices", 15),
                        },
                    });
                if (path.EndsWith("/lists", StringComparison.OrdinalIgnoreCase)) body = "{\"value\":[" + body + "]}";
            }
            else
                throw new InvalidOperationException("Unexpected offline operation: " + request.Method + " " + path);

            if (batch)
            {
                HttpResponseMessage response = new(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        "--batchresponse_fields\r\nContent-Type: application/http\r\nContent-Transfer-Encoding: binary\r\n\r\n" +
                        "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n" + body +
                        "\r\n--batchresponse_fields--\r\n",
                        Encoding.UTF8),
                };
                response.Content.Headers.ContentType = new("multipart/mixed");
                response.Content.Headers.ContentType.Parameters.Add(new("boundary", "batchresponse_fields"));
                return response;
            }

            return new(HttpStatusCode.OK)
            {
                RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }

        /// <summary>Declares exact field types so the real SDK, not a mock, creates its lazy-loaded value objects.</summary>
        private static FieldDeclaration Field(string name, int type) => new(Guid.NewGuid(), name, type);
    }

    /// <summary>Supplies the field metadata needed by the actual SDK list-stream parser.</summary>
    private sealed record FieldDeclaration(Guid Id, string InternalName, int FieldTypeKind)
    {
        public string Title => InternalName;

        public string TypeAsString =>
            FieldTypeKind switch
            {
                1 => "Integer",
                7 => "Lookup",
                15 => "MultiChoice",
                20 => "User",
                _ => "Text"
            };
    }

    /// <summary>Provides a non-optional SDK failure to verify the precise exception filter.</summary>
    private sealed class FailingLookup : IFieldLookupValue
    {
        public IField Field => throw new NotSupportedException();
        public int LookupId { get; set; }
        public string LookupValue => throw new ClientException(ErrorType.Unsupported, "Synthetic unsupported getter.");
        public bool IsSecretFieldValue => throw new NotSupportedException();
    }
}