using OpenCodex.Core.Services.Proxy;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class MultiAgentRepeatGuardTests
{
    [Fact]
    public void Apply_SubAgentTurnBelowLimit_KeepsPayloadUntouched()
    {
        var guard = new MultiAgentRepeatGuard();
        var payload = SubAgentPayload("amsg_task", "/root/researcher");

        var result = guard.Apply("session-a", payload);

        Assert.Same(payload, result);
        Assert.DoesNotContain("multi_agent_repeat_guard", Instructions(result));
    }

    [Fact]
    public void Apply_SubAgentTurnExceedingLimit_InjectsReminder()
    {
        var guard = new MultiAgentRepeatGuard();
        Dictionary<string, object?>? result = null;
        for (var turn = 1; turn <= MultiAgentRepeatGuard.TurnLimit; turn++)
        {
            result = guard.Apply("session-b", SubAgentPayload("amsg_task", "/root/researcher"));
        }

        Assert.NotNull(result);
        Assert.DoesNotContain("multi_agent_repeat_guard", Instructions(result!));

        var exceeded = guard.Apply("session-b", SubAgentPayload("amsg_task", "/root/researcher"));

        var instructions = Instructions(exceeded);
        Assert.Contains("multi_agent_repeat_guard", instructions);
        Assert.Contains("/root/researcher", instructions);
        Assert.Contains($"turn=\"{MultiAgentRepeatGuard.TurnLimit + 1}\"", instructions);
    }

    [Fact]
    public void Apply_RootTurn_IsNotCounted()
    {
        var guard = new MultiAgentRepeatGuard();
        Dictionary<string, object?>? result = null;
        for (var turn = 1; turn <= MultiAgentRepeatGuard.TurnLimit + 3; turn++)
        {
            result = guard.Apply("session-c", SubAgentPayload("amsg_root", "/root"));
        }

        Assert.NotNull(result);
        Assert.DoesNotContain("multi_agent_repeat_guard", Instructions(result!));
    }

    [Fact]
    public void Apply_DifferentSessionsAndTasks_UseIndependentCounters()
    {
        var guard = new MultiAgentRepeatGuard();
        for (var turn = 1; turn <= MultiAgentRepeatGuard.TurnLimit; turn++)
        {
            guard.Apply("session-d", SubAgentPayload("amsg_task", "/root/researcher"));
        }

        var otherTask = guard.Apply("session-d", SubAgentPayload("amsg_other", "/root/reviewer"));
        var otherSession = guard.Apply("session-e", SubAgentPayload("amsg_task", "/root/researcher"));

        Assert.DoesNotContain("multi_agent_repeat_guard", Instructions(otherTask));
        Assert.DoesNotContain("multi_agent_repeat_guard", Instructions(otherSession));
    }

    private static string Instructions(IReadOnlyDictionary<string, object?> payload)
    {
        return payload.TryGetValue("instructions", out var value) ? value as string ?? string.Empty : string.Empty;
    }

    private static Dictionary<string, object?> SubAgentPayload(string messageId, string recipient)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = "deepseek-v4.1-flash",
            ["instructions"] = "base instructions",
            ["input"] = new List<object?>
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "agent_message",
                    ["id"] = messageId,
                    ["author"] = "/root",
                    ["recipient"] = recipient,
                    ["content"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "encrypted_content",
                            ["encrypted_content"] = "task payload"
                        }
                    }
                }
            }
        };
    }
}
