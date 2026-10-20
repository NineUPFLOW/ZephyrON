using System.Text.RegularExpressions;
using Xunit;

namespace Zephyron.Core.Tests;

/// <summary>
/// Стили окна: базовый стиль годится целевому типу.
/// </summary>
/// <remarks>
/// <para>
/// <c>BasedOn</c> требует, чтобы тип базового стиля был типом-предком
/// целевого. WPF не проверяет это ни разбором XAML, ни компиляцией BAML —
/// проверка наступает, когда стиль впервые запечатывается, то есть при
/// загрузке окна. Так окно и падало 20.10: <c>NavMore</c> (Button) стоял
/// на <c>NavTab</c> (RadioButton), а RadioButton не предок Button.
/// </para>
/// <para>
/// Сборка этого не ловит, а тесты окна на Linux только собираются —
/// запускать их нечем. Потому проверка живёт здесь: она читает разметку
/// текстом и потому идёт и на Linux, и в CI.
/// </para>
/// </remarks>
public sealed class GuiStyleTests
{
    /// <summary>Открывающий тег <c>Style</c> со всеми свойствами в одной строке.</summary>
    private static readonly Regex StyleTag = new(@"<Style\b[^>]*>", RegexOptions.Compiled);

    private static readonly Regex TargetType = new(@"TargetType=""(?:{x:Type\s+)?([A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.Compiled);
    private static readonly Regex Key = new(@"x:Key=""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex BasedOn = new(@"BasedOn=""\{StaticResource\s+(?:\{x:Type\s+)?([^}""]+?)\s*\}*\s*""", RegexOptions.Compiled);

    /// <summary>
    /// Кто чей предок. Достаточно типов, что встречаются в разметке окна;
    /// новый тип надо внести сюда — иначе проверка о нём скажет.
    /// </summary>
    private static readonly Dictionary<string, string> Parent = new()
    {
        ["FrameworkElement"] = "UIElement",
        ["Control"] = "FrameworkElement",
        ["ContentControl"] = "Control",
        ["UserControl"] = "ContentControl",
        ["Window"] = "ContentControl",
        ["Label"] = "ContentControl",
        ["ToolTip"] = "ContentControl",
        ["ButtonBase"] = "ContentControl",
        ["Button"] = "ButtonBase",
        ["ToggleButton"] = "ButtonBase",
        ["RadioButton"] = "ToggleButton",
        ["CheckBox"] = "ToggleButton",
        ["RepeatButton"] = "ButtonBase",
        ["TextBox"] = "Control",
        ["PasswordBox"] = "Control",
        ["ScrollViewer"] = "ContentControl",
        ["ScrollBar"] = "Control",
        ["Thumb"] = "Control",
        ["Separator"] = "Control",
        ["Selector"] = "ItemsControl",
        ["ItemsControl"] = "Control",
        ["HeaderedItemsControl"] = "ItemsControl",
        ["MenuItem"] = "HeaderedItemsControl",
        ["MenuBase"] = "ItemsControl",
        ["ContextMenu"] = "MenuBase",
        ["ComboBox"] = "Selector",
        ["ComboBoxItem"] = "ContentControl",
        ["ListBox"] = "Selector",
        ["ListView"] = "ListBox",
        ["ListBoxItem"] = "ContentControl",
        ["Decorator"] = "FrameworkElement",
        ["Border"] = "Decorator",
        ["Panel"] = "FrameworkElement",
        ["Grid"] = "Panel",
        ["StackPanel"] = "Panel",
        ["Shape"] = "FrameworkElement",
        ["Path"] = "Shape",
        ["Ellipse"] = "Shape",
        ["Rectangle"] = "Shape",
        ["TextBlock"] = "FrameworkElement",
    };

    private static bool IsAncestor(string ancestor, string type)
    {
        for (var current = type; Parent.TryGetValue(current, out var parent); current = parent)
        {
            if (parent == ancestor)
                return true;
        }

        return false;
    }

    private static string? Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Zephyron.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }

    [Fact]
    public void ABaseStyleFitsTheTypeItIsBasedOn()
    {
        if (Repository() is not { } repo)
            return;

        var root = Path.Combine(repo, "gui", "Zephyron.Gui");

        var files = Directory
            .EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        // Разметки нет — проверять нечего; пустой список значил бы, что
        // путь переехал и проверка стала зелёной, ничего не проверив.
        Assert.NotEmpty(files);

        var styles = new Dictionary<string, (string Type, string File)>();

        foreach (var file in files)
        {
            foreach (Match tag in StyleTag.Matches(File.ReadAllText(file)))
            {
                var type = TargetType.Match(tag.Value);
                var key = Key.Match(tag.Value);

                if (type.Success && key.Success)
                    styles[key.Groups[1].Value] = (type.Groups[1].Value, Path.GetFileName(file));
            }
        }

        var problems = new List<string>();

        foreach (var file in files)
        {
            foreach (Match tag in StyleTag.Matches(File.ReadAllText(file)))
            {
                var based = BasedOn.Match(tag.Value);

                if (!based.Success)
                    continue;

                var name = based.Groups[1].Value;

                if (!styles.TryGetValue(name, out var baseStyle))
                {
                    // {x:Type X} — базовый стиль по типу, а не по имени.
                    if (Parent.ContainsKey(name) || name is "UIElement" or "DependencyObject")
                        continue;

                    problems.Add($"{Path.GetFileName(file)}: базовый стиль «{name}» не найден");
                    continue;
                }

                var own = TargetType.Match(tag.Value);

                if (!own.Success || own.Groups[1].Value == baseStyle.Type)
                    continue;

                var ownType = own.Groups[1].Value;

                if (!Parent.ContainsKey(ownType) || !Parent.ContainsKey(baseStyle.Type))
                {
                    problems.Add($"{Path.GetFileName(file)}: тип «{ownType}» или «{baseStyle.Type}» не внесён в карту предков");
                    continue;
                }

                if (!IsAncestor(baseStyle.Type, ownType))
                {
                    problems.Add(
                        $"{Path.GetFileName(file)}: стиль «{ownType}» основан на «{name}» типа «{baseStyle.Type}», " +
                        $"а тот не предок «{ownType}» — окно упадёт при загрузке");
                }
            }
        }

        Assert.Empty(problems);
    }
}
