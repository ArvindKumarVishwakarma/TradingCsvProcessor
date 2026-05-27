namespace TradingCsvProcessor.Domain.Exceptions;

public sealed class NotFoundException : DomainException
{
    public NotFoundException(string entityName, object id)
        : base($"{entityName} '{id}' was not found.") { }
}
