using Pos.Api.Exceptions;

namespace Pos.Api.Features.Invoices;

/// <summary>
/// Pure calculation/validation logic for building an invoice. Deliberately has no
/// dependency on IDbConnection/Dapper so it can be unit tested directly, and so the
/// same rules can later be reused by the Return/Exchange feature.
/// </summary>
public static class InvoiceCalculator
{
    /// <summary>
    /// BR-02.1: stock is always tracked in pieces regardless of how a product is sold.
    /// Converts a sold quantity (piece or package) into the base "pieces" unit.
    /// </summary>
    public static int ConvertToBaseUnits(string unitSold, int quantity, int piecesPerPackage)
    {
        if (unitSold == "package")
        {
            if (piecesPerPackage <= 0)
            {
                throw new BusinessRuleException(
                    "This product is not configured to be sold by package (missing PiecesPerPackage).");
            }

            return quantity * piecesPerPackage;
        }

        return quantity;
    }

    /// <summary>
    /// Picks the correct price snapshot (BR-04) for the unit the item was actually sold in.
    /// </summary>
    public static decimal ResolveUnitPrice(string unitSold, decimal? pricePerPiece, decimal? pricePerPackage)
    {
        if (unitSold == "package")
        {
            return pricePerPackage
                ?? throw new BusinessRuleException("This product has no package price configured.");
        }

        return pricePerPiece
            ?? throw new BusinessRuleException("This product has no piece price configured.");
    }

    public static decimal CalculateLineTotal(decimal unitPrice, int quantity) => unitPrice * quantity;

    /// <summary>
    /// BR-02: requested quantity (already converted to pieces) must not exceed the
    /// current stock, whether the item was sold by piece or by package.
    /// </summary>
    public static void EnsureSufficientStock(int productId, int requestedInPieces, int availableInPieces)
    {
        if (requestedInPieces > availableInPieces)
        {
            throw new BusinessRuleException(
                $"Insufficient stock for product {productId}. Available: {availableInPieces}, requested: {requestedInPieces}.");
        }
    }

    /// <summary>BR-01: an invoice must contain at least one line item.</summary>
    public static void EnsureNotEmpty(int itemCount)
    {
        if (itemCount == 0)
        {
            throw new BusinessRuleException("Invoice must contain at least one item.");
        }
    }

    /// <summary>
    /// BR-11: invoice-level discount only, fixed amount or percentage. Percentage cannot
    /// exceed 100%, and the resulting discount can never make the total negative.
    /// </summary>
    public static decimal CalculateDiscountAmount(decimal subtotal, string? discountType, decimal? discountValue)
    {
        if (discountType is null)
        {
            return 0m;
        }

        var value = discountValue ?? 0m;

        if (value < 0)
        {
            throw new BusinessRuleException("Discount value cannot be negative.");
        }

        decimal discountAmount = discountType switch
        {
            "fixed" => value,
            "percentage" => value > 100
                ? throw new BusinessRuleException("Percentage discount cannot exceed 100%.")
                : subtotal * value / 100m,
            _ => throw new BusinessRuleException($"Unknown discount type '{discountType}'.")
        };

        if (discountAmount > subtotal)
        {
            throw new BusinessRuleException(
                "Discount cannot exceed the invoice subtotal (BR-11: total cannot go below zero).");
        }

        return discountAmount;
    }

    /// <summary>BR-11: total = subtotal - discountAmount, and can never be negative.</summary>
    public static decimal CalculateTotal(decimal subtotal, decimal discountAmount)
    {
        var total = subtotal - discountAmount;

        if (total < 0)
        {
            throw new BusinessRuleException("Invoice total cannot be negative.");
        }

        return total;
    }

    /// <summary>
    /// BR-15: the cumulative returned quantity for a line item (across every previous
    /// return, plus this one) can never exceed the quantity originally sold on that line.
    /// </summary>
    public static void EnsureReturnQuantityAllowed(int alreadyReturned, int newReturnQuantity, int originallySold)
    {
        if (newReturnQuantity <= 0)
        {
            throw new BusinessRuleException("Returned quantity must be greater than zero.");
        }

        if (alreadyReturned + newReturnQuantity > originallySold)
        {
            throw new BusinessRuleException(
                $"Cannot return {newReturnQuantity} unit(s): {alreadyReturned} already returned out of {originallySold} sold.");
        }
    }

    /// <summary>
    /// Recalculates the invoice-level discount amount against the reduced subtotal after
    /// a return. Unlike CalculateDiscountAmount (used when an invoice is first created),
    /// this never throws: a percentage discount naturally scales down with the smaller
    /// subtotal, and a fixed discount is capped at the new subtotal so the total still
    /// never goes negative (BR-11), instead of rejecting an otherwise-valid return.
    /// </summary>
    public static decimal RecalculateDiscountAfterReturn(decimal newSubtotal, string? discountType, decimal? discountValue)
    {
        if (discountType is null)
        {
            return 0m;
        }

        var value = discountValue ?? 0m;

        return discountType switch
        {
            "percentage" => newSubtotal * value / 100m,
            _ => Math.Min(value, newSubtotal) // "fixed" (and any other stored value, defensively)
        };
    }
}