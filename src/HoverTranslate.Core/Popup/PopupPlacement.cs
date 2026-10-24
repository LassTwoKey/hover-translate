using HoverTranslate.Core.Models;

namespace HoverTranslate.Core.Popup;

/// <summary>С какой стороны карточки стрелка: Top — карточка под словом, стрелка смотрит вверх.</summary>
public enum ArrowSide
{
    Top,
    Bottom,
}

/// <summary>Размеры оформления в физических пикселях (логические × масштаб монитора).</summary>
public readonly record struct PopupMetrics(double Gap, double ArrowWidth, double ArrowHeight, double CornerRadius, double ScreenMargin)
{
    /// <summary>Логические размеры по умолчанию, пересчитанные для масштаба монитора.</summary>
    public static PopupMetrics ForScale(double scale) => new(
        Gap: 2 * scale, ArrowWidth: 20 * scale, ArrowHeight: 10 * scale, CornerRadius: 8 * scale, ScreenMargin: 8 * scale);
}

/// <summary>Итоговое положение: окно карточки и окно стрелки.</summary>
public sealed record PopupLayout(ScreenRect Card, ScreenRect Arrow, ArrowSide Side);

/// <summary>Размещение карточки относительно слова. Чистая геометрия, все величины — физические пиксели.</summary>
public static class PopupPlacement
{
    /// <summary>
    /// Под словом со стрелкой вверх; если снизу не помещается — над словом со стрелкой вниз;
    /// если не помещается нигде — туда, где больше места, с прижатием к краю. Карточка никогда
    /// не выходит за рабочую область монитора; стрелка указывает на центр слова, но не заходит
    /// на скруглённые углы.
    /// </summary>
    public static PopupLayout Place(ScreenRect word, double cardWidth, double cardHeight, ScreenRect workArea, PopupMetrics m)
    {
        double minX = workArea.X + m.ScreenMargin, maxRight = workArea.Right - m.ScreenMargin;
        double minY = workArea.Y + m.ScreenMargin, maxBottom = workArea.Bottom - m.ScreenMargin;
        cardWidth = Math.Min(cardWidth, maxRight - minX);
        cardHeight = Math.Min(cardHeight, maxBottom - minY);

        double belowY = word.Bottom + m.Gap + m.ArrowHeight;
        double aboveY = word.Y - m.Gap - m.ArrowHeight - cardHeight;
        double spaceBelow = maxBottom - belowY, spaceAbove = aboveY - minY;

        ArrowSide side;
        double y;
        if (cardHeight <= spaceBelow)
        {
            side = ArrowSide.Top;
            y = belowY;
        }
        else if (aboveY >= minY)
        {
            side = ArrowSide.Bottom;
            y = aboveY;
        }
        else if (spaceBelow >= spaceAbove + cardHeight)
        {
            side = ArrowSide.Top;
            y = Math.Min(belowY, maxBottom - cardHeight);
        }
        else
        {
            // Места нет ни сверху, ни снизу: прижимаем к краю, стрелка — к ближайшей стороне слова.
            side = word.CenterY - minY > maxBottom - word.CenterY ? ArrowSide.Bottom : ArrowSide.Top;
            y = side == ArrowSide.Top ? maxBottom - cardHeight : minY;
        }

        double x = Math.Clamp(word.CenterX - cardWidth / 2, minX, Math.Max(minX, maxRight - cardWidth));
        var card = new ScreenRect(x, y, cardWidth, cardHeight);

        double inset = m.CornerRadius + m.ArrowWidth / 2;
        double arrowCenter = Math.Clamp(word.CenterX, card.X + inset, Math.Max(card.X + inset, card.Right - inset));
        // Стрелка заходит на 1 px внутрь карточки, чтобы между ними не было щели.
        double arrowY = side == ArrowSide.Top ? card.Y - m.ArrowHeight + 1 : card.Bottom - 1;
        var arrow = new ScreenRect(arrowCenter - m.ArrowWidth / 2, arrowY, m.ArrowWidth, m.ArrowHeight);
        return new PopupLayout(card, arrow, side);
    }
}
