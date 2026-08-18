using System.Text;
using Xunit;

public sealed class VisionProjectorResolverTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "lltop-mmproj-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void FindBeside_UsesGgufIdentityToChooseMatchingProjector()
    {
        Directory.CreateDirectory(directory);
        var model = WriteGguf("Qwen3.6-35B-A3B-Q4.gguf", new()
        {
            ["general.name"] = "Qwen3.6 35B A3B",
            ["general.architecture"] = "qwen3moe"
        });
        var expected = WriteGguf("mmproj-BF16.gguf", new()
        {
            ["general.type"] = "mmproj",
            ["general.name"] = "Qwen3.6 35B A3B vision projector"
        });
        WriteGguf("mmproj-other.gguf", new()
        {
            ["general.type"] = "mmproj",
            ["general.name"] = "Gemma vision projector"
        });

        var result = VisionProjectorResolver.FindBeside(model);

        Assert.Equal(expected, result.Path);
        Assert.True(result.MetadataMatched);
        Assert.Contains("metadata", result.Message);
    }

    [Fact]
    public void FindBeside_SelectsOnlyReadableSiblingButLabelsUncertainMatch()
    {
        Directory.CreateDirectory(directory);
        var model = WriteGguf("Qwen3.6-35B-A3B-Q4.gguf", new() { ["general.name"] = "Qwen3.6 35B A3B" });
        var expected = WriteGguf("mmproj-BF16.gguf", new() { ["general.type"] = "mmproj" });

        var result = VisionProjectorResolver.FindBeside(model);

        Assert.Equal(expected, result.Path);
        Assert.False(result.MetadataMatched);
        Assert.Contains("only readable sibling", result.Message);
    }

    [Theory]
    [InlineData("Qwen3.6-35B-A3B-Q4.gguf", true)]
    [InlineData("Qwen3.8-27B-Q6K.gguf", false)]
    [InlineData("DeepSeek-V3.gguf", false)]
    public void SupportsModel_RecognizesTheVisionFamily(string modelName, bool expected)
    {
        Assert.Equal(expected, VisionProjectorResolver.SupportsModel(modelName));
    }

    string WriteGguf(string name, Dictionary<string, string> metadata)
    {
        var path = Path.Combine(directory, name);
        using var writer = new BinaryWriter(File.Create(path), Encoding.UTF8);
        writer.Write(Encoding.ASCII.GetBytes("GGUF"));
        writer.Write((uint)3);
        writer.Write((ulong)0);
        writer.Write((ulong)metadata.Count);
        foreach (var pair in metadata)
        {
            WriteString(writer, pair.Key);
            writer.Write((uint)8);
            WriteString(writer, pair.Value);
        }
        return path;
    }

    static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((ulong)bytes.Length);
        writer.Write(bytes);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
