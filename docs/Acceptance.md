# Acceptance and verification evidence

Актуальная реализация, конкретные тесты и ограничения доказательств: [VerificationEvidence.md](VerificationEvidence.md). Матрица ниже остаётся полным release gate; `not verified` не означает отсутствие реализации, если соответствующий полный системный сценарий ещё не проверен.

Это матрица проверок нового CSharpClipboard. Все результаты пока not verified; исходники и evidence ClipboardMac не доказывают прохождение этих проверок. Для каждого запуска фиксируются OS/build, app version, signing identity, оборудование, target, раскладка, результат и ссылка на evidence без clipboard content.

## End-to-end scenarios

| ID | Сценарий | Ожидаемый результат | Specifications | Result | Evidence |
| --- | --- | --- | --- | --- | --- |
| A01 | Первый запуск на macOS без разрешений | History/Settings доступны; нет фонового системного prompt; capture ждёт Allow | IDD-0001, IDD-0002, IDD-0007, IDD-0009 | not verified | — |
| A02 | Нажать Request Clipboard Access Once | Ровно явное пользовательское обращение; статус после реального ответа не подменён | IDD-0002, IDD-0007, IDD-0009 | not verified | — |
| A03 | Always Allow + AX denied | Новые обычные копирования фиксируются, Paste — Copy Only | IDD-0002, IDD-0005, IDD-0009 | not verified | — |
| A04 | Clipboard Ask/Deny + AX allowed | Фоновый capture недоступен; сохранённая история доступна | IDD-0002, IDD-0007, IDD-0009 | not verified | — |
| A05 | Внешняя смена Clipboard permission при запущенном app | Состояние capture меняется корректно без restart | IDD-0002, IDD-0009 | not verified | — |
| A06 | Mac concealed/transient/generated marker | Не сохранён даже plain text | IDD-0002, IDD-0009 | not verified | — |
| A07 | Plain copy из редактора, поиск, Paste | Выбранный точный текст попал в первоначальное поле; одна вставка | IDD-0003, IDD-0004, IDD-0005 | not verified | — |
| A08 | Потерян focus / сменилась программа до Paste | Только Copy Only, не отправлен shortcut | IDD-0005 | not verified | — |
| A09 | Clipboard изменился внешне во время Paste | Не вставлять stale text; не потерять чужую ревизию | IDD-0002, IDD-0005 | not verified | — |
| A10 | Быстро дважды Enter / двойной click | Не более одной попытки paste | IDD-0004, IDD-0005 | not verified | — |
| A11 | Нажать Pause; копировать; Resume | Во время Pause никаких reads; пропущенное не импортировано | IDD-0001, IDD-0002 | not verified | — |
| A12 | Повторный copy exact text | Одна запись, copiedAt изменён | IDD-0003 | not verified | — |
| A13 | Copy variants: CRLF, trailing space, emoji, NUL | Отличия сохранены точно | IDD-0002, IDD-0003 | not verified | — |
| A14 | Pin; 2000+ новых записей; Clear Recent; restart | Pin остался, recent очищены | IDD-0003 | not verified | — |
| A15 | History открыта; pin/recopy/new items | Нет прыжков существующих строк; новые копирования не меняют список и поиск; reopen загружает актуальные записи и пересортировывает | IDD-0003, IDD-0004 | not verified | — |
| A16 | Windows Clipboard update + отсутствие общего hook | Event listener работает без глобального keylogger | IDD-0002, IDD-0010 | not verified | — |
| A17 | Windows elevated target | Copy Only без elevation | IDD-0005, IDD-0010 | not verified | — |
| A18 | US + Russian + Dvorak на Mac | Реальный Cmd+V работает или корректный fallback | IDD-0005, IDD-0009 | not verified | — |
| A19 | Смена глобального хоткея | Старый снят, новый активен; конфликт не стирает старый | IDD-0006 | not verified | — |
| A20 | Launch at Login macOS; requiresApproval | Статус и инструкции соответствуют SMAppService, без ложной галочки | IDD-0007, IDD-0008, IDD-0009 | not verified | — |
| A21 | Запуск установленной .app через Finder | Status icon, permissions, bridge и paste работают без Terminal | IDD-0008, IDD-0009 | not verified | — |
| A22 | Release с недоверенным архивом | Нет тихой in-app установки | IDD-0008 | not verified | — |
| A23 | Две копии приложения | Одна инстанция/один монитор | IDD-0001 | not verified | — |
| A24 | Очистка БД и повторное заполнение | Файл не растёт бесконечно после maintenance | IDD-0003 | not verified | — |
| A25 | Работа при двух экранах / масштабировании / Spaces | Панель видима и принимает keyboard input | IDD-0004, IDD-0009, IDD-0010 | not verified | — |
| A26 | Системная блокировка clipboard/AX/SQLite на время | Нет бесконечных повторов, данные не теряются, понятная ошибка | IDD-0002, IDD-0005, IDD-0011 | not verified | — |

