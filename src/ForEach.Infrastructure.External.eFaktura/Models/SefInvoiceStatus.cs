namespace ForEach.Infrastructure.External.eFaktura.Models;

/// <summary>
/// Where a document stands at SEF, in their vocabulary rather than a host's.
///
/// Deliberately not a host's own column. A product storing this will have its own enum with its
/// own display strings and its own idea of which states are terminal — what this one is for is
/// reading the word SEF returned without every consumer writing the same switch.
/// </summary>
public enum SefInvoiceStatus
{
    /// <summary>Uploaded and not yet sent. Still ours to delete.</summary>
    Draft,

    /// <summary>At SEF, undecided. The state most invoices sit in for a day or two.</summary>
    Sent,

    /// <summary>The buyer has opened it, and has not answered.</summary>
    Delivered,

    /// <summary>The buyer accepted it.</summary>
    Approved,

    /// <summary>The buyer refused it, with a reason worth reading.</summary>
    Rejected,

    /// <summary>Withdrawn by the seller before the buyer decided.</summary>
    Cancelled,

    /// <summary>Reversed by the seller after the buyer had accepted it.</summary>
    Storno,

    /// <summary>A send that failed at SEF — not the same as a buyer's rejection.</summary>
    Failed
}
