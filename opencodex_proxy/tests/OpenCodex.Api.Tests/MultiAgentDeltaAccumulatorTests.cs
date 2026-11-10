using OpenCodex.Core.Errors;
using OpenCodex.Core.Services.MultiAgent;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class MultiAgentDeltaAccumulatorTests
{
    [Fact]
    public void LongDeltasDoNotMaterializePrefixesAndFieldsAreIndependentUntilTaken()
    {
        var budget=new MultiAgentDeltaBudget(new());var chunks=new MultiAgentDeltaAccumulator<int>(budget);
        for(var i=0;i<10000;i++) { chunks.Append(0,"中😀 ");chunks.Append(1,"x"); }
        Assert.Equal(0,chunks.Materializations);
        Assert.Equal(50000,budget.Characters);Assert.Equal(2,budget.Fields);
        Assert.Equal(string.Concat(Enumerable.Repeat("中😀 ",10000)),chunks.Take(0));
        Assert.Equal(new string('x',10000),chunks.Take(1));
        Assert.Equal(2,chunks.Materializations);Assert.Equal(0,budget.Characters);Assert.Equal(0,budget.Fields);
        Assert.Null(chunks.Take(0));
    }
    [Fact]
    public void SharedRequestBudgetRejectsWithoutMutationAndDiscardReleasesCapacity()
    {
        var budget=new MultiAgentDeltaBudget(new() { MaxAccumulatedDeltaCharacters=8,MaxAccumulatedDeltaFields=2 });
        var a=new MultiAgentDeltaAccumulator<int>(budget);var b=new MultiAgentDeltaAccumulator<int>(budget);
        a.Append(1,"abc","p");b.Append(1,"1234");
        Assert.Throws<UpstreamException>(()=>a.Append(1,"x"));
        Assert.Throws<UpstreamException>(()=>b.Append(2,""));
        Assert.Equal("pabc",a.Take(1));b.Discard(1);
        b.Append(2,"z");b.Clear();Assert.Equal(0,budget.Characters);Assert.Equal(0,budget.Fields);
        Assert.Equal(1,a.Materializations);Assert.Equal(0,b.Materializations);
    }
}
