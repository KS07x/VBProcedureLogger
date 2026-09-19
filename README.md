# VBProcedureLogger

VBProcedureLogger is a Visual Basic/.NET Framework logging library that records procedure calls at build time and writes the resulting entries at runtime. Add the `<Log>` attribute to a `Sub` or `Function`; the NuGet build target instruments the source automatically before the normal Visual Basic compilation step.

## Features

- Logs procedure names and input arguments.
- Logs function return values when enabled.
- Adds timestamps and optional execution durations.
- Supports per-procedure options and a process-wide default log path.
- Writes to `Procedure.log` by default.
- Provides a runtime API for applications that need manual logging or scopes.
- Uses a Roslyn-based MSBuild task to instrument Visual Basic source without requiring handwritten wrappers.

## Requirements

- A Visual Basic .NET Framework application.
- .NET Framework 4.0 or later for the runtime assembly.
- Visual Studio 2026 with MSBuild and the Visual Basic/.NET Framework build tools to build the repository package. The supplied build script searches for the Visual Studio 2026 MSBuild installation.
- NuGet CLI. If `nuget.exe` is not already available on `PATH`, `BuildNuget.ps1` downloads it to a temporary directory.

The instrumentation project targets .NET Framework 4.7.2 and uses the Microsoft.CodeAnalysis Visual Basic packages listed in `Source/VBProcedureLogger.Instrumentation/packages.config`.

## Build the NuGet package

Run the following from the repository root in PowerShell:

```powershell
.\BuildNuget.ps1
```

The default build is a `Release` build with version `1.0.0`. Configuration and package version can be supplied explicitly:

```powershell
.\BuildNuget.ps1 -Configuration Debug -Version 1.0.0
```

The script:

1. Restores the instrumentation project's NuGet dependencies.
2. Builds `VBProcedureLogger.Runtime`.
3. Builds `VBProcedureLogger.Instrumentation`.
4. Stages the runtime and instrumentation assemblies under `Nuget/lib`.
5. Packs `Nuget/VBProcedureLogger.nuspec`.

The resulting package is written to:

```text
Nuget/VBProcedureLogger.<version>.nupkg
```

## Installation

After building the package, add `Nuget` as a local package source and install `VBProcedureLogger` into the Visual Basic application. For example, from the NuGet Package Manager Console:

```powershell
Install-Package VBProcedureLogger -Source .\Nuget -Version 1.0.0
```

The package contributes both parts of the system:

- `VBProcedureLogger.Runtime.dll` is referenced by the application.
- The package's MSBuild target and instrumentation assembly are imported during compilation.

For a consuming project, use a normal build after installation. The package target runs before `CoreCompile` and generates instrumented source in the project's intermediate output directory. Design-time builds are skipped.

## Basic usage

Import the runtime namespace and apply `<Log>` to a procedure:

```vb
Imports VBProcedureLogger.Runtime

Public Class Calculator
    <Log>
    Public Function Add(ByVal left As Integer, ByVal right As Integer) As Integer
        Return left + right
    End Function
End Class
```

With default settings, calling `Add(2, 3)` produces entries similar to:

```text
[ 2026-09-19 15:30:12 ] Add(2, 3)
[ 2026-09-19 15:30:12 ] Add(2, 3) => 5
```

The default log file is `Procedure.log` in the application's execution directory. Parent directories for a configured path are created automatically.

## Per-procedure options

The following options are available on `LogAttribute`:

| Option | Default | Description |
| --- | --- | --- |
| `LogArguments` | `True` | Include input parameter values in the invocation text. |
| `LogReturnValue` | `True` | Include a function's return value in the completion entry. |
| `LogTimeStamp` | `True` | Prefix entries with the current local timestamp. |
| `LogExecutionTime` | `False` | Include elapsed execution time in milliseconds in the completion entry. |
| `LogFilePath` | `Nothing` | Use a procedure-specific log path. Relative paths are resolved from the application directory. |

Options can be overridden with named Visual Basic attribute arguments:

```vb
<Log(LogArguments:=False,
     LogExecutionTime:=True,
     LogFilePath:="logs\calculations.log")>
Public Function Calculate(ByVal value As Integer) As Integer
    Return value * 10
End Function
```

Unspecified options retain their defaults. The instrumentation recognizes both `<Log>` and `<LogAttribute>`.

## Global log configuration

Configure the process-wide default path once during application startup:

```vb
Imports VBProcedureLogger.Runtime

ProcedureLogger.Configure("logs\application.log")
' Equivalent:
' ProcedureLogger.LogFilePath = "logs\application.log"
```

