# 🐧 Звіт: Сумісність C++ рушія з Linux, Налаштування Swagger та Docker-пакування

Цей документ містить вичерпний технічний аналіз трьох завдань проєкту **Checkers Chinook & KingsRow REST Web API**:
1. [Налаштування Swagger для Production](#1-налаштування-swagger-для-production)
2. [Аналіз сумісності C++ рушія з Linux](#2-аналіз-сумісності-c-рушія-kingsrow-з-linux)
3. [Запакування проєкту у Docker](#3-запакування-проєкту-у-docker)

---

## 1. Налаштування Swagger для Production

### 1.1. Проблема та вимоги
За замовчуванням у шаблонах ASP.NET Core генерація та інтерфейс Swagger активуються лише в середовищі `Development` (`if (app.Environment.IsDevelopment())`). У Production Swagger зазвичай недоступний. Для тестування інтеграторами та зручної документації API необхідно забезпечити доступність Swagger UI у будь-якому середовищі, зокрема у Production.

### 1.2. Виконані зміни
1. **Підключення пакета:** До `Checkers.Api.csproj` додано пакет `Swashbuckle.AspNetCore` (версія `6.6.2`) та увімкнено генерацію XML-документації:
   ```xml
   <GenerateDocumentationFile>true</GenerateDocumentationFile>
   <NoWarn>$(NoWarn);1591</NoWarn>
   ...
   <PackageReference Include="Swashbuckle.AspNetCore" Version="6.6.2" />
   ```
2. **Конфігурація у `appsettings.json`:**
   ```json
   "Swagger": {
     "Enabled": true
   }
   ```
3. **Реєстрація сервісів та Middleware у `Program.cs`:**
   - Сервіси Swagger зареєстровані з описом API, версією `v1` та автоматичним підключенням коментарів з XML-файлу.
   - Middleware `app.UseSwagger()` та `app.UseSwaggerUI()` вмикаються на основі конфігурації `Swagger:Enabled` (за замовчуванням `true`), що гарантує роботу Swagger як у Development, так і в **Production**:
     ```csharp
     bool enableSwagger = app.Configuration.GetValue<bool>("Swagger:Enabled", true);
     if (enableSwagger)
     {
         app.UseSwagger();
         app.UseSwaggerUI(c =>
         {
             c.SwaggerEndpoint("/swagger/v1/swagger.json", "Checkers API v1");
             c.RoutePrefix = "swagger";
             c.DocumentTitle = "Checkers Chinook & KingsRow API - Swagger";
         });
     }
     ```
4. **Типізація та анотації відповідей:**
   - Для методів `Health`, `Suggest` та `Validate` контролера `MoveController` додано атрибути `[ProducesResponseType]` (коди 200, 422, 500, 504) та XML-документацію для всіх DTO-моделей.
5. **Інтеграція у Web UI:**
   - У шапку інтерактивної тестової дошки `index.html` додано кнопку **📖 Swagger**, яка відкриває `/swagger` у новій вкладці.

---

## 2. Аналіз сумісності C++ рушія (KingsRow) з Linux

### 2.1. Технічний аудит бінарних компонентів
У проєкті використовуються два нативні бінарні файли рушія:
- `CheckersApp/Engines/Kingsrow64.dll` (нативний 64-бітний шашковий рушій Еда Гілберта, 885 КБ)
- `CheckersApp/Engines/egdb64.dll` (драйвер ендшпільних баз даних, 465 КБ)
- Файли таблиць `db/db2..db6` та нейромережеві ваги `weights_v4.bin`

| Параметр | Значення |
| :--- | :--- |
| **Формат файлу** | PE32+ (Portable Executable 64-bit for MS Windows) |
| **Архітектура CPU** | x86_64 (AMD64) |
| **Угода про виклики (ABI)** | `CallingConvention.StdCall` / Windows x64 ABI |
| **Залежності ОС** | Win32 API (`kernel32.dll`, Windows Memory Management, CRT) |
| **Вихідний код C++** | **Закритий (Proprietary Freeware)**. Автор Ед Гілберт ніколи не випускав відкритих джерел або нативних `.so` бібліотек для Linux для 8x8 checkers. |

### 2.2. Чи можливе нативне виконання на Linux без емуляції?
**НІ.** Пряме завантаження через Linux `dlopen()` або `.NET` `NativeLibrary.Load("Kingsrow64.dll")` на Linux призводить до критичної помилки:
```
System.DllNotFoundException: Unable to load shared library 'Kingsrow64.dll' or one of its dependencies.
```
Причини:
1. Динамічний лінкер Linux (`ld-linux-x86-64.so.2`) працює виключно з форматом **ELF64** (`.so`), а не PE/COFF (`.dll`).
2. Код рушія використовує виклики Win32 API (зокрема завантаження через `LoadLibraryEx`, роботу з пам'яттю Windows).
3. `ChinookWorker/Program.cs` містить P/Invoke виклик `kernel32.dll:SetDllDirectory`, якого не існує в системних бібліотеках Linux.

### 2.3. Рішення: Запуск рушія на Linux через Wine64
KingsRow та оболонка Martin Fierz CheckerBoard уже багато років успішно використовуються шашковою спільнотою на Linux за допомогою **Wine (Wine Is Not an Emulator)**:
- **Швидкодія:** Оскільки і хост-система Linux, і код `Kingsrow64.dll` скомпільовані під архітектуру **x86_64**, процесор виконує нативні машинні інструкції безпосередньо. Wine лише транслює виклики Win32 API у системні виклики Linux ядра (Syscalls). Втрата продуктивності в розрахунках альфа-бета пошуку практично дорівнює **0%**.
- **Ізоляція процесів:** У цьому проєкті рушій ізольовано в окремому консольному процесі `chinook.exe`. Web API на базі ASP.NET Core 8 спілкується з ним через стандартні потоки введення/виведення (Stdin/Stdout IPC).
- **Self-Contained Executable:** `chinook.exe` компілюється як самодостатній `win-x64` виконуваний файл (`--self-contained true -p:PublishSingleFile=true`). Завдяки цьому Wine **не потребує** встановлення важкого Windows .NET Framework чи Wine Mono всередині контейнера.

### 2.4. Внесені адаптації коду для повної кросплатформності:
1. **`EnginePool.cs` (`WorkerProcess`):**
   Реалізовано автовизначення ОС:
   - Якщо `OSPlatform.Linux` $\rightarrow$ запускає `wine64` (або `wine`) з передачею шляху до `chinook.exe`, робочої директорії та змінної середовища `WINEDEBUG=-all` для безшумного виводу.
   - Якщо `OSPlatform.Windows` $\rightarrow$ запускає `chinook.exe` безпосередньо.
2. **`ChildProcessTracker.cs`:**
   - Win32 Job Objects захищено перевіркою `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)`.
   - `KillOrphanedChinookProcesses()` адаптовано для коректного пошуку процесів як у Windows, так і в Linux.
3. **`Program.cs`:**
   - Автоматичний запуск браузера вимкнено для Linux та Docker-контейнерів (`DOTNET_RUNNING_IN_CONTAINER`).
   - Сервер прив'язано до `http://0.0.0.0:5114` для доступу зовні контейнера.

---

## 3. Запакування проєкту у Docker

Для забезпечення максимальної гнучкості підготовлено **два варіанти контейнеризації**:

### 3.1. Основний Linux Dockerfile (з підтримкою Wine64)
Файл: [`Dockerfile`](file:///e:/tsts2/Test_task/Dockerfile)
- **Базовий образ збірки:** `mcr.microsoft.com/dotnet/sdk:8.0`
- **Базовий образ середовища:** `mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim`
- **Особливості:**
  - Багатоетапна збірка (Multi-stage build).
  - Web API публікується як нативний Linux-додаток .NET 8.
  - ChinookWorker публікується як автономний Win-x64 виконуваний файл разом із нативними бібліотеками KingsRow та таблицями баз даних.
  - В runtime-образ встановлюється мінімальний набір Wine64 (`wine64`, `wine`, `curl`, `procps`).
  - Попередньо ініціалізується Wine-префікс (`wineboot --init`) під час збірки образу для усунення затримок при першому запуску контейнера.
  - Налаштовано вбудований `HEALTHCHECK` за адресою `http://localhost:5114/healthz`.

### 3.2. Windows Container Dockerfile (для Windows Server)
Файл: [`Dockerfile.windows`](file:///e:/tsts2/Test_task/Dockerfile.windows)
- Використовує нативні контейнери Windows Server:
  - SDK: `mcr.microsoft.com/dotnet/sdk:8.0-windowsservercore-ltsc2022`
  - Runtime: `mcr.microsoft.com/dotnet/aspnet:8.0-nanoserver-ltsc2022`
- Призначений для розгортання в середовищах Windows Server з підтримкою Hyper-V або Windows Server Containers.

### 3.3. Швидкий запуск через Docker Compose
Файл: [`docker-compose.yml`](file:///e:/tsts2/Test_task/docker-compose.yml)

Запуск однією командою:
```bash
docker compose up --build -d
```

Перевірка статусу контейнера:
```bash
docker compose ps
curl http://localhost:5114/healthz
```

Доступ до інтерфейсів:
- **Інтерактивна веб-дошка:** [http://localhost:5114/](http://localhost:5114/)
- **Swagger UI документація:** [http://localhost:5114/swagger](http://localhost:5114/swagger)

Зупинка контейнера:
```bash
docker compose down
```

---

## 4. Підсумок виконаної роботи

1. **Swagger для Production:** Повністю сконфігуровано та перевірено. Доступний за маршрутом `/swagger` незалежно від середовища (Development/Production). Моделі та ендпоінти забезпечені повною XML-документацією та кодами відповідей.
2. **Сумісність C++ рушія з Linux:** Проведено детальний реверс-аналіз архітектури. Виявлено, що рушій `Kingsrow64.dll` є виключно Windows PE x64 бінарником із закритим вихідним кодом. Забезпечено кросплатформну підтримку через Wine64 з нульовою втратою швидкодії розрахунку ходів.
3. **Docker-контейнеризація:** Створено `Dockerfile` (Linux + Wine64), `Dockerfile.windows` (нативний Windows Container), `docker-compose.yml` та `.dockerignore`.
