# Engineering Rules Index

Next ID: ENG-0008

| Rule | Applicability | Applies when | Summary |
| --- | --- | --- | --- |
| ENG-0001 | Always | Every implementation change | C# / .NET 10 и Uno Skia Desktop; native API изолированы в платформенных адаптерах. |
| ENG-0002 | Always | Every implementation change | Сохранять направления зависимостей и платформенно-независимые контракты. |
| ENG-0003 | Conditional | Изменение persisted history | Низкоуровневый SQLite driver, точные UTF-8 BLOB и атомарное версионированное хранилище. |
| ENG-0004 | Always | Every implementation change | Изолировать interop с явными ownership, lifetime и thread contracts. |
| ENG-0005 | Conditional | macOS build/release | macOS bundle использует постоянный ID `dev.dimonsmart.CSharpClipboard` и согласованную signing identity. |
| ENG-0006 | Always | Every implementation change | Поддерживать automated/manual traceability и честное evidence обеих ОС. |
| ENG-0007 | Always | Every implementation change | CI проверяет обе ОС; релизы и bridge имеют явное версионирование. |
