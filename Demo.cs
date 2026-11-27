namespace Relay;

internal static class Demo
{
    public static readonly ChatEntry[] Chats =
    [
        new("demo-tests", "Почему падают тесты?", "signal-board", 0),
        new("demo-config", "Проверка конфигурации", "signal-board", 0)
    ];

    public static ChatTranscript OpenChat(string key) =>
        new(key == "demo-tests"
            ? [
                new("user", "Проверь, почему тесты пустого состояния падают."),
                new("assistant", "Список без элементов попадал в ветку загрузки. Вынес проверку isLoading в отдельное условие."),
                new("user", "Все тесты прошли?"),
                new("assistant", "Да, прошли все три теста: загрузка, пустой список и список с данными.")
              ]
            : [
                new("user", "Где хранится конфигурация проекта?"),
                new("assistant", "Основные настройки находятся в config/default.json. Для локальной разработки их можно переопределить переменными окружения.")
              ], false);

    public static readonly (int Delay, string Output)[] Frames =
    [
        (0, "\x1b[90mdev@demo:~/projects/signal-board\x1b[0m\r\n" +
            "\x1b[32m❯\x1b[0m claude\r\n\r\n" +
            "\x1b[1;36m◆  Claude Code · демонстрация\x1b[0m\r\n" +
            "\x1b[90m   ~/projects/signal-board  ·  main\x1b[0m\r\n\r\n" +
            "\x1b[1;37m❯ Проверь, почему тесты пустого состояния падают.\x1b[0m\r\n\r\n" +
            "\x1b[32m●\x1b[0m Посмотрю компонент и тесты.\r\n"),
        (500, "\r\n  \x1b[36mRead\x1b[0m  src/components/SignalList.tsx\r\n" +
              "  \x1b[36mRead\x1b[0m  src/components/SignalList.test.tsx\r\n"),
        (900, "\r\n  Список без элементов доходит до ветки загрузки.\r\n" +
              "  Вынесу проверку загрузки в отдельное условие.\r\n"),
        (900, "\r\n  \x1b[31m− if (!signals.length) return <Loading />;\x1b[0m\r\n" +
              "  \x1b[32m+ if (isLoading) return <Loading />;\x1b[0m\r\n" +
              "  \x1b[32m+ if (!signals.length) return <EmptyState />;\x1b[0m\r\n"),
        (900, "\r\n  \x1b[36mBash\x1b[0m  npm test -- SignalList\r\n  \x1b[90m⠋ Запуск тестов…\x1b[0m"),
        (350, "\r\x1b[2K  \x1b[90m⠙ Запуск тестов…\x1b[0m"),
        (350, "\r\x1b[2K  \x1b[90m⠹ Запуск тестов…\x1b[0m"),
        (450, "\r\x1b[2K  \x1b[32m✓\x1b[0m SignalList.test.tsx  \x1b[90m3 теста прошло\x1b[0m\r\n"),
        (700, "\r\n\x1b[32m●\x1b[0m Исправил пустое состояние. Все три теста проходят.\r\n" +
              "  Проверки загрузки, пустого списка и списка с данными разделены.\r\n")
    ];
}
