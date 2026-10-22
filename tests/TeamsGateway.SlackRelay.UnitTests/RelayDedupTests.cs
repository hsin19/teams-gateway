using TeamsGateway.Contracts;
using TeamsGateway.SlackRelay;

namespace TeamsGateway.SlackRelay.UnitTests;

/// <summary>轉過的訊息再來一次：只有真的新版才更新 + 留證，重送（斷線補收）不動。</summary>
public class RelayDedupTests
{
    private static MessageDto Msg(long version, bool edited = false, bool deleted = false) =>
        new("1792640000000", "19:x@thread.v2", "8:me", "me", "Text", "hi", 1, DateTimeOffset.UtcNow, version, edited, deleted);

    [Fact]
    public void Same_version_is_a_replay() =>
        Assert.False(RelayWorker.IsNewer(Msg(1792640000000), 1792640000000));

    [Fact]
    public void Older_version_is_a_replay() =>
        Assert.False(RelayWorker.IsNewer(Msg(1792640000000), 1792640480711));

    [Fact]
    public void Higher_version_is_an_edit() =>
        Assert.True(RelayWorker.IsNewer(Msg(1792640480711, edited: true), 1792640000000));

    // 這功能上線前轉的訊息沒記版本：只看編輯 / 刪除標記
    [Fact]
    public void Unknown_shown_version_needs_edit_or_delete_marker()
    {
        Assert.False(RelayWorker.IsNewer(Msg(1792640000000), null));
        Assert.True(RelayWorker.IsNewer(Msg(1792640480711, edited: true), null));
        Assert.True(RelayWorker.IsNewer(Msg(1792640480711, deleted: true), null));
    }
}
