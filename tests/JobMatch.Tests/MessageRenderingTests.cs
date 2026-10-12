using System.Text.Json;
using FluentAssertions;
using JobMatch.Domain.Conversation;
using JobMatch.Infrastructure.WhatsApp;
using Xunit;

namespace JobMatch.Tests;

public class MessageRenderingTests
{
    [Fact]
    public void Long_messages_split_at_paragraphs_within_500_chars_NFR05()
    {
        var paragraphs = Enumerable.Range(1, 6).Select(i => $"Paragraph {i}: " + new string('x', 150));
        var message = new OutboundMessage(string.Join("\n\n", paragraphs)) { Buttons = DialoguePrompts.YesNo };

        var parts = MessageRendering.Split(message);

        parts.Should().HaveCountGreaterThan(1);
        parts.Should().OnlyContain(p => p.Text.Length <= OutboundMessage.MaxLength);
        parts.Last().Buttons.Should().NotBeNull("options stay with the last part");
        parts.Take(parts.Count - 1).Should().OnlyContain(p => p.Buttons == null);
        string.Join("\n\n", parts.Select(p => p.Text)).Should().Be(message.Text);
    }

    [Fact]
    public void Short_messages_are_untouched()
    {
        var m = new OutboundMessage("hello");
        MessageRendering.Split(m).Should().ContainSingle().Which.Should().BeSameAs(m);
    }

    [Fact]
    public void Button_payload_matches_the_Graph_API_shape()
    {
        var json = Serialise(WhatsAppClient.BuildPayload("233200000001", DialoguePrompts.AskDelete()));
        json.GetProperty("type").GetString().Should().Be("interactive");
        var interactive = json.GetProperty("interactive");
        interactive.GetProperty("type").GetString().Should().Be("button");
        var buttons = interactive.GetProperty("action").GetProperty("buttons");
        buttons.GetArrayLength().Should().Be(2);
        buttons[0].GetProperty("reply").GetProperty("id").GetString().Should().Be("1");
    }

    [Fact]
    public void List_payload_matches_the_Graph_API_shape_and_trims_long_titles()
    {
        var msg = new OutboundMessage("Pick") { ListButton = "Choose", List = new[] { new ReplyOption("1", new string('a', 40), "desc") } };
        var row = Serialise(WhatsAppClient.BuildPayload("233200000001", msg))
            .GetProperty("interactive").GetProperty("action").GetProperty("sections")[0].GetProperty("rows")[0];
        row.GetProperty("id").GetString().Should().Be("1");
        row.GetProperty("title").GetString()!.Length.Should().Be(24);
        row.GetProperty("description").GetString().Should().Be("desc");
    }

    [Fact]
    public void Plain_text_payload_for_messages_without_options()
    {
        var json = Serialise(WhatsAppClient.BuildPayload("233200000001", new OutboundMessage("hi")));
        json.GetProperty("type").GetString().Should().Be("text");
        json.GetProperty("text").GetProperty("body").GetString().Should().Be("hi");
    }

    private static JsonElement Serialise(object payload) => JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
}
