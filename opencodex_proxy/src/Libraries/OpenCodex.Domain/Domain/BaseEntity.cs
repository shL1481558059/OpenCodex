namespace OpenCodex.Core.Domain;

public abstract class BaseEntity
{
    public abstract object? GetId();
}

public abstract class BaseEntity<TKey> : BaseEntity
    where TKey : IEquatable<TKey>
{
    public TKey Id { get; set; } = default!;

    public override object? GetId()
    {
        return EqualityComparer<TKey>.Default.Equals(Id, default) ? null : Id;
    }
}
