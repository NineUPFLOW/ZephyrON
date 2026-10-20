using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Zephyron.Core;
using Zephyron.Supervisor;

namespace Zephyron.Gui.Views;

/// <summary>Строка про один движок.</summary>
/// <param name="Name">Имя процесса: <c>winws2</c> или <c>sing-box</c>.</param>
/// <param name="Word">Отметка одним-двумя словами (EngineHealth.Word).</param>
/// <param name="Detail">Строка под ней: причина надзора, когда движок не в порядке.</param>
/// <param name="ColourKey">Ключ кисти состояния: Accent, Warn, Danger, Faint.</param>
/// <param name="ProcessId">Номер процесса — чтобы найти движок в диспетчере задач.</param>
public sealed record EngineRow(string Name, string Word, string Detail, string ColourKey, int? ProcessId = null);

/// <summary>Всё, что окно знает о связи прямо сейчас.</summary>
/// <param name="Problem">Настройки не прочитались — почему; тогда прочее пусто.</param>
/// <param name="Starting">Идёт запуск, начатый в <paramref name="StartingSince"/>.</param>
/// <param name="Engines">Движки, которые должны работать, и их состояние.</param>
public sealed record ConnectionSnapshot(
    AppSettings? Settings,
    string? Problem,
    string Subscriptions,
    SupervisorState? State,
    bool Running,
    bool SingBoxUp,
    bool Coming,
    DateTime? ConfigBuilt,
    DateTimeOffset? StartingSince,
    IReadOnlyList<EngineRow> Engines)
{
    /// <summary>Запуск идёт прямо сейчас.</summary>
    public bool Starting => StartingSince is not null;
}

/// <summary>
/// Единственный источник состояния связи: опрашивает его и раздаёт
/// подписчикам.
/// </summary>
/// <remarks>
/// <para>
/// Прежде состояние читалось в разделе «Главная» — и только пока он виден.
/// По референсу состояние и кнопка запуска переехали в левую панель окна,
/// а панель видна всегда; вместе с переносом ушло и «два года»: раздел мог
/// читать одно, панель другое, и на вопрос «работает ли» окно отвечало
/// двумя разными словами.
/// </para>
/// <para>
/// Поэтому опрос ровно один. Кто бы ни хотел показать состояние — панель,
/// «Главная», трей, — он подписывается на <see cref="Changed"/> и берёт
/// готовое; своего таймера не заводит. Опрос идёт, пока есть хоть один
/// подписчик, и встаёт, когда ушёл последний.
/// </para>
/// <para>
/// Чтение — не в потоке окна: в нём есть ожидания (занятый файл настроек
/// читается повтором, процессы перебираются системой), и сторож подвисаний
/// 01.10 записал на этом 300–860 мс «занят» при пустом процессоре.
/// </para>
/// </remarks>
public static class ConnectionStatus
{
    /// <summary>Обычный период опроса.</summary>
    private static readonly TimeSpan CalmInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Период опроса во время запуска: подпись фазы и счётчик секунд должны
    /// успевать за движками, иначе окно выглядит замершим ровно тогда, когда
    /// на него и смотрят.
    /// </summary>
    private static readonly TimeSpan StartingInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Сколько показывать запуск, прежде чем показать состояние как есть.
    /// </summary>
    /// <remarks>
    /// С запасом больше суммы таймаутов готовности обоих движков и первых
    /// перезапусков: объявить «не поднялось» раньше этого срока значило бы
    /// соврать про ещё идущую работу.
    /// </remarks>
    private static readonly TimeSpan StartupPatience = TimeSpan.FromSeconds(90);

    private static DispatcherTimer? _timer;
    private static int _watchers;
    private static DateTimeOffset? _startingSince;
    private static bool _busy;
    private static bool _again;
    private static ConnectionSnapshot? _current;

    /// <summary>Состояние изменилось — новое значение уже в <see cref="Current"/>.</summary>
    public static event Action<ConnectionSnapshot>? Changed;

    /// <summary>Последнее прочитанное состояние; <c>null</c> — ещё не читали.</summary>
    public static ConnectionSnapshot? Current => _current;

