namespace Solvelt.Api.Orders;

/// <summary>SQL used by the <c>RefundLineTaxBackfill</c> migration (kept here so a test can run exactly what the migration runs). Portable: the same text runs on SQLite and PostgreSQL.</summary>
public static class RefundTaxBackfill
{
    /// <summary>
    /// Refund lines written before the <c>OrderShippingTax</c> migration have a tax share of 0, so the last refund of such a line would report the earlier refunds' tax again.
    /// Gives each of them the share it would have had: floor(line tax x refunded quantity / line quantity). Only rows whose share is still 0 on a line that has tax are touched
    /// (a share of 0 that was computed afterwards is the same floor, so running it again changes nothing). Shipping tax needs nothing: legacy orders have none.
    /// </summary>
    public const string Sql = """
        UPDATE order_refund_lines SET "TaxCents" = (
            SELECT (ol."TaxCents" * order_refund_lines."Quantity") / ol."Quantity" FROM order_lines ol WHERE ol."Id" = order_refund_lines."OrderLineId")
        WHERE "TaxCents" = 0
          AND EXISTS (SELECT 1 FROM order_lines ol WHERE ol."Id" = order_refund_lines."OrderLineId" AND ol."TaxCents" > 0 AND ol."Quantity" > 0)
        """;
}
