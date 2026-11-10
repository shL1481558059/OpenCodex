using OpenCodex.Core.Protocols;
using OpenCodex.CoreBase.Domain.Proxy;
using Xunit;
namespace OpenCodex.Api.Tests;
public sealed class SharedCaptureTextTests:IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"ocx-shared-"+Guid.NewGuid().ToString("N"));
    private ProxyAuditSpool Spool()=>new(new(){Directory=_directory,MemoryThresholdBytes=0});
    [Fact]
    public void ExactFieldLeaseUsesOneSpoolAndOneFinalImmutableString()
    {
        var spool=Spool();
        using(var budget=new StreamCaptureBudget(1024*1024,100,100,spool,default))
        {
            var source=budget.CreateText();var converter=new ConverterText(budget);var field=new object();
            for(var i=0;i<100;i++)
            {
                budget.BeginEvent();budget.AppendTracked(source,field,"content",new string('x',1024));
                converter.Add(new string('x',1024),budget.BorrowText(field,"content"));
                Assert.Null(budget.BorrowText(new object(),"content"));
            }
            Assert.Equal(2L*100*1024,spool.Usage.Bytes);
            Assert.Same(source.ToString(),converter.ToString());
            budget.BeginEvent();Assert.Null(budget.BorrowText(field,"content"));
        }
        Assert.Equal((0L,0),spool.Usage);
    }
    [Fact]
    public void InterleavedChoicesAndRefusalDoNotReorderOrDeduplicateEqualDeltas()
    {
        var spool=Spool();using var budget=new StreamCaptureBudget(1024,100,100,spool,default);
        var first=budget.CreateText();var second=budget.CreateText();var converter=new ConverterText(budget);
        var a=new object();var b=new object();
        budget.AppendTracked(first,a,"content","same");budget.AppendTracked(second,b,"content","same");
        converter.Add("same",budget.BorrowText(a,"content"));converter.Add("same",budget.BorrowText(b,"content"));
        converter.Add("refusal");Assert.Equal("samesamerefusal",converter.ToString());
    }
    [Fact]
    public void PrefixLeaseCannotReadFutureBytesAndResetDetachesBeforeDiscardingSource()
    {
        var spool=Spool();using var budget=new StreamCaptureBudget(1024,100,100,spool,default);
        var source=budget.CreateText();var converter=new ConverterText(budget);var field=new object();
        budget.AppendTracked(source,field,"delta","a\ud83d");converter.Add("a\ud83d",budget.BorrowText(field,"delta"));
        budget.BeginEvent();budget.AppendTracked(source,field,"delta","\ude00b");
        Assert.Equal("a\ud83d",converter.ToString());converter.Add("\ude00b",budget.BorrowText(field,"delta"));
        source.Clear();budget.Append(source,"different snapshot");Assert.Equal("a😀b",converter.ToString());
    }
    [Fact]
    public void RepeatedOwnerFieldWithinOneEventIsNotAnAmbiguousBorrowAndCancellationReleasesAll()
    {
        var spool=Spool();using var stop=new CancellationTokenSource();
        using(var budget=new StreamCaptureBudget(1024,100,100,spool,stop.Token))
        {
            var source=budget.CreateText();var field=new object();var converter=new ConverterText(budget);
            budget.AppendTracked(source,field,"text","x");budget.AppendTracked(source,field,"text","x");
            Assert.Null(budget.BorrowText(field,"text"));
            budget.BeginEvent();budget.AppendTracked(source,field,"text","tail");converter.Add("tail",budget.BorrowText(field,"text"));
            stop.Cancel();Assert.ThrowsAny<OperationCanceledException>(()=>converter.Add("late"));
        }
        Assert.Equal((0L,0),spool.Usage);
    }
    public void Dispose(){if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}
