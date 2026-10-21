namespace TeamsGateway.SlackRelay;

/// <summary>綁 <c>Relay</c> 設定區。secret（gateway API key、Slack bot token）走 env / user-secrets，不進 appsettings。</summary>
public sealed class RelayOptions
{
    public GatewaySettings Gateway { get; set; } = new();
    public SlackSettings Slack { get; set; } = new();
    public LinkSettings Links { get; set; } = new();

    public sealed class LinkSettings
    {
        /// <summary>啟動時從 Slack 歷史回補多久內的訊息對應（給回覆連結 / 原地編輯用）；更舊的不管。</summary>
        public int RetentionHours { get; set; } = 24;
    }

    public sealed class GatewaySettings
    {
        public string BaseUrl { get; set; } = "http://localhost:5199";
        public string? ApiKey { get; set; }
    }

    public sealed class SlackSettings
    {
        public string? BotToken { get; set; }
        /// <summary>沒對應到 <see cref="Routes"/> 的對話落這個頻道；null = 不轉。</summary>
        public string? DefaultChannel { get; set; }
        /// <summary>
        /// Teams 對話 → Slack 頻道 的轉送路由。
        /// 用 list 而非 dict：Teams 對話 id 含 <c>:</c>，當 config dict 的 key 會被 .NET config 的階層分隔符拆掉。
        /// </summary>
        public List<ChannelRoute> Routes { get; set; } = [];
    }

    public sealed class ChannelRoute
    {
        /// <summary>Teams 對話 id（<c>19:…@thread.v2</c> / <c>@thread.skype</c>）。</summary>
        public string Chat { get; set; } = "";
        /// <summary>Slack 頻道 id 或 <c>#name</c>。</summary>
        public string Channel { get; set; } = "";
    }
}
