namespace Pos.Api.Exceptions;

// Inherits from BusinessException on purpose: ExceptionHandlingMiddleware only has a
// catch (BusinessException) block. Before this fix, BusinessRuleException derived
// directly from System.Exception, so it slipped past that block and was returned to
// the client as a generic 500 instead of a 409 Conflict.
public class BusinessRuleException : BusinessException
{
    public BusinessRuleException(string message) : base(message)
    {
    }
}