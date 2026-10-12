using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JobMatch.Domain.Abstractions;
using JobMatch.Domain.Conversation;
using JobMatch.Infrastructure.Nlu;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JobMatch.Tests;

/// <summary>
/// Contract tests against <c>nlp/tests/fixtures/nlu_response.json</c>: the Python service's
/// tests validate the same file against its response schema, so a breaking change on either
/// side of the HTTP boundary fails a test.
/// </summary>
public class NluClientTests
{
    private static readonly string FixtureJson =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nlu_response.json"));

    [Fact]
    public void Parses_the_shared_contract_fixture()
    {
        using var doc = JsonDocument.Parse(FixtureJson);
        var r = HttpNluClient.Parse(doc.RootElement);

        r.Intent.Should().Be("SKILL_DECLARE");
        r.Confidence.Should().Be(0.92);
        r.BelowThreshold.Should().BeFalse();
        r.Skills.Select(s => s.CanonicalName).Should().Equal("Block laying", "Plastering");
        r.Trade!.CategoryId.Should().Be("construction");
        r.Trade.ExampleSkills.Should().HaveCount(3);
        r.LocationDistrict.Should().Be("La Nkwantanang Madina");
        r.ExperienceYears.Should().Be(5.0);
        r.ModelVersion.Should().StartWith("intent@");
        r.LatencyMs.Should().Be(84.2);
        JsonDocument.Parse(r.EntitiesJson!).RootElement.GetArrayLength().Should().Be(5);
    }

    [Fact]
    public async Task Posts_utterance_and_state_to_the_nlu_endpoint()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, FixtureJson);
        var client = new HttpNluClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000") });

        var r = await client.AnalyzeAsync("i can lay blocks", "ElicitSkills", CancellationToken.None);

        handler.Uri!.AbsolutePath.Should().Be("/nlu");
        using var sent = JsonDocument.Parse(handler.Body!);
        sent.RootElement.GetProperty("utterance").GetString().Should().Be("i can lay blocks");
        sent.RootElement.GetProperty("state").GetString().Should().Be("ElicitSkills");
        r.Skills.Should().HaveCount(2);
    }

    [Fact]
    public async Task Falls_back_to_the_stub_when_the_service_fails()
    {
        var down = new HttpNluClient(new HttpClient(new RecordingHandler(HttpStatusCode.ServiceUnavailable, "{}"))
            { BaseAddress = new Uri("http://localhost:8000") });
        var client = new FallbackNluClient(down, new StubNluClient(TestCatalog.Instance), NullLogger<FallbackNluClient>.Instance);

        var r = await client.AnalyzeAsync("I am a plumber, i fix burst pipes in tema", "ElicitTrade", CancellationToken.None);

        r.ModelVersion.Should().Be(FallbackNluClient.FallbackVersion);
        r.Skills.Should().ContainSingle(s => s.CanonicalName == "Pipe fitting");
        r.Trade!.CategoryId.Should().Be("plumbing_metal");
        r.LocationDistrict.Should().Be("Tema");
    }

    [Fact]
    public async Task Does_not_swallow_caller_cancellation()
    {
        var primary = new ThrowingNluClient();
        var client = new FallbackNluClient(primary, new StubNluClient(TestCatalog.Instance), NullLogger<FallbackNluClient>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => client.AnalyzeAsync("x", "s", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class ThrowingNluClient : INluClient
    {
        public Task<NluResult> AnalyzeAsync(string utterance, string state, CancellationToken ct)
            => Task.FromException<NluResult>(new OperationCanceledException(ct));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _response;
        public Uri? Uri { get; private set; }
        public string? Body { get; private set; }

        public RecordingHandler(HttpStatusCode status, string response) => (_status, _response) = (status, response);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_response, Encoding.UTF8, "application/json")
            };
        }
    }
}
