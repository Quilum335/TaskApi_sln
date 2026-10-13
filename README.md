# Task API

Тестовое на .NET 9: Minimal API, EF Core, PostgreSQL и RabbitMQ.

## Запуск

С Docker:

```sh
docker compose up --build -d
```

API: http://localhost:5080. RabbitMQ Management: http://localhost:15672, логин и пароль `guest`.

Без контейнера для приложения (нужен .NET 9 SDK):

```sh
docker compose up -d postgres rabbitmq
dotnet run --project TaskApi --urls http://localhost:5080
```

Подключения находятся в `TaskApi/appsettings.json`. Их можно переопределить переменными `ConnectionStrings__Tasks`, `RabbitMQ__HostName`, `RabbitMQ__Port`, `RabbitMQ__UserName`, `RabbitMQ__Password`.

Таблицы создаются при старте через `EnsureCreatedAsync`. Для небольшого тестового этого достаточно; при дальнейшем развитии нужно перейти на миграции. PostgreSQL должен быть доступен при запуске, RabbitMQ может быть недоступен.

## API

| Метод | Путь | Результат |
|---|---|---|
| POST | `/tasks` | Создание, `201` |
| GET | `/tasks` | Все задачи, `200` |
| PUT | `/tasks/{id}/complete` | Завершение, `200`; повторное или конкурентное завершение, `409` |
| DELETE | `/tasks/{id}` | Удаление, `204` |

При некорректном заголовке, приоритете или GUID возвращается `400`, для отсутствующей задачи — `404`.

Тело создания:

```json
{
  "title": "Buy milk",
  "priority": "High"
}
```

`priority` можно опустить: будет `Medium`. Допустимы `Low`, `Medium`, `High`; числовые значения не принимаются. При создании `isCompleted = false`, `completedAt = null`. Даты хранятся в UTC.

Пример для PowerShell:

```powershell
$task = Invoke-RestMethod http://localhost:5080/tasks -Method Post -ContentType 'application/json' -Body '{"title":"Buy milk","priority":"High"}'
Invoke-RestMethod "http://localhost:5080/tasks/$($task.id)/complete" -Method Put
Invoke-RestMethod http://localhost:5080/tasks
Invoke-RestMethod "http://localhost:5080/tasks/$($task.id)" -Method Delete
```

## Конкурентность и события

`RowVersion` настроен через `IsRowVersion()`: Npgsql использует системную колонку PostgreSQL `xmin`. Если два запроса прочитали одну версию, второй не сможет сохранить изменение и получит `409`.

Событие ставится в очередь только после успешного сохранения в БД. Один фоновый обработчик последовательно публикует сообщения через асинхронный RabbitMQ Client. Обменник `task.events` создаётся как durable topic, routing key — `task.completed`.

```json
{
  "taskId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "title": "Buy milk",
  "completedAt": "2026-05-07T12:00:00Z",
  "priority": "High"
}
```

Очередь в памяти ограничена 1000 событиями. Если она заполнена или RabbitMQ недоступен, задача остаётся завершённой, а полное неотправленное событие попадает в лог. Соединение создаётся лениво; после сбоя следующая публикация пробует подключиться снова. При остановке очередь закрывается для новых событий: до 4 секунд на отправку и до 1 секунды на закрытие канала и соединения. Если клиент завис на подключении или закрытии, по таймауту закрывается TCP-сокет.

Publisher confirms и повторная отправка не используются. Очередь не переживает аварийное завершение процесса; для гарантированной доставки нужен transactional outbox. Чтобы увидеть сообщения в RabbitMQ Management, до завершения задачи создайте очередь и привяжите её к `task.events` с ключом `task.completed`: сам обменник сообщения не хранит.

## Проверки

```sh
dotnet test TaskApi.sln
```

Тесты поднимают приложение через `WebApplicationFactory`. База заменена на SQLite, издатель событий — на записывающую заглушку, поэтому Docker для тестов не нужен. Проверяются создание и завершение через HTTP, сохранённое состояние, событие, валидация, удаление и конфликт двух одновременных запросов. В тесте конкурентности оба запроса сначала читают задачу, затем одновременно переходят к сохранению.


