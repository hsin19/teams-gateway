using TeamsGateway.Contracts;

namespace TeamsGateway.Gateway.Teams;

/// <summary>
/// 一段時間範圍內的文字訊息，照發話時間由舊到新。
/// <c>More</c>：範圍內可能還有更舊的沒回（被 limit 截掉、或翻頁到上限還沒翻到範圍起點）。
/// </summary>
public sealed record MessageWindow(IReadOnlyList<MessageDto> Messages, bool More);

/// <summary>
/// 從 chatsvc 由新往舊一頁頁收 [since, until) 內的文字訊息，留最新的 limit 則，並判斷什麼時候可以不用再翻。
/// chatsvc 每頁是照 version（最後變動時間）由新到舊排，不是照發話時間：被編輯、被按表情的舊訊息會排到前面。
/// version 不會比發話時間早，所以翻到 version 比某個時間舊的，後面就不會再有那個時間之後發的訊息——停不停看 version，不能看 time。
/// </summary>
internal sealed class MessageWindowCollector(DateTimeOffset? since, DateTimeOffset? until, int limit)
{
    private readonly List<MessageDto> _candidates = [];
    private bool _coveredSince;

    /// <summary>收一頁；回 true 表示範圍內不會再有能進前 limit 則的訊息，不用再翻。</summary>
    public bool Add(IReadOnlyList<MessageDto> page)
    {
        foreach (var m in page)
            if (m.Type is "Text" or "RichText/Html" && (since is null || m.Time >= since) && (until is null || m.Time < until))
                _candidates.Add(m);

        // version 0 是解析不到的，不拿來判斷
        var versions = page.Where(m => m.Version > 0).Select(m => m.Version).ToList();
        if (versions.Count == 0)
            return false;
        var oldest = versions.Min();
        if (since is { } s && oldest < s.ToUnixTimeMilliseconds())
        {
            _coveredSince = true;
            return true;
        }
        // 已經有 limit 則：比第 limit 新的那則還舊的版本出現之後，後面發的都擠不進前 limit
        return _candidates.Count >= limit && oldest < KthNewest().ToUnixTimeMilliseconds();
    }

    /// <param name="reachedTop">已經翻到對話最舊的一頁。</param>
    public MessageWindow Build(bool reachedTop)
    {
        var newest = _candidates.DistinctBy(m => m.Id).OrderByDescending(m => m.Time).ToList();
        var more = newest.Count > limit || (!reachedTop && !_coveredSince);
        return new MessageWindow(newest.Take(limit).Reverse().ToList(), more);
    }

    private DateTimeOffset KthNewest() => _candidates.Select(m => m.Time).OrderByDescending(t => t).ElementAt(limit - 1);
}
