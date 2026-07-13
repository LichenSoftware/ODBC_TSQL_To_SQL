namespace SchemaConversion.Core.Models;

public enum ConversionStatus
{
    Pending,
    Converted,
    Flagged,
    Failed,
    OutOfScope,
    ManuallyReviewed
}
