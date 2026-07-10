using Xunit;

public sealed class ArgumentTextTests
{
    [Fact]
    public void FormatAndParsePreserveSpacedArguments()
    {
        var args = new[] { "--verbose", "value with spaces", "quoted\"value" };
        Assert.Equal(args, ArgumentText.Parse(ArgumentText.Format(args)));
    }
}
