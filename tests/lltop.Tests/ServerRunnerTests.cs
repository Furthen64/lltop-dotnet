using Xunit;

public sealed class ServerRunnerTests
{
    [Fact]
    public void BuildArguments_PreservesValuesAndFiltersConflictingFlashAttn()
    {
        var profile = new Profile
        {
            Model = "/models/a model.gguf", Port = 9000, Alias = "coding model", FlashAttn = "on",
            ExtraArgs = ["--verbose", "--flash-attn", "off", "--threads-http", "4"]
        };

        var args = ServerRunner.BuildArguments(profile).ToList();

        Assert.Contains("/models/a model.gguf", args);
        Assert.Contains("coding model", args);
        Assert.Equal(1, args.Count(x => x == "--flash-attn"));
        Assert.Contains("--verbose", args);
        Assert.Contains("--threads-http", args);
    }

    [Fact]
    public void Validate_RejectsInvalidLaunchValues()
    {
        var profile = new Profile { Name = "bad", Port = 70000 };
        var error = Assert.Throws<InvalidOperationException>(() => profile.Validate());
        Assert.Contains("Port", error.Message);
    }
}
