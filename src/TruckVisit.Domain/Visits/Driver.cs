using TruckVisit.Domain.Common;

namespace TruckVisit.Domain.Visits;

/// <summary>
/// The person driving the truck. The case states only that "driver information must be captured"
/// without listing fields, so the shape below is an assumption.
/// </summary>
/// <remarks>
/// <para>
/// We capture the minimum a gate operator actually needs to admit someone: a name to call out, a
/// document number to check against the ID presented at the barrier, and an optional phone number
/// so the yard can reach the cab. Anything more would be collecting personal data we have no stated
/// purpose for.
/// </para>
/// <para>
/// This is the only personal data in the model and it sits under a seven-year retention rule, which
/// puts it squarely in GDPR scope. The retention and erasure implications are covered in the
/// architecture document rather than solved in code here.
/// </para>
/// </remarks>
public sealed record Driver
{
    public const int MaxFullNameLength = 200;
    public const int MaxDocumentIdLength = 50;
    public const int MaxPhoneNumberLength = 30;

    private Driver(string fullName, string documentId, string? phoneNumber)
    {
        FullName = fullName;
        DocumentId = documentId;
        PhoneNumber = phoneNumber;
    }

    /// <summary>Driver's name as printed on the document presented at the gate.</summary>
    public string FullName { get; }

    /// <summary>Identity or driving-licence number used to verify the driver at the barrier.</summary>
    public string DocumentId { get; }

    /// <summary>Optional contact number for the cab.</summary>
    public string? PhoneNumber { get; }

    public static Driver Create(string? fullName, string? documentId, string? phoneNumber) =>
        new(
            DomainText.Required(fullName, "driver.fullName", MaxFullNameLength),
            DomainText.Required(documentId, "driver.documentId", MaxDocumentIdLength),
            DomainText.Optional(phoneNumber, "driver.phoneNumber", MaxPhoneNumberLength));
}
