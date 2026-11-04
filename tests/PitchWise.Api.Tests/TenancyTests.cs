using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PitchWise.Api.Tests;

[Collection(nameof(ApiCollection))]
public class TenancyTests(ApiFixture api)
{
    private static async Task<int> CreateAnalysis(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/analyses", new { name = "Match", sport = "football" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task Api_RejectsMissingAndInvalidKeys()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/analyses")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient("wrong").GetAsync("/api/analyses")).StatusCode);
    }

    [Fact]
    public async Task PublicEndpoints_NeedNoKey()
    {
        var anonymous = api.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/share/not-a-token")).StatusCode);
    }

    [Fact]
    public async Task Tenant_CannotSeeAnotherTenantsAnalysis()
    {
        var other = await api.CreateTenantClient();
        var id = await CreateAnalysis(api.Client);

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/analyses/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/analyses/{id}/events")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/analyses/{id}/videos")).StatusCode);
        var write = await other.PostAsJsonAsync($"/api/analyses/{id}/events", new { timestamp_seconds = 1 });
        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);

        var list = await (await other.GetAsync("/api/analyses")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(list.EnumerateArray());
    }

    [Fact]
    public async Task Admin_RequiresTheOperatorToken()
    {
        var response = await api.CreateClient("wrong").PostAsJsonAsync("/api/admin/tenants", new { name = "X" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_CreatedKeyWorks()
    {
        var client = await api.CreateTenantClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/analyses")).StatusCode);
    }
}
