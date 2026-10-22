using TeamsGateway.SlackRelay;

namespace TeamsGateway.SlackRelay.UnitTests;

public class VersionLabelTests
{
    // 原文的 version = 訊息 id（發送時間）；編輯後 version = 編輯時間
    [Fact]
    public void Original_version_is_labelled_with_send_time()
    {
        var label = RelayWorker.VersionLabel("1792640000000", 1792640000000);

        Assert.StartsWith("_Original_", label);
        Assert.Contains("<!date^1792640000^", label); // Slack 日期格式吃 unix 秒
    }

    [Fact]
    public void Edited_version_is_labelled_with_edit_time()
    {
        var label = RelayWorker.VersionLabel("1792640000000", 1792640480711);

        Assert.StartsWith("_Edited_", label);
        Assert.Contains("<!date^1792640480^", label);
        Assert.Contains("|2026-", label); // 不支援日期格式的 client 顯示 fallback
    }

    // 這功能上線前轉的訊息沒存版本，退回不帶時間的標題
    [Fact]
    public void Unknown_version_falls_back_to_plain_label() =>
        Assert.Equal("_Previous version_", RelayWorker.VersionLabel("1792640000000", null));
}
