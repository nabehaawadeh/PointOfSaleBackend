using FluentValidation;
using MediatR;

namespace Pos.Api.Behaviors;

public class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        _validators = validators;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (_validators.Any())
        {
            var context = new ValidationContext<TRequest>(request);
            var failures = (await Task.WhenAll(
                    _validators.Select(v => v.ValidateAsync(context, cancellationToken))))
                .SelectMany(result => result.Errors)
                .Where(f => f != null)
                .ToList();

            if (failures.Count != 0)
            {
                var errors = failures
                    .Select(f => $"{f.PropertyName}: {f.ErrorMessage}")
                    .ToList();

                throw new Pos.Api.Exceptions.ValidationException(errors);
            }
        }

        return await next();
    }
}
