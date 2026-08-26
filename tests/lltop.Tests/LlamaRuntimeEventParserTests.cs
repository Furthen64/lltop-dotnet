using Xunit;

public sealed class LlamaRuntimeEventParserTests
{
    [Theory]
    [InlineData("prompt_save: - saving prompt with length 42057, total state size = 1546.853 MiB (draft: 0.000 MiB)", "prompt_cache_save", "prompt_tokens", 42057d)]
    [InlineData("cache state: 4 prompts, 7492.431 MiB (limits: 8192.000 MiB, 75008 tokens, 167792 est)", "prompt_cache_state", "cache_tokens", 75008d)]
    [InlineData("cache size limit reached, removing oldest entry (size = 2201.968 MiB)", "prompt_cache_evict", "evicted_mib", 2201.968d)]
    [InlineData("looking for better prompt, base f_keep = 0.001, sim = 0.069", "prompt_cache_lookup", "f_keep", 0.001d)]
    [InlineData("created context checkpoint 1 of 4 (pos_min = 576, pos_max = 576, n_tokens = 577, size = 149.626 MiB)", "checkpoint_create", "checkpoint", 1d)]
    [InlineData("restored context checkpoint (pos_min = 50665, pos_max = 50665, n_tokens = 50666, n_past = 50666, size = 149.626 MiB)", "checkpoint_restore", "n_past", 50666d)]
    [InlineData("erased invalidated context checkpoint (pos_min = 36022, pos_max = 36022, n_tokens = 36023, ...)", "checkpoint_erase", "tokens", 36023d)]
    [InlineData("selected slot by LCP similarity, sim_best = 0.998 (> 0.100 thold), f_keep = 0.998", "context_reuse", "similarity", 0.998d)]
    [InlineData("processing task, is_child = 0", "request_start", "is_child", 0d)]
    [InlineData("stop processing: n_tokens = 42057, truncated = 0", "request_end", "context_tokens", 42057d)]
    [InlineData("prompt eval time = 2969.40 ms / 2915 tokens", "prompt_eval", "prompt_eval_ms", 2969.40d)]
    public void ParsesStructuredEvent(string line, string name, string field, double value)
    {
        var parsed = Assert.IsType<LlamaRuntimeEvent>(LlamaRuntimeEventParser.Parse(line));
        Assert.Equal(name, parsed.Event);
        Assert.Equal(value, Convert.ToDouble(parsed.Fields[field]));
        Assert.Equal(line, parsed.Raw);
    }

    [Theory]
    [InlineData("updating prompt cache", "prompt_cache_update")]
    [InlineData("forcing full prompt re-processing due to lack of cache data", "full_prompt_reprocess")]
    [InlineData("all slots are idle", "slots_idle")]
    public void ParsesMarkerWithoutNumbers(string line, string name) => Assert.Equal(name, LlamaRuntimeEventParser.Parse(line)?.Event);

    [Fact]
    public void IgnoresUnrelatedLines() => Assert.Null(LlamaRuntimeEventParser.Parse("I server listening at http://127.0.0.1:8080"));

    [Fact]
    public void ParsesGenerationAsTelemetry()
    {
        var values = LlamaRuntimeEventParser.ParseGenerationTelemetry("slot print_timing: id 0 | task 1587 | n_decoded = 100, tg = 28.70 t/s");
        Assert.Equal(100, values!["decoded_tokens"]);
        Assert.Equal(28.70, values["generation_tps"]);
    }

    [Fact]
    public void IncludesSlotAndTaskWhenTheyAppearOnTheEventLine()
    {
        var parsed = LlamaRuntimeEventParser.Parse("slot: id 0 | task 110675 | processing task, is_child = 0");
        Assert.Equal(0, parsed!.Fields["slot"]);
        Assert.Equal(110675, parsed.Fields["task"]);
    }
}
