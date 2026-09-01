using Xunit;

public sealed class BenchmarkTests
{
    [Fact]
    public void Generate_ExcludesBaselineAndChangesOneSettingAtATime()
    {
        var baseline = new Profile { Name = "test", Ctx = 8192, Ngl = 20, Batch = 512, UBatch = 256, Parallel = 1 };
        var cases = BenchmarkCases.Generate(baseline,
        [
            new BenchmarkSweep { Setting = "ctx", Minimum = "4096", Maximum = "12288" },
            new BenchmarkSweep { Setting = "cache_k", Values = ["q4_0", "q8_0"] }
        ]);

        Assert.Equal(3, cases.Count);
        Assert.DoesNotContain(cases, x => x.Setting == "");
        Assert.All(cases.Where(x => x.Setting != "ctx"), x => Assert.Equal(8192, x.Profile.Ctx));
        Assert.Contains(cases, x => x.Setting == "ctx" && x.Profile.Ctx == 4096);
        Assert.Contains(cases, x => x.Setting == "ctx" && x.Profile.Ctx == 12288);
        Assert.Contains(cases, x => x.Setting == "cache_k" && x.Profile.CacheK == "q8_0");
    }

    [Fact]
    public void Generate_SkipsRangeValuesMatchingTheBaseline()
    {
        var profile = new Profile { Name = "test", Ctx = 4, Ngl = 0, Batch = 1, UBatch = 1, Parallel = 1 };
        var cases = BenchmarkCases.Generate(profile, [new BenchmarkSweep { Setting = "ctx", Minimum = "4", Maximum = "4" }]);

        Assert.Empty(cases);
    }

    [Fact]
    public void Generate_UsesTheRequestedNumberOfContextSteps()
    {
        var profile = new Profile { Name = "test", Ctx = 100, Ngl = 0, Batch = 1, UBatch = 1, Parallel = 1 };

        var cases = BenchmarkCases.Generate(profile, [new BenchmarkSweep { Setting = "ctx", Minimum = "10", Maximum = "50", Steps = 5 }]);

        Assert.Equal([10, 20, 30, 40, 50], cases.Select(x => x.Profile.Ctx));
    }

    [Fact]
    public void GenerateCacheLayer_KeepsTheChosenContextAndCoversCacheFormats()
    {
        var contextProfile = new Profile { Name = "test", Ctx = 32768, Ngl = 0, Batch = 1, UBatch = 1, Parallel = 1 };

        var cases = BenchmarkCases.GenerateCacheLayer(contextProfile);

        Assert.Equal(5, cases.Count);
        Assert.All(cases, item => Assert.Equal(32768, item.Profile.Ctx));
        Assert.Contains(cases, item => item.Profile.CacheK == "q4_0" && item.Profile.CacheV == "q4_0");
        Assert.Contains(cases, item => item.Profile.CacheK == "q8_0" && item.Profile.CacheV == "q8_0");
        Assert.Contains(cases, item => item.Profile.CacheK == "f16" && item.Profile.CacheV == "f16");
    }

    [Theory]
    [InlineData("The result is 93", 93, true)]
    [InlineData("The result is 93.", 93, true)]
    [InlineData("The result is 92", 93, false)]
    public void MathSuite_GradesTheFinalInteger(string response, int answer, bool expected)
    {
        Assert.Equal(expected, MathSuite.IsCorrect(response, answer));
    }

    [Fact]
    public void Generate_RejectsInvalidSettings()
    {
        var profile = new Profile { Name = "test" };
        var error = Assert.Throws<InvalidOperationException>(() => BenchmarkCases.Generate(profile, [new BenchmarkSweep { Setting = "unknown", Minimum = "1", Maximum = "2" }]));
        Assert.Contains("Unsupported", error.Message);
    }

    [Fact]
    public void Report_IsSelfContainedAndEscapesContent()
    {
        var report = new BenchmarkRecord
        {
            ProfileName = "<unsafe>",
            StartedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            Workload = new BenchmarkWorkload { Prompt = "<script>alert(1)</script>" },
            Cases = [new BenchmarkCase { Label = "baseline", Status = BenchmarkCaseStatus.Completed, TelemetryAvailable = true, VramUsedBytes = 1024 }]
        };

        var html = BenchmarkReport.Html(report);

        Assert.Contains("<!doctype html>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;unsafe&gt;", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("Embedded data", html);
        Assert.Contains("Pre-Warmup VRAM", html);
        Assert.Contains("Post-Warmup VRAM", html);
        Assert.Contains("Free-VRAM", html);
    }

    [Fact]
    public void Report_LabelsCloseToOomHeadroom()
    {
        var warning = new BenchmarkCase { TelemetryAvailable = true, VramUsedBytes = 13, VramTotalBytes = 16 };
        var critical = new BenchmarkCase { TelemetryAvailable = true, VramUsedBytes = 15, VramTotalBytes = 16 };

        Assert.StartsWith("WARNING", BenchmarkReport.Headroom(warning));
        Assert.StartsWith("CRITICAL", BenchmarkReport.Headroom(critical));
        Assert.Contains("81%", BenchmarkReport.FormatVram(warning));
        Assert.Contains("post-warmup peak VRAM 0.0/0.0 GiB (81%)", BenchmarkReport.ProgressVramDetail(warning));
        Assert.Equal("WARNING", BenchmarkReport.Risk(warning));
        Assert.Equal("NORMAL", BenchmarkReport.Risk(new BenchmarkCase { TelemetryAvailable = true, VramUsedBytes = 7, VramTotalBytes = 16 }));
    }

    [Fact]
    public void MemoryPosture_SeparatesHealthyTightAndPartialOffload()
    {
        var healthy = new BenchmarkCase { TelemetryAvailable = true, VramUsedBytes = 10, VramTotalBytes = 16, Profile = new Profile { Ngl = 99 } };
        var tight = new BenchmarkCase { TelemetryAvailable = true, VramUsedBytes = 14, VramTotalBytes = 16, Profile = new Profile { Ngl = 99 } };
        var partial = new BenchmarkRecord { Cases = [new BenchmarkCase { Status = BenchmarkCaseStatus.OutOfMemory, Profile = new Profile { Ngl = 99 } }] };

        Assert.Contains("HEALTHY", BenchmarkReport.MemoryPosture(new BenchmarkRecord(), healthy));
        Assert.Contains("TIGHT", BenchmarkReport.MemoryPosture(new BenchmarkRecord(), tight));
        Assert.StartsWith("PARTIAL OFFLOAD REQUIRED", BenchmarkReport.MemoryPosture(partial, null));
    }

    [Fact]
    public void Store_RoundTripsTerminalStatuses()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lltop-benchmark-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = new BenchmarkRecord
            {
                ProfileName = "profile", StartedAt = DateTimeOffset.Now,
                Cases =
                [
                    new BenchmarkCase { Status = BenchmarkCaseStatus.Completed },
                    new BenchmarkCase { Status = BenchmarkCaseStatus.Failed },
                    new BenchmarkCase { Status = BenchmarkCaseStatus.Cancelled },
                    new BenchmarkCase { Status = BenchmarkCaseStatus.OutOfMemory }
                ]
            };
            var path = BenchmarkStore.SaveJson(directory, source);
            var loaded = BenchmarkStore.Load(path);

            Assert.Equal(source.Cases.Select(x => x.Status), loaded.Cases.Select(x => x.Status));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
