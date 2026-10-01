namespace NetLift.SchemaDiff;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public static class SchemaDiffCommand
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length != 2)
        {
            error.WriteLine("Usage: NetLift.SchemaDiff <before-schema.json> <after-schema.json>");
            return 64;
        }

        try
        {
            var report = SchemaDiffEngine.Compare(ReadSchema(args[0]), ReadSchema(args[1]));
            output.WriteLine(SchemaDiffEngine.ToJson(report));
            return report.Classification switch
            {
                "compatible" => 0,
                "breaking" => 1,
                "unsupported" => 2,
                _ => 2
            };
        }
        catch (SchemaDiffInputException exception)
        {
            error.WriteLine("error: " + exception.Message);
            return 64;
        }
        catch (IOException exception)
        {
            error.WriteLine("error: " + exception.Message);
            return 64;
        }
        catch (UnauthorizedAccessException exception)
        {
            error.WriteLine("error: " + exception.Message);
            return 64;
        }
    }

    private static string ReadSchema(string path)
    {
        var content = File.ReadAllText(path);
        if (!string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
            return content;

        var tree = CSharpSyntaxTree.ParseText(content);
        var errors = tree.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length != 0)
            throw new SchemaDiffInputException(
                $"Generated C# input is invalid: {string.Join("; ", errors.Select(error => error.GetMessage()))}");

        var constants = tree.GetRoot().DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(variable => variable.Identifier.ValueText == "CanonicalJson" &&
                variable.Initializer?.Value is LiteralExpressionSyntax literal &&
                literal.IsKind(SyntaxKind.StringLiteralExpression))
            .Select(variable => ((LiteralExpressionSyntax)variable.Initializer!.Value).Token.ValueText)
            .ToArray();
        return constants.Length switch
        {
            1 => constants[0],
            0 => throw new SchemaDiffInputException(
                $"{path} does not contain a generated CanonicalJson string constant."),
            _ => throw new SchemaDiffInputException(
                $"{path} contains multiple CanonicalJson string constants.")
        };
    }
}