    /// <summary>
    /// Начинает следить за состоянием. Парно <see cref="Unwatch"/>.
    /// </summary>
    /// <remarks>
    /// Счётчик, а не флаг: подписчиков может быть несколько (панель и раздел),
    /// и уход одного не должен гасить опрос у другого.
    /// </remarks>
    public static void Watch()
    {
        _watchers++;

        if (_watchers != 1)
            return;

        _timer ??= new DispatcherTimer();
        _timer.Interval = _startingSince is not null ? StartingInterval : CalmInterval;
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();

        Refresh();
    }

    /// <summary>Прекращает следить — когда ушёл последний подписчик, встаёт и опрос.</summary>
    public static void Unwatch()
    {
        _watchers = Math.Max(0, _watchers - 1);

        if (_watchers != 0)
            return;

        _timer?.Stop();
    }

    private static void OnTick(object? sender, EventArgs e) => Refresh();

    /// <summary>
    /// Перечитывает состояние в пуле и раздаёт его.
    /// </summary>
    /// <remarks>
    /// Вызов посреди идущего чтения не теряется и не множит чтения:
    /// отмечается, и чтение повторяется по окончании.
    /// </remarks>
    public static async void Refresh()
    {
        if (_busy)
        {
            _again = true;
            return;
        }

        _busy = true;

        try
        {
            do
            {
                _again = false;

                bool starting = _startingSince is not null;
                Publish(await Task.Run(() => Read(starting)));
            }
            while (_again);
        }
        finally
        {
            _busy = false;
        }
    }

    private static void Publish(ConnectionSnapshot snapshot)
    {
        _current = snapshot;

        // Пора перестать показывать запуск, если он кончился или затянулся.
        if (_startingSince is not null && FinishStarting(snapshot))
            return;

        _timer!.Interval = _startingSince is not null ? StartingInterval : CalmInterval;

        Changed?.Invoke(snapshot);
    }

    /// <summary>
    /// Кончился ли запуск. <c>true</c> — только что закончился, и состояние
    /// надо перечитать немедленно, уже как обычное.
    /// </summary>
    private static bool FinishStarting(ConnectionSnapshot snapshot)
    {
        // Запуск начинают через BeginStarting, который и ставит время. Если
        // его нет — возвращаться некуда: считаем, что запуск уже кончился.
        if (_startingSince is not { } since)
            return false;

        var services = snapshot.Running ? snapshot.State!.Services : [];

        bool ready = services.Count > 0 && services.All(s => s.Health == ServiceHealth.Healthy);
        bool gaveUp = services.Any(s => s.Health == ServiceHealth.Faulted);

        // Ни состояния, ни живого супервизора, ни запуска в очереди — значит,
        // он ушёл, не подняв движков. Ждать тут все 90 с незачем.
        bool gone = !snapshot.Running && !snapshot.Coming
            && DateTimeOffset.Now - since > TimeSpan.FromSeconds(3);

        if (!(ready || gaveUp || gone
              || DateTimeOffset.Now - since > StartupPatience))
            return false;

        _startingSince = null;
        Refresh();

        return true;
    }

    /// <summary>Запуск начат — показывать его ход.</summary>
    public static void BeginStarting()
    {
        _startingSince = DateTimeOffset.Now;
        Refresh();
    }

    /// <summary>Запуск окончен (или брошен) — показывать состояние как есть.</summary>
    public static void EndStarting() => _startingSince = null;

