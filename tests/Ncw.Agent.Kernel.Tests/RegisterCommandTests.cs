using Ncw.Agent.Kernel.Extensions;

namespace Ncw.Agent.Kernel.Tests;

/// <summary><c>RegisterCommand</c> 扩展点(06 §1):一方扩展贡献斜杠命令(一段提示词模板),宿主按注册顺序收集;不关心命令的注册表实现不用改(默认实现什么也不做)。</summary>
public sealed class RegisterCommandTests
{
    private sealed class Ext(string id, params CommandDefinition[] commands) : IAgentExtension
    {
        public string Id => id;

        public int Order => 0;

        public void Register(IExtensionRegistry registry)
        {
            foreach (var command in commands)
            {
                registry.RegisterCommand(command);
            }
        }
    }

    private sealed class MinimalRegistry : IExtensionRegistry
    {
        public void On<TEvent>(ExtensionPoint<TEvent> point, Func<TEvent, ExtensionContext, CancellationToken, ValueTask> handler)
        {
        }

        public void Mutate<T>(MutatePoint<T> point, Func<T, ExtensionContext, CancellationToken, ValueTask> handler)
            where T : class
        {
        }

        public void Transform<T>(TransformPoint<T> point, Func<T, ExtensionContext, CancellationToken, ValueTask<T>> handler)
        {
        }

        public void Gate<TEvent, TDecision>(GatePoint<TEvent, TDecision> point, Func<TEvent, ExtensionContext, CancellationToken, ValueTask<TDecision>> handler, Func<Exception, TDecision>? onFailure = null)
        {
        }

        public void LastWins<T>(LastWinsPoint<T> point, Func<T, ExtensionContext, CancellationToken, ValueTask<T?>> handler)
            where T : class
        {
        }

        public void RegisterTool(Ncw.Agent.Kernel.Tools.IAgentTool tool)
        {
        }

        public void ContributeSystemSection(string name, Func<SessionInfo, string?> section)
        {
        }
    }

    [Fact]
    public void The_host_collects_registered_commands_in_registration_order_across_extensions()
    {
        var host = new ExtensionHost([new Ext("a", new CommandDefinition("one", "d1", "p1"), new CommandDefinition("two", "d2", "p2", "<x>")), new Ext("b", new CommandDefinition("three", "d3", "p3"))]);

        Assert.Equal(["one", "two", "three"], host.RegisteredCommands.Select(c => c.Name));
        Assert.Equal("<x>", host.RegisteredCommands[1].ArgumentHint);
    }

    [Fact]
    public void A_registry_that_does_not_care_about_commands_compiles_and_ignores_them()
    {
        new Ext("a", new CommandDefinition("one", "d", "p")).Register(new MinimalRegistry());
    }

    [Fact]
    public void An_extension_without_commands_leaves_the_list_empty()
    {
        Assert.Empty(new ExtensionHost([]).RegisteredCommands);
    }
}
