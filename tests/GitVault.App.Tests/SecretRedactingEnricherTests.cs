using FluentAssertions;
using GitVault.App.Logging;
using GitVault.Core.Security;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace GitVault.App.Tests;

public sealed class SecretRedactingEnricherTests
{
    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static (ILogger Logger, CapturingSink Sink) BuildLogger()
    {
        var sink = new CapturingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.With(new SecretRedactingEnricher(new SecretRedactor()))
            .WriteTo.Sink(sink)
            .CreateLogger();

        return (logger, sink);
    }

    [Fact]
    public void Secret_valued_properties_are_redacted_before_reaching_the_sink()
    {
        var (logger, sink) = BuildLogger();

        logger.Information("Read config line {Line}", "password=hunter2");

        var rendered = sink.Events.Single().RenderMessage();
        rendered.Should().NotContain("hunter2");
        rendered.Should().Contain(SecretRedactor.Placeholder);
    }

    [Fact]
    public void Nested_collections_are_redacted()
    {
        var (logger, sink) = BuildLogger();

        logger.Information("Lines {Lines}", new[] { "safe line", "token=ghp_abcdefghijklmnopqrstuvwxyz012345" });

        var rendered = sink.Events.Single().RenderMessage();
        rendered.Should().NotContain("ghp_abcdefghijklmnopqrstuvwxyz012345");
        rendered.Should().Contain("safe line");
    }

    [Fact]
    public void Dictionaries_are_redacted()
    {
        var (logger, sink) = BuildLogger();

        logger.Information("Map {Map}", new Dictionary<string, string>
        {
            ["helper"] = "manager",
            ["secret"] = "password=hunter2",
        });

        sink.Events.Single().RenderMessage().Should().NotContain("hunter2");
    }

    [Fact]
    public void Structures_are_redacted()
    {
        var (logger, sink) = BuildLogger();

        logger.Information("Entry {@Entry}", new { Host = "github.com", Note = "token=ghp_abcdefghijklmnopqrstuvwxyz012345" });

        var rendered = sink.Events.Single().RenderMessage();
        rendered.Should().Contain("github.com");
        rendered.Should().NotContain("ghp_abcdefghijklmnopqrstuvwxyz012345");
    }

    [Fact]
    public void Ordinary_properties_are_left_alone()
    {
        var (logger, sink) = BuildLogger();

        logger.Information("Scanned {Path} and found {Count} keys", "/home/user/.ssh", 3);

        var rendered = sink.Events.Single().RenderMessage();
        rendered.Should().Contain("/home/user/.ssh");
        rendered.Should().NotContain(SecretRedactor.Placeholder);
    }
}
