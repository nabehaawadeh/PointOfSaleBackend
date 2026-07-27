using Dapper;
using FluentValidation;
using MediatR;
using Pos.Api.Exceptions;
using Pos.Api.Interfaces;

namespace Pos.Api.Features.Invoices;

public class FinalizeInvoiceRequest
{
    public int CashierId { get; set; }
    public List<InvoiceItemRequest> Items { get; set; } = new();
    public string? DiscountType { get; set; }
    public decimal? DiscountValue { get; set; }
}

public class InvoiceItemRequest
{
    public int ProductId { get; set; }
    public string UnitSold { get; set; } = "piece";
    public int Quantity { get; set; }
}

public class FinalizeInvoiceResponse
{
    public int InvoiceId { get; set; }
    public int InvoiceNumber { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal Total { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class FinalizeInvoiceCommand : IRequest<FinalizeInvoiceResponse>
{
    public int CashierId { get; set; }
    public List<InvoiceItemRequest> Items { get; set; } = new();
    public string? DiscountType { get; set; }
    public decimal? DiscountValue { get; set; }

    public static FinalizeInvoiceCommand FromRequest(FinalizeInvoiceRequest request) => new()
    {
        CashierId = request.CashierId,
        Items = request.Items,
        DiscountType = request.DiscountType,
        DiscountValue = request.DiscountValue
    };
}

public class FinalizeInvoiceValidator : AbstractValidator<FinalizeInvoiceCommand>
{
    public FinalizeInvoiceValidator()
    {
        RuleFor(x => x.CashierId)
            .GreaterThan(0).WithMessage("CashierId is required.");

        RuleFor(x => x.Items)
            .NotEmpty().WithMessage("Invoice must contain at least one item.");

        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.ProductId).GreaterThan(0);
            item.RuleFor(i => i.Quantity).GreaterThan(0)
                .WithMessage("Quantity must be greater than zero.");
            item.RuleFor(i => i.UnitSold)
                .Must(u => u == "piece" || u == "package")
                .WithMessage("UnitSold must be either 'piece' or 'package'.");
        });

        RuleFor(x => x.DiscountType)
            .Must(t => t == null || t == "fixed" || t == "percentage")
            .WithMessage("DiscountType must be 'fixed', 'percentage', or null.");

        When(x => x.DiscountType == "percentage" && x.DiscountValue.HasValue, () =>
        {
            RuleFor(x => x.DiscountValue!.Value)
                .InclusiveBetween(0, 100)
                .WithMessage("Percentage discount must be between 0 and 100.");
        });

        When(x => x.DiscountType == "fixed" && x.DiscountValue.HasValue, () =>
        {
            RuleFor(x => x.DiscountValue!.Value)
                .GreaterThanOrEqualTo(0)
                .WithMessage("Fixed discount value cannot be negative.");
        });

        When(x => x.DiscountType != null, () =>
        {
            RuleFor(x => x.DiscountValue)
                .NotNull().WithMessage("DiscountValue is required when DiscountType is set.");
        });
    }
}

public class FinalizeInvoiceHandler : IRequestHandler<FinalizeInvoiceCommand, FinalizeInvoiceResponse>
{
    private readonly IPosDatabase _database;
    private readonly ILogger<FinalizeInvoiceHandler> _logger;

    public FinalizeInvoiceHandler(IPosDatabase database, ILogger<FinalizeInvoiceHandler> logger)
    {
        _database = database;
        _logger = logger;
    }

