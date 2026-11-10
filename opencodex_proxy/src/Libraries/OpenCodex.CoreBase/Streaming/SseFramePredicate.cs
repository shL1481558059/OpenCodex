namespace OpenCodex.Core.Protocols;

/// <summary>Keep the public string predicate API, while known pipeline predicates can
/// inspect an already-owned event without materializing its complete generated wire.
/// Arbitrary extension predicates still receive exactly their original wire string.</summary>
public sealed class SseFramePredicate
{
    private readonly Func<string,bool> _legacy;
    private readonly Func<SseFrame,bool> _typed;
    private SseFramePredicate(Func<string,bool> legacy,Func<SseFrame,bool> typed){_legacy=legacy;_typed=typed;}
    private bool Invoke(string wire)=>_legacy(wire);
    public static Func<string,bool> Create(Func<string,bool> legacy,Func<SseFrame,bool> typed)=>new SseFramePredicate(legacy,typed).Invoke;
    public static Func<string,bool> Never { get; }=Create(static _=>false,static _=>false);
    public static bool Evaluate(Func<string,bool> predicate,SseFrame frame)=>predicate.HasSingleTarget && predicate.Target is SseFramePredicate known
        ?known._typed(frame):predicate(frame.WireText);
}