    /// <summary>
    /// Читает состояние с диска и у системы.
    /// </summary>
    private static ConnectionSnapshot Read(bool starting)
    {
        AppSettings settings;

        try
        {
            settings = AppSettings.Load(AppSettings.DefaultPath);
        }
        catch (Exception ex)
        {
            return new ConnectionSnapshot(null, $"Настройки не читаются: {ex.Message}",
                string.Empty, null, false, false, false, null, _startingSince, []);
        }

        // Ссылки на подписки — пароли, и в окне им не место. Показываем лишь
        // счёт: его хватает, чтобы понять, почему нет серверов.
        string subscriptions;

        try
        {
            subscriptions = SubscriptionBook.Load().Describe(settings);
        }
        catch (Exception)
        {
            subscriptions = string.Empty;
        }

        var state = SupervisorState.Load(SupervisorState.DefaultPath);
        bool running = state is not null && state.IsSupervisorAlive();

        DateTime? built = null;

        try
        {
            // Конфиг собирается при каждом запуске, и серверы подписок берутся
            // тогда же, — поэтому время файла и есть время, когда их взяли.
            // Отдельного «подписки обновлены» программа не хранит.
            var config = new FileInfo(Path.Combine("runtime", "singbox.json"));

            if (config.Exists)
                built = config.LastWriteTime;
        }
        catch (Exception)
        {
        }

        bool coming = !running && EngineControl.Coming;
        bool singBoxUp = starting && !running && IsRunning("sing-box");

        return new ConnectionSnapshot(settings, null, subscriptions, state, running, singBoxUp, coming, built,
            _startingSince, Engines(settings, state, running, coming));
    }

    /// <summary>
    /// Список движков: их состояние, когда работают, и что должно подняться,
    /// когда нет.
    /// </summary>
    /// <remarks>
    /// Список тот же, что собирает супервизор: туннель — когда режим ведёт
    /// хоть что-то через VPN, десинк — когда выбран пресет. Движки остаются
    /// на виду и остановленными: пустое место на их месте читается как «их
    /// нет вовсе», тогда как вопрос — «что должно работать и работает ли».
    /// </remarks>
    private static IReadOnlyList<EngineRow> Engines(AppSettings settings, SupervisorState? state, bool running, bool coming)
    {
        bool starting = _startingSince is not null;

        if (running)
        {
            // Умерший движок здесь не конец запуска: супервизор его перезапустит.
            // Слова — общие с треем и zy status (EngineHealth.Status).
            return state!.Services.Select(Row).ToList();
        }

        var rows = new List<EngineRow>();

        string detail = starting || coming ? "запускается" : "остановлен";
        string colour = starting || coming ? "Warn" : "Faint";

        if (settings.NeedsProxy)
            rows.Add(new EngineRow("sing-box", detail, string.Empty, colour));

        if (settings.NeedsDesync)
            rows.Add(new EngineRow("winws2", detail, string.Empty, colour));

        return rows;
    }

    private static EngineRow Row(ServiceState service)
    {
        // Degraded — это «процесс жив, а проверка не проходит»: самый коварный
        // случай, и называть его «работает» нельзя, иначе окно будет уверять
        // в исправности молчащей трубы.
        var key = service.Health switch
        {
            ServiceHealth.Healthy => "Accent",
            ServiceHealth.Degraded => "Warn",
            ServiceHealth.Dead or ServiceHealth.Faulted => "Danger",
            _ => "Faint",
        };

        // Причина — только у того, что не работает: у работающего Status
        // говорит «работает, процесс N», а номер и так стоит рядом.
        var detail = service.Health == ServiceHealth.Healthy
            ? service.Remark ?? string.Empty
            : EngineHealth.Status(service);

        return new EngineRow(service.Name, EngineHealth.Word(service.Health), detail, key, service.ProcessId);
    }

    /// <summary>
    /// Слово состояния связи — то, что видно с любого раздела.
    /// </summary>
    /// <remarks>
    /// Три состояния, а не два. «Оговорки» бывают разной тяжести: движок,
    /// которому не удалась глубокая проверка, ещё несёт трафик, а умерший
    /// не несёт ничего. Одним словом «работает» это выглядело одинаково.
    /// </remarks>
    public static (string Word, string Hint, string ColourKey) Overall(ConnectionSnapshot s)
    {
        if (s.Starting)
        {
            var seconds = (int)(DateTimeOffset.Now - s.StartingSince!.Value).TotalSeconds;

            return ("Запускается…", $"{Phase(s)} — {seconds} с", "Warn");
        }

        if (s.Problem is { } problem)
            return ("Не читается", problem, "Danger");

        if (!s.Running)
        {
            var settings = s.Settings;

            return ("Остановлено",
                settings is null || settings.NeedsProxy || settings.NeedsDesync
                    ? "Обход не работает: трафик идёт напрямую."
                    : "В этом режиме запускать нечего: VPN выключен, пресет не выбран.",
                "Faint");
        }

        var services = s.State!.Services;
        bool healthy = services.All(x => x.Health == ServiceHealth.Healthy);
        bool broken = services.Any(x => x.Health is ServiceHealth.Dead or ServiceHealth.Faulted);

        return healthy
            ? ("Работает", $"Запущено {Ago(s.State.StartedAt)}.", "Accent")
            : broken
                ? ("Движок не работает", "Один из движков не запущен — обход работает не полностью.", "Danger")
                : ("Работает с оговорками", "Часть движков не в порядке — подробности в «Десинке».", "Warn");
    }

