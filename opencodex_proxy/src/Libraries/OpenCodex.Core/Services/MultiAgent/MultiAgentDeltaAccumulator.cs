using System.Text;
using OpenCodex.Core.Errors;

namespace OpenCodex.Core.Services.MultiAgent;

public sealed class MultiAgentStreamingOptions
{
    public int MaxPendingInjections {get;set;}=8;
    public long MaxPendingInjectionBytes {get;set;}=16L*1024*1024;
    public int MaxBufferedEventBytes { get; set; }=64*1024*1024;
    // UTF16 code units, shared across all live item builders in one coordinator request.
    public int MaxAccumulatedDeltaCharacters { get; set; } = 8 * 1024 * 1024;
    public int MaxAccumulatedDeltaFields { get; set; } = 4096;
}

internal sealed class MultiAgentDeltaBudget
{
    private readonly int _maxCharacters, _maxFields;
    internal int Characters { get; private set; }
    internal int Fields { get; private set; }
    internal MultiAgentDeltaBudget(MultiAgentStreamingOptions options)
    {
        if (options.MaxAccumulatedDeltaCharacters <= 0 || options.MaxAccumulatedDeltaFields <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        _maxCharacters=options.MaxAccumulatedDeltaCharacters;_maxFields=options.MaxAccumulatedDeltaFields;
    }
    internal void Reserve(int characters,bool newField)
    {
        if (characters > _maxCharacters-Characters || newField && Fields >= _maxFields)
            throw new UpstreamException("The multi-agent delta accumulation budget was exceeded; output was not truncated.");
        Characters+=characters;if(newField)Fields++;
    }
    internal void Release(int characters) { Characters-=characters;Fields--; }
}

/// <summary>One append per delta; snapshots are explicitly materialized only at acceptance,
/// cancellation/failure closure, or final validation. No per-delta prefix string is created.</summary>
internal sealed class MultiAgentDeltaAccumulator<TKey>(MultiAgentDeltaBudget budget) where TKey:notnull
{
    private readonly Dictionary<TKey,StringBuilder> _fields=[];
    internal IEnumerable<TKey> Keys => _fields.Keys;
    internal int Materializations { get; private set; }
    internal void Append(TKey key,string delta,string initial="")
    {
        var existing=_fields.TryGetValue(key,out var builder);
        var characters=existing?delta.Length:checked(initial.Length+delta.Length);
        budget.Reserve(characters,!existing);
        if(!existing) _fields.Add(key,builder=new StringBuilder(initial));
        builder!.Append(delta);
    }
    internal string? Take(TKey key)
    {
        if(!_fields.Remove(key,out var builder))return null;
        var value=builder.ToString();Materializations++;budget.Release(builder.Length);return value;
    }
    internal void Discard(TKey key)
    { if(_fields.Remove(key,out var builder))budget.Release(builder.Length); }
    internal void Clear() { foreach(var builder in _fields.Values)budget.Release(builder.Length);_fields.Clear(); }
}
