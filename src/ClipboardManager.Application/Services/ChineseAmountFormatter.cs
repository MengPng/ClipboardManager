using System.Text;

namespace ClipboardManager.Application.Services;

/// <summary>
/// 报销单/财务用的大写金额：1234.50 → 壹仟贰佰叁拾肆元伍角整。
/// 只精确到分，超出两位小数按四舍五入进位（调用方据此提示）；金额上限 9999 万亿（16 位整数）。
/// </summary>
public static class ChineseAmountFormatter
{
    /// <summary>Exclusive upper bound: 10^16 yuan (一亿亿) has no unit name in this scheme.</summary>
    public const decimal MaxAmount = 10_000_000_000_000_000m;

    private const string Digits = "零壹贰叁肆伍陆柒捌玖";
    private static readonly string[] PlaceUnits = ["", "拾", "佰", "仟"];
    private static readonly string[] GroupUnits = ["", "万", "亿", "万亿"];

    /// <param name="roundedToCent">True when the value had more than two decimals and was rounded to 分.</param>
    /// <returns>False when the amount is beyond <see cref="MaxAmount"/>; <paramref name="text"/> is then empty.</returns>
    public static bool TryFormat(decimal value, out string text, out bool roundedToCent)
    {
        var cents = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        roundedToCent = cents != value;
        text = string.Empty;
        var negative = cents < 0;
        var magnitude = Math.Abs(cents);
        if (magnitude >= MaxAmount) return false;

        var yuan = decimal.Truncate(magnitude);
        var fraction = (int)Math.Round((magnitude - yuan) * 100m, MidpointRounding.AwayFromZero);
        var jiao = fraction / 10;
        var fen = fraction % 10;

        var builder = new StringBuilder();
        if (negative && magnitude != 0m) builder.Append('负');
        if (yuan > 0m)
        {
            AppendYuan(builder, yuan.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append('元');
        }
        else if (fraction == 0)
        {
            builder.Append("零元整");
            text = builder.ToString();
            return true;
        }

        if (fraction == 0)
        {
            builder.Append('整');
        }
        else
        {
            if (jiao > 0) builder.Append(Digits[jiao]).Append('角');
            else if (yuan > 0m) builder.Append('零');
            if (fen > 0) builder.Append(Digits[fen]).Append('分');
            else builder.Append('整');
        }

        text = builder.ToString();
        return true;
    }

    private static void AppendYuan(StringBuilder builder, string digits)
    {
        var zeroPending = false;
        var wroteAny = false;
        var groupHasDigit = false;
        for (var index = 0; index < digits.Length; index++)
        {
            var position = digits.Length - 1 - index;
            var digit = digits[index] - '0';
            if (digit == 0)
            {
                zeroPending = wroteAny;
            }
            else
            {
                if (zeroPending) builder.Append('零');
                zeroPending = false;
                builder.Append(Digits[digit]).Append(PlaceUnits[position % 4]);
                wroteAny = true;
                groupHasDigit = true;
            }

            if (position % 4 == 0)
            {
                if (groupHasDigit)
                {
                    if (position > 0) builder.Append(GroupUnits[position / 4]);
                    // Trailing zeros of a non-zero group are absorbed by its unit; only the next group's own leading zeros count.
                    zeroPending = false;
                }
                groupHasDigit = false;
            }
        }
    }
}
