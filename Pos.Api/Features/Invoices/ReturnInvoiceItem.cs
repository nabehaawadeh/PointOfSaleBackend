using Dapper;
using FluentValidation;
using MediatR;
using Pos.Api.Exceptions;
using Pos.Api.Interfaces;

namespace Pos.Api.Features.Invoices;

public class ReturnInvoiceItemRequest
{
    public int InvoiceItemId { get; set; }
    public int ReturnedQuantity { get; set; }
    public int ProcessedBy { get; set; }
    public string? Reason { get; set; }
}

public class ReturnInvoiceItemResponse
{
    public int ReturnId { get; set; }
    public int InvoiceId { get; set; }
    public int InvoiceItemId { get; set; }
    public int ReturnedQuantity { get; set; }
    public decimal RefundAmount { get; set; }
    public decimal NewSubtotal { get; set; }
    public decimal NewTotal { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ReturnInvoiceItemCommand : IRequest<ReturnInvoiceItemResponse>
{
    public int InvoiceItemId { get; set; }
    public int ReturnedQuantity { get; set; }
    public int ProcessedBy { get; set; }
    public string? Reason { get; set; }

    public static ReturnInvoiceItemCommand FromRequest(ReturnInvoiceItemRequest request) => new()
    {
        InvoiceItemId = request.InvoiceItemId,
        ReturnedQuantity = request.ReturnedQuantity,
        ProcessedBy = request.ProcessedBy,
        Reason = request.Reason
    };
}

public class ReturnInvoiceItemValidator : AbstractValidator<ReturnInvoiceItemCommand>
{
    public ReturnInvoiceItemValidator()
    {
        RuleFor(x => x.InvoiceItemId)
            .GreaterThan(0).WithMessage("InvoiceItemId is required.");

        RuleFor(x => x.ReturnedQuantity)
            .GreaterThan(0).WithMessage("ReturnedQuantity must be greater than zero.");

        RuleFor(x => x.ProcessedBy)
            .GreaterThan(0).WithMessage("ProcessedBy (user id) is required.");

        RuleFor(x => x.Reason)
            .MaximumLength(255).WithMessage("Reason cannot exceed 255 characters.");
    }
}

public class ReturnInvoiceItemHandler : IRequestHandler<ReturnInvoiceItemCommand, ReturnInvoiceItemResponse>
{
    private readonly IPosDatabase _database;
    private readonly ILogger<ReturnInvoiceItemHandler> _logger;

    public ReturnInvoiceItemHandler(IPosDatabase database, ILogger<ReturnInvoiceItemHandler> logger)
    {
        _database = database;
        _logger = logger;
    }

    public async Task<ReturnInvoiceItemResponse> Handle(ReturnInvoiceItemCommand request, CancellationToken ct)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        try
        {
            // Lock the invoice item together with its parent invoice so a concurrent
            // return or a concurrent finalize can't read stale Subtotal/Total values.
            var row = await connection.QuerySingleOrDefaultAsync<dynamic>(
                @"SELECT
                      ii.Id AS InvoiceItemId, ii.InvoiceId, ii.ProductId, ii.UnitSold,
                      ii.Quantity, ii.UnitPriceSnapshot,
                      i.Subtotal, i.DiscountType, i.DiscountValue
                  FROM InvoiceItems ii
                  JOIN Invoices i ON i.Id = ii.InvoiceId
                  WHERE ii.Id = @InvoiceItemId
                  FOR UPDATE",
                new { request.InvoiceItemId },
                transaction);

            if (row is null)
            {
                throw new NotFoundException($"Invoice item {request.InvoiceItemId} not found.");
            }

            // BR-15: cumulative returns for this line can never exceed what was sold.
            int alreadyReturned = await connection.QuerySingleAsync<int>(
                @"SELECT COALESCE(SUM(ReturnedQuantity), 0) FROM InvoiceReturns
                  WHERE InvoiceItemId = @InvoiceItemId AND Type = 'return'",
                new { request.InvoiceItemId },
                transaction);

            InvoiceCalculator.EnsureReturnQuantityAllowed(
                alreadyReturned, request.ReturnedQuantity, (int)row.Quantity);

            // Lock the product row for the stock update (BR-07 restock, BR-03 atomicity).
            var product = await connection.QuerySingleOrDefaultAsync<dynamic>(
                "SELECT PiecesPerPackage FROM Products WHERE Id = @ProductId FOR UPDATE",
                new { ProductId = (int)row.ProductId },
                transaction);

            if (product is null)
            {
                throw new NotFoundException($"Product {(int)row.ProductId} not found.");
            }

            int piecesPerPackage = (int)(product.PiecesPerPackage ?? 0);
            int returnedInPieces = InvoiceCalculator.ConvertToBaseUnits(
                (string)row.UnitSold, request.ReturnedQuantity, piecesPerPackage);

            // BR-07: restock immediately, in the same transaction as the return itself.
            await connection.ExecuteAsync(
                "UPDATE Products SET StockInPieces = StockInPieces + @ReturnedInPieces WHERE Id = @ProductId",
                new { ReturnedInPieces = returnedInPieces, ProductId = (int)row.ProductId },
                transaction);

            // Auto-recalculate the invoice total.
            decimal refundAmount = InvoiceCalculator.CalculateLineTotal(
                (decimal)row.UnitPriceSnapshot, request.ReturnedQuantity);

            decimal newSubtotal = (decimal)row.Subtotal - refundAmount;
            decimal newDiscountAmount = InvoiceCalculator.RecalculateDiscountAfterReturn(
                newSubtotal, (string?)row.DiscountType, (decimal?)row.DiscountValue);
            decimal newTotal = InvoiceCalculator.CalculateTotal(newSubtotal, newDiscountAmount);

            // Update has_return flag + recalculated totals on the invoice.
            await connection.ExecuteAsync(
                @"UPDATE Invoices
                  SET Subtotal = @NewSubtotal, Total = @NewTotal, HasReturn = 1
                  WHERE Id = @InvoiceId",
                new { NewSubtotal = newSubtotal, NewTotal = newTotal, InvoiceId = (int)row.InvoiceId },
                transaction);

            var returnId = await connection.QuerySingleAsync<int>(
                @"INSERT INTO InvoiceReturns
                    (InvoiceId, InvoiceItemId, Type, ReturnedQuantity, ProcessedBy, Reason, CreatedAt)
                  VALUES
                    (@InvoiceId, @InvoiceItemId, 'return', @ReturnedQuantity, @ProcessedBy, @Reason, UTC_TIMESTAMP());
                  SELECT LAST_INSERT_ID();",
                new
                {
                    InvoiceId = (int)row.InvoiceId,
                    request.InvoiceItemId,
                    request.ReturnedQuantity,
                    request.ProcessedBy,
                    request.Reason
                },
                transaction);

            transaction.Commit();

            return new ReturnInvoiceItemResponse
            {
                ReturnId = returnId,
                InvoiceId = (int)row.InvoiceId,
                InvoiceItemId = request.InvoiceItemId,
                ReturnedQuantity = request.ReturnedQuantity,
                RefundAmount = refundAmount,
                NewSubtotal = newSubtotal,
                NewTotal = newTotal,
                CreatedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, "Failed to process return for invoice item {InvoiceItemId}", request.InvoiceItemId);
            throw;
        }
    }
}