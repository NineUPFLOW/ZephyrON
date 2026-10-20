using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Zephyron.Core;
using Zephyron.Core.Diagnostics;
using Zephyron.Core.Rules;
using Zephyron.Core.Updates;
using Zephyron.Proxy;
using Zephyron.Supervisor;

namespace Zephyron.Gui.Views;

/// <summary>
/// «Главная»: режим работы, сведения о защите и ссылки.
/// </summary>
/// <remarks>
/// <para>
/// Состояние связи и кнопка запуска живут в левой панели окна
/// (<see cref="ConnectionStatus"/>), а не здесь: по референсу это роль
/// панели, и состояние должно быть видно с любого раздела. Здесь его нет
/// намеренно — второй показ того же означал бы второй источник истины,
/// а два слова про одно состояние расходятся.
/// </para>
/// <para>
/// Поэтому раздел читает только то, что относится к настройке: пресет,
/// параметры подключения, автозапуск, обновление, предупреждения. Всё это
/// не меняется каждые две секунды, и своего таймера раздел не заводит —
/// перечитывает по подписке на общее состояние.
/// </para>
/// </remarks>
public partial class StatusView : UserControl
{
    /// <summary>Предупреждение, которое стоит прочитать до запуска.</summary>
    private sealed record WarningRow(string Title, string Body);

    /// <summary>Пресет, описание которого уже показано, — чтобы не читать файл на каждом тике.</summary>
    private string? _notedPreset = "\0";

    private void OnTelegram(object sender, RoutedEventArgs e) => OpenLink(About.Telegram);

    private void OnGitHub(object sender, RoutedEventArgs e) => OpenLink(About.Repository);

    /// <summary>Описание — README на GitHub: он и есть документация, другой нет.</summary>
    private void OnDocs(object sender, RoutedEventArgs e) => OpenLink(About.Repository + "#readme");