    /// <summary>
    /// Чем занят запуск прямо сейчас.
    /// </summary>
    /// <remarks>
    /// До появления состояния фаза определяется по живым процессам, а не по
    /// файлу: супервизор пишет состояние впервые лишь после того, как поднял
    /// все службы, — то есть ровно после окончания промежутка, который здесь
    /// и показывается. Файл в это время либо отсутствует, либо остался от
    /// прошлого запуска.
    /// </remarks>
    private static string Phase(ConnectionSnapshot s)
    {
        if (!s.Running)
        {
            if (!s.SingBoxUp)
                return "Собираем конфиг и поднимаем супервизор";

            // Проверка прохода трафика уходит в сеть и занимает основную часть
            // ожидания, поэтому названа отдельно: иначе эти секунды выглядели
            // как необъяснённая пауза.
            return s.Settings?.VerifyTraffic == true
                ? "Туннель поднимается, проверяем проход трафика"
                : "Туннель поднимается";
        }

        var singBox = s.State!.Services.FirstOrDefault(x => x.Name == "sing-box");

        return singBox?.Health switch
        {
            ServiceHealth.Degraded => "Туннель поднят, проверка ещё не прошла",
            ServiceHealth.Dead => "Туннель не поднялся, идёт перезапуск",
            _ => "Движки поднимаются",
        };
    }

    /// <summary>
    /// Строка внизу панели: состояние с длительностью и время сборки конфига.
    /// </summary>
    public static string Footer(ConnectionSnapshot s) => s.ConfigBuilt is { } at
        ? $"Конфиг туннеля собран {at:dd.MM.yyyy} в {at:HH:mm}"
        : string.Empty;

    /// <summary>«winws2 работает · sing-box работает» — одной строкой для подсказки.</summary>
    public static string EnginesLine(ConnectionSnapshot s) => s.Engines.Count == 0
        ? string.Empty
        : string.Join(" · ", s.Engines.Select(e => $"{e.Name} {e.Word}"));

    private static bool IsRunning(string processName)
    {
        var found = Process.GetProcessesByName(processName);

        // Каждый Process держит системный дескриптор, а опрос идёт раз
        // в секунду: без освобождения они копятся всё время запуска.
        foreach (var process in found)
            process.Dispose();

        return found.Length > 0;
    }

    /// <summary>«только что», «4 ч 12 мин назад».</summary>
    private static string Ago(DateTimeOffset since)
    {
        var span = DateTimeOffset.Now - since;

        if (span.TotalSeconds < 90)
            return "только что";

        return Span(span) + " назад";
    }

    /// <summary>«23 дня, 4 часа», «4 ч 12 мин», «7 мин».</summary>
    internal static string Span(TimeSpan span)
    {
        if (span.TotalDays >= 1)
        {
            int days = (int)span.TotalDays;
            return $"{days} {Plural(days, "день", "дня", "дней")}, {span.Hours} {Plural(span.Hours, "час", "часа", "часов")}";
        }

        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours} ч {span.Minutes} мин"
            : $"{(int)Math.Max(1, span.TotalMinutes)} мин";
    }

    /// <summary>Русское склонение после числа.</summary>
    private static string Plural(int count, string one, string few, string many)
    {
        int tail = count % 100;

        if (tail is >= 11 and <= 14)
            return many;

        return (tail % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }

    /// <summary>Кисть состояния по ключу — для точки и полосы.</summary>
    public static Brush Brush(FrameworkElement owner, string colourKey) =>
        (Brush)owner.FindResource(colourKey);
}
