using System.Windows;

namespace Zephyron.Gui;

/// <summary>
/// Узкий режим бокового меню: подписи пунктов скрыты, остаются значки.
/// </summary>
/// <remarks>
/// <para>
/// Присоединённым свойством, а не двумя шаблонами: шаблон пункта один
/// (Controls.xaml, <c>RailItem</c>), и переключение — это одно свойство
/// у каждого пункта, а не второй набор разметки, который пришлось бы
/// править парами.
/// </para>
/// <para>
/// Ставится окном (MainWindow), когда ширина окна не оставляет меню места.
/// Само меню при этом не исчезает: спрятать разделы значило бы лишить
/// человека доступа к ним, а на узком окне это ровно то, что нужно меньше
/// всего.
/// </para>
/// </remarks>
public static class RailCompact
{
    public static readonly DependencyProperty Is = DependencyProperty.RegisterAttached(
        "Is", typeof(bool), typeof(RailCompact), new PropertyMetadata(false));

    public static void SetIs(DependencyObject target, bool value) => target.SetValue(Is, value);

    public static bool GetIs(DependencyObject target) => (bool)target.GetValue(Is);
}