## Acceptance criterion traceability

Номера AC соответствуют порядку отдельных критериев в Acceptance Criteria owning spec. Здесь перечислены проверки, а не утверждения о существующих тестах.

| Criterion | Requirement | Planned verification | Result | Evidence |
| --- | --- | --- | --- | --- |
| IDD-0001-AC1 | На каждой ОС запуск, повторный запуск, закрытие окон и Quit имеют ожидаемую семантику. | Lifecycle integration + installed-app manual matrix | not verified | — |
| IDD-0001-AC2 | Второй процесс не создаёт второе активное наблюдение clipboard и не приводит к повреждению БД. | Lifecycle integration + installed-app manual matrix | not verified | — |
| IDD-0001-AC3 | При запуске и Resume предшествующий clipboard item не импортируется. | Lifecycle integration + installed-app manual matrix | not verified | — |
| IDD-0001-AC4 | Pause полностью запрещает **чтение содержимого** буфера. | Lifecycle integration + installed-app manual matrix | not verified | — |
| IDD-0001-AC5 | Ошибка и отсутствие разрешения не закрывают приложение и не делают сохранённую историю недоступной. | Lifecycle integration + installed-app manual matrix | not verified | — |
| IDD-0002-AC1 | Повторное считывание без изменения ревизии не добавляет дубль. | Capture/race automated tests + real OS permission/marker checks | not verified | — |
| IDD-0002-AC2 | Unicode, пробелы и line endings идентичны исходному UTF-8. | Capture/race automated tests + real OS permission/marker checks | not verified | — |
| IDD-0002-AC3 | Запись, превышающая лимит, защищённые маркеры и file URL не сохраняются. | Capture/race automated tests + real OS permission/marker checks | not verified | — |
| IDD-0002-AC4 | Pause, `.default`, `.ask`, `.alwaysDeny` на macOS **не вызывают чтение содержимого** и не вызывают prompt loops. | Capture/race automated tests + real OS permission/marker checks | not verified | — |
| IDD-0002-AC5 | После собственного write быстрое внешнее copy сохраняется корректно, чужой текст не теряется. | Capture/race automated tests + real OS permission/marker checks | not verified | — |
| IDD-0002-AC6 | На Windows clipboard update обнаруживается без глобального keyboard hook; временное `OpenClipboard` contention не портит историю. | Capture/race automated tests + real OS permission/marker checks | not verified | — |
| IDD-0002-AC7 | Пользователь понимает ограничения «не все clipboard changes обнаруживаются» и «источники определяются best-effort». | Capture/race automated tests + real OS permission/marker checks | not verified | — |
| IDD-0003-AC1 | Циклы заполнения 2000 → очистка → повторное заполнение не приводят к неограниченному росту файла после maintenance. | Real SQLite integration + Unicode corpus + measured maintenance/search | not verified | — |
| IDD-0003-AC2 | Exact Unicode corpus с emoji, CRLF, NUL, combining marks, trailing spaces — все различные ожидаемые варианты сохранены. | Real SQLite integration + Unicode corpus + measured maintenance/search | not verified | — |
| IDD-0003-AC3 | Pin переживает retention, Clear Recent и restart. | Real SQLite integration + Unicode corpus + measured maintenance/search | not verified | — |
| IDD-0003-AC4 | Нет потери atomicity при исключении во время транзакции. | Real SQLite integration + Unicode corpus + measured maintenance/search | not verified | — |
| IDD-0003-AC5 | 500 элементов + разумное число pins фильтруются без ощутимых задержек; начальная проверка: 100 поисков в контрольном наборе менее ~2 секунд на тестовой машине (фиксировать аппаратные условия, не выдавать этот порог за универсальную гарантию). | Real SQLite integration + Unicode corpus + measured maintenance/search | not verified | — |
| IDD-0004-PIN | Иконки закреплённой и незакреплённой записи визуально различаются; pin/unpin сразу меняет иконку, подсказку и доступное имя в обеих темах. | Manual pin/unpin + light/dark themes + accessibility | not verified | — |
| IDD-0004-AC1 | Все команды доступны с клавиатуры и мышью, доступны для screen reader. | UI automation + manual keyboard/focus/accessibility/displays | not verified | — |
| IDD-0004-AC2 | History открывается на корректном мониторе и остаётся в видимой области после изменения DPI/подключения дисплея. | UI automation + manual keyboard/focus/accessibility/displays | not verified | — |
| IDD-0004-AC3 | Click вне панели, Escape, закрытие и повторный hotkey не вставляют текст. | UI automation + manual keyboard/focus/accessibility/displays | not verified | — |
| IDD-0004-AC4 | После pin/recopy порядок открытого списка не скачет; при повторном открытии пересортирован. | UI automation + manual keyboard/focus/accessibility/displays | not verified | — |
| IDD-0005-AC1 | При смене поля/окна/приложения между OpenHistory и Paste синтетических клавиш не отправляется. | Deterministic race tests + real targets/permissions/layout matrix | not verified | — |
| IDD-0005-AC2 | После появления чужой clipboard revision между выбором и попыткой вставки синтетических клавиш не отправляется, чужой clipboard не затирается повторной записью. | Deterministic race tests + real targets/permissions/layout matrix | not verified | — |
| IDD-0005-AC3 | За один пользовательский выбор допускается не более одного paste shortcut. | Deterministic race tests + real targets/permissions/layout matrix | not verified | — |
| IDD-0005-AC4 | Недоступная Accessibility macOS / UIPI Windows ведёт к Copy Only, без elevation и без аварии. | Deterministic race tests + real targets/permissions/layout matrix | not verified | — |
| IDD-0005-AC5 | Комбинация работает под минимум двумя раскладками на каждой ОС, в том числе одной альтернативной. | Deterministic race tests + real targets/permissions/layout matrix | not verified | — |
| IDD-0005-AC6 | Многострочный текст вставляется нормальным paste, без симуляции ввода символов и без автоматического нажатия Enter отдельно. | Deterministic race tests + real targets/permissions/layout matrix | not verified | — |
| IDD-0006-AC1 | Смена и удаление hotkey без рестарта; явная ошибка занятой комбинации; Command/Control+V невозможно перехватить как Open History; регистрация после повторного запуска работает. | Hotkey registration integration + conflict/manual checks | not verified | — |
| IDD-0007-AC1 | macOS `.default` корректно объясняет, почему приложение пока не присутствует в System Settings clipboard list. | Preferences integration + real permissions/login/onboarding | not verified | — |
| IDD-0007-AC2 | При deny + отсутствии Accessibility история доступна, можно Copy Only, не возникает повторных системных запросов. | Preferences integration + real permissions/login/onboarding | not verified | — |
| IDD-0007-AC3 | Launch at Login показывает системное состояние, включая `requiresApproval`/pending, вместо ложного checked=On. | Preferences integration + real permissions/login/onboarding | not verified | — |
| IDD-0007-AC4 | Изменения настроек вступают в силу без рестарта там, где возможно. | Preferences integration + real permissions/login/onboarding | not verified | — |
| IDD-0008-AC1 | Обе платформы стартуют из установленного build без SDK. | Packaging/release integration + installed artifact manual checks | not verified | — |
| IDD-0008-AC2 | Новый stable release распознаётся, prerelease/draft игнорируется, офлайн/ошибки сети не ломают приложение. | Packaging/release integration + installed artifact manual checks | not verified | — |
| IDD-0008-AC3 | Update без доверенной подписи не устанавливается бесконтрольно; ошибок разрешений на запись достаточно для понятного fallback. | Packaging/release integration + installed artifact manual checks | not verified | — |
| IDD-0009-AC1 | `Finder` запускает `.app`, status item виден, Dock icon отсутствует, History получает keyboard focus. | Real Finder-launched app + clean permission/signing/layout matrix | not verified | — |
| IDD-0009-AC2 | Чистая установка: `.default` не вызывает автоматических запросов. После явной `Request Clipboard Access Once` системный запрос появляется по правилам ОС и UI не утверждает false Allow. | Real Finder-launched app + clean permission/signing/layout matrix | not verified | — |
| IDD-0009-AC3 | Состояния `.ask`, `.alwaysDeny` не запускают prompt loop и запрещают фоновые reads. | Real Finder-launched app + clean permission/signing/layout matrix | not verified | — |
| IDD-0009-AC4 | Изменение на `.alwaysAllow` в System Settings включает capture; отзыв выключает его; нет рестарта. | Real Finder-launched app + clean permission/signing/layout matrix | not verified | — |
| IDD-0009-AC5 | Захват одного обычного копирования без AX-доступа, Copy Only без AX; после granting AX — guarded Paste. | Real Finder-launched app + clean permission/signing/layout matrix | not verified | — |
| IDD-0009-AC6 | Secure text field, утраченный focus, другое активное приложение и remote/unsupported target — только Copy Only. | Real Finder-launched app + clean permission/signing/layout matrix | not verified | — |
| IDD-0009-AC7 | US/Russian/Dvorak paste shortcut реальной раскладкой (не только unit test resolver). | Real Finder-launched app + clean permission/signing/layout matrix | not verified | — |
| IDD-0009-AC8 | Установленный signed/ad-hoc bundle имеет понятный статус Gatekeeper, отсутствие требуемой нотификации не обходится отключением системы защиты. | Real Finder-launched app + clean permission/signing/layout matrix | not verified | — |
| IDD-0009-AC9 | Изменения версии при стабильной signing identity не приводят к неожиданной потере privacy authorization; если dev vs release identity разная — проблема ясно задокументирована, права надо перепредоставить. | Real Finder-launched app + clean permission/signing/layout matrix | not verified | — |
| IDD-0010-AC1 | В нормальных приложениях copy/lookup/paste работает; в elevated window без доверенной возможности инъекции — Copy Only. | Windows adapter integration + real tray/UIPI/focus checks | not verified | — |
| IDD-0010-AC2 | Windows tray поддерживает двойной запуск без второго экземпляра; закрытие окна не завершает мониторинг. | Windows adapter integration + real tray/UIPI/focus checks | not verified | — |
| IDD-0010-AC3 | clipboard contention/необычные текстовые форматы не вызывают падение и не сохраняют мусор. | Windows adapter integration + real tray/UIPI/focus checks | not verified | — |
| IDD-0011-AC1 | Воспроизводимые отказные сценарии имеют понятный статус и не теряют пользовательские данные. | Failure injection + source/runtime diagnostics review | not verified | — |
| IDD-0011-AC2 | Автоматический поиск в исходниках и runtime логах не выявляет случайного логирования содержимого буфера. | Failure injection + source/runtime diagnostics review | not verified | — |

