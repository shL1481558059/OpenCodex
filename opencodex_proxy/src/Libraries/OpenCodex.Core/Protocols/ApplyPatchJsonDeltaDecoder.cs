using System.Text;

namespace OpenCodex.Core.Protocols;

internal sealed class ApplyPatchJsonDeltaDecoder
{
    private static readonly string[] CandidateKeys = ["\"patch\"", "\"input\"", "\"command\""];

    private readonly StringBuilder _buffer = new();
    private int _valueStartIndex = -1;
    private int _consumeIndex = -1;
    private bool _completed;
    private bool _escapePending;
    private int _unicodeDigitsRemaining;
    private int _unicodeValue;

    public string Append(string jsonFragment)
    {
        if (string.IsNullOrEmpty(jsonFragment) || _completed)
        {
            return string.Empty;
        }

        _buffer.Append(jsonFragment);
        if (_valueStartIndex < 0)
        {
            _valueStartIndex = FindValueStart(_buffer);
            if (_valueStartIndex < 0)
            {
                return string.Empty;
            }

            _consumeIndex = _valueStartIndex;
        }

        var delta = new StringBuilder();
        while (_consumeIndex >= 0 && _consumeIndex < _buffer.Length)
        {
            var current = _buffer[_consumeIndex];
            if (_unicodeDigitsRemaining > 0)
            {
                if (!TryParseHex(current, out var hex))
                {
                    break;
                }

                _unicodeValue = (_unicodeValue << 4) | hex;
                _unicodeDigitsRemaining--;
                _consumeIndex++;
                if (_unicodeDigitsRemaining == 0)
                {
                    delta.Append((char)_unicodeValue);
                    _unicodeValue = 0;
                }

                continue;
            }

            if (_escapePending)
            {
                if (!TryDecodeEscape(current, delta))
                {
                    break;
                }

                _escapePending = false;
                _consumeIndex++;
                continue;
            }

            if (current == '\\')
            {
                _escapePending = true;
                _consumeIndex++;
                continue;
            }

            if (current == '"')
            {
                _completed = true;
                _consumeIndex++;
                break;
            }

            delta.Append(current);
            _consumeIndex++;
        }

        return delta.ToString();
    }

    private static int FindValueStart(StringBuilder buffer)
    {
        var json = buffer.ToString();
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
