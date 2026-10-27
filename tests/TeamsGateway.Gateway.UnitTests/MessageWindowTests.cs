using TeamsGateway.Contracts;
using TeamsGateway.Gateway.Teams;

namespace TeamsGateway.Gateway.UnitTests;

public class MessageWindowTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    // 發在 T0 + minutes 分；version 預設等於發話時間（沒被動過），edited 時用指定的 version
    private static MessageDto Msg(int minutes, long? version = null, string type = "RichText/Html")
    {
        var at = T0.AddMinutes(minutes);
        var ms = at.ToUnixTimeMilliseconds();
        return new MessageDto(ms.ToString(), "19:abc@thread.v2", "8:live:a", "A", type, $"m{minutes}", minutes, at, version ?? ms);
    }

    private static long VersionAt(int minutes) => T0.AddMinutes(minutes).ToUnixTimeMilliseconds();

    // 範圍內的訊息照發話時間由舊到新回；系統訊息（加人之類）不算
    [Fact]
    public void Collects_text_messages_in_range_oldest_first()
    {
        var collector = new MessageWindowCollector(since: T0.AddMinutes(10), until: T0.AddMinutes(40), limit: 50);

        var done = collector.Add([Msg(50), Msg(30), Msg(25, type: "ThreadActivity/AddMember"), Msg(20), Msg(5)]);
        var window = collector.Build(reachedTop: false);

        Assert.True(done); // m5 的 version 已經比 since 舊
        Assert.Equal(["m20", "m30"], window.Messages.Select(m => m.Content));
        Assert.False(window.More);
    }

    // chatsvc 照 version 排：被編輯過的舊訊息會排在前面，不能因為它的發話時間比 since 舊就停，後面還有範圍內的
    [Fact]
    public void Edited_old_message_on_first_page_does_not_stop_paging()
    {
        var collector = new MessageWindowCollector(since: T0.AddMinutes(10), until: null, limit: 50);

        var done = collector.Add([Msg(1, version: VersionAt(60)), Msg(40)]);
        Assert.False(done);
        done = collector.Add([Msg(20), Msg(2)]);
        var window = collector.Build(reachedTop: false);

        Assert.True(done);
        Assert.Equal(["m20", "m40"], window.Messages.Select(m => m.Content));
    }

    // 超過 limit：留最新的，並標示範圍內還有更舊的
    [Fact]
    public void Keeps_newest_limit_and_reports_more()
    {
        var collector = new MessageWindowCollector(since: null, until: null, limit: 2);

        var done = collector.Add([Msg(30), Msg(20), Msg(10)]);
        var window = collector.Build(reachedTop: false);

        Assert.True(done); // 已有 2 則，第 3 則的 version 比第 2 新的那則舊，後面都擠不進來
        Assert.Equal(["m20", "m30"], window.Messages.Select(m => m.Content));
        Assert.True(window.More);
    }

    // 剛好收滿 limit 則就停（沒有被截掉的），但沒翻到 since 也沒到頂：範圍內可能還有更舊的
    [Fact]
    public void Exactly_limit_without_reaching_since_reports_more()
    {
        var collector = new MessageWindowCollector(since: null, until: T0.AddMinutes(25), limit: 2);

        Assert.False(collector.Add([Msg(40), Msg(20), Msg(15)]));
        Assert.True(collector.Add([Msg(12, type: "ThreadActivity/AddMember")]));
        var window = collector.Build(reachedTop: false);

        Assert.Equal(["m15", "m20"], window.Messages.Select(m => m.Content));
        Assert.True(window.More);
    }

    // 翻到對話最頂：沒有更舊的了
    [Fact]
    public void Reaching_the_top_means_no_more()
    {
        var collector = new MessageWindowCollector(since: null, until: null, limit: 10);

        collector.Add([Msg(30), Msg(20)]);
        var window = collector.Build(reachedTop: true);

        Assert.Equal(2, window.Messages.Count);
        Assert.False(window.More);
    }

    // until 之後（含）的不收；拿上一次最舊那則的時間當 until 往回翻，不會重複
    [Fact]
    public void Until_is_exclusive()
    {
        var collector = new MessageWindowCollector(since: null, until: T0.AddMinutes(20), limit: 10);

        collector.Add([Msg(30), Msg(20), Msg(10)]);
        var window = collector.Build(reachedTop: true);

        Assert.Equal(["m10"], window.Messages.Select(m => m.Content));
    }
}