## Release gate

Первый релиз требует успешных сборок и общих тестов; A01–A26 на релевантных платформах либо явно принятый совместимый fallback/формальный exception; evidence IDD-0012, IDD-0013, IDD-0014; реальную проверку Mac permissions и focus/paste из Finder; документированные ограничения. Никакой exception этим импортом не принят.

## Reference boundary

### Implemented safe-paste service scope

The platform-independent portions of IDD-0001-AC4/AC5, IDD-0002-AC2/AC5,
IDD-0005-AC1/AC2/AC3/AC4/AC6 and IDD-0011-AC1 are covered by the concrete
`PasteServiceTests` mapping in [VerificationEvidence.md](VerificationEvidence.md#safe-paste-service-evidence--2026-10-21).
The invocation API also covers IDD-0004's snapshot-before-activation and duplicate-action behavior.
These are service-level checks with injected ports: target refusal is supplied by the fake
adapter, and `ShortcutSent` asserts dispatch, not insertion. They do not change any
end-to-end or native acceptance result above. A03/A04/A07–A11/A13/A17/A18/A26
still require the native adapters, History UI integration and real-device evidence;
IDD-0013 remains open.

Локальный behavioral reference: C:\Private\ClipboardMac. Приоритет у исходного ТЗ CSharpClipboard. Reference Verification.md содержит gaps реальных permissions/paste/layouts; cross-build, named pasteboard и layout resolver tests не заменяют real-device acceptance нового приложения.

## Open decisions

- Постоянный новый macOS bundle ID и dev/release signing policy: выбрать и зафиксировать до permission tests. Значение из ТЗ приведено как пример.
- Доказанный bridge/ABI и memory ownership: IDD-0012; альтернативу нельзя считать выбранной.
- Windows source identity, нормализация, exclusions и ограничения best-effort attribution.
- Windows installer/portable, механизм per-user login, точные settings/storage paths через OS APIs.
- Проверенная цепочка доверия updater; до неё только release check/link и ручная установка.
- Native fullscreen macOS и x86_64: evidence и явно согласованные gaps.
- Сравнение Unicode-aware поиска .NET/Swift на корпусе; идентичность не доказана.
