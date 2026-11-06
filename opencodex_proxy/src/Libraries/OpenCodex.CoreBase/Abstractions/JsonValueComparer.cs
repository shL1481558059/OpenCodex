using System.Text.Json;

namespace OpenCodex.CoreBase.Abstractions;

/// <summary>Compares parsed JSON values without materializing canonical JSON text.</summary>
public static class JsonValueComparer
{
    public static bool AreEqual(object? left, object? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;

        if (left is JsonElement leftElement)
        {
            return right is JsonElement rightElement
                ? JsonElement.DeepEquals(leftElement, rightElement)
                : JsonElementEqualsValue(leftElement, right);
        }

        if (right is JsonElement rightValue)
        {
            return JsonElementEqualsValue(rightValue, left);
        }

        if (left is string leftText)
        {
            return right is string rightText
                && string.Equals(leftText, rightText, StringComparison.Ordinal);
        }

        if (left is bool leftBoolean)
        {
            return right is bool rightBoolean && leftBoolean == rightBoolean;
        }

        if (IsJsonNumber(left) || IsJsonNumber(right))
        {
            return IsJsonNumber(left)
                && IsJsonNumber(right)
                && JsonNumbersEqual(left, right);
        }

        if (left is IReadOnlyDictionary<string, object?> leftObject)
        {
            if (right is not IReadOnlyDictionary<string, object?> rightObject
                || leftObject.Count != rightObject.Count)
            {
                return false;
            }

            foreach (var (key, value) in leftObject)
            {
                if (!rightObject.TryGetValue(key, out var candidate)
                    || !AreEqual(value, candidate))
                {
                    return false;
                }
            }

            return true;
        }

        if (left is IReadOnlyList<object?> leftArray)
        {
            if (right is not IReadOnlyList<object?> rightArray
                || leftArray.Count != rightArray.Count)
            {
                return false;
            }

            for (var index = 0; index < leftArray.Count; index++)
            {
                if (!AreEqual(leftArray[index], rightArray[index])) return false;
            }

            return true;
        }

        return left.Equals(right);
    }

    private static bool JsonElementEqualsValue(JsonElement element, object? value)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object when value is IReadOnlyDictionary<string, object?> objectValue =>
                ObjectEqualsElement(objectValue, element),
            JsonValueKind.Array when value is IReadOnlyList<object?> arrayValue =>
                ArrayEqualsElement(arrayValue, element),
            JsonValueKind.String => value is string text
                && string.Equals(element.GetString(), text, StringComparison.Ordinal),
            JsonValueKind.Number => TryReadNumber(element, out var number)
                && value is { } numberValue
                && IsJsonNumber(numberValue)
                && JsonNumbersEqual(number, numberValue),
            JsonValueKind.True => value is true,
            JsonValueKind.False => value is false,
            JsonValueKind.Null => value is null,
            _ => false
        };
    }

    private static bool ObjectEqualsElement(
        IReadOnlyDictionary<string, object?> value,
        JsonElement element)
    {
        var propertyCount = 0;
        foreach (var property in element.EnumerateObject())
        {
            propertyCount++;
            if (!value.TryGetValue(property.Name, out var candidate)
                || !JsonElementEqualsValue(property.Value, candidate))
            {
                return false;
            }
        }

        return propertyCount == value.Count;
    }

    private static bool ArrayEqualsElement(IReadOnlyList<object?> value, JsonElement element)
    {
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (index >= value.Count || !JsonElementEqualsValue(item, value[index])) return false;
            index++;
        }

        return index == value.Count;
    }

    private static bool TryReadNumber(JsonElement element, out object number)
    {
        if (element.TryGetInt32(out var integer))
        {
            number = integer;
            return true;
        }

        if (element.TryGetInt64(out var longValue))
        {
            number = longValue;
            return true;
        }

        if (element.TryGetDouble(out var doubleValue))
        {
            number = doubleValue;
            return true;
        }

        number = 0;
        return false;
    }

    private static bool JsonNumbersEqual(object left, object right)
    {
        if (TryGetDecimal(left, out var leftDecimal)
            && TryGetDecimal(right, out var rightDecimal))
        {
            return leftDecimal == rightDecimal;
        }

        return Convert.ToDouble(left, System.Globalization.CultureInfo.InvariantCulture)
            .Equals(Convert.ToDouble(right, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static bool TryGetDecimal(object value, out decimal result)
    {
        switch (value)
        {
            case byte number:
                result = number;
                return true;
            case sbyte number:
                result = number;
                return true;
            case short number:
                result = number;
                return true;
            case ushort number:
                result = number;
                return true;
            case int number:
                result = number;
                return true;
            case uint number:
                result = number;
                return true;
            case long number:
                result = number;
                return true;
            case ulong number:
                result = number;
                return true;
            case decimal number:
                result = number;
                return true;
            case float number when float.IsFinite(number):
                result = (decimal)number;
                return true;
            case double number when double.IsFinite(number)
                && number >= (double)decimal.MinValue
                && number <= (double)decimal.MaxValue:
                result = (decimal)number;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static bool IsJsonNumber(object value)
    {
        return value is byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal;
    }
}
