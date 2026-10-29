using System.Runtime.CompilerServices;

namespace TeamsGateway.Gateway.E2E;

/// <summary>
/// 打真的 gateway、會真的在 Teams 發訊息的測試：沒設 <c>GATEWAY_API_KEY</c>（沒配置 live 目標）就 skip，
/// 一般 <c>dotnet test</c> 不會誤打到 Teams。要跑的版本有門檻時再加 <see cref="MinGatewayVersionAttribute"/>。
/// </summary>
/// <remarks>xunit v3 的 <c>SkipUnless</c> 在執行時才看 <see cref="GatewayTarget.Configured"/>，不是 discovery 時。</remarks>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "GATEWAY_API_KEY is not set; skipping live E2E canary";
        SkipUnless = nameof(GatewayTarget.Configured);
        SkipType = typeof(GatewayTarget);
    }
}