    public async Task<FinalizeInvoiceResponse> Handle(FinalizeInvoiceCommand request, CancellationToken ct)
    {
        // BR-01: reject an empty invoice before touching the database at all.
        InvoiceCalculator.EnsureNotEmpty(request.Items.Count);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        try
        {
            decimal subtotal = 0;
            var lineItems = new List<(int ProductId, string UnitSold, int Quantity, decimal UnitPrice, int QuantityInPieces, decimal LineTotal)>();

            foreach (var item in request.Items)
            {
                // FOR UPDATE locks the product row for the rest of this transaction, so a
                // second, concurrent checkout on the same product has to wait its turn
                // instead of reading stale stock and overselling (BR-02 + BR-03 atomicity).
                var stock = await connection.QuerySingleOrDefaultAsync<dynamic>(
                    "SELECT PricePerPiece, PricePerPackage, PiecesPerPackage, StockInPieces, IsActive FROM Products WHERE Id = @ProductId FOR UPDATE",
                    new { item.ProductId },
                    transaction);

                if (stock is null)
                {
                    throw new NotFoundException($"Product {item.ProductId} not found.");
                }

                // BR-05: a discontinued/inactive product stays visible on old invoices,
                // but must not be sellable on a new one.
                if (!(bool)stock.IsActive)
                {
                    throw new BusinessRuleException($"Product {item.ProductId} is inactive and cannot be sold.");
                }

                int piecesPerPackage = (int)(stock.PiecesPerPackage ?? 0);
                int quantityInPieces = InvoiceCalculator.ConvertToBaseUnits(item.UnitSold, item.Quantity, piecesPerPackage);

                InvoiceCalculator.EnsureSufficientStock(item.ProductId, quantityInPieces, (int)stock.StockInPieces);

                decimal unitPrice = InvoiceCalculator.ResolveUnitPrice(
                    item.UnitSold,
                    (decimal?)stock.PricePerPiece,
                    (decimal?)stock.PricePerPackage);

                // BR-04: unit price is captured here as a snapshot; later changes to
                // Products.PricePerPiece/PricePerPackage never touch this saved invoice.
                decimal lineTotal = InvoiceCalculator.CalculateLineTotal(unitPrice, item.Quantity);
                subtotal += lineTotal;

                lineItems.Add((item.ProductId, item.UnitSold, item.Quantity, unitPrice, quantityInPieces, lineTotal));
            }

            // BR-11: invoice-level discount only; can never push total below zero.
            decimal discountAmount = InvoiceCalculator.CalculateDiscountAmount(subtotal, request.DiscountType, request.DiscountValue);
            decimal total = InvoiceCalculator.CalculateTotal(subtotal, discountAmount);

            var nextNumber = await connection.QuerySingleAsync<int>(
                "SELECT IFNULL(MAX(InvoiceNumber), 0) + 1 FROM Invoices FOR UPDATE",
                transaction: transaction);

            var invoiceId = await connection.QuerySingleAsync<int>(
                @"INSERT INTO Invoices
                    (InvoiceNumber, CashierId, HasReturn, Subtotal, DiscountType, DiscountValue, Total, CreatedAt)
                  VALUES
                    (@InvoiceNumber, @CashierId, 0, @Subtotal, @DiscountType, @DiscountValue, @Total, UTC_TIMESTAMP());
                  SELECT LAST_INSERT_ID();",
                new
                {
                    InvoiceNumber = nextNumber,
                    request.CashierId,
                    Subtotal = subtotal,
                    request.DiscountType,
                    request.DiscountValue,
                    Total = total
                },
                transaction);

            foreach (var line in lineItems)
            {
                await connection.ExecuteAsync(
                    @"INSERT INTO InvoiceItems
                        (InvoiceId, ProductId, UnitSold, Quantity, UnitPriceSnapshot, QuantityInBaseUnits, LineTotal)
                      VALUES
                        (@InvoiceId, @ProductId, @UnitSold, @Quantity, @UnitPrice, @QuantityInPieces, @LineTotal)",
                    new
                    {
                        InvoiceId = invoiceId,
                        line.ProductId,
                        line.UnitSold,
                        line.Quantity,
                        UnitPrice = line.UnitPrice,
                        line.QuantityInPieces,
                        line.LineTotal
                    },
                    transaction);

                await connection.ExecuteAsync(
                    "UPDATE Products SET StockInPieces = StockInPieces - @QuantityInPieces WHERE Id = @ProductId",
                    new { line.ProductId, line.QuantityInPieces },
                    transaction);
            }

            transaction.Commit();

            return new FinalizeInvoiceResponse
            {
                InvoiceId = invoiceId,
                InvoiceNumber = nextNumber,
                Subtotal = subtotal,
                DiscountAmount = discountAmount,
                Total = total,
                CreatedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, "Failed to finalize invoice for cashier {CashierId}", request.CashierId);
            throw;
        }
    }
}
