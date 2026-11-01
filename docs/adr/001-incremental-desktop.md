# ADR-001: Retain WinForms and migrate runtime incrementally
Status: accepted for this increment, 2026-11-01.

Evaluate .NET 10, WPF, WinUI 3 and retaining WinForms. .NET 10 is Microsoft's LTS runtime; a local SDK avoids changing machine-wide tooling. Existing WinForms contains classroom workflows worth retaining. WPF offers mature binding, accessibility and flexible layout but replacing every workflow would postpone security fixes; WinUI 3 adds Windows App SDK packaging/runtime complexity. Choose .NET 10 + incremental WinForms modernization, original icons, accessible labels, persisted layouts, theme and Arabic/Hebrew/English direction support. Do not add a second desktop application with duplicated behavior.

Sources: https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/overview , https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/ , https://learn.microsoft.com/en-us/windows/apps/winui/winui3/ . Actual SDK installation/build results belong in AUDIT/PROGRESS.
