using System.Text.Json;
using TeamsGateway.SlackRelay;

namespace TeamsGateway.SlackRelay.UnitTests;

public class SlackImageBlocksTests
{
    [Fact]
    public void Text_section_then_one_image_block_per_file()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(SlackClient.ImageBlocks("*Alice*: look", ["F1", "F2"])));
        var blocks = json.RootElement.EnumerateArray().ToList();

        Assert.Equal(3, blocks.Count);
        Assert.Equal("section", blocks[0].GetProperty("type").GetString());
        Assert.Equal("mrkdwn", blocks[0].GetProperty("text").GetProperty("type").GetString());
        Assert.Equal("*Alice*: look", blocks[0].GetProperty("text").GetProperty("text").GetString());
        Assert.All(blocks.Skip(1), b => Assert.Equal("image", b.GetProperty("type").GetString()));
        Assert.Equal(["F1", "F2"], blocks.Skip(1).Select(b => b.GetProperty("slack_file").GetProperty("id").GetString()));
    }

    // section 文字上限 3000 字，超過要切成多塊，內容不能少
    [Fact]
    public void Long_text_splits_into_sections_within_limit()
    {
        var text = new string('a', 7000);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(SlackClient.ImageBlocks(text, ["F1"])));
        var sections = json.RootElement.EnumerateArray()
            .Where(b => b.GetProperty("type").GetString() == "section")
            .Select(b => b.GetProperty("text").GetProperty("text").GetString()!)
            .ToList();

        Assert.Equal(3, sections.Count);
        Assert.All(sections, s => Assert.True(s.Length <= 3000));
        Assert.Equal(text, string.Concat(sections));
    }
}
