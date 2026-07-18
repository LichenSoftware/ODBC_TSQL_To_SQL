using Microsoft.Extensions.Configuration;
using MigrationValidation.Runner;
using Spectre.Console;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddCommandLine(args)
    .Build();

// Determine which connection to use
var activeConnection = configuration["connection"]
    ?? configuration["ActiveConnection"]
    ?? "SqlServer";

var connectionString = configuration[$"ConnectionStrings:{activeConnection}"];
if (string.IsNullOrEmpty(connectionString))
{
    AnsiConsole.MarkupLine($"[red]Error:[/] Connection string '{activeConnection}' not found in configuration.");
    return 1;
}

var category = configuration["category"] ?? "All";
var verbose = configuration["verbose"] != null || 
              configuration.GetValue<bool>("Validation:VerboseOutput");
var stopOnFirst = configuration.GetValue<bool>("Validation:StopOnFirstFailure");
var timeoutSeconds = configuration.GetValue<int>("Validation:TimeoutSeconds", 30);

// Banner
AnsiConsole.Write(new Rule("[bold blue]Migration Validation Test Suite[/]").RuleStyle("blue"));
AnsiConsole.MarkupLine($"  Target:     [yellow]{activeConnection}[/]");
AnsiConsole.MarkupLine($"  Category:   [yellow]{category}[/]");
AnsiConsole.MarkupLine($"  Verbose:    [yellow]{verbose}[/]");
AnsiConsole.MarkupLine($"  Timeout:    [yellow]{timeoutSeconds}s[/]");
AnsiConsole.WriteLine();

var runner = new ValidationRunner(connectionString, timeoutSeconds, verbose);

var categories = category.Equals("All", StringComparison.OrdinalIgnoreCase)
    ? new[] { "Tables", "Views", "Functions", "StoredProcedures", "Synonyms" }
    : new[] { category };

var allResults = new List<TestResult>();

foreach (var cat in categories)
{
    var tests = TestCatalog.GetTests(cat);
    if (tests.Count == 0)
    {
        AnsiConsole.MarkupLine($"[yellow]Warning:[/] No tests found for category '{cat}'");
        continue;
    }

    AnsiConsole.Write(new Rule($"[bold]{cat}[/]").LeftJustified());

    foreach (var test in tests)
    {
        var result = await runner.ExecuteTestAsync(test);
        allResults.Add(result);

        var icon = result.Passed ? "[green]✓[/]" : "[red]✗[/]";
        var timing = $"[grey]({result.ElapsedMs}ms)[/]";
        AnsiConsole.MarkupLine($"  {icon} {Markup.Escape(test.Name)} {timing}");

        if (!result.Passed)
        {
            AnsiConsole.MarkupLine($"    [red]{Markup.Escape(result.ErrorMessage ?? "Unknown error")}[/]");
        }

        if (verbose && result.RowCount.HasValue)
        {
            AnsiConsole.MarkupLine($"    [grey]Rows returned: {result.RowCount}[/]");
        }

        if (!result.Passed && stopOnFirst)
        {
            AnsiConsole.MarkupLine("[red]Stopping on first failure.[/]");
            goto Summary;
        }
    }

    AnsiConsole.WriteLine();
}

Summary:

// Summary
AnsiConsole.Write(new Rule("[bold]Summary[/]").RuleStyle("blue"));
var passed = allResults.Count(r => r.Passed);
var failed = allResults.Count(r => !r.Passed);
var total = allResults.Count;

var table = new Table();
table.AddColumn("Metric");
table.AddColumn("Value");
table.AddRow("Total Tests", total.ToString());
table.AddRow("[green]Passed[/]", $"[green]{passed}[/]");
table.AddRow("[red]Failed[/]", $"[red]{failed}[/]");
table.AddRow("Pass Rate", $"{(total > 0 ? (passed * 100.0 / total) : 0):F1}%");
table.AddRow("Target", activeConnection);

AnsiConsole.Write(table);

if (failed > 0)
{
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[bold red]Failed Tests:[/]");
    foreach (var fail in allResults.Where(r => !r.Passed))
    {
        AnsiConsole.MarkupLine($"  [red]•[/] {Markup.Escape(fail.TestName)}: {Markup.Escape(fail.ErrorMessage ?? "Unknown")}");
    }
}

return failed > 0 ? 1 : 0;
