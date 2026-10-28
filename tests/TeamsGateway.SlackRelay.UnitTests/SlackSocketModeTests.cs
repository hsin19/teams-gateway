using System.Text;
using System.Text.Json;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>Socket Mode 的 envelope 與 ack（ADR 0012）。連線本身要真的 Slack，不在單元測試裡。</summary>
public class SlackSocketModeTests
{
    [Fact]
    public void Events_envelope_parses_and_acks_with_its_id()
    {
        var envelope = SlackSocketMode.Parse("""
            {"envelope_id":"e1","type":"events_api","accepts_response_payload":false,"retry_attempt":0,
             "payload":{"type":"event_callback","event_id":"Ev1","event":{"type":"app_mention","user":"U1","text":"<@UBOT> hi","ts":"1.2","channel":"C1","thread_ts":"1.1"}}}
            """u8);

        Assert.Equal(("events_api", "e1"), (envelope?.Type, envelope?.EnvelopeId));
        var mention = envelope!.Payload!.Value.Deserialize(SlackJsonContext.Default.EventCallback)!.Event!;
        Assert.Equal(("app_mention", "U1", "C1", "1.1"), (mention.Type, mention.User, mention.Channel, mention.ThreadTs));
        Assert.Equal("""{"envelope_id":"e1"}""", Encoding.UTF8.GetString(SlackSocketMode.Ack("e1")));
    }

    [Fact]
    public void Button_payload_parses()
    {
        var envelope = SlackSocketMode.Parse("""
            {"envelope_id":"e2","type":"interactive","payload":{"type":"block_actions","user":{"id":"U1"},"channel":{"id":"C1"},
             "response_url":"https://hooks.slack.com/actions/T/1/x","actions":[{"action_id":"send","value":"C1|1.2|"}]}}
            """u8);

        var click = envelope!.Payload!.Value.Deserialize(SlackJsonContext.Default.BlockActionsPayload)!;
        Assert.Equal(("U1", "https://hooks.slack.com/actions/T/1/x", "send", "C1|1.2|"),
            (click.User?.Id, click.ResponseUrl, click.Actions![0].ActionId, click.Actions[0].Value));
        Assert.Equal(new SlackSender.MessageRef("C1", "1.2", null), SlackSender.ParseRef(click.Actions[0].Value));
    }

    [Fact]
    public void Disconnect_and_garbage()
    {
        Assert.Equal(("disconnect", "refresh_requested"), (SlackSocketMode.Parse("""{"type":"disconnect","reason":"refresh_requested"}"""u8)?.Type,
            SlackSocketMode.Parse("""{"type":"disconnect","reason":"refresh_requested"}"""u8)?.Reason));
        Assert.Null(SlackSocketMode.Parse("not json"u8));
    }

    // response_url 帶著 bot token 打出去：只接受 Slack 自己的主機
    [Theory]
    [InlineData("https://hooks.slack.com/actions/T/1/x", true)]
    [InlineData("http://hooks.slack.com/actions/T/1/x", false)]
    [InlineData("https://hooks.slack.com.evil.example/x", false)]
    [InlineData("https://evil.example/hooks.slack.com", false)]
    public void Only_slack_response_urls_are_used(string url, bool expected) => Assert.Equal(expected, SlackClient.IsSlackUrl(url));
}
