namespace PlottR0._2.Services.Abstractions;

public sealed class ForbiddenAccessException : Exception
{
    public ForbiddenAccessException(string message) : base(message) { }
}