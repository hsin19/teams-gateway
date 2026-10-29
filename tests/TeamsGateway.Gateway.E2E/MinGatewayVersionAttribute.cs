using System.Reflection;
using Xunit.v3;

namespace TeamsGateway.Gateway.E2E;

/// <summary>
/// 目標 gateway 比 <see cref="Minimum"/> 舊就 skip：功能合併了、部署端還沒換版時，每天的 canary 不會因為舊版沒有這個功能而紅。
/// 本機沒帶版本時是 <c>1.0.0</c>，一律會跑；讀不到版本（連不上、看不懂）也照跑，讓真正的錯誤露出來。
/// 沒配置 live 目標（沒有 API key）時不查，交給測試本身 skip。
/// </summary>
/// <remarks>
/// xunit v3 在 <see cref="BeforeAfterTestAttribute.Before"/> 裡 <c>Assert.Skip</c> 會算成 skipped、不跑測試本體。
/// <c>[Fact(Skip = …)]</c> 在 discovery 時就定了，<c>SkipUnless</c> 只能指向沒有參數的 static 屬性，都帶不了版本號。
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class MinGatewayVersionAttribute(string minimum) : BeforeAfterTestAttribute
{
    public string Minimum { get; } = minimum;

    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (!GatewayTarget.Configured)
            return;
        // Before 只有同步版；版本整個測試行程只查一次
        var target = GatewayTarget.Version.Value.GetAwaiter().GetResult();
        Assert.SkipWhen(target is { } v && v < Version.Parse(Minimum), $"Gateway {target} is older than {Minimum}; skipping");
    }
}
