namespace OpenCodex.Core.Services.MultiAgent;

// Conservative admission weight, not a physical heap measurement. Unknown borrowed or
// mutable extension values cannot be admitted without first normalizing their ownership.
internal static class ClientGraphWeight
{
    internal static long Estimate(object? value, long cap, int depth = 0)
    {
        if (depth > 64) return cap + 1;
        switch (value)
        {
            case null: return 8;
            case string text: return 24L + 2L * text.Length;
            case IReadOnlyDictionary<string,object?> dictionary:
                long total = 128;
                foreach (var pair in dictionary)
                {
                    total += 64L + 2L * pair.Key.Length + Estimate(pair.Value, cap, depth + 1);
                    if (total > cap) return cap + 1;
                }
                return total;
            case IReadOnlyList<object?> list:
                long bytes = 64;
                foreach (var item in list) { bytes += 8L + Estimate(item, cap, depth + 1); if (bytes > cap) return cap + 1; }
                return bytes;
            case bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal: return 32;
            default: return cap + 1;
        }
    }
}
