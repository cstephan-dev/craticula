using System.Reflection;

namespace Craticula.Sql.Tests;

/// <summary>
/// #2834: the docs site's C# quickstart page shows <c>samples/Craticula.Quickstart/Program.cs</c> and the output recorded in
/// <c>expected-output.txt</c>. This runs the program's entry point and compares what it prints with that file, so a change to the
/// compilers that alters the quickstart's SQL (or breaks the sample) fails here instead of leaving the page stale. It writes to
/// the console, so it shares a collection that never runs beside another test.
/// </summary>
[Collection("Console output")]
public class QuickstartSampleTests
{
    [Fact]
    public void The_Quickstart_Prints_Exactly_The_Output_The_Page_Shows()
    {
        var entryPoint = Assembly.Load(new AssemblyName("Craticula.Quickstart")).EntryPoint
            ?? throw new InvalidOperationException("Craticula.Quickstart has no entry point.");

        var original = Console.Out;
        using var captured = new StringWriter();
        try
        {
            Console.SetOut(captured);
            entryPoint.Invoke(null, entryPoint.GetParameters().Length == 0 ? null : [Array.Empty<string>()]);
        }
        finally
        {
            Console.SetOut(original);
        }

        var expected = File.ReadAllText(Path.Combine(RepoPaths.Root, "csharp", "samples", "Craticula.Quickstart", "expected-output.txt"));
        Assert.Equal(Normalize(expected), Normalize(captured.ToString()));
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd('\n');
}

[CollectionDefinition("Console output", DisableParallelization = true)]
public sealed class ConsoleOutputCollection;
