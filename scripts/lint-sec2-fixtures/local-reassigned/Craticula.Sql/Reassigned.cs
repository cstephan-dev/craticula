namespace Fixture.Sql;

public interface IParameterSink
{
    string Bind(object? value);
}

public static class Reassigned
{
    // A local is safe only if EVERY value assigned to it is: the second assignment undoes the first.
    public static string Where(string userValue, bool inline, IParameterSink parameters)
    {
        var operand = parameters.Bind(userValue);
        if (inline)
        {
            operand = userValue;
        }

        return $"\"name\" = {operand}";
    }
}
