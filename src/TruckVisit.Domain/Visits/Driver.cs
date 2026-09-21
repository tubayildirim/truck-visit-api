using TruckVisit.Domain.Common;

namespace TruckVisit.Domain.Visits;

/// <summary>
/// The person driving the truck. The case states only that "driver information must be captured"
/// without listing fields, so the shape below is an assumption.
/// </summary>
/// <remarks>
/// <para>
/// Four fields, and the ones that are absent were as deliberate as the ones that are here.
/// </para>
/// <para>
/// A gate operator admitting a truck needs: a name to call out, a document number to check against
/// the ID presented at the barrier, the haulier to hold accountable for the unit, and a number to
/// reach the cab. <see cref="CompanyName"/> is required rather than optional because in a ro-ro
/// terminal the accountable party for a unit is the haulier, not the individual driver — it is who
/// gets billed, who a damage claim goes to, and who is called when a booked truck does not arrive.
/// It is also the only field here that is not about a person.
/// </para>
/// <para>
/// Nationality, date of birth and licence expiry were considered and rejected. Customs systems
/// already hold them, the gate admission decision does not use them, and every additional personal
/// field is a liability under a seven-year retention rule rather than an asset. Collecting data
/// because it might one day be useful is how a retention policy becomes unenforceable.
/// </para>
/// <para>
/// This remains the only personal data in the model, which puts it squarely in GDPR scope. The
/// retention and erasure implications are covered in the architecture document.
/// </para>
/// </remarks>
public sealed record Driver
{
    public const int MaxFullNameLength = 200;
    public const int MaxDocumentIdLength = 50;
    public const int MaxCompanyNameLength = 200;
    public const int MaxPhoneNumberLength = 30;

    private Driver(string fullName, string documentId, string companyName, string? phoneNumber)
    {
        FullName = fullName;
        DocumentId = documentId;
        CompanyName = companyName;
        PhoneNumber = phoneNumber;
    }

    /// <summary>Driver's name as printed on the document presented at the gate.</summary>
    public string FullName { get; }

    /// <summary>Identity or driving-licence number used to verify the driver at the barrier.</summary>
    public string DocumentId { get; }

    /// <summary>The haulier the unit is booked under — the party accountable for it.</summary>
    public string CompanyName { get; }

    /// <summary>Optional contact number for the cab.</summary>
    public string? PhoneNumber { get; }

    public static Driver Create(
        string? fullName,
        string? documentId,
        string? companyName,
        string? phoneNumber) =>
        new(
            DomainText.Required(fullName, "driver.fullName", MaxFullNameLength),
            DomainText.Required(documentId, "driver.documentId", MaxDocumentIdLength),
            DomainText.Required(companyName, "driver.companyName", MaxCompanyNameLength),
            DomainText.Optional(phoneNumber, "driver.phoneNumber", MaxPhoneNumberLength));
}
