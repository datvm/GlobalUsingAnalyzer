# GlobalUsingAnalyzer

Roslyn analyzer and Visual Studio extension that helps you **promote using directives into the right shared place**:

| Source | Destination |
|--------|-------------|
| C# `using` / `global using` | Project `<Using />` items and/or `ZGlobalUsings.cs` |
| Blazor `@using` in `.razor` | Nearest hierarchical `_Imports.razor` |
| MVC / Razor Pages `@using` in `.cshtml` | Nearest hierarchical `_ViewImports.cshtml` |

Diagnostics:

| ID | Severity | What it reports |
|----|----------|-----------------|
| **GUA001** | Info | Promotable C# `using` directives |
| **GUA003** | Warning | Promotable Razor `@using` directives |

> Visual Studio may also show its own built-in **“Promote using directive to \_Imports.razor”**. This extension’s actions are labeled **(GlobalUsingAnalyzer)** so you can tell them apart. Prefer GlobalUsingAnalyzer when you care about correct multi-project ownership (e.g. Blazor Web App Client vs Server) and consistent sorted imports.

---

## Install

[Placeholder for Marketplace links]

## Features

### GUA001 — C# usings

Reports ordinary, `static`, alias, and `global using` directives and offers code fixes to:

- **Move to `ZGlobalUsings.cs` as `global using`**
- **Move to the project file as `<Using />`**

Supports **Fix All** in document / project / solution (grouped by destination).

### GUA003 — Razor `@using`

Reports `@using` lines in component / page sources (not in the hierarchical imports files themselves) and offers:

- **Move to nearest `_Imports.razor` (GlobalUsingAnalyzer)** — for `.razor`
- **Move to nearest `_ViewImports.cshtml` (GlobalUsingAnalyzer)** — for `.cshtml`

Behavior:

- Chooses the **nearest existing** imports file walking up from the source folder; otherwise creates one at the **project root** (the folder that contains the `.csproj`).
- **Never** creates `_Imports.razor` / `_ViewImports.cshtml` outside the owning project (important for multi-project Blazor Web Apps).
- **Skips** `@using` values already inherited from an applicable imports file.
- **Sorts** `@using` lines in the destination the same way C# `<Using />` sorting works.
- On `.razor` / `.cshtml`, the lightbulb entry comes from a VSIX suggested-action (C# code fixes alone cannot appear in the Razor language lightbulb).

---

## Usage

### C# (`.cs`)

1. Place the caret on a `using` that shows **GUA001** (or open the lightbulb / Error List).
2. Choose **Move to ZGlobalUsings.cs…** or **Move to .csproj as \<Using /\>**.
3. Optionally use **Fix all** for the chosen destination.

### Razor (`.razor` / `.cshtml`)

1. Place the caret on an `@using` line (for example in `Counter.razor`).
2. Open the lightbulb (`Ctrl+.`).
3. Choose **Move to nearest \_Imports.razor (GlobalUsingAnalyzer)**  
   (or **\_ViewImports.cshtml** for MVC pages).

You can also apply fixes from the **Error List** when **GUA003** is listed (filter **Build + IntelliSense**, include **Warnings**).