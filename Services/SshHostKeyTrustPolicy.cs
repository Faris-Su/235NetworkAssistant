using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Services;

public enum SshHostKeyDecision
{
    Unknown,
    Match,
    Changed,
}

/// <summary>纯本地 pin 校验规则，供 SSH 握手回调和场景测试共用。</summary>
public static class SshHostKeyTrustPolicy
{
    public static SshHostKeyDecision Evaluate(SshHostKeyInfo? trusted, SshHostKeyInfo presented)
    {
        if (trusted is null)
        {
            return SshHostKeyDecision.Unknown;
        }

        return SshHostKeyTrustStore.Matches(trusted, presented)
            ? SshHostKeyDecision.Match
            : SshHostKeyDecision.Changed;
    }
}