An absolute path is used as supplied. A relative path is resolved against `AppDomain.CurrentDomain.BaseDirectory`. A procedure-level `LogFilePath` takes precedence over the global default for that invocation.

## Manual runtime logging

The runtime assembly can also be used without source instrumentation:

```vb
Imports VBProcedureLogger.Runtime

ProcedureLogger.Write(
    "LoadCustomer",
    New Object() {customerId},
    customerName)
```

For more control, use `LogOptions` with `ProcedureLogger.Begin`:

```vb
Dim options As New LogOptions With {
    .LogArguments = True,
    .LogReturnValue = True,
    .LogTimeStamp = True,
    .LogExecutionTime = True,
    .LogFilePath = "logs\customer.log"
}

Dim scope As ProcedureLogScope = ProcedureLogger.Begin(
    "LoadCustomer",
    New Object() {customerId},
    options)

' Complete() logs a Sub-style completion.
' Complete(value) logs a function result.
scope.Complete(customerName)
```

`ProcedureLogScope.CompleteAndReturn` is provided for generated code and preserves the value of a return expression while recording it.

## Log behavior and formatting

- Entry logging happens when a scope is created.
- Function return logging happens when the scope is completed with a return value.
- A `Sub` normally produces an entry only. It produces a completion line when `LogExecutionTime` is enabled.
- If both return logging and execution-time logging are disabled, no completion line is written.
- Strings and characters are quoted, dates use an invariant `yyyy-MM-dd HH:mm:ss.fff` format, and enumerable values are rendered as bracketed lists.
- Log writes are synchronized within the process.
- Logging failures are swallowed so a failure to write a log does not change the behavior of the application.

The generated instrumentation handles explicit `Return` statements and the normal end of a procedure. It does not add a `Try...Finally` around the procedure, so an exception or an early `Exit Function`/`Exit Sub` can leave only the entry line in the log.

Do not enable argument or return logging for values that contain secrets or other data that must not be written to disk; the runtime does not provide redaction.

## Methodology

VBProcedureLogger separates build-time transformation from runtime logging:

1. The NuGet `.targets` file registers `InstrumentVisualBasicSources` before `CoreCompile`.
2. The MSBuild task copies each Visual Basic source file to an intermediate `VBProcedureLogger` directory.
3. `SourceInstrumenter` parses the source with the Roslyn Visual Basic syntax API and finds methods marked with `<Log>` or `<LogAttribute>`.
4. For each marked method, the rewriter injects a `ProcedureLogger.Begin` call, passes the method parameters as an object array, and converts return expressions to `CompleteAndReturn(...)` calls.
5. The generated files replace the original compile items for that build.
6. `ProcedureLogScope` writes the entry immediately and writes the completion data when the method completes.

This design keeps the application-facing runtime assembly small while allowing the package to add logging at compile time. Unmarked methods are passed through unchanged.

## Repository structure

```text
VBProcedureLogger/
├── BuildNuget.ps1
├── Nuget/
│   ├── VBProcedureLogger.nuspec
│   └── VBProcedureLogger.targets
└── Source/
    ├── VBProcedureLogger.Runtime/
    │   ├── LogAttribute.vb
    │   ├── ProcedureLogger.vb
    │   └── VBProcedureLogger.Runtime.vbproj
    └── VBProcedureLogger.Instrumentation/
        ├── Class1.vb
        ├── packages.config
        └── VBProcedureLogger.Instrumentation.vbproj
```

### Runtime project

`VBProcedureLogger.Runtime` contains the public API and runtime implementation:

- `LogAttribute.vb` defines the procedure attribute and its options.
- `ProcedureLogger.vb` defines global configuration, formatting, file output, `LogOptions`, and `ProcedureLogScope`.

### Instrumentation project

`VBProcedureLogger.Instrumentation` contains the Roslyn syntax rewriter and MSBuild task. It is used while compiling a consuming application and is not the application-facing logging API.

### NuGet packaging

The package places the runtime assembly under `lib/net40`, the instrumentation assembly under `build`, and imports `VBProcedureLogger.targets` automatically for compatible NuGet project types.

## Development notes

The repository currently contains the runtime and instrumentation projects but no automated test project. When changing the rewriter or runtime behavior, rebuild the package and verify a consuming Visual Basic project with marked and unmarked procedures, function returns, option overrides, custom paths, and failure/exception paths.

## License

VBProcedureLogger is distributed under the MIT license.