    private static void OpenLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception)
        {
            // Нет браузера по умолчанию — те же ссылки есть в «Ещё», в карточке «О программе».
        }
    }

    private void OnDesyncSettings(object sender, RoutedEventArgs e) => Open("desync");

    private void Open(string section) => (Window.GetWindow(this) as MainWindow)?.Open(section);

    /// <summary>
    /// Папка настроек в проводнике, с выделенным файлом.
    /// </summary>
    /// <remarks>
    /// Папку, а не файл в блокноте: окно работает от администратора, и блокнот,
    /// открытый им, тоже был бы администраторским — правка настроек мимо окна
    /// с полными правами без нужды. Проводник открывается от человека.
    /// </remarks>
    internal void OnQuickConfig(object sender, RoutedEventArgs e)
    {
        try
        {
            var file = Path.GetFullPath(AppSettings.DefaultPath);

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { File.Exists(file) ? "/select," + file : Path.GetDirectoryName(file)! },
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            ShowProblem("Проводник не открылся: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>То же окно, что у шестерёнки на вкладке VPN.</summary>
    private void OnTunnelSettings(object sender, RoutedEventArgs e)
    {
        var window = new TunnelSettingsWindow { Owner = Window.GetWindow(this) };

        window.ShowDialog();

        if (window.Changed)
        {
            ConnectionStatus.Refresh();
            this.Offer("Настройки туннеля изменены");
        }
    }

    public StatusView()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            ShowModes(settings);
            ShowAutostart();
            ShowSupport(settings);

            // Проверка обновлений идёт при запуске окна и может закончиться
            // уже после того, как «Главная» показана, — поэтому и подписка.
            // Отписка обязательна: событие статическое, а раздел пересоздаётся
            // при каждом возврате.
            UpdateNotice.Changed += OnUpdateChanged;
            ShowUpdate();

            // Состояние связи — по подписке; своего таймера раздел не заводит.
            ConnectionStatus.Changed += OnConnection;
            Apply(ConnectionStatus.Current);

            ShowWarnings();
        };

        Unloaded += (_, _) =>
        {
            UpdateNotice.Changed -= OnUpdateChanged;
            ConnectionStatus.Changed -= OnConnection;
        };

        // Сетка сведений: две колонки на широком окне, одна на узком.
        SizeChanged += (_, _) => ArrangeGrid();
    }

    private void OnUpdateChanged() => Dispatcher.InvokeAsync(ShowUpdate);

    private void ShowSupport(AppSettings settings) =>
        SupportCard.Visibility = settings.OnboardingDone && !settings.SupportCardHidden
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>Звезда ставится на странице репозитория — на неё и ведём.</summary>
    private void OnStar(object sender, RoutedEventArgs e) => OpenLink(About.Repository);

    /// <summary>«Поддержать» ведёт на страницу донатов владельца.</summary>
    private void OnSupport(object sender, RoutedEventArgs e) => OpenLink(About.Support);

    private void OnSupportHide(object sender, RoutedEventArgs e)
    {
        SupportCard.Visibility = Visibility.Collapsed;

        try
        {
            (AppSettings.Load(AppSettings.DefaultPath) with { SupportCardHidden = true })
                .Save(AppSettings.DefaultPath);
        }
        catch (Exception)
        {
            // Не сохранилось — карточка вернётся при следующем запуске, беды в том нет.
        }
    }

    /// <summary>Карточка «вышла новая версия» — если есть что и о нём не сказали «не сейчас».</summary>
    private void ShowUpdate()
    {
        var release = UpdateNotice.Available;

        if (!UpdateNotice.ShouldOffer(release, AppSettings.Load(AppSettings.DefaultPath)))
        {
            UpdateCard.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateTitle.Text = $"Вышла {release!.Version}";

        var highlights = UpdateNotice.Highlights(release.Notes);

        UpdateWhat.Text = highlights.Count > 0
            ? string.Join(" · ", highlights) + "."
            : "Установлена " + UpdateCheck.Current + ".";

        UpdateCard.Visibility = Visibility.Visible;
    }

    // Установка та же, что в блоке «Обновление» ниже, и на той же странице:
    // прежде кнопка уводила в «Ещё» ради одного вопроса «обновить?».
    private void OnUpdateNow(object sender, RoutedEventArgs e) => Updates.Install();

    // «Что нового» — окно обновления (владелец, 06.10), а не страница выпуска:
    // там примечания всех пропущенных версий, а браузер — одной кнопкой в нём.
    private void OnUpdateNotes(object sender, RoutedEventArgs e)
    {
        if (UpdateNotice.Available is not { } release)
            return;

        (Window.GetWindow(this) as MainWindow)?.ShowUpdateWindow(release);
    }

    /// <summary>Перечитать карточку обновления — после «Пропустить версию» в окне.</summary>
    internal void RefreshUpdate() => ShowUpdate();

    /// <summary>
    /// «Не сейчас»: об этой версии больше не напоминать. Точка у значка
    /// обновления в шапке окна остаётся — она тихая.
    /// </summary>
    private void OnUpdateLater(object sender, RoutedEventArgs e)
    {
        if (UpdateNotice.Available is not { } release)
            return;

        try
        {
            (AppSettings.Load(AppSettings.DefaultPath) with { DismissedUpdate = release.Version })
                .Save(AppSettings.DefaultPath);
        }
        catch (Exception)
        {
            // Не сохранилось — напомним при следующем запуске, беды в том нет.
        }

        UpdateCard.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Две колонки или одна — по ширине раздела.
    /// </summary>
    /// <remarks>
    /// Разметка одна; раскладку меняет <c>Grid.SetRow/SetColumn</c>. Так
    /// на узком окне карточки встают в столбик, а не сжимаются в две
    /// нечитаемые колонки: кегль ради экономии места не уменьшаем.
    /// </remarks>
    private void ArrangeGrid()
    {
        bool narrow = ActualWidth < 900;

        if (narrow)
        {
            CardParams.SetValue(Grid.RowProperty, 1); CardParams.SetValue(Grid.ColumnProperty, 0);
            CardAutostart.SetValue(Grid.RowProperty, 3); CardAutostart.SetValue(Grid.ColumnProperty, 0);
            Updates.SetValue(Grid.RowProperty, 5); Updates.SetValue(Grid.ColumnProperty, 0);

            foreach (var name in new[] { "CardPreset", "CardParams", "CardAutostart", "Updates" })
            {
                if (FindName(name) is FrameworkElement element)
                    element.SetValue(Grid.ColumnSpanProperty, 3);
            }
        }
        else
        {
            CardParams.SetValue(Grid.RowProperty, 0); CardParams.SetValue(Grid.ColumnProperty, 2);
            CardAutostart.SetValue(Grid.RowProperty, 2); CardAutostart.SetValue(Grid.ColumnProperty, 0);
            Updates.SetValue(Grid.RowProperty, 2); Updates.SetValue(Grid.ColumnProperty, 2);

            foreach (var name in new[] { "CardPreset", "CardParams", "CardAutostart", "Updates" })
            {
                if (FindName(name) is FrameworkElement element)
                    element.SetValue(Grid.ColumnSpanProperty, 1);
            }
        }

        // Три карточки режимов в строку только когда ширины хватает; иначе
        // они встают столбиком, и выбор остаётся читаемым.
        if (ModeGrid.Children.OfType<RadioButton>().ToList() is { Count: 3 } modes)
        {
            if (narrow)
            {
                modes[0].SetValue(Grid.RowProperty, 0); modes[0].SetValue(Grid.ColumnProperty, 0);
                modes[1].SetValue(Grid.RowProperty, 2); modes[1].SetValue(Grid.ColumnProperty, 0);
                modes[2].SetValue(Grid.RowProperty, 4); modes[2].SetValue(Grid.ColumnProperty, 0);
            }
            else
            {
                modes[0].SetValue(Grid.RowProperty, 0); modes[0].SetValue(Grid.ColumnProperty, 0);
                modes[1].SetValue(Grid.RowProperty, 0); modes[1].SetValue(Grid.ColumnProperty, 2);
                modes[2].SetValue(Grid.RowProperty, 0); modes[2].SetValue(Grid.ColumnProperty, 4);
            }
        }
    }

    /// <summary>
    /// Общее состояние связи изменилось — обновляем то, что от него зависит.
    /// </summary>
    private void OnConnection(ConnectionSnapshot snapshot) => Apply(snapshot);

    private void Apply(ConnectionSnapshot? snapshot)
    {
        if (snapshot?.Settings is not { } settings)
        {
            if (snapshot?.Problem is { } problem)
                ShowProblem(problem);

            return;
        }

        Problem.Visibility = Visibility.Collapsed;

        PresetValue.Text = settings.DescribePreset();
        ShowPresetNote(settings.PresetName);
        ServerValue.Text = TunnelStatus.ServerLine(settings, _exit);
        DnsValue.Text = settings.DnsServer;
        SubscriptionValue.Text = snapshot.Subscriptions;

        SetEngines(snapshot.Engines);
        ShowModes(settings);

        // Выход туннеля спрашивается у движка, как zy status: настройки
        // могут говорить «авто», а движок держаться WARP из кэша. Читается
        // в фоне и не чаще, чем движок пишет.
        _ = ReadExitAsync(settings, snapshot.Running && snapshot.Engines.Any(e => e.Name == "sing-box" && e.Word.Length > 0));
    }

    /// <summary>Выход, который держит движок; <c>null</c> — не знаем или туннеля нет.</summary>
    private string? _exit;
    private bool _readingExit;

    /// <summary>
    /// Перечитывает выход туннеля — в фоне и не чаще, чем пишет движок.
    /// </summary>
    /// <remarks>
    /// Прежний ответ держится до нового, чтобы строка не мигала на каждом тике.
    /// </remarks>
    private async Task ReadExitAsync(AppSettings settings, bool tunnel)
    {
        if (!tunnel)
        {
            _exit = null;
            return;
        }

        if (_readingExit)
            return;

        _readingExit = true;

        try
        {
            // В пуле целиком: до первого ожидания запрос идёт синхронно
            // (клиент, поиск прокси системы), а ответ пишется в журнал
            // недавних выходов — файлом.
            var (server, _) = await Task.Run(() => TunnelStatus.CurrentExitAsync(CancellationToken.None));

            if (server is null || server == _exit)
                return;

            _exit = server;
            ServerValue.Text = TunnelStatus.ServerLine(settings, server);
        }
        finally
        {
            _readingExit = false;
        }
    }

    private IReadOnlyList<EngineRow>? _engines;

    /// <summary>
    /// Строки движков — только если что-то в них изменилось.
    /// </summary>
    /// <remarks>
    /// Строки — записи, и сравниваются по значению: одинаковые не трогаем,
    /// иначе новый ItemsSource раз в тик — это новые элементы со всеми
    /// шаблонами, и окно дёргалось бы даже в покое.
    /// </remarks>
    private void SetEngines(IReadOnlyList<EngineRow> rows)
    {
        if (_engines is not null && _engines.SequenceEqual(rows))
            return;

        _engines = rows;

        ShowEngine(rows.FirstOrDefault(r => r.Name == "winws2"), DesyncDot, DesyncState, DesyncPid);
        ShowEngine(rows.FirstOrDefault(r => r.Name == "sing-box"), TunnelDot, TunnelState, TunnelPid);
    }

    /// <summary>Строка состояния движка в его карточке.</summary>
    /// <remarks>
    /// Нет строки — движок в этот запуск не поднимали: выключен или нет
    /// подписки. «Остановлен» здесь соврало бы, что он был.
    /// </remarks>
    private void ShowEngine(EngineRow? row, System.Windows.Shapes.Ellipse dot, TextBlock state, TextBlock pid)
    {
        if (row is null)
        {
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Faint");
            state.Text = "выключен";
            pid.Text = string.Empty;
            return;
        }

        dot.Fill = ConnectionStatus.Brush(this, row.ColourKey);
        state.Text = row.Word;

        // Номер процесса — чтобы найти движок в диспетчере задач (владелец,
        // 26.09). У движка не в порядке на его месте причина надзора: она
        // важнее номера.
        pid.Text = row.Detail.Length > 0
            ? row.Detail
            : row.ProcessId is { } id ? $"№{id}" : string.Empty;
        pid.ToolTip = row.Detail.Length > 0
            ? row.Detail
            : row.ProcessId is { } shown ? $"Процесс {shown} — так его ищут в диспетчере задач" : null;
    }

    private IReadOnlyList<WarningRow>? _warnings;
    private DateTimeOffset _warningsAt = DateTimeOffset.MinValue;
    private bool _warningsBusy;

    /// <summary>Сколько держать проверку антивирусов и hosts, прежде чем повторить.</summary>
    private static readonly TimeSpan WarningsInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Предупреждения — в фоне и раз в полминуты, а не в потоке окна.
    /// </summary>
    /// <remarks>
    /// Проверка антивирусов перебирает все процессы системы, а проверка hosts
    /// читает почти тысячу строк файла. Ни то, ни другое за две секунды
    /// не меняется.
    /// </remarks>
    private void ShowWarnings()
    {
        if (_warningsBusy || DateTimeOffset.Now - _warningsAt < WarningsInterval)
            return;

        _warningsBusy = true;

        var settings = AppSettings.TryLoad(AppSettings.DefaultPath, out _);
        bool needsDesync = settings?.NeedsDesync ?? false;
        bool hybrid = settings is not null && WorkModes.Of(settings.Engines) == WorkMode.Route;

        _ = Task.Run(() => CollectWarnings(needsDesync, hybrid)).ContinueWith(task =>
        {
            _warningsBusy = false;
            _warningsAt = DateTimeOffset.Now;

            if (task.IsCompletedSuccessfully)
                SetWarnings(task.Result);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void SetWarnings(IReadOnlyList<WarningRow> rows)
    {
        if (_warnings is not null && _warnings.SequenceEqual(rows))
            return;

        _warnings = rows;
        Warnings.ItemsSource = rows;
    }

    private static IReadOnlyList<WarningRow> CollectWarnings(bool needsDesync, bool hybrid)
    {
        var rows = new List<WarningRow>();

        // «Гибрид» решает, что через VPN, по именам. Браузер со своим
        // шифрованным DNS спрашивает имена мимо системы — наш резолвер их
        // не видит, правило не срабатывает, и сайт, которому нужен туннель,
        // идёт напрямую и не открывается.
        if (hybrid)
        {
            foreach (var browser in BrowserDns.Scan())
            {
                rows.Add(new WarningRow(
                    $"{browser.Browser} спрашивает имена сам, мимо системы",
                    "В «Гибриде» путь имени (через VPN или напрямую) решает наш резолвер, "
                    + "а такой браузер его не спрашивает: сайт, которому нужен туннель, "
                    + "пойдёт напрямую и не откроется. Выключите свой DNS в браузере: "
                    + browser.Setting + "."));
            }
        }

        if (HostsEditor.WhoReplaced() is { } who)
        {
            rows.Add(new WarningRow(
                $"Файл hosts переписан: {who}",
                "Изменённый hosts он считает признаком заражения и вернул файл к своему "
                + "умолчанию — вместе со всеми пинами. Пин на этой машине не живёт, пока "
                + "файл не внесён в доверенные."));
        }

        var guards = SecuritySoftware.Running();

        if (guards.Count > 0 && needsDesync)
        {
            rows.Add(new WarningRow(
                $"Работает {string.Join(", ", guards.Select(g => g.Name))}",
                "Его сетевой фильтр встаёт на тот же слой, что и наш, проверка защищённых "
                + "соединений переустанавливает TLS своим клиентом, а WinDivert он помечает "
                + "как RiskTool и может увезти в карантин. Мы этого отсюда не видим; если "
                + "обход не помогает без внятной причины — отключите защиту на десять минут "
                + "и повторите."));
        }

        return rows;
    }

    private void ShowProblem(string text)
    {
        Problem.Text = text;
        Problem.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Первая фраза описания пресета — из его же файла.
    /// </summary>
    /// <remarks>
    /// Читается при смене пресета, а не каждые две секунды: разбор файла —
    /// сотни строк, а меняется выбор раз в неделю.
    /// </remarks>
    private void ShowPresetNote(string? preset)
    {
        if (preset == _notedPreset)
            return;

        _notedPreset = preset;
        PresetNote.Text = preset is null ? "Десинк без пресета не поднимается." : string.Empty;

        if (preset is null)
            return;

        try
        {
            if (Zephyron.Zapret.ZapretPaths.FindPreset(preset) is not { } file)
            {
                PresetNote.Text = "Файла пресета нет в папке presets.";
                return;
            }

            var description = new Zephyron.Zapret.PresetReader().Read([file]).FirstOrDefault()?.Description;

            PresetNote.Text = FirstSentence(description) ?? "Описания в файле пресета нет.";
        }
        catch (Exception)
        {
            PresetNote.Text = string.Empty;
        }
    }

    /// <summary>
    /// До первой точки, двоеточия или тире с пробелом после — дальше в описаниях
    /// пресетов начинаются подробности.
    /// </summary>
    /// <remarks>
    /// С пробелом, а не любая точка: в описаниях стоят версии и имена
    /// («v1.0.3», «www.facebook.com»), и по голой точке фраза рвалась бы на них.
    /// </remarks>
    internal static string? FirstSentence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        text = text.Trim();

        var end = System.Text.RegularExpressions.Regex.Match(text, @"[.:](\s|$)|\s—\s");

        return end.Success && end.Index > 0 ? text[..end.Index].TrimEnd() + "." : text;
    }

    /// <summary>
    /// Выпадающий список пресетов у «Изменить».
    /// </summary>
    /// <remarks>
    /// Список тот же и в том же порядке, что в «Десинке» (PresetNewness), и запись
    /// та же — PresetName. Последним пунктом — сам раздел: там видно, чем
    /// пресеты отличаются, а здесь только имена.
    /// </remarks>
    private void OnPresetMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = PresetMenu, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        string? chosen;

        try
        {
            chosen = AppSettings.Load(AppSettings.DefaultPath).PresetName;

            var presets = Zephyron.Zapret.PresetNewness.Order(
                new Zephyron.Zapret.PresetReader().Read(Zephyron.Zapret.ZapretPaths.PresetFiles),
                preset => Path.GetFileNameWithoutExtension(preset.FilePath),
                preset => preset.BuiltinVersion);

            foreach (var preset in presets)
            {
                var name = Path.GetFileNameWithoutExtension(preset.FilePath);
                var item = new MenuItem { Header = name, Tag = name };

                if (string.Equals(name, chosen, StringComparison.OrdinalIgnoreCase))
                    item.Icon = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), Foreground = (Brush)FindResource("Accent") };

                item.Click += (_, _) => ChoosePreset(name);
                menu.Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            ShowProblem("Пресеты не читаются: " + ex.GetBaseException().Message);
            return;
        }

        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuLine") });

        var all = new MenuItem { Header = "Все пресеты — раздел «Десинк»" };
        all.Click += (_, _) => Open("desync");
        menu.Items.Add(all);

        menu.IsOpen = true;
    }

    private void ChoosePreset(string name)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            if (string.Equals(settings.PresetName, name, StringComparison.OrdinalIgnoreCase))
                return;

            (settings with { PresetName = name }).Save(AppSettings.DefaultPath);

            _notedPreset = "\0";
            ConnectionStatus.Refresh();
            this.Offer($"Выбран пресет «{name}»");
        }
        catch (Exception ex)
        {
            ShowProblem("Не удалось записать выбор: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Три режима с ценой каждого.
    /// </summary>
    /// <remarks>
    /// Цена названа в самом описании, а не выясняется опытом: «всё через VPN
    /// без исключений» ломает отечественные сервисы, и узнавать об этом
    /// по неработающим госуслугам человек не должен.
    /// </remarks>
    private void ShowModes(AppSettings settings)
    {
        var engines = settings.Engines;
        var mode = WorkModes.Of(engines);

        ModeDesyncName.Text = WorkModes.Name(WorkMode.Desync);
        ModeTunnelName.Text = WorkModes.Name(WorkMode.Tunnel);
        ModeRouteName.Text = WorkModes.Name(WorkMode.Route);

        // Короткие пояснения на карточках: выбор читается без подсказки.
        ModeDesyncNote.Text = "По имени, адрес домашний";
        ModeTunnelNote.Text = "Весь трафик через VPN";
        ModeRouteNote.Text = "Что нужно — в VPN, остальное напрямую";

        // Пояснение — выбранного режима. «Без исключений» — настройка внутри
        // «Туннеля»: включённая, она меняет смысл режима, и молчать о ней нельзя.
        ModeText.Text = mode is not { } chosen
            ? "Режим не выбран — движки не поднимутся."
            : WorkModes.Explain(chosen)
                + (chosen == WorkMode.Tunnel && engines.IgnoreExclusions
                    ? " Сейчас без исключений: в VPN идёт и «напрямую»."
                    : string.Empty);
        ModeText.ToolTip = ModeText.Text;

        // «Ничего не поднято» режимом не считается — ни одна карточка не выбрана.
        ModeDesync.IsChecked = mode == WorkMode.Desync;
        ModeTunnel.IsChecked = mode == WorkMode.Tunnel;
        ModeRoute.IsChecked = mode == WorkMode.Route;

        // Режиму с туннелем без выхода везти некуда: «Туннель» не поднимет
        // ничего, «Гибрид» сработает как «Десинк». Сказать об этом здесь,
        // у выбора, а не узнавать по неработающему VPN.
        var complaint = engines.Complaint
            ?? (engines.Tunnel && !settings.HasTunnelExit
                ? (engines.Desync
                    ? $"VPN не задан — «{WorkModes.RouteName}» работает как «{WorkModes.Name(WorkMode.Desync)}». Подписка или WARP — на вкладке VPN."
                    : $"Для «{WorkModes.Name(WorkMode.Tunnel)}» нужна подписка или WARP — вкладка VPN.")
                : null);

        EnginesLine.Text = complaint ?? string.Empty;
        EnginesLine.Visibility = complaint is null ? Visibility.Collapsed : Visibility.Visible;

        // Автозапуск поднимает ровно это. Сказано здесь же, где задано:
        // иначе про связь пришлось бы догадываться.
        AutostartRaises.Text = mode is { } raised
            ? $"При входе в Windows поднимется режим «{WorkModes.Name(raised)}»."
            : "При входе в Windows ничего не поднимется: режим не выбран.";
    }

    /// <summary>
    /// Переключает движки под выбранный режим.
    /// </summary>
    /// <remarks>
    /// Через <see cref="AppSettings.With(EngineChoice)"/>: тот пишет заодно
    /// и выведенный режим, на языке которого говорят конфиг движка, отчёты
    /// и консоль. Оставленный отставшим, режим развёл бы показания.
    /// </remarks>
    private void Choose(Func<EngineChoice, EngineChoice> change)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var next = settings.With(change(settings.Engines));

            next.Save(AppSettings.DefaultPath);

            ShowModes(next);
            ConnectionStatus.Refresh();

            this.Offer("Режим: " + next.Engines.Describe());
        }
        catch (Exception ex)
        {
            ShowProblem("Не удалось записать: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>Выбран режим — движки под него (Rules.WorkModes) и предложение перезапустить.</summary>
    private void OnMode(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<WorkMode>(tag, out var mode))
            Choose(c => WorkModes.Apply(c, mode));
    }

    /// <summary>Что планировщик ответил в прошлый раз — на время работы окна.</summary>
    private static (bool Installed, bool Stale)? _autostart;

    /// <remarks>
    /// Планировщик спрашивается в фоне, а до ответа показывается прошлый.
    /// Замер 28.09: два запуска schtasks — «заведена ли» и «не устарела ли» —
    /// 73–91 мс на главном потоке при каждом открытии «Главной».
    /// </remarks>
    private async void ShowAutostart()
    {
        if (_autostart is { } known)
        {
            ApplyAutostart(known);
        }
        else
        {
            AutostartValue.Text = "Задача планировщика: проверяю…";
            AutostartValue.Visibility = Visibility.Visible;
            AutostartSwitch.IsEnabled = false;
        }

        try
        {
            var fresh = await Task.Run(() =>
            {
                bool installed = AutostartTask.IsInstalled(AutostartTask.DefaultTaskName);
                return (Installed: installed, Stale: installed && IsStale());
            });

            _autostart = fresh;
            ApplyAutostart(fresh);
        }
        catch (Exception ex)
        {
            AutostartValue.Text = "Задача планировщика не читается.";
            AutostartValue.Visibility = Visibility.Visible;
            AutostartSwitch.IsEnabled = false;
            ShowProblem("Планировщик не отвечает: " + ex.GetBaseException().Message);
        }
    }

    private void ApplyAutostart((bool Installed, bool Stale) state)
    {
        bool installed = state.Installed;

        AutostartValue.Visibility = Visibility.Collapsed;
        AutostartSwitch.IsChecked = installed;
        AutostartSwitch.IsEnabled = true;

        if (state.Stale)
        {
            AutostartValue.Text = "Задача устарела — выключите и включите снова.";
            AutostartValue.Visibility = Visibility.Visible;

            ShowProblem(
                "Задача автозапуска осталась от прежней версии и запускает не то, "
                + "что нужно: до 0.5.0 это была консольная программа, которой в поставке "
                + "больше нет. Выключите автозапуск и включите снова — это два щелчка.");
        }
    }

    /// <summary>
    /// Запускает ли задача не ту программу.
    /// </summary>
    /// <remarks>
    /// Задача переживает обновление, а её команда — нет: она записана внутрь
    /// задачи целиком, путём и ключами. Сверяется имя файла, а не путь
    /// целиком: путь может содержать кириллицу, а schtasks при
    /// перенаправлении отвечает однобайтовой OEM — имя же латинское
    /// и переживает любую из них.
    /// </remarks>
    private static bool IsStale()
    {
        try
        {
            // Кодировка не задаётся намеренно. Заданная Unicode здесь уже
            // стояла и всё ломала: schtasks пишет однобайтовый текст, а тот,
            // прочитанный парами байт, превращался в кашу, не содержащую
            // вообще ничего, — и всякая задача объявлялась устаревшей.
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                ArgumentList = { "/query", "/tn", AutostartTask.DefaultTaskName, "/xml" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });

            if (process is null)
                return false;

            var xml = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);

            if (xml.Length == 0)
                return false;

            return Path.GetFileName(Environment.ProcessPath) is { Length: > 0 } name
                && !xml.Contains(name, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Не прочиталось — молчим: пугать задачей, о которой мы ничего
            // не выяснили, хуже, чем не сказать.
            return false;
        }
    }

    private void OnAutostart(object sender, RoutedEventArgs e)
    {
        // Задача поднимает эту же программу в роли супервизора. Прежде она
        // звала консольную рядом, и без неё автозапуск молча не работал.
        var exe = Environment.ProcessPath;

        if (exe is null)
        {
            ShowProblem("Не удалось определить путь к программе — задача не заведена.");
            return;
        }

        try
        {
            if (AutostartTask.IsInstalled(AutostartTask.DefaultTaskName))
            {
                var (removed, output) = AutostartTask.Remove(AutostartTask.DefaultTaskName);

                if (!removed)
                    ShowProblem("Не удалось убрать автозапуск: " + output);
            }
            else
            {
                var (ok, output) = AutostartTask.Install(new AutostartOptions
                {
                    TaskName = AutostartTask.DefaultTaskName,
                    ExecutablePath = exe,

                    // Задача поднимает интерфейс в трей, а движки он заводит
                    // сам — теми же ключами, что и кнопка «Запустить».
                    Arguments = TrayIcon.Switch,
                    WorkingDirectory = Path.GetFullPath("."),
                    // SID, а не имя: см. AutostartTask.CurrentUserId.
                    UserId = AutostartTask.CurrentUserId(),
                });

                if (!ok)
                    ShowProblem("Не удалось завести автозапуск: " + output);
            }
        }
        catch (Exception ex)
        {
            ShowProblem("Планировщик отказал: " + ex.GetBaseException().Message);
        }

        // Задачу только что завели или убрали — запомненный ответ устарел.
        _autostart = null;
        ShowAutostart();
    }
}
