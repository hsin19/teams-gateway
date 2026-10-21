namespace TeamsGateway.SlackRelay;

/// <summary>綁 <c>Relay</c> 設定區。secret（gateway API key、Slack bot token）走 env / user-secrets，不進 appsettings。</summary>
public sealed class RelayOptions
{
    public GatewaySettings Gateway { get; set; } = new();
    public SlackSettings Slack { get; set; } = new();

    public sealed class GatewaySettings
    {
        public string BaseUrl { get; set; } = "http://localhost:5199";
        public string? ApiKey { get; set; }
    }

    public sealed class SlackSettings
    {
        public string? BotToken { get; set; }
        /// <summary>沒對應到 <see cref="ChannelMap"/> 的對話落這個頻道；null = 不轉。</summary>
        public string? DefaultChannel { get; set; }
        /// <summary>Teams 對話 id → Slack 頻道（id 或 #name）。空 = 全部走 DefaultChannel。</summary>
        public Dictionary<string, string> ChannelMap { get; set; } = new();
    }
}
