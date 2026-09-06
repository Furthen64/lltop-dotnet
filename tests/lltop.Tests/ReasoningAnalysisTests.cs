using Xunit;

public sealed class ReasoningAnalysisTests
{
    [Fact]
    public void OlderServerReceivesEffortThroughMergedTemplateParameters()
    {
        var profile = new Profile { ReasoningEffort = "medium", ExtraArgs = ["--chat-template-kwargs", "{\"preserve_thinking\":true,\"reasoning_effort\":\"xhigh\"}"] };
        var capability = new ServerCapabilityRecord { SupportedOptions = new() { ["--chat-template-kwargs"] = true } };
        var args = ServerRunner.BuildLaunchPlan("server", profile, capability).FilteredArguments.ToList();
        Assert.DoesNotContain("--reasoning-effort", args);
        Assert.Equal(1, args.Count(x => x == "--chat-template-kwargs"));
        var json = System.Text.Json.Nodes.JsonNode.Parse(args[args.IndexOf("--chat-template-kwargs") + 1])!;
        Assert.Equal("medium", json["reasoning_effort"]!.GetValue<string>());
        Assert.True(json["preserve_thinking"]!.GetValue<bool>());
        Assert.Contains("xhigh", profile.ExtraArgs[1]);
    }

    [Fact]
    public void UnsupportedEffortCannotBeSilentlyDropped()
    {
        Assert.Throws<InvalidOperationException>(() => ServerRunner.BuildLaunchPlan("server", new Profile { ReasoningEffort = "medium" }, new()));
        ServerRunner.BuildLaunchPlan("server", new Profile { ReasoningEffort = "default" }, new());
    }

    [Fact]
    public void GuidanceOffersOnlyDetectedChoicesAndFlagsUnlistedCurrentEffort()
    {
        var metadata = new GgufMetadata(3, new Dictionary<string, object?>
        {
            ["general.name"] = "Qwen3.8-27B",
            ["tokenizer.chat_template"] = "{% if resolved_reasoning_effort not in ('xhigh', 'medium', 'low') %}{% endif %}"
        });
        Assert.Equal(new[] { "low", "medium", "xhigh" }, ReasoningAnalysis.EffortLevels(metadata));
        var guidance = ReasoningAnalysis.Guidance(metadata, "high");
        Assert.Contains("Start with Medium", guidance);
        Assert.Contains("'high' was not found", guidance);
        Assert.DoesNotContain("was not found", ReasoningAnalysis.Guidance(metadata, ""));
        Assert.DoesNotContain("was not found", ReasoningAnalysis.Guidance(metadata, "default"));
    }

    [Fact]
    public void UnknownTemplateDoesNotOfferInventedChoices()
    {
        var metadata = new GgufMetadata(3, new Dictionary<string, object?> { ["general.name"] = "Qwen3.8" });
        Assert.Empty(ReasoningAnalysis.EffortLevels(metadata));
        Assert.Contains("No effort choices could be confirmed", ReasoningAnalysis.Guidance(metadata, "high"));
        Assert.DoesNotContain("Start with Medium", ReasoningAnalysis.Guidance(metadata, "high"));
    }

    [Fact]
    public void ReportsTemplateEvidenceWithoutGuessingFromName()
    {
        var metadata = new GgufMetadata(3, new Dictionary<string, object?>
        {
            ["general.name"] = "Qwen3.8 custom",
            ["tokenizer.chat_template"] = "{% if enable_thinking %}{% endif %}"
        });
        var report = ReasoningAnalysis.Describe(metadata);
        Assert.Contains("on/off control", report);
        Assert.Contains("no reasoning_effort reference", report);
        Assert.DoesNotContain("xhigh", report);
    }

    [Fact]
    public void ExtractsEffortLevelsFromTemplateValidation()
    {
        var metadata = new GgufMetadata(3, new Dictionary<string, object?>
        {
            ["tokenizer.chat_template"] = "{% if resolved_reasoning_effort not in ('xhigh', 'medium', 'low') %}{{ raise_exception('invalid') }}{% endif %}"
        });
        Assert.Contains("xhigh, medium, low", ReasoningAnalysis.Describe(metadata));
    }

    [Fact]
    public void MissingTemplateIsUnknown()
    {
        Assert.Contains("support is unknown", ReasoningAnalysis.Describe(new(3, new Dictionary<string, object?>())));
    }

    [Fact]
    public void EffortSurvivesProfileStorageAndCopyAndReachesLaunchArguments()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lltop-reasoning-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(directory);
            store.Save(new Profile { Name = "Reasoning", ReasoningEffort = "medium" });
            var loaded = Assert.Single(store.LoadAll().Profiles).Copy("Copy");
            Assert.Equal("medium", loaded.ReasoningEffort);
            var args = ServerRunner.BuildArguments(loaded).ToList();
            Assert.Equal("medium", args[args.IndexOf("--reasoning-effort") + 1]);
            loaded.ApplyRecommendedSettings();
            Assert.Equal("", loaded.ReasoningEffort);
            Assert.DoesNotContain("--reasoning-effort", ServerRunner.BuildArguments(loaded));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
