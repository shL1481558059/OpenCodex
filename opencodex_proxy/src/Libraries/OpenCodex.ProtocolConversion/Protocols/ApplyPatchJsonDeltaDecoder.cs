using System.Text;

namespace OpenCodex.ProtocolConversion;

internal sealed class ApplyPatchJsonDeltaDecoder
{
    private static readonly string[] CandidateKeys = ["\"patch\"", "\"input\"", "\"command\""];

    private StringBuilder? _prefix = new();
    private readonly StringBuilder _delta = new();
    private bool _invalid;
    private bool _completed;
    private bool _escapePending;
    private int _unicodeDigitsRemaining;
    private int _unicodeValue;
    private char? _pendingHighSurrogate;

    internal int BufferedCharacters => _prefix?.Length ?? 0;

    public string Append(string jsonFragment)
    {
        if (jsonFragment.Length == 0 || _completed || _invalid)
        {
            return string.Empty;
        }

        if (_prefix is { } prefix)
        {
            prefix.Append(jsonFragment);
            var buffered = prefix.ToString();
            var start = FindValueStart(buffered);
            if (start < 0)
            {
                return string.Empty;
            }
            _prefix = null;
            return Decode(buffered.AsSpan(start));
        }

        if (!_escapePending && _unicodeDigitsRemaining == 0 && _pendingHighSurrogate is null
            && jsonFragment.AsSpan().IndexOfAny('\\', '"') < 0
            && !char.IsHighSurrogate(jsonFragment[^1]))
        {
            return jsonFragment;
        }
        return Decode(jsonFragment.AsSpan());
    }

    private string Decode(ReadOnlySpan<char> fragment)
    {
        var delta = _delta;
        delta.Clear();
        foreach (var current in fragment)
        {
            if (_unicodeDigitsRemaining > 0)
            {
                if (!TryParseHex(current, out var hex))
                {
                    _invalid = true;
                    break;
                }

                _unicodeValue = (_unicodeValue << 4) | hex;
                _unicodeDigitsRemaining--;
                if (_unicodeDigitsRemaining == 0)
                {
                    AppendDecodedChar((char)_unicodeValue, delta);
                    _unicodeValue = 0;
                }

                continue;
            }

            if (_escapePending)
            {
                if (!TryDecodeEscape(current, delta))
                {
                    _invalid = true;
                    break;
                }

                _escapePending = false;
                continue;
            }

            if (current == '\\')
            {
                _escapePending = true;
                continue;
            }

            if (current == '"')
            {
                _completed = true;
                break;
            }

            AppendDecodedChar(current, delta);
        }

        return delta.ToString();
    }

    private void AppendDecodedChar(char value, StringBuilder delta)
    {
        if (_pendingHighSurrogate is char highSurrogate)
        {
            if (char.IsLowSurrogate(value))
            {
                delta.Append(highSurrogate);
                delta.Append(value);
                _pendingHighSurrogate = null;
                return;
            }

            delta.Append(highSurrogate);
            _pendingHighSurrogate = null;
        }

        if (char.IsHighSurrogate(value))
        {
            _pendingHighSurrogate = value;
        }
        else
        {
            delta.Append(value);
        }
    }

    private static int FindValueStart(string json)
    {
        var bestIndex = -1;
        foreach (var candidate in CandidateKeys)
        {
            var searchStart = 0;
            while (true)
            {
                var propertyIndex = json.IndexOf(candidate, searchStart, StringComparison.Ordinal);
                if (propertyIndex < 0)
                {
                    break;
                }

                var index = propertyIndex + candidate.Length;
                while (index < json.Length && char.IsWhiteSpace(json[index]))
                {
                    index++;
                }

                if (index >= json.Length || json[index] != ':')
                {
                    searchStart = propertyIndex + 1;
                    continue;
                }

                index++;
                while (index < json.Length && char.IsWhiteSpace(json[index]))
                {
                    index++;
                }

                if (index < json.Length && json[index] == '"')
                {
                    var valueStart = index + 1;
                    if (bestIndex < 0 || valueStart < bestIndex)
                    {
                        bestIndex = valueStart;
                    }

                    break;
                }

                searchStart = propertyIndex + 1;
            }
        }

        return bestIndex;
    }

    private static bool TryParseHex(char current, out int value)
    {
        if (current is >= '0' and <= '9')
        {
            value = current - '0';
            return true;
        }

        if (current is >= 'a' and <= 'f')
        {
            value = current - 'a' + 10;
            return true;
        }

        if (current is >= 'A' and <= 'F')
        {
            value = current - 'A' + 10;
            return true;
        }

        value = 0;
        return false;
    }

    private bool TryDecodeEscape(char current, StringBuilder delta)
    {
        switch (current)
        {
            case '"':
            case '\\':
            case '/':
                delta.Append(current);
                return true;
            case 'b':
                delta.Append('\b');
                return true;
            case 'f':
                delta.Append('\f');
                return true;
            case 'n':
                delta.Append('\n');
                return true;
            case 'r':
                delta.Append('\r');
                return true;
            case 't':
                delta.Append('\t');
                return true;
            case 'u':
                _unicodeDigitsRemaining = 4;
                _unicodeValue = 0;
                return true;
            default:
                return false;
        }
    }
}
